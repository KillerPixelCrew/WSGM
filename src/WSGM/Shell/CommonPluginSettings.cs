using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>Desired configuration delivery. Effective-state publications have no reference to this owner.</summary>
internal sealed class CommonPluginSettings
{
    private readonly IConfigurablePlugin _plugin;
    private readonly IPluginConfigurationStore _store;
    private readonly PluginInstanceIdentity _identity;
    private readonly IReadOnlyList<PluginSetting> _schema;

    internal CommonPluginSettings(IConfigurablePlugin plugin, IPluginConfigurationStore store, PluginInstanceIdentity identity)
    {
        var declared = plugin.Settings;
        if (!PluginConfigurationRules.IsValid(declared)) { throw new ArgumentException("Plugin settings declaration is invalid."); }
        _schema = Array.AsReadOnly(declared.Select(setting => setting with
        { Choices = setting.Choices is null ? null : Array.AsReadOnly(setting.Choices.ToArray()) }).ToArray());
        _plugin = plugin;
        _store = store;
        _identity = identity;
    }

    internal PluginConfiguration? Desired { get; private set; }
    internal PluginConfigurationResult? Result { get; private set; }
    internal IReadOnlyList<PluginSetting> Schema => _schema;

    internal Task<PluginConfigurationResult> RestoreAsync(PluginContext context, CancellationToken cancellationToken) =>
        DeliverAsync(_store.Read(_identity), PluginConfigurationOrigin.Restore, context, cancellationToken);

    internal async Task<PluginConfigurationResult> ChangeAsync(long expectedRevision, IReadOnlyDictionary<string, PluginValue> changes,
        PluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<string, PluginValue> captured = new(changes, StringComparer.Ordinal);
        var current = _store.Read(_identity);
        if (current.Revision != expectedRevision) { throw new InvalidOperationException("Plugin preferences changed; refresh before editing."); }
        foreach (var pair in captured)
        {
            var setting = _schema.FirstOrDefault(candidate => candidate.Key == pair.Key);
            if (setting is null || !PluginConfigurationRules.Accepts(setting, pair.Value))
            { throw new ArgumentException("Preference changes do not match the declared schema."); }
        }
        Dictionary<string, PluginValue> combined = new(current.Values, StringComparer.Ordinal);
        foreach (var pair in captured) { combined[pair.Key] = pair.Value; }
        if (!TryCompose(new(current.Revision, combined), out _))
        { throw new ArgumentException("Saved preferences do not match the current schema."); }
        cancellationToken.ThrowIfCancellationRequested();
        // Persist only the explicit change set. Defaults, readback and a plugin response never save.
        var saved = _store.Save(_identity, expectedRevision, captured);
        return await DeliverAsync(saved, PluginConfigurationOrigin.User, context, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PluginConfigurationResult> DeliverAsync(SavedPluginConfiguration saved, PluginConfigurationOrigin origin,
        PluginContext context, CancellationToken cancellationToken)
    {
        if (!TryCompose(saved, out var values))
        { return Result = new(saved.Revision, PluginConfigurationOutcome.Rejected, "Saved preferences no longer match the declaration."); }
        var desired = new PluginConfiguration(saved.Revision, origin, values);
        Desired = desired;
        Result = new(saved.Revision, PluginConfigurationOutcome.Unconfirmed, "Configuration delivery pending");
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _plugin.ConfigureAsync(desired, context, cancellationToken).ConfigureAwait(false);
        if (result is null || result.Revision != saved.Revision || !Enum.IsDefined(result.Outcome))
        { return Result = new(saved.Revision, PluginConfigurationOutcome.Unconfirmed, "The plugin returned an invalid configuration confirmation."); }
        return Result = result with { Detail = result.Detail is { Length: > 2048 } detail ? detail[..2048] : result.Detail };
    }

    private bool TryCompose(SavedPluginConfiguration saved, out IReadOnlyDictionary<string, PluginValue> values)
    {
        Dictionary<string, PluginValue> configured = new(StringComparer.Ordinal);
        foreach (var setting in _schema)
        {
            var value = saved.Values.TryGetValue(setting.Key, out var stored) ? stored : setting.Default;
            if (!PluginConfigurationRules.Accepts(setting, value)) { values = configured; return false; }
            configured.Add(setting.Key, value);
        }
        values = new ReadOnlyDictionary<string, PluginValue>(configured);
        return true;
    }
}
