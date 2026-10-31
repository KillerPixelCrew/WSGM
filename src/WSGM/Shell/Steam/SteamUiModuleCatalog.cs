using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Every Steam UI surface the session declares, built from the session's backends: the adapters
///     that project WSGM's services onto Steam's rows, the host's own modules, the page list, and the
///     composition of the ready plugins' modules beside them.
/// </summary>
/// <remarks>
///     The session host owns the bridge, the patch registry, the runtime and every switch; this owns
///     what is registered with them. Each module's publication gate reads the switches the host last
///     applied. The adapters built here are the host's to subscribe to and dispose, in its own order.
/// </remarks>
internal sealed class SteamUiModuleCatalog
{
    private const string ShellPatchId = "wsgm.native-qam.shell";

    private readonly SteamUiBackends _backends;

    /// <summary>Whether the host has closed admission; nothing is replaced or indicated after it.</summary>
    private readonly Func<bool> _closed;

    private readonly DeviceCoordinatorLightingZonesService _lightingZones;

    /// <summary>The Quick Access plugin tab, which carries WSGM's own tools as well.</summary>
    private readonly SteamExtensionsTabBackend _extensionsTab;

    private readonly SteamGameContextMenuBackend _gameContextMenu;

    /// <summary>Hears what Big Picture Home's carousel holds.</summary>
    private readonly HomeCarouselBackend _homeCarousel = new();

    /// <summary>Hears the library badge's Home layout report.</summary>
    private readonly LibraryBadgeBackend _libraryBadge;

    /// <summary>The overlay-activation patch the host registers beside the module set.</summary>
    private readonly string _overlayActivationId;

    private readonly PerformanceServiceNativeQamAdapter _performance;

    // Serializes plugin module replacement; _composedPluginModules is the list the runtime holds.
    private readonly SemaphoreSlim _pluginModulesChange = new(1, 1);

    private readonly ScreensaverInputActivity _screensaverInput = new();

    // Every switch write takes this, the host's switch gate; the plugin patch ids are replaced under it.
    private readonly Lock _switchGate;

    /// <summary>The switches the host last applied.</summary>
    private readonly Func<SteamUiSurfaceSwitches> _switches;

    private readonly Func<CancellationToken, Task<bool>> _toggleQuickAccess;

    /// <summary>Whether WSGM's settings page can be drawn, as the host last reconciled it.</summary>
    private readonly Func<bool> _wsgmSettingsReady;

    private IReadOnlyList<ISteamUiModule> _composedPluginModules = [];

    // The modules this catalog declares itself; the ready plugins' modules are composed beside them.
    private IReadOnlyList<ISteamUiModule> _hostModules = [];

    // The registered plugin patches' ids. Replaced whole under _switchGate.
    private volatile HashSet<string> _pluginPatchIds = [];

    /// <summary>Builds the surface adapters over the session's backends.</summary>
    /// <param name="backends">The session-owned services behind the surfaces; read, never disposed here.</param>
    /// <param name="switchGate">The host's switch gate, taken whenever the plugin patch ids change.</param>
    /// <param name="switches">Reads the switches the host last applied.</param>
    /// <param name="closed">Whether the host has closed admission.</param>
    /// <param name="wsgmSettingsReady">Whether WSGM's settings page can be drawn.</param>
    /// <param name="toggleQuickAccess">Opens or closes WSGM's overlay.</param>
    /// <param name="queueStatePublication">Queues one publication round; the network surface raises it.</param>
    /// <param name="overlayActivationId">The overlay-activation patch no plugin module may claim.</param>
    internal SteamUiModuleCatalog(
        SteamUiBackends backends,
        Lock switchGate,
        Func<SteamUiSurfaceSwitches> switches,
        Func<bool> closed,
        Func<bool> wsgmSettingsReady,
        Func<CancellationToken, Task<bool>> toggleQuickAccess,
        Action queueStatePublication,
        string overlayActivationId)
    {
        _backends = backends;
        _switchGate = switchGate;
        _switches = switches;
        _closed = closed;
        _wsgmSettingsReady = wsgmSettingsReady;
        _toggleQuickAccess = toggleQuickAccess;
        _overlayActivationId = overlayActivationId;
        var deviceCoordinator = backends.DeviceCoordinator;
        // The Steam pages keep their own browsing, as each overlay view does; they open on Browse.
        ThemesPage = backends.Themes?.CreateBrowserSession("browse");
        AnimationsPage = backends.Animations?.CreateBrowserSession("browse");
        _libraryBadge = new LibraryBadgeBackend(backends.LibraryImport is { } importer
            ? importer.RecheckShortcutAsync
            : null);
        // The menu exists for WSGM's own Change Artwork entry, so it is not conditional on a plugin
        // source the way it was while artwork was a package.
        var artwork = backends.Artwork;
        var launchActions = backends.LaunchActions;
        var gameTools = backends.GameTools;
        _gameContextMenu = new SteamGameContextMenuBackend(
            backends.PluginSteamUi,
            artwork is null ? null : artwork.OpenAsync,
            artwork is null ? null : SteamArtworkBrowserSurface.RouteFor,
            launchActions is null ? null : launchActions.OpenAsync,
            gameTools is null ? null : gameTools.OpenAsync,
            gameTools is null ? null : SteamGameToolsSurface.RouteFor);
        var libraryImport = backends.LibraryImport;
        var libraryManager = backends.LibraryManager;
        _extensionsTab = new SteamExtensionsTabBackend(
            backends.PluginSteamUi,
            libraryImport is null ? null : () => SteamLibraryImportSurface.Route,
            libraryImport is null
                ? null
                : () => string.Join(", ", libraryImport.ReadState().Reading),
            [
                .. new IExtensionsTabSection?[]
                {
                    libraryManager?.CardsSection, libraryManager?.TabsSection,
                    backends.Themes is { } themes && ThemesPage is { } themesPage
                        ? new ThemeQuickAccessSection(themes, themesPage)
                        : null,
                    backends.Animations is { } animations && AnimationsPage is { } animationsPage
                        ? new AnimationQuickAccessSection(animations, animationsPage)
                        : null,
                    backends.Notes is null ? null : new NotesExtensionsSection(),
                    new BrowserExtensionsSection()
                }.OfType<IExtensionsTabSection>()
            ],
            backends.Emulators is not null);
        // One fold store for every Quick Access tab: the Extensions tab's sections and the
        // Performance and Quick Settings groups.
        PanelFolds = new SteamPanelFoldsBackend(backends.Folds);
        _lightingZones = new DeviceCoordinatorLightingZonesService(deviceCoordinator);
        _performance = new PerformanceServiceNativeQamAdapter(backends.Performance, backends.Profiles)
        {
            PerfSupport = backends.PerfSupport,
            ApplyRefreshRate = backends.ApplyRefreshRate,
            ApplyVariableRefreshRate = backends.ApplyVariableRefreshRate
        };
        Audio = backends.Audio is { } audio ? new AudioManagerNativeQamAudioService(audio) : null;
        AudioFormat = backends.Audio is { } formatAudio && backends.AudioProfiles is { } audioProfiles
            ? new NativeQamAudioFormatService(formatAudio, audioProfiles)
            : null;
        // A session that must not move the display has no resolution owner, so no resolution row is offered.
        NativeSettings = new SteamNativeSettingsService(backends.Brightness, backends.Resolution, _performance,
            backends.PowerProfiles, backends.CpuBoost, backends.HybridCores, AudioFormat,
            backends.Graphics, deviceCoordinator, backends.Profiles, backends.DisplayTimeouts,
            scaledResolutions: backends.ScaledResolutions);
        Network = backends.Radios is { } radios
            ? new NativeQamNetworkService(
                radios,
                () => !_closed() && _switches().NetworkIndicator,
                queueStatePublication)
            : null;
        Bluetooth = backends.Radios is { } bluetoothRadios
            ? new NativeQamBluetoothService(bluetoothRadios, backends.ShowBluetoothPanel)
            : null;
    }

    /// <summary>The Themes page's own store browsing, tab and detail, or null without themes.</summary>
    internal ThemeBrowseSession? ThemesPage { get; }

    /// <summary>The Animations page's own tab, listing and detail, or null without boot movies.</summary>
    internal AnimationBrowseSession? AnimationsPage { get; }

    /// <summary>Which Quick Access sections the user opened.</summary>
    internal SteamPanelFoldsBackend PanelFolds { get; }

    /// <summary>
    ///     Null when no audio manager exists for this session.
    /// </summary>
    /// <remarks>
    ///     Unlike the semantic services there is no "unavailable" stand-in, because audio is
    ///     supplied as a namespace rather than drawn as a row: with nothing to supply, the right
    ///     behaviour is to leave the namespace absent so Steam's own store stays unavailable, not to
    ///     install one that answers with nothing.
    /// </remarks>
    internal AudioManagerNativeQamAudioService? Audio { get; }

    /// <summary>The playback-format owner the audio rows read, or null without an audio manager and advanced audio.</summary>
    internal NativeQamAudioFormatService? AudioFormat { get; }

    /// <summary>The one row producer for Steam's native Settings pages and the Quick Access rows.</summary>
    internal SteamNativeSettingsService NativeSettings { get; }

    /// <summary>The Wi-Fi surface, or null when this session has no radio manager.</summary>
    internal NativeQamNetworkService? Network { get; }

    /// <summary>The Bluetooth surface, riding the same radio-manager condition.</summary>
    internal NativeQamBluetoothService? Bluetooth { get; }

    /// <summary>The glyph presentation the glyph stylesheet and the controller capabilities read.</summary>
    internal SteamInputGlyphDeliveryState GlyphDelivery { get; } = new();

    /// <summary>The registered plugin patches' ids, replaced whole under the host's switch gate.</summary>
    internal IReadOnlySet<string> PluginPatchIds => _pluginPatchIds;

    /// <summary>Whether the plugin source now offers a module list other than the one composed.</summary>
    internal bool PluginModulesOutdated =>
        _backends.PluginSteamUi is { } plugins && !ReferenceEquals(plugins.ReadModules(), _composedPluginModules);

    /// <summary>Declares the host's own modules and composes the plugins ready now beside them.</summary>
    /// <returns>The module set the host's bridge and runtime start from.</returns>
    /// <remarks>Called once, by the host's constructor; later plugin changes go through <see cref="ReplacePluginModulesAsync" />.</remarks>
    internal SteamUiModuleSet ComposeInitial()
    {
        _hostModules = CreateModules();
        // Plugins that became ready later are added by ReplacePluginModulesAsync.
        var pluginModules = _backends.PluginSteamUi?.ReadModules() ?? [];
        var composed = ComposeModules(pluginModules);
        _pluginPatchIds = composed.PluginPatchIds;
        _composedPluginModules = pluginModules;
        return composed.Modules;
    }

    /// <summary>Hands the runtime the module set for the plugins ready now, then applies their switches.</summary>
    /// <param name="runtime">The host's module runtime, which swaps the set.</param>
    /// <param name="applySwitches">Re-derives every patch switch; called under the switch gate.</param>
    /// <param name="queueSynchronization">Queues the patch pass that applies the swap.</param>
    /// <param name="shutdown">The host's shutdown source; its token ends the wait and the swap.</param>
    /// <returns>Completion once the swap is applied, skipped, or abandoned to shutdown.</returns>
    /// <remarks>
    ///     The runtime registers added patches, swaps the bridge vocabulary and the set, and retracts
    ///     removed patches. Their switches follow the host surfaces like every plugin patch, and the
    ///     queued pass applies them and installs the bridge again with the new vocabulary.
    /// </remarks>
    internal async Task ReplacePluginModulesAsync(
        SteamUiModuleRuntime runtime, Action applySwitches, Action queueSynchronization,
        CancellationTokenSource shutdown)
    {
        if (_closed() || _backends.PluginSteamUi is not { } plugins)
        {
            return;
        }

        try
        {
            await _pluginModulesChange.WaitAsync(shutdown.Token).ConfigureAwait(false);
            try
            {
                var pluginModules = plugins.ReadModules();
                if (_closed() || ReferenceEquals(pluginModules, _composedPluginModules))
                {
                    return;
                }

                var composed = ComposeModules(pluginModules);
                lock (_switchGate)
                {
                    // Both lists while the runtime swaps, so neither an added nor a leaving plugin
                    // patch is switched as a Quick Access row in between.
                    _pluginPatchIds = [.. _pluginPatchIds, .. composed.PluginPatchIds];
                }

                await runtime.ReplaceModulesAsync(composed.Modules, shutdown.Token).ConfigureAwait(false);
                _composedPluginModules = pluginModules;
                lock (_switchGate)
                {
                    _pluginPatchIds = composed.PluginPatchIds;
                    if (!_closed())
                    {
                        applySwitches();
                    }
                }

                queueSynchronization();
            }
            finally
            {
                _pluginModulesChange.Release();
            }
        }
        catch (ObjectDisposedException) when (_closed())
        {
            // Shutdown took the runtime or the patch registry first; it retracts every patch itself.
        }
    }

    /// <summary>
    ///     Every Steam UI surface this session offers, one declaration each: which toolkit surface it
    ///     is, the state WSGM feeds it, and the backend that answers it.
    /// </summary>
    /// <remarks>
    ///     The toolkit owns each surface's patches, wire shapes and payload readers, so a module here is
    ///     exactly "this is our data, and it maps to that feature". A surface whose backend is absent
    ///     in this session is simply not declared. WSGM's own features — download sorting and glyph
    ///     delivery — are patches of WSGM's own and are declared beside them. Plugin modules are not
    ///     declared here: <see cref="ComposeModules" /> adds the ready plugins' modules.
    /// </remarks>
    private List<ISteamUiModule> CreateModules()
    {
        List<ISteamUiModule> modules =
        [
            new SteamUiModule(
                "shell",
                commands: [new SteamUiCommandHandler(ShellPatchId, "toggleQuickAccess", HandleToggleQuickAccessAsync)]),

            // Every host row in Quick Access that is a plain Steam field, from the same producer as
            // Steam's Settings pages, and the component host that mounts every Quick Access row. Declared
            // unconditionally: which rows exist is decided by what each owner publishes.
            SteamQuickAccessRowsSurface.Module(Enabled, NativeSettings.ReadQuickAccessAsync, NativeSettings),

            // The frame limit is the toolkit's unified row rather than Valve's notch slider, and
            // the Q12 retirement does not apply: a free 30-120 range made Valve's unusable.
            SteamFrameLimitRow.Module(Enabled, () => new ValueTask<SteamFrameLimitState?>(_performance.FrameLimit),
                _performance),

            // The backend behind Valve's own Performance tab and the Valve rows that read it.
            // Declared unconditionally because the performance service always exists; what the
            // panel then shows is decided entirely by which fields the projected state carries.
            SteamPerformanceSurface.Module(
                Enabled,
                () => new ValueTask<SteamPerformanceState?>(_performance.PerfState),
                _performance,
                "perf"),

            // Declared unconditionally: the panel backlight depends on nothing WSGM has to supply.
            SteamBrightnessSurface.Module(Enabled, _backends.Brightness.ReadAsync, _backends.Brightness),

            // The inline lighting-colour editor; the charge limit and lighting brightness are plain rows.
            SteamLightingZonesRow.Module(
                Enabled,
                () => new ValueTask<SteamLightingZonesState?>(_lightingZones.Current),
                _lightingZones),

            SteamDownloadSort.Module(),

            new SteamUiModule(
                "glyph-style",
                [new SteamInputGlyphStylePatch(GlyphDelivery)]),

            // The library badge on every library tile, fed from the card model. Declared
            // unconditionally: which cards exist is the reading's business, and a session with
            // none publishes an empty list, which names every installed game as internal.
            SteamLibraryBadgeSurface.Module(
                () => SteamUiPatchSwitches.LibraryAvailabilityWanted(_switches()),
                () => new ValueTask<SteamLibraryBadgeState?>(LibraryBadges.Current is { } state
                    ? state with { ShowBadges = _switches().LibraryBadge }
                    : null),
                _libraryBadge),

            // Home's carousel, built from the same card reading as the badge so the two never
            // disagree about which card is in the reader. Rides LibraryBadges.Changed for card moves.
            SteamHomeCarouselSurface.Module(
                () => _switches().HomeCarousel,
                () => new ValueTask<SteamHomeCarouselState?>(HomeCarousel.Build(LibraryBadges.Current,
                    _switches().CarouselShowUninstalled)),
                _homeCarousel)
        ];

        if (_backends.ChordMirror is { } chordMirror)
        {
            // The hook exists whenever the mirror does; its state says whether a reset needs
            // reporting, so a session with the mirror off installs the hook and never hears from it.
            modules.Add(SteamChordResetSurface.Module(HostSteamUiEnabled, chordMirror));
        }

        // The controller's capabilities as Steam's pages read them. Declared unconditionally: the
        // state carries an empty mask until a profile marks a whole pair of controls absent.
        modules.Add(SteamControllerCapsSurface.Module(HostSteamUiEnabled, () => GlyphDelivery.Current));

        // Steam's game menu. Declared unconditionally: WSGM's own Change Artwork entry is in it
        // whether or not a plugin contributes anything.
        modules.Add(SteamGameContextMenuSurface.Module(
            HostSteamUiEnabled,
            () => new ValueTask<SteamGameContextMenuState?>(_gameContextMenu.ReadState()),
            _gameContextMenu));

        if (_backends.PowerMenu is { } powerMenu)
        {
            // Steam's own Switch to Desktop, offered while WSGM holds Game Mode.
            modules.Add(SteamPowerMenuSurface.Module(
                HostSteamUiEnabled,
                () => new ValueTask<SteamPowerMenuState?>(powerMenu.ReadState()),
                powerMenu));
        }

        // Every custom route in one module. The page host owns one patch and one publication, so a
        // second page owner cannot register the same patch id and take the whole session down with
        // it; each owner contributes routes and the host merges them.
        modules.Add(SteamPageSurface.Module(
            HostSteamUiEnabled,
            () => new ValueTask<SteamPageState?>(ReadPages())));

        if (_backends.Artwork is { } artwork)
        {
            modules.Add(SteamArtworkBrowserSurface.Module(
                HostSteamUiEnabled,
                () => new ValueTask<SteamArtworkBrowserState?>(artwork.ReadState()),
                artwork));
        }

        if (_backends.LibraryImport is { } libraryImport)
        {
            modules.Add(SteamLibraryImportSurface.Module(
                HostSteamUiEnabled,
                () => new ValueTask<GameLibraryState?>(libraryImport.ReadState()),
                () => libraryImport.Revision,
                libraryImport));
        }

        // The toolkit's file and folder picker, which the Game Library opens to add a shortcuts
        // folder and the artwork page to browse for a local image. It lists names only, and only
        // while WSGM's own Steam pages are enabled.
        if (_backends.Emulators is { } emulators)
        {
            modules.Add(SteamEmulatorSurface.Module(HostSteamUiEnabled, emulators));
        }

        if (_backends.LaunchActions is { } launchActions)
        {
            modules.Add(SteamLaunchActionsSurface.Module(HostSteamUiEnabled, launchActions));
        }

        if (_backends.GameTools is { } gameTools)
        {
            modules.Add(SteamGameToolsSurface.Module(HostSteamUiEnabled, gameTools));
        }

        if (_backends.LibraryManager is { } libraryManager)
        {
            modules.Add(SteamCardManagerSurface.Module(
                () => HostSteamUiEnabled() && libraryManager.CardManagerAvailable, libraryManager));
            modules.Add(SteamLibraryTabsSurface.Module(
                () => HostSteamUiEnabled() && libraryManager.TabManagerAvailable, libraryManager));
        }

        if (_backends.LibraryImport is not null || _backends.Artwork is not null || _backends.Emulators is not null
            || _backends.LaunchActions is not null || _backends.GameTools is not null
            || _backends.Screensavers is not null)
        {
            modules.Add(SteamFilePickerSurface.Module(HostSteamUiEnabled));
        }

        // WSGM's settings page, and the WSGM row in Steam's main menu that opens it. The row carries
        // the page's route, so Valve's own entry navigates and the backend is never asked.
        if (_backends.WsgmSettings is { } wsgmSettings)
        {
            modules.Add(SteamWsgmSettingsSurface.Module(
                HostSteamUiEnabled,
                () => new ValueTask<WsgmSteamSettingsState?>(wsgmSettings.ReadState()),
                wsgmSettings));
            modules.Add(SteamNavigationPanelSurface.Module(
                HostSteamUiEnabled,
                () => new ValueTask<SteamNavigationPanelState?>(WsgmSteamSettingsService.ReadMenu(_wsgmSettingsReady())),
                wsgmSettings));
        }

        modules.Add(SteamNativeSettingsSurface.Module(HostSteamUiEnabled, NativeSettings.ReadAsync, NativeSettings));

        // The themes: the page they are browsed and managed on, and the cascade the toolkit installs
        // into every window. Both follow CEF itself; the cascade also follows the themes' own switch,
        // and publishes nothing while it is off, which leaves Steam's styling as it was.
        if (_backends.Themes is { } themes && ThemesPage is { } themesPage)
        {
            modules.Add(SteamThemesSurface.Module(
                HostSteamUiEnabled,
                () => new ValueTask<SteamThemesState?>(themesPage.ReadState()),
                () => themesPage.Revision,
                themesPage,
                themes));
            modules.Add(SteamThemeStyleSurface.Module(
                () => HostSteamUiEnabled() && themes.Enabled,
                () => new ValueTask<SteamThemeState?>(themes.ReadStyles()),
                () => themes.StylesRevision));
        }

        if (_backends.Sounds is { } sounds)
        {
            modules.Add(SteamSoundOverrideSurface.Module(HostSteamUiEnabled,
                () => new ValueTask<SteamSoundOverrideState?>(sounds.ReadOverrides()), () => sounds.Revision,
                reportStatus: sounds.ReportPlaybackStatus));
            // The UI sounds page, where packs are browsed, previewed, installed and chosen.
            modules.Add(SteamSoundsSurface.Module(HostSteamUiEnabled, sounds));
        }

        // The boot movie's page. Follows CEF itself, like the themes' page.
        if (_backends.Animations is { } animations && AnimationsPage is { } animationsPage)
        {
            modules.Add(SteamAnimationsSurface.Module(
                HostSteamUiEnabled,
                () => new ValueTask<SteamAnimationsState?>(animationsPage.ReadState()),
                () => animationsPage.Revision,
                animationsPage,
                animations));
        }

        // Settings > Customization as the hub for the three above. It reads their pages'
        // publications and sends their commands, so it declares none of its own.
        modules.Add(SteamCustomizationSurface.Module());
        modules.Add(SteamBrowserSurface.Module(HostSteamUiEnabled));

        if (_backends.Notes is { } notes)
        {
            modules.Add(NotesSteamSurface.Module(HostSteamUiEnabled, notes));
        }

        // Which Quick Access sections the user opened. Declared unconditionally: the sections exist
        // whenever the tabs do.
        modules.Add(SteamPanelFoldsSurface.Module(
            Enabled,
            () => new ValueTask<SteamPanelFoldsState?>(PanelFolds.ReadState()),
            PanelFolds));

        // The sections those folds belong to, and the label a game-override row leads with. Constant,
        // and declared beside the folds for the same reason.
        modules.Add(SteamQuickAccessLayoutSurface.Module(
            Enabled,
            () => new ValueTask<SteamQuickAccessLayout?>(NativeQamLayout.Layout)));

        // The plugin tab. Declared unconditionally: WSGM's own tools are on it whether or not any
        // package is installed, which is the state every install was actually in.
        modules.Add(SteamExtensionsTabSurface.Module(
            HostSteamUiEnabled,
            () => new ValueTask<SteamExtensionsTabState?>(_extensionsTab.ReadState()),
            _extensionsTab));

        // WSGM's display-off rows in Steam's Screensaver settings, over the same timeouts the overlay
        // edits. Reading goes to Windows each time, so an overlay change reaches Steam on the next
        // publication and a change made in Windows reaches it when the page next opens.
        if (_backends.DisplayTimeouts is { } timeouts)
        {
            modules.Add(SteamScreensaverSurface.Module(
                () => SteamUiPatchSwitches.ScreensaverRowsWanted(_switches(), timeouts),
                () => new ValueTask<SteamScreensaverState?>(timeouts.ReadState() with
                {
                    Activity = _screensaverInput.Read(),
                    Content = _backends.Screensavers?.ReadState()
                }),
                timeouts, content: _backends.Screensavers));
        }

        if (Audio is { } audio)
        {
            // Publishing once after injection updates the store whose availability was cached when
            // Steam started before the replacement namespace existed.
            modules.Add(SteamAudioSurface.Module(Enabled, () => new ValueTask<SteamAudioState?>(audio.Current), audio));
        }

        // The gate reveals Steam's Wi-Fi surface, and the surface is only worth revealing if
        // something can populate it — which is the radio manager. Bluetooth rides the same
        // condition for the same reason.
        if (Network is { } network)
        {
            modules.Add(SteamNetworkSurface.Module(
                // The one publication not gated on native Quick Access alone: the header Wi-Fi
                // indicator is shown on the desktop side too, where the rest of the QAM is not.
                () =>
                {
                    var switches = _switches();
                    return switches.NativeQuickAccess || switches.NetworkIndicator;
                },
                () => network.ReadStateAsync(_switches().NetworkIndicator),
                network));
        }

        if (Bluetooth is { } bluetooth)
        {
            modules.Add(SteamBluetoothSurface.Module(Enabled, bluetooth.ReadStateAsync, bluetooth));
        }

        // Reading is synchronous: it projects the managers' rows and reads the local volumes, Steam's
        // library file and each card's marker, skipping network and optical drives.
        if (_backends.Storage is { } storage)
        {
            modules.Add(SteamStorageSurface.Module(Enabled,
                () => new ValueTask<SteamStorageState?>(storage.ReadState()), storage));
        }

        return modules;
    }

    /// <summary>The publication gate every surface shares: on while native Quick Access is.</summary>
    private bool Enabled()
    {
        return _switches().NativeQuickAccess;
    }

    /// <summary>Whether the host-rendered Steam surfaces may publish.</summary>
    /// <returns>Whether CEF itself is on, regardless of the native Quick Access switch.</returns>
    private bool HostSteamUiEnabled()
    {
        return _switches().HostSurfaces;
    }

    /// <summary>Every custom route this session serves, WSGM's own first.</summary>
    /// <remarks>
    ///     One owner publishes the route list, because the page host keys its patch and publication by
    ///     one id: two owners publishing it would alternately clobber each other's routes, and two
    ///     owners registering the patch is refused outright by the module set. A plugin route that
    ///     collides with one already admitted is dropped and named, so a package cannot shadow
    ///     another's page or WSGM's.
    /// </remarks>
    private SteamPageState ReadPages()
    {
        List<SteamPage> pages = [];
        if (_backends.Artwork is not null)
        {
            pages.Add(new SteamPage(
                "artwork-browser",
                SteamArtworkBrowserSurface.Route,
                "Change Artwork",
                Template: "artwork-browser"));
        }

        if (_backends.LibraryImport is not null)
        {
            pages.Add(new SteamPage(
                "library-import",
                SteamLibraryImportSurface.Route,
                "Import games",
                Template: SteamLibraryImportSurface.Template));
        }

        if (_backends.Emulators is not null)
        {
            pages.Add(new SteamPage(
                "emulator-manager",
                SteamEmulatorSurface.Route,
                "Emulator Manager",
                Template: SteamEmulatorSurface.Template));
        }

        if (_backends.GameTools is not null)
        {
            pages.Add(new SteamPage(
                SteamGameToolsSurface.PageId,
                SteamGameToolsSurface.Route,
                SteamGameToolsSurface.Title,
                Template: SteamGameToolsSurface.Template));
        }

        if (_backends.LibraryManager is { } libraryManager)
        {
            if (libraryManager.CardManagerAvailable)
            {
                pages.Add(new SteamPage("card-manager", SteamCardManagerSurface.Route, "SD Card Manager",
                    Template: SteamCardManagerSurface.Template));
            }

            if (libraryManager.TabManagerAvailable)
            {
                pages.Add(new SteamPage("library-tab-manager", SteamLibraryTabsSurface.Route, "Library Tab Manager",
                    Template: SteamLibraryTabsSurface.Template));
            }
        }

        if (_backends.WsgmSettings is not null)
        {
            pages.Add(new SteamPage(
                "wsgm-settings",
                SteamWsgmSettingsSurface.Route,
                "WSGM",
                Template: SteamWsgmSettingsSurface.Template));
        }

        if (_backends.Notes is not null)
        {
            pages.Add(new SteamPage("notes", NotesSteamSurface.Route, "Notes", Template: NotesSteamSurface.Template));
        }

        pages.Add(new SteamPage("browser", SteamBrowserSurface.Route, "Browser", Template: SteamBrowserSurface.Template));

        if (_backends.Themes is not null)
        {
            pages.Add(new SteamPage(
                "themes",
                SteamThemesSurface.Route,
                "Themes",
                Template: SteamThemesSurface.Template));
        }

        if (_backends.Sounds is not null)
        {
            pages.Add(new SteamPage(
                "sounds",
                SteamSoundsSurface.Route,
                "UI sounds",
                Template: SteamSoundsSurface.Template));
        }

        if (_backends.Animations is not null)
        {
            pages.Add(new SteamPage(
                "animations",
                SteamAnimationsSurface.Route,
                "Animations",
                Template: SteamAnimationsSurface.Template));
        }

        if (_switches().HostSurfaces && _backends.PluginSteamUi is { } pluginSteamUi)
        {
            HashSet<string> claimed = new(pages.Select(page => page.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var page in pluginSteamUi.ReadPages())
            {
                if (claimed.Add(page.Path))
                {
                    pages.Add(page);
                }
                else
                {
                    Log.Change(
                        $"steam-page-collision:{page.Path}",
                        $"A plugin page was refused: {page.Path} is already served.",
                        LogLevel.Warn);
                }
            }
        }

        return new SteamPageState(pages);
    }

    private async Task<SteamUiCommandResult> HandleToggleQuickAccessAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        var succeeded = await _toggleQuickAccess(cancellationToken).ConfigureAwait(false);
        return succeeded
            ? SteamUiCommandResult.Applied
            : new SteamUiCommandResult(false, "Quick access is not currently available.");
    }

    /// <summary>The host's own modules and the ready plugins' ones, as one set.</summary>
    /// <param name="pluginModules">The plugin modules, in the source's order.</param>
    /// <returns>The set, and the patch ids of the plugin modules it took.</returns>
    /// <remarks>
    ///     A plugin module whose id, patch, state or command collides with one already taken is dropped
    ///     with one log line, so one package cannot take every Steam surface down with it.
    /// </remarks>
    private (SteamUiModuleSet Modules, HashSet<string> PluginPatchIds) ComposeModules(
        IReadOnlyList<ISteamUiModule> pluginModules)
    {
        var pluginSteamUi = _backends.PluginSteamUi;
        List<ISteamUiModule> accepted = [];
        foreach (var module in pluginModules)
        {
            // The bridge and the overlay activation are registered beside the set, so the set cannot
            // refuse a module claiming them; the patch registry would, after part of the swap.
            if (module.Patches.Any(patch => patch.Id == SteamUiBridgePatch.PatchId
                                            || patch.Id == _overlayActivationId))
            {
                Log.Warn($"Steam UI plugin module {module.Id} was not registered: it claims a host patch.");
                pluginSteamUi?.FailModule(module.Id, "Module collides with a host-owned patch.");
                continue;
            }

            try
            {
                _ = new SteamUiModuleSet([.. _hostModules, .. accepted, module]);
                accepted.Add(module);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                pluginSteamUi?.FailModule(module.Id, ex.Message);
                Log.Warn($"Steam UI plugin module {module.Id} was not registered: {ex.Message}");
            }
        }

        accepted = [.. accepted.Where(module => pluginSteamUi?.ModuleEnabled(module.Id) != false)];
        HashSet<string> pluginPatchIds = [.. accepted.SelectMany(module => module.Patches).Select(patch => patch.Id)];
        return (new SteamUiModuleSet([.. _hostModules, .. accepted]), pluginPatchIds);
    }
}
