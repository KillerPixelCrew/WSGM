using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>
///     The session's removable-storage set: library registration policy, the drive and format
///     managers with Steam's revived storage pages over them, the game-mode card services that keep
///     Steam's library list and the card badge current, and the badge's watcher.
/// </summary>
/// <param name="state">The session's live state.</param>
/// <param name="steamUi">The Steam client, readiness, master switch and tab boot sync the card services use.</param>
/// <param name="libraryManager">Reads the library manager the card ACF watcher syncs the tabs through, once it exists.</param>
internal sealed class StorageServices(
    SessionState state,
    SteamUiServices steamUi,
    Func<LibraryManagerService?> libraryManager)
{
    private LibraryBadgeWatcher? _badgeWatcher;
    private CardAcfWatcher? _cardAcfWatcher;
    private CardVolumeMonitor? _cardVolumes;

    /// <summary>
    ///     The one owner of removable-library registration for this session, shared by the card
    ///     monitor, the overlay's eject panel and Steam's storage pages.
    /// </summary>
    /// <remarks>
    ///     Session-lifetime and unconditional: an eject intent has to outlive the surface that made it
    ///     and the card-monitor configuration toggle, or turning card reconciliation off and on would
    ///     silently re-register a library the user ejected.
    /// </remarks>
    internal LibraryPolicy LibraryPolicy { get; } = new(steamUi.Client);

    /// <summary>
    ///     The one removable-drive manager for this session, shared by the quick access sheet's eject
    ///     pill and Steam's revived storage pages.
    /// </summary>
    /// <remarks>
    ///     Session-scoped for the same reason as the audio manager: Steam's storage service is asked
    ///     while the overlay is closed, when no sheet exists to own a manager. Two would
    ///     enumerate every volume twice and could disagree about what is still ejectable.
    /// </remarks>
    internal RemovableDriveManager? Drives { get; private set; }

    /// <summary>
    ///     The one format manager for this session, shared by the overlay's Format SD Card flow and
    ///     Steam's storage pages.
    /// </summary>
    /// <remarks>
    ///     Session-scoped because a format outlives the surface that started it, which was already true
    ///     of the overlay's own: it kept one for the controller's lifetime so a completion reached with
    ///     the sheet closed still surfaced. Steam's pages make that matter twice over, since the format
    ///     can now be started from either side and both have to see the same run.
    /// </remarks>
    internal SdFormatManager? Formats { get; private set; }

    /// <summary>Steam's revived storage pages over those two managers, or null in overlay-test.</summary>
    internal SteamStorageBridge? SteamStorage { get; private set; }

    /// <summary>
    ///     Starts the drive and format managers and Steam's storage pages. Started here rather than by
    ///     the sheet, for the same reason as audio: Steam's storage pages ask what is ejectable while the
    ///     overlay is closed, and an unstarted manager would answer "nothing" to someone holding a card.
    /// </summary>
    /// <param name="messageWindow">The process's message window, whose device notifications the drive manager hears.</param>
    internal void Start(MessageWindow? messageWindow)
    {
        var drives = Drives = new RemovableDriveManager(messageWindow);

        // Every eject surface reaches the manager, so the policy hangs here rather than at
        // each call site: the overlay's panel and Steam's storage page then mean the same
        // thing by an eject without either knowing about the other.
        // The card ACF watcher is handed to both managers by ApplyCardServices, which creates it.
        drives.EjectObserver = LibraryPolicy;
        var formats = Formats = new SdFormatManager(state.Store, state.ShutdownToken, steamUi.Client);
        // The format targets follow every storage read that changed the topology, not a second walk.
        drives.TopologyChanged += () => formats.Refresh(drives.Inventory);
        ShellSession.TryStart("removable drive manager", drives.Start);

        // Over the same two managers the overlay's storage flows use. Steam's revived pages are
        // a second surface on one backend, not a second implementation. The format switch is
        // read through the session's live config, so switching it in Settings takes effect on
        // the next press rather than the next session.
        SteamStorage = new SteamStorageBridge(
            drives, formats, () => state.Config.SteamStorageFormatEnabled,
            () => Drives?.Inventory.Volumes ?? [],
            LibraryPolicy);
        // Whatever is already inserted; after this the drive manager's own changes drive it.
        formats.Refresh();
    }

    /// <summary>
    ///     Publishes the badge's first reading and starts its watcher. The first reading comes before
    ///     any library-tab sync: the host publishes whatever reading exists when the bridge comes up, and
    ///     a null one publishes nothing, which named every card game internal until a sync happened to
    ///     run (Claw, 2026-09-11).
    /// </summary>
    internal void StartBadge()
    {
        try
        {
            LibraryBadges.Update(state.Config, SdCardLibraryCatalog.PresentCardContentIds());
        }
        catch (Exception ex)
        {
            Log.Warn($"Library badge: initial reading failed: {ex.Message}");
        }

        ShellSession.TryStart("library badge watcher", () => _badgeWatcher = LibraryBadgeWatcher.StartNew(state.Store));
    }

    /// <summary>Starts or stops the game-mode card services from one shared policy.</summary>
    /// <remarks>
    ///     Initial direct boot and a later desktop-to-game transition are separate entry
    ///     paths: only the latter raises <c>GameModeEntered</c>. Keeping their activation
    ///     here prevents one path from silently losing volume notifications again.
    /// </remarks>
    /// <param name="gameModeActive">Whether the destination/current mode is game mode.</param>
    internal void ApplyCardServices(bool gameModeActive)
    {
        var decision = GameModeCardServicePolicy.Decide(
            gameModeActive, state.OverlayTestOnly, steamUi.MasterEnabled);

        if (decision.WatchAppManifests && Drives is { } watchDrives && steamUi.Client is not null
            && libraryManager() is { } manager)
        {
            _cardAcfWatcher ??= CardAcfWatcher.StartNew(watchDrives, manager, steamUi.Readiness);
        }
        else
        {
            _cardAcfWatcher?.Dispose();
            _cardAcfWatcher = null;
        }

        // Eject and format stand the watcher down for their whole run; its directory handles
        // would otherwise veto their own volume lock.
        if (Drives is not null)
        {
            Drives.CardWatcher = _cardAcfWatcher;
        }

        if (Formats is not null)
        {
            Formats.CardWatcher = _cardAcfWatcher;
        }

        if (decision.ReconcileSteamLibraries && Drives is { } volumeDrives)
        {
            // Card swaps are reconciled against Steam's install-folder list on the drive
            // manager's settled storage read. The callback refreshes both consumers of
            // the changed library membership after Steam accepts the reconcile.
            _cardVolumes ??= CardVolumeMonitor.StartNew(
                volumeDrives,
                () => steamUi.MasterEnabled,
                () =>
                {
                    Dispatcher.UIThread.Post(steamUi.TabBootSync.Kick);
                    return Task.CompletedTask;
                },
                LibraryPolicy,
                steamUi.Readiness);
        }
        else
        {
            _cardVolumes?.Dispose();
            _cardVolumes = null;
        }
    }

    /// <summary>Asks the volume monitor to reconcile again, because Steam rebuilt its registrations.</summary>
    /// <param name="reason">Why, for the log.</param>
    internal void KickCardVolumes(string reason)
    {
        _cardVolumes?.Kick(reason);
    }

    /// <summary>Disposes the card volume monitor, the card ACF watcher and the badge watcher, in that order.</summary>
    /// <param name="steps">The shutdown's step runner.</param>
    internal void DisposeCardServices(ShutdownSteps steps)
    {
        steps.DisposeOwner("card volume monitor", ref _cardVolumes);
        steps.DisposeOwner("card ACF watcher", ref _cardAcfWatcher);
        steps.DisposeOwner("library badge watcher", ref _badgeWatcher);
    }

    /// <summary>Joins the format work, a task cancelled with the session; the manager holds no timer or handle.</summary>
    /// <param name="steps">The shutdown's step runner.</param>
    /// <returns>Completion of the join, which never faults.</returns>
    internal async Task StopFormatsAsync(ShutdownSteps steps)
    {
        await steps.JoinAsync("Format work", Formats?.Completion ?? Task.CompletedTask)
            .ConfigureAwait(false);
        Formats = null;
    }

    /// <summary>
    ///     Disposes the native providers: the Steam storage bridge, then the drive manager with its
    ///     message-window registration.
    /// </summary>
    /// <param name="steps">The shutdown's step runner.</param>
    /// <returns>Completion of the steps, which never fault.</returns>
    internal async Task DisposeDrivesAsync(ShutdownSteps steps)
    {
        await steps.UiStepAsync("Disposing the Steam storage bridge during application shutdown failed", () =>
        {
            SteamStorage?.Dispose();
            SteamStorage = null;
        }).ConfigureAwait(false);
        await steps.UiStepAsync("Disposing the drive manager during application shutdown failed", () =>
        {
            Drives?.Dispose();
            Drives = null;
        }).ConfigureAwait(false);
    }
}
