using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>What answers WSGM's settings page in Steam.</summary>
internal interface IWsgmSteamSettingsBackend
{
    /// <summary>Changes one setting.</summary>
    /// <param name="key">The row's key, as published.</param>
    /// <param name="value">The new value, in the row's own shape.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>Whether it was saved, and why not when it was not.</returns>
    Task<SteamUiCommandResult> SetAsync(string key, JsonElement value, CancellationToken cancellationToken);
}

/// <summary>What WSGM's settings page in Steam draws.</summary>
/// <param name="Pages">The sidebar's pages.</param>
/// <param name="Revision">Increases whenever what the page shows may have changed.</param>
internal sealed record WsgmSteamSettingsState(IReadOnlyList<SteamSettingsPage> Pages, long Revision);

/// <summary>An installed common plugin package.</summary>
/// <param name="PluginId">Its package identity.</param>
/// <param name="Name">Its display name.</param>
internal sealed record InstalledCommonPlugin(string PluginId, string Name);

/// <summary>WSGM's own settings, reached from a WSGM row in Steam's main menu.</summary>
/// <remarks>
///     <para>
///         A limited set of WSGM's global settings, drawn with Steam's own Settings components by the
///         toolkit's renderer: which Steam features WSGM injects, how it starts, Steam Input, and the
///         installed plugins' settings. Every one of them configures WSGM itself, which is what
///         WSGM Settings is for; this is a second place to reach them from, not a second owner.
///     </para>
///     <para>
///         Each change is one field written through the config store's read-modify-write path, so it
///         never rewrites anything else. The shell's config reload then applies it live, exactly as
///         a save from WSGM Settings does. The start settings also rewrite boot.json in the same
///         transaction, and Steam Input management reconciles the shim after the save, outside the
///         lock, through the helper WSGM Settings uses.
///     </para>
/// </remarks>
internal sealed class WsgmSteamSettingsService : IWsgmSteamSettingsBackend, ISteamNavigationPanelBackend
{
    /// <summary>The main menu row's id.</summary>
    internal const string MenuItemId = "wsgm.settings";

    /// <summary>
    ///     WSGM's mark for the main menu, drawn from <c>docs/branding/wsgm-icon-24.svg</c> in Steam's
    ///     menu convention: one solid shape in the row's colour, the device outline with its sticks and
    ///     the play mark.
    /// </summary>
    internal const string Glyph =
        "M1 6h22v12H1ZM3 8v8h18V8ZM5 11h2v2H5ZM17 11h2v2h-2ZM10 9.5v5l4.5-2.5Z";

    private const string PluginEnabledPrefix = "plugins.enabled:";
    private const string PluginSettingPrefix = "plugins.setting:";
    private const string DeviceSettingPrefix = "device.setting:";
    private const string StartModeKey = "startup.mode";
    private const string ShimStateKey = "steamInput.shim";
    private const int MaximumTextLength = 4096;

    private static readonly SettingToggle[] Toggles =
    [
        new("cef.enabled", "Steam CEF integration",
            "Everything WSGM adds to Steam, including this page. Off leaves Steam's debug port closed.",
            config => config.Cef.Enabled, (config, value) => config.Cef.Enabled = value,
            new SteamSettingsConfirmation(false, "Turn off Steam integration?",
                "This page and every WSGM feature in Steam go away. Turn it back on from WSGM's overlay "
                + "or WSGM Settings.", "Turn off")),
        new("cef.libraryTabs", "Library & tabs", "Custom filter tabs, tab order, and hiding native Steam tabs.",
            config => config.Cef.LibraryTabs, (config, value) => config.Cef.LibraryTabs = value),
        new("cef.cardManager", "SD-card library manager",
            "Per-card library tabs, the library badge on game tiles, and live library labels.",
            config => config.Cef.CardManager, (config, value) => config.Cef.CardManager = value),
        new("cef.sdFormat", "Format SD Card + add library",
            "Format a card as a Steam library and register it into the running Steam.",
            config => config.Cef.SdFormat, (config, value) => config.Cef.SdFormat = value),
        new("steam.storageFormat", "Allow formatting from Steam's storage page",
            "Let Steam's own Format Drive dialog erase a card through WSGM's format flow.",
            config => config.SteamStorageFormatEnabled, (config, value) => config.SteamStorageFormatEnabled = value),
        new("cef.connectedLibraryCarousel", "Connected-library Home carousel",
            "Home shows every game on the libraries attached right now, last played first.",
            config => config.Cef.ConnectedLibraryCarousel,
            (config, value) => config.Cef.ConnectedLibraryCarousel = value),
        new("cef.carouselShowUninstalled", "Show uninstalled games in the carousel",
            "Also list owned games that are not installed, greyed, after the installed ones.",
            config => config.Cef.CarouselShowUninstalled,
            (config, value) => config.Cef.CarouselShowUninstalled = value),
        new("cef.wifiIndicator", "Wi-Fi indicator", "Make the header's Wi-Fi icon reflect the real connection.",
            config => config.Cef.WifiIndicator, (config, value) => config.Cef.WifiIndicator = value),
        // Off does not remove this page: WSGM's pages follow CEF itself, not native Quick Access.
        new("cef.nativeQuickAccess", "Native Quick Access bridge",
            "WSGM's controls in Steam's Quick Access menu. An incompatible Steam build stays untouched.",
            config => config.Cef.NativeQuickAccess, (config, value) => config.Cef.NativeQuickAccess = value,
            new SteamSettingsConfirmation(false, "Turn off the Quick Access bridge?",
                "WSGM's controls in Quick Access go away until you turn this back on. This page stays.",
                "Turn off", false)),
        new("cef.downloadKeepAwake", "Keep awake during downloads",
            "Hold a wake lock while Steam downloads, so standby cannot interrupt them.",
            config => config.Cef.DownloadKeepAwake, (config, value) => config.Cef.DownloadKeepAwake = value),
        new("cef.downloadQueueSort", "Download queue sorting",
            "Add Name, Size and Type sort buttons to the download queue.",
            config => config.Cef.DownloadQueueSort, (config, value) => config.Cef.DownloadQueueSort = value),
        new("startup.atSignIn", "Start WSGM at sign-in",
            "The WSGM service starts WSGM once Windows finishes signing you in. Takes effect at the next sign-in.",
            config => config.StartAtSignIn, (config, value) => config.StartAtSignIn = value, Boot: true),
        new("steamInput.lease", "Block Steam Input while WSGM's panels are open",
            "Keeps the controller usable in WSGM's panels by leasing it away from Steam Input.",
            config => config.SteamInputLeaseEnabled, (config, value) => config.SteamInputLeaseEnabled = value),
        new("steamInput.management", "Steam Input management",
            "Puts a small WSGM file next to Steam so WSGM can take the controller for its own panels. "
            + "Off leaves Steam's folder alone.",
            config => config.SteamInputManagementEnabled,
            (config, value) => config.SteamInputManagementEnabled = value, SteamInput: true)
    ];

    private readonly Action<AppConfig> _applySteamInput;
    private readonly Func<Action<AppConfig>, bool, AppConfig> _commit;
    private readonly Func<AppConfig> _config;

    private readonly Func<string, string, JsonElement, long, CancellationToken, Task<SteamUiCommandResult>>?
        _configurePlugin;

    private readonly Lock _gate = new();
    private readonly Func<string?> _installedDevicePlugin;
    private readonly Func<IReadOnlyList<InstalledCommonPlugin>> _installedPlugins;
    private readonly Func<IReadOnlyList<CommonPluginSettingsView>> _pluginSettings;
    private readonly Func<SteamInputShimStatus> _shimStatus;

    // One shim apply at a time, and always of the latest save: two quick toggles would otherwise run
    // in either order and could leave the shim matching the older one.
    private readonly SemaphoreSlim _steamInputGate = new(1, 1);
    private string? _devicePluginId;
    private bool _devicePluginRead;
    private AppConfig? _pendingSteamInput;
    private long _revision = 1;
    private AppConfig? _written;

    /// <summary>Creates the page's backend.</summary>
    /// <param name="config">The configuration the session is running on.</param>
    /// <param name="commit">
    ///     Applies one change through the config store and answers what was persisted; the flag asks
    ///     for boot.json to be rewritten in the same transaction.
    /// </param>
    /// <param name="applySteamInput">Reconciles the Steam Input shim with a saved configuration.</param>
    /// <param name="shimStatus">The shim's last known state.</param>
    /// <param name="installedPlugins">The installed common plugin packages.</param>
    /// <param name="pluginSettings">Every running plugin's declared settings.</param>
    /// <param name="configurePlugin">Changes one running plugin's setting, or null without plugins.</param>
    /// <param name="installedDevicePlugin">The installed device plugin's id, or null when none is installed.</param>
    internal WsgmSteamSettingsService(
        Func<AppConfig> config,
        Func<Action<AppConfig>, bool, AppConfig> commit,
        Action<AppConfig> applySteamInput,
        Func<SteamInputShimStatus> shimStatus,
        Func<IReadOnlyList<InstalledCommonPlugin>>? installedPlugins = null,
        Func<IReadOnlyList<CommonPluginSettingsView>>? pluginSettings = null,
        Func<string, string, JsonElement, long, CancellationToken, Task<SteamUiCommandResult>>? configurePlugin =
            null,
        Func<string?>? installedDevicePlugin = null)
    {
        _installedDevicePlugin = installedDevicePlugin ?? (() => null);
        _config = config;
        _commit = commit;
        _applySteamInput = applySteamInput;
        _shimStatus = shimStatus;
        _installedPlugins = installedPlugins ?? (() => []);
        _pluginSettings = pluginSettings ?? (() => []);
        _configurePlugin = configurePlugin;
    }

    /// <summary>The route the page is served at.</summary>
    internal static string Route => SteamWsgmSettingsSurface.Route;

    /// <inheritdoc />
    /// <remarks>
    ///     WSGM's menu row carries a route, so Valve's own entry navigates and this is never asked. An
    ///     activation that does arrive names nothing this answers.
    /// </remarks>
    public Task<SteamUiCommandResult> ActivateAsync(string id, CancellationToken cancellationToken)
    {
        return Task.FromResult(new SteamUiCommandResult(false, "This menu entry opens its page directly."));
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetAsync(string key, JsonElement value, CancellationToken cancellationToken)
    {
        if (Toggles.FirstOrDefault(toggle => toggle.Key == key) is { } found)
        {
            return value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? CommitAsync(config => found.Write(config, value.GetBoolean()), found.Boot, found.SteamInput,
                    cancellationToken)
                : Invalid();
        }

        if (key == StartModeKey)
        {
            return value.ValueKind == JsonValueKind.String
                   && Enum.TryParse<SessionStartMode>(value.GetString(), false, out var mode)
                   && Enum.IsDefined(mode)
                ? CommitAsync(config => config.StartMode = mode, true, false, cancellationToken)
                : Invalid();
        }

        if (key.StartsWith(PluginEnabledPrefix, StringComparison.Ordinal))
        {
            return SetPluginEnabledAsync(key[PluginEnabledPrefix.Length..], value, cancellationToken);
        }

        if (key.StartsWith(PluginSettingPrefix, StringComparison.Ordinal))
        {
            return SetPluginSettingAsync(key[PluginSettingPrefix.Length..], value, cancellationToken);
        }

        return key.StartsWith(DeviceSettingPrefix, StringComparison.Ordinal)
            ? SetDeviceSettingAsync(key[DeviceSettingPrefix.Length..], value, cancellationToken)
            : Task.FromResult(new SteamUiCommandResult(false, "This setting is no longer available."));
    }

    /// <summary>Raised when what the page shows may have changed.</summary>
    internal event Action? Changed;

    /// <summary>The main menu row: WSGM, before Power, opening this page.</summary>
    /// <param name="pageReady">Whether the page can be drawn; without it there is no row.</param>
    /// <returns>The navigation panel's state.</returns>
    internal static SteamNavigationPanelState ReadMenu(bool pageReady)
    {
        return new SteamNavigationPanelState(
            pageReady ? [new SteamNavigationItem(MenuItemId, "WSGM", Before: "power", Route: Route, Glyph: Glyph)] : [],
            []);
    }

    /// <summary>Tells the page the configuration or the plugins changed outside it.</summary>
    /// <remarks>
    ///     Called on the shell's config reload, which is also how this page's own writes come back:
    ///     from then on the reloaded configuration is the authority again.
    /// </remarks>
    internal void ConfigurationChanged()
    {
        lock (_gate)
        {
            _written = null;
            // A package change is not a config change, but it comes with one: the device cycle that
            // follows it rewrites the cached declaration. Read the slot again then.
            _devicePluginRead = false;
            _revision++;
        }

        Changed?.Invoke();
    }

    /// <summary>The page as it should be drawn now.</summary>
    /// <returns>The page model.</returns>
    internal WsgmSteamSettingsState ReadState()
    {
        AppConfig config;
        long revision;
        lock (_gate)
        {
            // What this page just wrote, until the shell's reload catches up with it: without this the
            // row would show the old value for the half second the file watcher debounces.
            config = _written ?? _config();
            revision = _revision;
        }

        return new WsgmSteamSettingsState(
        [
            new SteamSettingsPage("steam", "Steam integration",
            [
                new SteamSettingsSection(null, [Row("cef.enabled", config)]),
                new SteamSettingsSection("Library",
                [
                    Row("cef.libraryTabs", config), Row("cef.cardManager", config), Row("cef.sdFormat", config),
                    Row("steam.storageFormat", config), Row("cef.connectedLibraryCarousel", config),
                    Row("cef.carouselShowUninstalled", config)
                ]),
                new SteamSettingsSection("Client",
                [
                    Row("cef.wifiIndicator", config), Row("cef.nativeQuickAccess", config),
                    Row("cef.downloadKeepAwake", config), Row("cef.downloadQueueSort", config)
                ])
            ], PageGlyphs.Integration),
            new SteamSettingsPage("startup", "Startup",
            [
                new SteamSettingsSection(null,
                [
                    Row("startup.atSignIn", config),
                    new SteamSettingsRow(StartModeKey, SteamSettingsRowKind.Choice, "Start in",
                        "Game mode switches to Big Picture. Desktop mode keeps Windows Explorer and waits in "
                        + "the notification area. Takes effect at the next start.",
                        Text: config.StartMode.ToString(),
                        Choices:
                        [
                            new SteamSettingsChoice(nameof(SessionStartMode.Game), "Game mode"),
                            new SteamSettingsChoice(nameof(SessionStartMode.Desktop), "Desktop mode")
                        ])
                ])
            ], PageGlyphs.Startup),
            new SteamSettingsPage("steam-input", "Steam Input",
            [
                new SteamSettingsSection(null,
                [
                    Row("steamInput.lease", config),
                    Row("steamInput.management", config),
                    new SteamSettingsRow(ShimStateKey, SteamSettingsRowKind.Note, "Status",
                        Text: SteamInputManagement.Describe(_shimStatus()))
                ])
            ], PageGlyphs.SteamInput),
            new SteamSettingsPage("plugins", "Plugins", PluginSections(config), PageGlyphs.Plugins)
        ], revision);
    }

    private static SteamSettingsRow Row(string key, AppConfig config)
    {
        var toggle = Toggles.First(candidate => candidate.Key == key);
        return new SteamSettingsRow(key, SteamSettingsRowKind.Boolean, toggle.Label, toggle.Description,
            toggle.Read(config), Confirm: toggle.Confirm);
    }

    private IReadOnlyList<SteamSettingsSection> PluginSections(AppConfig config)
    {
        List<SteamSettingsSection> sections = [];
        var running = _pluginSettings();
        var instances = PluginInstances(config).ToArray();
        foreach (var (pluginId, instanceId, name, enabled) in instances)
        {
            List<SteamSettingsRow> rows =
            [
                new(PluginEnabledPrefix + pluginId + "/" + instanceId, SteamSettingsRowKind.Boolean, "Enabled",
                    "Whether WSGM runs this plugin.", enabled)
            ];
            if (running.FirstOrDefault(view => view.Identity.PluginId == pluginId
                                               && view.Identity.InstanceId == instanceId) is { } view)
            {
                rows.AddRange(view.Settings.Select(setting => ProjectPluginSetting(view.Id, setting)));
            }

            // Named by instance as well when one package has several, since they share a display name
            // and each switch and setting belongs to one of them.
            var title = instances.Count(other => other.PluginId == pluginId) > 1 || instanceId != "default"
                ? $"{name} ({instanceId})"
                : name;
            sections.Add(new SteamSettingsSection(title, rows));
        }

        sections.AddRange(DeviceSections(config));
        if (sections.Count == 0)
        {
            sections.Add(new SteamSettingsSection(null,
            [
                new SteamSettingsRow("plugins.none", SteamSettingsRowKind.Note, "No plugins",
                    Text: "No plugin that declares settings is installed.")
            ]));
        }

        return sections;
    }

    /// <summary>The installed plugins' instances, as WSGM Settings lists them.</summary>
    private IEnumerable<(string PluginId, string InstanceId, string Name, bool Enabled)> PluginInstances(
        AppConfig config)
    {
        foreach (var package in _installedPlugins())
        {
            var configured = config.PluginInstances.Where(entry => entry.PluginId == package.PluginId).ToArray();
            if (configured.Length == 0)
            {
                yield return (package.PluginId, "default", package.Name, false);
                continue;
            }

            foreach (var instance in configured)
            {
                yield return (instance.PluginId, instance.InstanceId, package.Name, instance.Enabled);
            }
        }
    }

    private static SteamSettingsRow ProjectPluginSetting(string ownerId, CommonPluginSettingValue entry)
    {
        var (setting, value) = (entry.Setting, entry.Value);
        var key = PluginSettingPrefix + ownerId + "/" + setting.Key;
        return setting.Kind switch
        {
            PluginSettingKind.Boolean => new SteamSettingsRow(key, SteamSettingsRowKind.Boolean, setting.Label,
                Checked: value.Boolean ?? false),
            // Text, bounds or not: the contract allows any finite number, and a slider only reaches
            // the values its step lands on. The bounds are said, and the plugin checks them.
            PluginSettingKind.Number => new SteamSettingsRow(key, SteamSettingsRowKind.Text, setting.Label,
                setting is { Minimum: { } minimum, Maximum: { } maximum }
                    ? string.Create(CultureInfo.InvariantCulture, $"From {minimum} to {maximum}.")
                    : null,
                Text: value.Number?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
            PluginSettingKind.Secret => new SteamSettingsRow(key, SteamSettingsRowKind.Secret, setting.Label,
                Text: value.Text is { Length: > 0 } ? "Set" : "Not set", MaximumLength: MaximumTextLength),
            PluginSettingKind.OrderedChoices => new SteamSettingsRow(key, SteamSettingsRowKind.Order, setting.Label,
                Order: SplitOrder(value.Text),
                Choices: [.. (setting.Choices ?? []).Select(choice => new SteamSettingsChoice(choice, choice))]),
            _ when setting.Choices is { Count: > 0 } choices => new SteamSettingsRow(key, SteamSettingsRowKind.Choice,
                setting.Label, Text: value.Text ?? string.Empty,
                Choices: [.. choices.Select(choice => new SteamSettingsChoice(choice, choice))]),
            _ => new SteamSettingsRow(key, SteamSettingsRowKind.Text, setting.Label, Text: value.Text ?? string.Empty,
                MaximumLength: MaximumTextLength)
        };
    }

    private static IReadOnlyList<string> SplitOrder(string? text)
    {
        return
        [
            .. (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ];
    }

    /// <summary>The installed device plugin's scope, when it has a cached declaration.</summary>
    /// <remarks>
    ///     A declaration is cached in configuration and outlives its package: removing the plugin, or a
    ///     replacement failing before it publishes, leaves the old one behind. So the scope is the one
    ///     belonging to the installed device plugin, as in WSGM Settings, and with none installed
    ///     there is nothing to draw or accept.
    /// </remarks>
    private PluginSettingsScope? ActiveDeviceScope(AppConfig config)
    {
        if (InstalledDevicePlugin() is not { } pluginId)
        {
            return null;
        }

        return config.DeviceIntegration.PluginSettings.LastOrDefault(scope =>
            scope.Declaration is not null && string.Equals(scope.PluginId, pluginId, StringComparison.Ordinal));
    }

    /// <summary>The installed device plugin's id, read from the Plugins folder once per configuration.</summary>
    private string? InstalledDevicePlugin()
    {
        lock (_gate)
        {
            if (_devicePluginRead)
            {
                return _devicePluginId;
            }
        }

        var pluginId = _installedDevicePlugin();
        lock (_gate)
        {
            _devicePluginId = pluginId;
            _devicePluginRead = true;
        }

        return pluginId;
    }

    private IEnumerable<SteamSettingsSection> DeviceSections(AppConfig config)
    {
        if (ActiveDeviceScope(config) is not { Declaration: { } declaration } scope)
        {
            yield break;
        }

        var view = PluginSettingsCoordinator.Project(declaration,
            PluginSettingsResolver.Resolve(declaration, scope.Values));
        foreach (var section in view.Sections)
        {
            if (!view.Settings.TryGetValue(section.SectionId, out var settings))
            {
                continue;
            }

            yield return new SteamSettingsSection(
                "Device: " + SectionTitle(section),
                [.. settings.Select(setting => ProjectDeviceSetting(setting.Descriptor, setting.Value))]);
        }
    }

    private static string SectionTitle(PluginSettingSection section)
    {
        if (section.SectionId == PluginSettingsCoordinator.FallbackSectionId)
        {
            return "Other";
        }

        return section.Key is SettingSectionKey.Custom
            ? section.CustomTitle ?? section.SectionId
            : section.Key.ToString();
    }

    private static SteamSettingsRow ProjectDeviceSetting(PluginSettingDescriptor descriptor, CapabilityValue value)
    {
        var key = DeviceSettingPrefix + descriptor.SettingId;
        var label = CapabilityDisplayLabels.For(descriptor.Display, descriptor.SettingId);
        return descriptor.ValueKind switch
        {
            CapabilityValueKind.Boolean => new SteamSettingsRow(key, SteamSettingsRowKind.Boolean, label,
                Checked: value.BooleanValue ?? false),
            CapabilityValueKind.Integer when descriptor is { Minimum: { } minimum, Maximum: { } maximum } =>
                new SteamSettingsRow(key, SteamSettingsRowKind.Range, label, Number: value.IntegerValue ?? minimum,
                    Minimum: minimum, Maximum: maximum, Step: descriptor.Step ?? 1),
            CapabilityValueKind.Integer => new SteamSettingsRow(key, SteamSettingsRowKind.Text, label,
                Text: value.IntegerValue?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
            CapabilityValueKind.Choice => new SteamSettingsRow(key, SteamSettingsRowKind.Choice, label,
                Text: value.ChoiceValue ?? string.Empty,
                Choices:
                [
                    .. descriptor.Choices.Select(choice =>
                        new SteamSettingsChoice(choice.Value,
                            CapabilityDisplayLabels.For(choice.Display, choice.Value)))
                ]),
            CapabilityValueKind.Text => new SteamSettingsRow(key, SteamSettingsRowKind.Text, label,
                Text: value.TextValue ?? string.Empty,
                MaximumLength: descriptor.MaximumLength ?? PluginSettingDescriptor.MaxTextLength),
            CapabilityValueKind.Color => new SteamSettingsRow(key, SteamSettingsRowKind.Text, label,
                "A colour as #RRGGBB.",
                Text: value.ColorValue is { } color ? "#" + color.ToString("X6", CultureInfo.InvariantCulture) : "",
                MaximumLength: 7),
            _ => new SteamSettingsRow(key, SteamSettingsRowKind.Note, label, Text: "Change this in WSGM Settings.")
        };
    }

    private Task<SteamUiCommandResult> SetPluginEnabledAsync(
        string identity, JsonElement value, CancellationToken cancellationToken)
    {
        var separator = identity.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0 || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return Invalid();
        }

        var (pluginId, instanceId) = (identity[..separator], identity[(separator + 1)..]);
        // Reconcile refuses the whole instance list over one malformed identity, so one stored here
        // would stop every plugin from starting until the file was fixed by hand.
        if (!PluginConfigurationRules.ValidKey(pluginId) || !PluginConfigurationRules.ValidKey(instanceId))
        {
            return Invalid();
        }

        if (_installedPlugins().All(package => package.PluginId != pluginId))
        {
            return Task.FromResult(new SteamUiCommandResult(false, "This plugin is no longer installed."));
        }

        var enabled = value.GetBoolean();
        return CommitAsync(config =>
        {
            var instance = config.PluginInstances.FirstOrDefault(entry =>
                entry.PluginId == pluginId && entry.InstanceId == instanceId);
            if (instance is null)
            {
                instance = new CommonPluginInstanceConfig { PluginId = pluginId, InstanceId = instanceId };
                config.PluginInstances.Add(instance);
            }

            instance.Enabled = enabled;
        }, false, false, cancellationToken);
    }

    private async Task<SteamUiCommandResult> SetPluginSettingAsync(
        string address, JsonElement value, CancellationToken cancellationToken)
    {
        var separator = address.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0 || _configurePlugin is null)
        {
            return new SteamUiCommandResult(false, "The plugin setting is no longer available.");
        }

        var (ownerId, settingKey) = (address[..separator], address[(separator + 1)..]);
        if (_pluginSettings().FirstOrDefault(view => view.Id == ownerId) is not { } view
            || view.Settings.FirstOrDefault(entry => entry.Setting.Key == settingKey) is not { } entry)
        {
            return new SteamUiCommandResult(false, "The plugin setting is no longer available.");
        }

        if (!TryPluginValue(entry.Setting, value, out var converted))
        {
            return new SteamUiCommandResult(false, "The plugin setting value is invalid.");
        }

        // The Quick Access tab's own path, against the revision in force now: the user is changing
        // what they can see, and the plugin answers whether it took it.
        var result = await _configurePlugin(ownerId, settingKey, converted, view.Revision, cancellationToken)
            .ConfigureAwait(false);
        Refresh();
        return result;
    }

    /// <summary>Puts the renderer's value into the shape the plugin setting path reads.</summary>
    /// <remarks>
    ///     An order arrives as a list and is stored comma-joined, as the Quick Access tab sends it; a
    ///     number without bounds arrives as text from a text field.
    /// </remarks>
    private static bool TryPluginValue(PluginSetting setting, JsonElement value, out JsonElement converted)
    {
        converted = default;
        switch (setting.Kind)
        {
            case PluginSettingKind.OrderedChoices when value.ValueKind == JsonValueKind.Array:
                if (value.GetArrayLength() > 64
                    || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                {
                    return false;
                }

                converted = JsonText(string.Join(",", value.EnumerateArray().Select(item => item.GetString())));
                return true;
            case PluginSettingKind.Number when value.ValueKind == JsonValueKind.String:
                if (!double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var number) || !double.IsFinite(number))
                {
                    return false;
                }

                converted = JsonNumber(number);
                return true;
            default:
                converted = value.Clone();
                return true;
        }
    }

    private Task<SteamUiCommandResult> SetDeviceSettingAsync(
        string settingId, JsonElement value, CancellationToken cancellationToken)
    {
        var config = CurrentConfig();
        if (ActiveDeviceScope(config) is not { Declaration: { } declaration } scope
            || declaration.Settings.FirstOrDefault(setting => setting.SettingId == settingId) is not { } descriptor)
        {
            return Task.FromResult(new SteamUiCommandResult(false, "The device setting is no longer available."));
        }

        if (!TryDeviceValue(descriptor, value, out var candidate)
            || !descriptor.TryValidateValue(candidate, out _))
        {
            return Task.FromResult(new SteamUiCommandResult(false, "The device setting value is invalid."));
        }

        var (device, plugin) = (scope.DeviceDefinitionId, scope.PluginId);
        return CommitAsync(persisted =>
        {
            // Found again in the fresh copy the store loaded: the scope object above belongs to a
            // snapshot that is never written.
            if (persisted.DeviceIntegration.PluginSettings.FirstOrDefault(candidateScope =>
                    candidateScope.DeviceDefinitionId == device && candidateScope.PluginId == plugin) is { } fresh)
            {
                PluginSettingsResolver.Store(fresh, settingId, candidate);
            }
        }, false, false, cancellationToken);
    }

    private static bool TryDeviceValue(PluginSettingDescriptor descriptor, JsonElement value,
        out CapabilityValue candidate)
    {
        candidate = CapabilityValue.None();
        switch (descriptor.ValueKind)
        {
            case CapabilityValueKind.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                candidate = new CapabilityValue
                    { Kind = CapabilityValueKind.Boolean, BooleanValue = value.GetBoolean() };
                return true;
            case CapabilityValueKind.Integer when value.ValueKind == JsonValueKind.Number
                                                  && value.TryGetInt32(out var integer):
                candidate = new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = integer };
                return true;
            case CapabilityValueKind.Integer when value.ValueKind == JsonValueKind.String
                                                  && int.TryParse(value.GetString(), NumberStyles.Integer,
                                                      CultureInfo.InvariantCulture, out var typed):
                candidate = new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = typed };
                return true;
            case CapabilityValueKind.Choice when value.ValueKind == JsonValueKind.String:
                candidate = new CapabilityValue { Kind = CapabilityValueKind.Choice, ChoiceValue = value.GetString() };
                return true;
            case CapabilityValueKind.Text when value.ValueKind == JsonValueKind.String:
                candidate = new CapabilityValue { Kind = CapabilityValueKind.Text, TextValue = value.GetString() };
                return true;
            case CapabilityValueKind.Color when value.ValueKind == JsonValueKind.String
                                                && value.GetString() is { Length: 7 } hex && hex[0] == '#'
                                                && int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber,
                                                    CultureInfo.InvariantCulture, out var color):
                candidate = new CapabilityValue { Kind = CapabilityValueKind.Color, ColorValue = color };
                return true;
            default:
                return false;
        }
    }

    /// <summary>Writes one change, then reports it to the page before the shell's reload does.</summary>
    private async Task<SteamUiCommandResult> CommitAsync(
        Action<AppConfig> change, bool boot, bool steamInput, CancellationToken cancellationToken)
    {
        AppConfig persisted;
        try
        {
            // Off the bridge's thread: the store takes a named lock and writes the file.
            persisted = await Task.Run(() => _commit(change, boot), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Log.Warn($"WSGM settings in Steam: the change was not saved: {ex.Message}");
            return new SteamUiCommandResult(false, "The setting could not be saved.");
        }

        lock (_gate)
        {
            _written = persisted;
            _revision++;
        }

        Changed?.Invoke();
        if (steamInput)
        {
            lock (_gate)
            {
                _pendingSteamInput = persisted;
            }

            // After the save and outside the lock, like WSGM Settings: it may ask for elevation.
            _ = Task.Run(ApplyLatestSteamInputAsync, CancellationToken.None);
        }

        return SteamUiCommandResult.Applied;
    }

    /// <summary>Applies the latest saved Steam Input setting, if no earlier run already has.</summary>
    /// <remarks>
    ///     Every save queues one of these, and each takes whatever is pending when it gets the gate, so
    ///     the last one to run applies the last save and the others find nothing left to do.
    /// </remarks>
    private async Task ApplyLatestSteamInputAsync()
    {
        await _steamInputGate.WaitAsync().ConfigureAwait(false);
        try
        {
            AppConfig? latest;
            lock (_gate)
            {
                latest = _pendingSteamInput;
                _pendingSteamInput = null;
            }

            if (latest is null)
            {
                return;
            }

            try
            {
                _applySteamInput(latest);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"WSGM settings in Steam: Steam Input management was not applied: {ex.Message}");
            }
        }
        finally
        {
            _steamInputGate.Release();
        }

        Refresh();
    }

    private AppConfig CurrentConfig()
    {
        lock (_gate)
        {
            return _written ?? _config();
        }
    }

    /// <summary>Tells the page something it shows changed without the configuration changing.</summary>
    /// <remarks>A plugin starting, stopping or taking a setting is one.</remarks>
    internal void Refresh()
    {
        lock (_gate)
        {
            _revision++;
        }

        Changed?.Invoke();
    }

    private static Task<SteamUiCommandResult> Invalid()
    {
        return Task.FromResult(new SteamUiCommandResult(false, "The setting value is invalid."));
    }

    private static JsonElement JsonText(string text)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStringValue(text);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static JsonElement JsonNumber(double number)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteNumberValue(number);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>One boolean WSGM setting the page can change.</summary>
    /// <param name="Key">The row's key.</param>
    /// <param name="Label">Its label, worded as in WSGM Settings.</param>
    /// <param name="Description">Its description.</param>
    /// <param name="Read">Reads the field.</param>
    /// <param name="Write">Writes the field.</param>
    /// <param name="Confirm">A confirmation to ask first, or null.</param>
    /// <param name="Boot">Whether boot.json follows this field.</param>
    /// <param name="SteamInput">Whether the Steam Input shim follows this field.</param>
    private sealed record SettingToggle(
        string Key,
        string Label,
        string Description,
        Func<AppConfig, bool> Read,
        Action<AppConfig, bool> Write,
        SteamSettingsConfirmation? Confirm = null,
        bool Boot = false,
        bool SteamInput = false);

    /// <summary>The sidebar's icons, in the toolkit's glyph convention: one solid shape on 24x24.</summary>
    private static class PageGlyphs
    {
        /// <summary>A plug, for what WSGM plugs into Steam.</summary>
        internal const string Integration =
            "M8 2h2v5H8ZM14 2h2v5h-2ZM5 7h14v4a7 7 0 0 1-6 6.9V22h-2v-4.1A7 7 0 0 1 5 11Z";

        /// <summary>The power symbol, for how WSGM starts.</summary>
        internal const string Startup =
            "M11 2h2v10h-2ZM6.3 5.3l1.4 1.4A7 7 0 1 0 16.3 6.7l1.4-1.4A9 9 0 1 1 6.3 5.3Z";

        /// <summary>A controller, with its d-pad and a button cut out.</summary>
        internal const string SteamInput =
            "M7 7h10a5 5 0 0 1 4.8 6.4l-1.3 4.4A2.5 2.5 0 0 1 16.3 19L14 16h-4l-2.3 3a2.5 2.5 0 0 1-4.2-1.2"
            + "l-1.3-4.4A5 5 0 0 1 7 7ZM7 10v1.5H5.5V13H7v1.5h1.5V13H10v-1.5H8.5V10ZM16 10.5a1 1 0 1 0 0 2a1 1 0 1 0 0-2Z";

        /// <summary>Four blocks, for the plugins that extend WSGM.</summary>
        internal const string Plugins = "M4 4h7v7H4ZM13 4h7v7h-7ZM4 13h7v7H4ZM13 13h7v7h-7Z";
    }
}
