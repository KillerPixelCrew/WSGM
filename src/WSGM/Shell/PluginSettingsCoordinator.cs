using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Ipc;
using WSGM.Device.Sdk.Settings;

namespace WSGM.Shell;

/// <summary>One plugin setting as a surface should draw it.</summary>
/// <param name="Descriptor">What the plugin declared.</param>
/// <param name="Value">The value in force.</param>
/// <param name="Origin">Whether it is the declared default, a stored value, or a rejected one.</param>
internal readonly record struct PluginSettingView(
    PluginSettingDescriptor Descriptor,
    CapabilityValue Value,
    PluginSettingOrigin Origin
);

/// <summary>The whole declared settings surface, ordered as it should be drawn.</summary>
/// <param name="Sections">Declared sections, by sort order then declaration order.</param>
/// <param name="Settings">Settings grouped under the section id they belong to.</param>
internal readonly record struct PluginSettingsView(
    IReadOnlyList<PluginSettingSection> Sections,
    IReadOnlyDictionary<string, IReadOnlyList<PluginSettingView>> Settings
);

/// <summary>
/// Holds the active plugin's settings declaration, reconciles it with what is stored, and keeps the
/// plugin supplied with the values in force.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="DeviceCapabilityRouter"/>. A capability writes hardware and
/// the device keeps the value; a setting configures the plugin and WSGM keeps it. Sharing one
/// projection would blur exactly the boundary that decides which surface a control belongs on.
/// </remarks>
internal sealed class PluginSettingsCoordinator : IDisposable
{
    /// <summary>Section id used for a setting that names one the manifest never declared.</summary>
    internal const string FallbackSectionId = "wsgm.other";

    private readonly object _gate = new();
    private DeviceHostClient? _client;
    private PluginSettingsManifest? _manifest;
    private string _deviceDefinitionId = string.Empty;
    private string _pluginId = string.Empty;
    private AppConfig? _config;
    private bool _disposed;

    /// <summary>Raised whenever the declaration or the values in force change.</summary>
    internal event Action<PluginSettingsView>? Changed;

    /// <summary>Whether the active plugin declared any settings at all.</summary>
    internal bool HasSettings
    {
        get
        {
            lock (_gate)
            {
                return _manifest is { Settings.Count: > 0 };
            }
        }
    }

    /// <summary>
    /// Begins tracking a plugin's settings for one cycle.
    /// </summary>
    /// <param name="client">The connected host client.</param>
    /// <param name="deviceDefinitionId">Device definition the values are keyed under.</param>
    /// <param name="pluginId">Plugin the values are keyed under.</param>
    /// <param name="config">Current configuration, for stored values.</param>
    internal void Attach(
        DeviceHostClient client,
        string deviceDefinitionId,
        string pluginId,
        AppConfig config
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(config);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DetachUnderGate();
            _client = client;
            _deviceDefinitionId = deviceDefinitionId ?? string.Empty;
            _pluginId = pluginId ?? string.Empty;
            _config = config;
            _manifest = null;
            client.SettingsManifestReceived += OnManifest;
        }
    }

    /// <summary>Stops tracking and forgets the declaration.</summary>
    internal void Detach()
    {
        lock (_gate)
        {
            DetachUnderGate();
        }

        Changed?.Invoke(Empty);
    }

    /// <summary>Replaces the configuration used for stored values after a reload.</summary>
    /// <param name="config">The replacement configuration.</param>
    internal void ApplyConfig(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_gate)
        {
            _config = config;
        }

        PublishAndPush();
    }

    /// <summary>
    /// Stores a new value for one declared setting and hands the plugin the updated set.
    /// </summary>
    /// <param name="settingId">The declared setting to change.</param>
    /// <param name="value">The new value.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns><see langword="true"/> when the value was accepted and stored.</returns>
    /// <remarks>
    /// Validated against the live declaration before it is stored, so a surface cannot persist a
    /// value the plugin would refuse; the refusal is logged with the reason the descriptor gave.
    /// </remarks>
    internal async Task<bool> SetAsync(
        string settingId,
        CapabilityValue value,
        CancellationToken cancellationToken
    )
    {
        PluginSettingDescriptor? descriptor;
        string device;
        string plugin;
        lock (_gate)
        {
            descriptor = _manifest?.Settings.FirstOrDefault(setting =>
                string.Equals(setting.SettingId, settingId, StringComparison.Ordinal));
            device = _deviceDefinitionId;
            plugin = _pluginId;
        }

        if (descriptor is null)
        {
            Log.Warn($"Plugin setting '{settingId}' refused: not declared by the active plugin.");
            return false;
        }

        if (!descriptor.TryValidateValue(value, out string? error))
        {
            Log.Warn($"Plugin setting '{settingId}' refused: {error}");
            return false;
        }

        AppConfig persisted = await Task.Run(
            () => ConfigStore.Mutate(config => Store(config, device, plugin, settingId, value)),
            cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _config = persisted;
        }

        Log.Info($"Plugin setting '{settingId}' stored for '{plugin}'.");
        await PublishAndPushAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DetachUnderGate();
        }
    }

    private static void Store(
        AppConfig config,
        string device,
        string plugin,
        string settingId,
        CapabilityValue value
    )
    {
        List<PluginSettingsScope> scopes = config.DeviceIntegration.PluginSettings;
        PluginSettingsScope? scope = scopes.FirstOrDefault(candidate =>
            string.Equals(candidate.DeviceDefinitionId, device, StringComparison.Ordinal)
            && string.Equals(candidate.PluginId, plugin, StringComparison.Ordinal));
        if (scope is null)
        {
            scope = new PluginSettingsScope { DeviceDefinitionId = device, PluginId = plugin };
            scopes.Add(scope);
        }

        PluginSettingValue? entry = scope.Values.FirstOrDefault(candidate =>
            string.Equals(candidate.SettingId, settingId, StringComparison.Ordinal));
        if (entry is null)
        {
            entry = new PluginSettingValue { SettingId = settingId };
            scope.Values.Add(entry);
        }

        // Only the field matching the kind is written, and the others are cleared, so a setting
        // whose kind changed cannot leave a stale value of the old shape behind it.
        entry.Boolean = value.Kind is CapabilityValueKind.Boolean ? value.BooleanValue : null;
        entry.Integer = value.Kind is CapabilityValueKind.Integer ? value.IntegerValue : null;
        entry.Choice = value.Kind is CapabilityValueKind.Choice ? value.ChoiceValue : null;
        entry.Color = value.Kind is CapabilityValueKind.Color ? value.ColorValue : null;
        entry.Text = value.Kind is CapabilityValueKind.Text ? value.TextValue : null;
    }

    private void OnManifest(PluginSettingsManifest manifest)
    {
        lock (_gate)
        {
            _manifest = manifest;
        }

        PublishAndPush();
    }

    private void PublishAndPush() => _ = PublishAndPushAsync(CancellationToken.None);

    private async Task PublishAndPushAsync(CancellationToken cancellationToken)
    {
        PluginSettingsManifest? manifest;
        DeviceHostClient? client;
        IReadOnlyList<PluginSettingValue> stored;
        lock (_gate)
        {
            manifest = _manifest;
            client = _client;
            stored = StoredUnderGate();
        }

        if (manifest is null)
        {
            Changed?.Invoke(Empty);
            return;
        }

        PluginSettingsResolution resolution = PluginSettingsResolver.Resolve(manifest, stored);
        foreach (EffectivePluginSetting rejected in resolution.Values.Where(
            value => value.Origin is PluginSettingOrigin.Rejected))
        {
            Log.Warn(
                $"Plugin setting '{rejected.SettingId}' fell back to its default: {rejected.Reason}");
        }

        if (resolution.Orphans.Count > 0)
        {
            Log.Info(
                "Plugin settings no longer declared: "
                + string.Join(", ", resolution.Orphans));
        }

        Changed?.Invoke(Project(manifest, resolution));

        if (client is null)
        {
            return;
        }

        IReadOnlyList<DeviceSettingValue> values =
            [.. resolution.Values.Select(value => new DeviceSettingValue(value.SettingId, value.Value))];
        try
        {
            await client.ApplySettingsValuesAsync(values, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The plugin keeps whatever it had. Reporting this matters because the surface will now
            // show values the plugin is not acting on, which is otherwise invisible.
            Log.Warn($"Plugin settings not delivered: {ex.Message}");
        }
    }

    /// <summary>
    /// Arranges a declaration and its resolved values into what a surface draws.
    /// </summary>
    /// <param name="manifest">The plugin's declaration.</param>
    /// <param name="resolution">The values in force.</param>
    /// <returns>Sections in draw order, with their settings grouped underneath.</returns>
    /// <remarks>Internal so the placement and ordering rules can be pinned without a device.</remarks>
    internal static PluginSettingsView Project(
        PluginSettingsManifest manifest,
        PluginSettingsResolution resolution
    )
    {
        Dictionary<string, CapabilityValue> byId = resolution.Values.ToDictionary(
            value => value.SettingId,
            value => value.Value,
            StringComparer.Ordinal);
        Dictionary<string, PluginSettingOrigin> originById = resolution.Values.ToDictionary(
            value => value.SettingId,
            value => value.Origin,
            StringComparer.Ordinal);
        HashSet<string> declaredSections = new(
            manifest.Sections.Select(section => section.SectionId),
            StringComparer.Ordinal);

        Dictionary<string, List<PluginSettingView>> grouped = new(StringComparer.Ordinal);
        // Declaration order is the tiebreak, so an ordering the plugin left unset still renders the
        // same way every time rather than following dictionary iteration.
        foreach (PluginSettingDescriptor setting in manifest.Settings
            .Select((setting, index) => (setting, index))
            .OrderBy(pair => pair.setting.SortOrder)
            .ThenBy(pair => pair.index)
            .Select(pair => pair.setting))
        {
            string section = setting.SectionId is { Length: > 0 } named
                && declaredSections.Contains(named)
                ? named
                : FallbackSectionId;
            if (section == FallbackSectionId && setting.SectionId is { Length: > 0 } missing)
            {
                Log.Info(
                    $"Plugin setting '{setting.SettingId}' names undeclared section '{missing}'; "
                    + "drawn under the fallback section.");
            }

            if (!grouped.TryGetValue(section, out List<PluginSettingView>? list))
            {
                list = [];
                grouped[section] = list;
            }

            list.Add(new PluginSettingView(
                setting,
                byId.GetValueOrDefault(setting.SettingId, setting.Default),
                originById.GetValueOrDefault(setting.SettingId, PluginSettingOrigin.Default)));
        }

        IReadOnlyList<PluginSettingSection> sections =
        [
            .. manifest.Sections
                .Select((section, index) => (section, index))
                .OrderBy(pair => pair.section.SortOrder)
                .ThenBy(pair => pair.index)
                .Select(pair => pair.section)
                // An empty section is not drawn; a heading with nothing under it reads as a
                // feature that failed rather than one the device does not have.
                .Where(section => grouped.ContainsKey(section.SectionId)),
            .. grouped.ContainsKey(FallbackSectionId)
                ? new[]
                {
                    new PluginSettingSection
                    {
                        SectionId = FallbackSectionId,
                        Key = SettingSectionKey.General,
                        SortOrder = int.MaxValue,
                    },
                }
                : [],
        ];

        return new PluginSettingsView(
            sections,
            grouped.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<PluginSettingView>)pair.Value,
                StringComparer.Ordinal));
    }

    private IReadOnlyList<PluginSettingValue> StoredUnderGate()
    {
        if (_config is null)
        {
            return [];
        }

        return _config.DeviceIntegration.PluginSettings
            .FirstOrDefault(scope =>
                string.Equals(scope.DeviceDefinitionId, _deviceDefinitionId, StringComparison.Ordinal)
                && string.Equals(scope.PluginId, _pluginId, StringComparison.Ordinal))
            ?.Values ?? [];
    }

    private void DetachUnderGate()
    {
        if (_client is not null)
        {
            _client.SettingsManifestReceived -= OnManifest;
            _client = null;
        }

        _manifest = null;
    }

    private static PluginSettingsView Empty => new(
        [],
        new Dictionary<string, IReadOnlyList<PluginSettingView>>(StringComparer.Ordinal));
}
