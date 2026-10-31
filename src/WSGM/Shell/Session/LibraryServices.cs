using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>
///     The session's Steam library set: the library manager, artwork, themes, sound packs and boot
///     movies, the Game Library importer with its availability tracker, the emulators and the launch
///     actions, all over the one serialized path to Steam's shortcuts.
/// </summary>
/// <remarks>
///     Overlay-test creates none of these owners; the import state still exists, because the overlay's
///     Notes read imported titles from it.
/// </remarks>
/// <param name="state">The session's live state.</param>
/// <param name="steamUi">The Steam client and readiness the library talks to Steam through.</param>
internal sealed class LibraryServices(SessionState state, SteamUiServices steamUi)
{
    private EmulatorService? _closedEmulatorTool;
    private LibraryManagerService? _closedLibraryManager;
    private GameLibraryArtwork? _libraryArtwork;

    /// <summary>The one serialized, account-checked path to Steam's shortcuts for the library, availability and emulators.</summary>
    private SteamShortcutGateway? _shortcuts;

    /// <summary>The import state every library surface reads.</summary>
    internal ImportStateStore ImportState { get; } = new(state.Store.Context);

    /// <summary>The emulator manager, created off the UI thread.</summary>
    internal EmulatorManager? Emulators { get; private set; }

    /// <summary>The emulator surfaces' service over <see cref="Emulators" />.</summary>
    internal EmulatorService? EmulatorTool { get; private set; }

    /// <summary>The library manager behind the overlay's and Steam's library pages.</summary>
    internal LibraryManagerService? LibraryManager { get; private set; }

    /// <summary>The artwork browser's shared owner, or null in overlay-test.</summary>
    internal ArtworkBrowser? Artwork { get; private set; }

    /// <summary>The browsing behind Steam's Change Artwork page, which the Game Library opens too; or null.</summary>
    internal SteamArtworkBrowserSource? ArtworkPage { get; private set; }

    /// <summary>The Steam themes.</summary>
    internal ThemeService? Themes { get; private set; }

    /// <summary>The sound packs.</summary>
    internal SoundPackService? Sounds { get; private set; }

    /// <summary>The boot movies.</summary>
    internal AnimationService? Animations { get; private set; }

    /// <summary>The Xbox and launcher library importer behind the Quick Access tab's page, or null in overlay-test.</summary>
    internal GameLibraryService? GameLibrary { get; private set; }

    /// <summary>The one tracker that reports every non-Steam shortcut's availability.</summary>
    internal ShortcutAvailabilityTracker? Availability { get; private set; }

    /// <summary>The per-game launch actions.</summary>
    internal LaunchActionService? LaunchActions { get; private set; }

    /// <summary>Creates the emulator manager off the UI thread; a failure leaves the session without emulators.</summary>
    /// <returns>Completion of the start, which faults only when shutdown cancelled it.</returns>
    internal async Task StartEmulatorsAsync()
    {
        try
        {
            Emulators = await Task.Run(() => new EmulatorManager(state.Store.Context, null, ImportState),
                    state.ShutdownToken)
                .ConfigureAwait(false);
            EmulatorTool = new EmulatorService(Emulators, url => AppLauncher.Open(url));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            Log.Warn($"The emulator manager could not start: {ex.Message}");
        }
    }

    /// <summary>Starts the library manager, artwork, themes, sound packs and boot movies.</summary>
    /// <param name="steamRunning">Whether Steam runs now, for the boot movie's choice.</param>
    internal void StartPersonalization(Func<bool> steamRunning)
    {
        // Artwork reads its providers from the session's live config, so a key entered in Settings
        // applies to the next search rather than the next session.
        var steam = steamUi.Client ?? throw new InvalidOperationException("The Steam client was not created.");
        _shortcuts = new SteamShortcutGateway(steam, ImportState);
        ShellSession.TryStart("library manager", () =>
        {
            LibraryManager = new LibraryManagerService(state.Store, () => steamUi.Client, () => state.Config);
            LibraryManager.Start();
        });
        ShellSession.TryStart("artwork browser", () =>
        {
            Artwork = new ArtworkBrowser(() => state.Config.Artwork, new ArtworkStateStore(state.Store.Context.Root),
                steam);
            ArtworkPage = Artwork.CreateViewSession();
        });

        // The Steam themes: CSSLoader-compatible themes from DeckThemes, kept in WSGM's own folder
        // and published into every Big Picture window through the toolkit. Reads the session's live
        // config and writes its own fields one at a time through the store.
        // One DeckThemes client for the themes and the sound packs, which browse the same store.
        var themeStore = new ThemeStoreClient();
        ShellSession.TryStart("Steam themes", () =>
        {
            Themes = new ThemeService(
                new ThemeLoader(ThemePaths.DefaultRoot(state.Store.Context)),
                themeStore,
                () => state.Config.Themes,
                change => state.CommitSetting(config => change(config.Themes), false),
                () => Steam.InstallDirectory);
            Themes.Start();
        });

        ShellSession.TryStart("sound packs", () =>
        {
            Sounds = new SoundPackService(new SoundPackLibrary(SoundPackLibrary.DefaultRoot(state.Store.Context)),
                () => state.Config.Sounds.Selected,
                id => state.CommitSetting(config => config.Sounds.Selected = id, false),
                () => Steam.InstallDirectory,
                themeStore);
            Log.Observe(Sounds.RefreshAsync(state.ShutdownToken), "Sound pack startup refresh", true);
            Sounds.SetHostState(state.Config.Cef.Enabled, null);
        });

        // The boot movie: SteamDeckRepo's boot movies and the user's own in WSGM's library, the
        // chosen one copied to the file Steam's client asks for. Started before Steam so a shuffle on start
        // is what Steam reads.
        ShellSession.TryStart("boot movies", () =>
        {
            Animations = new AnimationService(
                new AnimationLibrary(AnimationLibrary.DefaultRoot(state.Store.Context)),
                new AnimationRepoClient(),
                () => state.Config.Animations,
                change => state.CommitSetting(config => change(config.Animations), false),
                () => Steam.InstallDirectory,
                steamRunning: steamRunning,
                steamChoice: new SteamStartupMovieAccess(
                    steam.StartupMovie.SetAsideAsync,
                    steam.StartupMovie.RestoreAsync,
                    (operation, attempt, token) => state.Config.Cef.Enabled
                        ? steamUi.Readiness.RunWhenReadyAsync(operation, attempt, token)
                        : Task.FromResult(false)));
            Animations.Start();
        });
    }

    /// <summary>Starts the Game Library importer and its availability tracker, the launch actions and the emulator shortcuts.</summary>
    /// <param name="profiles">The profile owner the importer saves controller targets through.</param>
    /// <param name="storage">The drive manager the availability tracker follows.</param>
    /// <param name="messageWindow">The process's message window.</param>
    internal void StartGameLibrary(ProfileService profiles, StorageServices storage, MessageWindow? messageWindow)
    {
        ReleaseAbandonedPackageExemptions();

        var steam = steamUi.Client ?? throw new InvalidOperationException("The Steam client was not created.");
        var shortcuts = _shortcuts ?? throw new InvalidOperationException("The shortcut gateway was not created.");
        // The Game Library talks to the same running Steam client everything else here does, and reads
        // each launcher's own files. Every seam is injected so the discovery and planning rules stay
        // testable without a live Steam or a real launcher.
        ShellSession.TryStart("game library", () =>
        {
            StoreCatalogClient catalog = new();
            GameLibraryArtwork libraryArtwork = new(new ArtworkSearchProviders(() => state.Config.Artwork));
            // One tracker reports every non-Steam shortcut's availability; the importer reads its results.
            Availability = new ShortcutAvailabilityTracker(ImportState, shortcuts.ReadShortcutsAsync,
                () => EmulatorTool?.ReadStore() ?? new EmulatorStore(), storage.Drives, messageWindow, Emulators);
            GameLibrary = new GameLibraryService(
                [
                    new XboxLibrarySource(
                        XboxPackages.Enumerate,
                        XboxPackages.ReadPackageFile,
                        (package, token) => catalog.LookUpAsync(package.FamilyName, token)),
                    new EpicLibrarySource(),
                    new EaLibrarySource(),
                    new GogLibrarySource(),
                    new UbisoftLibrarySource(),
                    new BattleNetLibrarySource(),
                    new ItchLibrarySource(),
                    new AmazonLibrarySource(),
                    new PrismLauncherSource(),
                    new AtLauncherSource()
                ],
                UninstallEntries.Read,
                ImportState,
                shortcuts.CreateWriter,
                shortcuts.ReadShortcutsAsync,
                shortcuts.ReadShortcutAsync,
                state.Config.GameLibrary,
                (appId, images, token) =>
                    SteamArtwork.ApplyManyFromUrlsAsync(steam, appId, images, state.Config.Artwork, token),
                (id, name, target, removeEmptyProfile, token) =>
                    profiles.SetApplicationControllerTargetAsync(id, name, target, removeEmptyProfile, token),
                openArtwork: ArtworkPage is { } artwork ? artwork.OpenAsync : null,
                controllerManaged: () => state.Config.DeviceIntegration is
                    { Enabled: true, ControllerManagementEnabled: true },
                updateSettings: change =>
                    state.CommitSetting(config => change(config.GameLibrary), false).GameLibrary,
                folderSource: folder => new ShortcutFolderSource(folder),
                artwork: libraryArtwork,
                syncCollection: (id, name, add, remove, token) =>
                    steam.Collections.SyncAsync(id, name, add, remove, true, token),
                emulators: EmulatorTool,
                availability: Availability,
                readCollections: shortcuts.ReadCollectionsAsync);
            _libraryArtwork = libraryArtwork;
            GameLibrary.Start();
            Availability.Start();
        });

        LaunchActions ??= new LaunchActionService(state.Store, () => steamUi.Client,
            () => state.Config.SteamInputManagementEnabled);
        EmulatorTool?.AttachSteamShortcuts(ImportState, shortcuts.CreateWriter,
            shortcuts.ReadShortcutsAsync, shortcuts.ReadShortcutAsync, () => state.Config.SteamInputManagementEnabled,
            () => steamUi.Readiness.IsReady, async (definition, appId, token) =>
            {
                var images = await EmulatorShortcutArtwork.FindAsync(definition, state.Config.Artwork, token)
                    .ConfigureAwait(false);
                var applied = await SteamArtwork.ApplyManyFromUrlsAsync(steam, appId, images, state.Config.Artwork,
                        token)
                    .ConfigureAwait(false);
                return applied.Count(result => result.Succeeded);
            });
        EmulatorTool?.RequestShortcutSync();
    }

    /// <summary>Runs the one-shot jobs that waited for Steam's UI, once the transport opened.</summary>
    internal void SteamUiReady()
    {
        EmulatorTool?.RequestShortcutSync();
        Availability?.Request();
    }

    /// <summary>Puts packages a killed launcher left exempt back under lifetime management.</summary>
    /// <remarks>
    ///     <para>
    ///         Steam's Stop button terminates the launcher outright, so its own release never runs
    ///         and the package stays exempt. Nothing else ever puts it back: the next launcher only
    ///         sweeps when the user happens to start another imported game, so without this a single
    ///         Stop leaves a package outside lifetime management for the life of the machine.
    ///     </para>
    ///     <para>
    ///         The launcher owns the COM interop for this, so it does the work and WSGM just asks.
    ///         Fire and forget, off the startup path: a sweep that cannot run is not a reason to
    ///         hold up a session.
    ///     </para>
    /// </remarks>
    private static void ReleaseAbandonedPackageExemptions()
    {
        if (PackagedLauncherShortcut.ResolveLauncher() is not { } launcher)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(launcher, "--recover")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
                                           or ObjectDisposedException or IOException)
            {
                Log.Warn($"Packaged launcher recovery sweep did not run: {ex.Message}");
            }
        });
    }

    /// <summary>Closes the library import, emulator operation and library manager admissions, in that order.</summary>
    /// <param name="steps">The shutdown's step runner.</param>
    internal void CloseAdmission(ShutdownSteps steps)
    {
        steps.Step("Closing library import admission failed", () => GameLibrary?.CloseAdmission());
        var emulatorTool = _closedEmulatorTool = EmulatorTool;
        steps.Step("Closing emulator operation admission failed", () => emulatorTool?.Dispose());
        var libraryManager = _closedLibraryManager = LibraryManager;
        steps.Step("Closing library manager admission failed", () => libraryManager?.CloseAdmission());
    }

    /// <summary>Closes the admissions of owners a startup still running created after <see cref="CloseAdmission" />.</summary>
    /// <param name="steps">The shutdown's step runner.</param>
    internal void CloseLateAdmission(ShutdownSteps steps)
    {
        if (!ReferenceEquals(_closedEmulatorTool, EmulatorTool))
        {
            var emulatorTool = _closedEmulatorTool = EmulatorTool;
            steps.Step("Closing late emulator operation admission failed", () => emulatorTool?.Dispose());
        }

        if (!ReferenceEquals(_closedLibraryManager, LibraryManager))
        {
            var libraryManager = _closedLibraryManager = LibraryManager;
            steps.Step("Closing late library manager admission failed", () => libraryManager?.CloseAdmission());
        }
    }

    /// <summary>
    ///     Hands Steam's own Startup Movie choice back while the transport is still open, at every exit when
    ///     no WSGM movie is chosen and at uninstall whatever is chosen, then joins the emulator and library
    ///     manager work.
    /// </summary>
    /// <param name="steps">The shutdown's step runner.</param>
    /// <param name="uninstall">Whether WSGM is being uninstalled.</param>
    /// <returns>Completion of the steps, which never fault.</returns>
    internal async Task FinishSteamWorkAsync(ShutdownSteps steps, bool uninstall)
    {
        if (Animations is { } bootMovies)
        {
            await steps.StepAsync("Handing back Steam's startup movie choice failed",
                    () => bootMovies.HandBackSteamChoiceAsync(uninstall, Deadline.At(steps.Deadline)))
                .ConfigureAwait(false);
        }

        if (_closedEmulatorTool is { } emulatorTool)
        {
            await steps.JoinAsync("Emulator operations",
                    Task.WhenAll(emulatorTool.OperationCompletion, emulatorTool.ShortcutCompletion))
                .ConfigureAwait(false);
        }

        await steps.JoinAsync("Library manager work", _closedLibraryManager?.Completion ?? Task.CompletedTask)
            .ConfigureAwait(false);
    }

    /// <summary>Disposes the library manager, after the Steam host that reached it stopped.</summary>
    /// <param name="steps">The shutdown's step runner.</param>
    internal void DisposeLibraryManager(ShutdownSteps steps)
    {
        var libraryManager = LibraryManager;
        LibraryManager = null;
        steps.Step("Disposing the library manager failed", () => libraryManager?.Dispose());
    }

    /// <summary>
    ///     Stops the availability tracker, before the drive manager whose collection the importer's bridge is
    ///     subscribed to, then the importer and the emulator manager.
    /// </summary>
    /// <param name="steps">The shutdown's step runner.</param>
    /// <returns>Completion of the steps, which never fault.</returns>
    internal async Task StopImportAsync(ShutdownSteps steps)
    {
        await steps.StepAsync("Stopping content availability during application shutdown failed", async () =>
        {
            var availability = Availability;
            Availability = null;
            if (availability is not null)
            {
                await availability.DisposeAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        await steps.StepAsync("Disposing the library importer during application shutdown failed", async () =>
        {
            var libraryImport = GameLibrary;
            GameLibrary = null;
            if (libraryImport is not null)
            {
                await libraryImport.StopAsync(Deadline.At(steps.Deadline)).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        await steps.StepAsync("Disposing the emulator manager during application shutdown failed", () =>
        {
            EmulatorTool = null;
            Emulators?.Dispose();
            Emulators = null;
            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    /// <summary>
    ///     Disposes the library artwork stage, the themes, sound packs and boot movies, then the artwork
    ///     browser. Disposing cancels the store and repository reads, installs, downloads, update checks and
    ///     Steam choice work; the joins wait for them.
    /// </summary>
    /// <param name="steps">The shutdown's step runner.</param>
    /// <returns>Completion of the steps, which never fault.</returns>
    internal async Task DisposePersonalizationAsync(ShutdownSteps steps)
    {
        await steps.UiStepAsync("Disposing the library artwork stage during application shutdown failed", () =>
        {
            _libraryArtwork?.Dispose();
            _libraryArtwork = null;
        }).ConfigureAwait(false);

        var themes = Themes;
        Themes = null;
        await steps.UiStepAsync("Disposing the themes during application shutdown failed",
            () => themes?.Dispose()).ConfigureAwait(false);
        await steps.JoinAsync("Theme work", themes?.Completion ?? Task.CompletedTask)
            .ConfigureAwait(false);
        await steps.StepAsync("Disposing sound packs during application shutdown failed", async () =>
        {
            var sounds = Sounds;
            Sounds = null;
            if (sounds is not null)
            {
                await sounds.DisposeAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        var animations = Animations;
        Animations = null;
        await steps.UiStepAsync("Disposing the animations during application shutdown failed",
            () => animations?.Dispose()).ConfigureAwait(false);
        await steps.JoinAsync("Animation work", animations?.Completion ?? Task.CompletedTask)
            .ConfigureAwait(false);
        await steps.UiStepAsync("Disposing the artwork browser during application shutdown failed", () =>
        {
            // Disposes the Steam page's session with every other.
            Artwork?.Dispose();
            Artwork = null;
            ArtworkPage = null;
        }).ConfigureAwait(false);
    }
}
