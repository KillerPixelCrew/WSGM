using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>Projects commands from admitted, ready plugins without exposing plugin code to Steam.</summary>
internal sealed class CommonPluginSteamUiSource : ISteamExtensionsTabBackend, IDisposable
{
    private readonly Dictionary<string, Command> _commands = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly PluginHost _host;
    private readonly CommonPluginManager _manager;
    private readonly Dictionary<string, PluginRegistration> _settingsOwners = new(StringComparer.Ordinal);
    private readonly HashSet<PluginRegistration> _subscriptions = [];
    private bool _disposed;
    private long _revision;

    internal CommonPluginSteamUiSource(CommonPluginManager manager, PluginHost host)
    {
        _manager = manager;
        _host = host;
        manager.Changed += OnChanged;
        host.HealthChanged += OnHealthChanged;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var owner in _subscriptions)
            {
                owner.Actions?.UnsubscribeSteamUiChanged(OnChanged);
            }

            _subscriptions.Clear();
            _settingsOwners.Clear();
            _commands.Clear();
            _manager.Changed -= OnChanged;
            _host.HealthChanged -= OnHealthChanged;
            Changed = null;
        }
    }

    public Task<SteamUiCommandResult> ActivateAsync(string id, CancellationToken cancellationToken)
    {
        return ActivateAsync(null, id, cancellationToken);
    }

    public async Task<SteamUiCommandResult> ConfigureAsync(
        string id,
        string key,
        JsonElement value,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        PluginRegistration owner;
        PluginSetting setting;
        lock (_gate)
        {
            Refresh();
            if (_disposed || !_settingsOwners.TryGetValue(id, out owner!)
                          || owner.Settings is not { } settings
                          || settings.Schema.FirstOrDefault(candidate => candidate.Key == key) is not { } found)
            {
                return new SteamUiCommandResult(false, "The plugin setting is no longer available.");
            }

            setting = found;
        }

        if (!TryReadValue(setting.Kind, value, out var pluginValue))
        {
            return new SteamUiCommandResult(false, "The plugin setting value is invalid.");
        }

        try
        {
            var result = await owner.ConfigureAsync(
                expectedRevision,
                new Dictionary<string, PluginValue>(StringComparer.Ordinal) { [key] = pluginValue },
                DateTimeOffset.UtcNow.AddSeconds(10),
                cancellationToken).ConfigureAwait(false);
            OnChanged();
            return result.Outcome == PluginConfigurationOutcome.Applied
                ? SteamUiCommandResult.Applied
                : new SteamUiCommandResult(false, result.Detail ?? "The plugin did not confirm the setting.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or TimeoutException)
        {
            return new SteamUiCommandResult(false, ex.Message);
        }
    }

    internal event Action? Changed;

    internal SteamExtensionsTabState ReadExtensionsTab()
    {
        lock (_gate)
        {
            Refresh();
            var instances = _manager.Snapshot();
            return new SteamExtensionsTabState(_settingsOwners.Where(pair =>
                    instances.Any(instance => ReferenceEquals(instance.Registration, pair.Value)))
                .Select(pair =>
                {
                    var owner = pair.Value;
                    var commandActions = _commands.Where(command => ReferenceEquals(command.Value.Owner, owner)
                                                                    && command.Value.Contribution.Placement
                                                                    == PluginSteamUiPlacement.ExtensionsTab)
                        .Select(command => new SteamExtensionsTabAction(
                            command.Key, command.Value.Contribution.Label)).ToArray();
                    var settings = owner.Settings?.Schema.Select(setting => ProjectSetting(
                        setting,
                        owner.Settings.Desired?.Values[setting.Key] ?? setting.Default)).ToArray() ?? [];
                    var instance = instances.Single(candidate => ReferenceEquals(candidate.Registration, owner));
                    return new SteamExtensionsTabItem(
                        pair.Key,
                        instance.Manifest.Name,
                        instance.Manifest.Version,
                        owner.Health.Health.ToString(),
                        owner.Health.Detail,
                        commandActions,
                        settings,
                        owner.Settings?.Desired?.Revision ?? 0);
                }).ToArray(), _revision);
        }
    }

    internal SteamGameContextMenuState ReadGameContextMenu()
    {
        lock (_gate)
        {
            Refresh();
            return new SteamGameContextMenuState(_commands.Where(pair =>
                    pair.Value.Contribution.Placement == PluginSteamUiPlacement.GameContextMenu)
                .Select(pair => new SteamGameContextMenuItem(pair.Key,
                    MenuLabel(pair.Value.Name, pair.Value.Contribution.Label))).ToArray(), _revision);
        }
    }

    internal IReadOnlyList<ISteamUiModule> ReadModules()
    {
        lock (_gate)
        {
            return
            [
                .. _manager.Snapshot()
                    .Where(instance => instance.Registration is { } owner && CanInvoke(owner)
                                                                          && owner.Actions is not null)
                    .SelectMany(instance => instance.Registration!.Actions!.SteamUiModules)
            ];
        }
    }

    internal Task<SteamUiCommandResult> ActivateAsync(uint appId, string id, CancellationToken cancellationToken)
    {
        return ActivateAsync((uint?)appId, id, cancellationToken);
    }

    private async Task<SteamUiCommandResult> ActivateAsync(uint? appId, string id,
        CancellationToken cancellationToken)
    {
        Command command;
        lock (_gate)
        {
            Refresh();
            if (_disposed || !_commands.TryGetValue(id, out command!)
                          || (appId.HasValue
                              ? appId == 0 || command.Contribution.Placement != PluginSteamUiPlacement.GameContextMenu
                              : command.Contribution.Placement != PluginSteamUiPlacement.ExtensionsTab))
            {
                return new SteamUiCommandResult(false, "The plugin command is no longer available.");
            }
        }

        var arguments = new Dictionary<string, PluginValue>(StringComparer.Ordinal);
        if (appId.HasValue)
        {
            arguments.Add(command.Contribution.AppIdArgumentKey!, new PluginValue(Number: appId.Value));
        }

        try
        {
            // Keep the captured registration: an identity lookup could select a replacement whose
            // generation happens to equal that of the retired registration.
            var result = await command.Owner.InvokeActionAsync(command.Generation, command.Contribution.ActionId,
                    arguments, PluginActionOrigin.User, DateTimeOffset.UtcNow.AddSeconds(10), cancellationToken)
                .ConfigureAwait(false);
            return result.Outcome is PluginActionOutcome.Dispatched or PluginActionOutcome.AppliedVerified
                ? result.SteamRoute is { } route
                    ? new SteamUiCommandResult(true, null,
                        JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["route"] = route }))
                    : SteamUiCommandResult.Applied
                : new SteamUiCommandResult(false,
                    result.Detail ?? $"Plugin action was not confirmed: {result.Outcome}.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or TimeoutException)
        {
            return new SteamUiCommandResult(false, ex.Message);
        }
    }

    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var instances = _manager.Snapshot();
        foreach (var pair in _settingsOwners.ToArray())
        {
            if (!instances.Any(instance => ReferenceEquals(instance.Registration, pair.Value)
                                           && CanInvoke(pair.Value)))
            {
                _settingsOwners.Remove(pair.Key);
            }
        }

        foreach (var owner in _subscriptions.ToArray())
        {
            if (!instances.Any(instance => ReferenceEquals(instance.Registration, owner) && CanInvoke(owner)))
            {
                owner.Actions?.UnsubscribeSteamUiChanged(OnChanged);
                _subscriptions.Remove(owner);
            }
        }

        foreach (var pair in _commands.ToArray())
        {
            if (!instances.Any(instance => ReferenceEquals(instance.Registration, pair.Value.Owner)
                                           && CanInvoke(pair.Value.Owner)
                                           && pair.Value.Generation == pair.Value.Owner.Context.Generation))
            {
                _commands.Remove(pair.Key);
            }
        }

        foreach (var instance in instances)
        {
            if (instance.Registration is not { } owner || !CanInvoke(owner) || owner.Actions is null)
            {
                continue;
            }

            if (_subscriptions.Add(owner))
            {
                owner.Actions.SubscribeSteamUiChanged(OnChanged);
            }

            if (!_settingsOwners.ContainsValue(owner))
            {
                _settingsOwners.Add(Guid.NewGuid().ToString("N"), owner);
            }

            foreach (var contribution in owner.Actions.SteamUiContributions)
            {
                if (_commands.Values.Any(command => ReferenceEquals(command.Owner, owner)
                                                    && command.Generation == owner.Context.Generation
                                                    && command.Contribution.Id == contribution.Id))
                {
                    continue;
                }

                _commands.Add(Guid.NewGuid().ToString("N"), new Command(owner, owner.Context.Generation,
                    contribution, instance.Manifest.Name, instance.Manifest.Version));
            }
        }
    }

    private static bool CanInvoke(PluginRegistration owner)
    {
        return !owner.IsStopping && !owner.Quarantined && owner.Health.Health == PluginHealth.Ready;
    }

    private static SteamExtensionsTabSetting ProjectSetting(PluginSetting setting, PluginValue value)
    {
        var kind = setting.Kind switch
        {
            PluginSettingKind.Boolean => "boolean",
            PluginSettingKind.Number => "number",
            PluginSettingKind.Secret => "secret",
            PluginSettingKind.OrderedChoices => "order",
            _ => "text"
        };
        return new SteamExtensionsTabSetting(
            setting.Key,
            setting.Label,
            kind,
            value.Boolean,
            value.Number,
            setting.Kind == PluginSettingKind.Secret ? null : value.Text,
            setting.Minimum,
            setting.Maximum,
            setting.Choices);
    }

    private static bool TryReadValue(PluginSettingKind kind, JsonElement value, out PluginValue pluginValue)
    {
        pluginValue = default;
        switch (kind)
        {
            case PluginSettingKind.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                pluginValue = new PluginValue(value.GetBoolean());
                return true;
            case PluginSettingKind.Number when value.ValueKind == JsonValueKind.Number
                                               && value.TryGetDouble(out var number) && double.IsFinite(number):
                pluginValue = new PluginValue(Number: number);
                return true;
            case PluginSettingKind.Text or PluginSettingKind.Secret or PluginSettingKind.OrderedChoices
                when value.ValueKind == JsonValueKind.String
                     && value.GetString() is { Length: <= 4096 } text:
                pluginValue = new PluginValue(Text: text);
                return true;
            default:
                return false;
        }
    }

    private static string MenuLabel(string name, string label)
    {
        // Both admitted labels may be 128 characters, but Steam's menu allows 160 in total.
        // Preserve the action label and shorten only its package attribution.
        var nameBudget = 160 - " / ".Length - label.Length;
        if (name.Length > nameBudget)
        {
            var prefixLength = nameBudget - 1;
            if (char.IsHighSurrogate(name[prefixLength - 1]))
            {
                prefixLength--;
            }

            name = name[..prefixLength] + "…";
        }

        return name + " / " + label;
    }

    private void OnHealthChanged(PluginHealthPublication publication)
    {
        OnChanged();
    }

    private void OnChanged()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _revision++;
            Refresh();
        }

        Changed?.Invoke();
    }

    private sealed record Command(
        PluginRegistration Owner,
        long Generation,
        PluginSteamUiContribution Contribution,
        string Name,
        string Version);
}
