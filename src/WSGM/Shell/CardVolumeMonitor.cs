using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Keeps Steam's install-folder list honest about which SD card is actually
/// in the reader, driven by volume arrival/removal instead of by the user noticing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> A card reader hands every card the same drive letter, and
/// Steam keys install folders by PATH with no dedup. Swap a card and Steam still holds
/// the previous card's library at <c>E:\SteamLibrary</c> — its app list, its capacity,
/// its content id. Ejecting does not clear it, because the registration was never tied
/// to the card; only a Steam restart rebuilds the list from disk. Adding the new card
/// on top produces TWO registrations at one path, which is what the user sees as "the
/// new card shows the previous card's games but the right size" (live-verified against
/// a running client, 2026-08-20). The reconcile below is what makes an insert behave
/// the way it does on a Steam Deck: the card that is in the reader is the library Steam
/// has.
/// </para>
/// <para>
/// <b>Detection is reader-agnostic on purpose.</b> The signal is a
/// <c>GUID_DEVINTERFACE_VOLUME</c> device notification (see
/// <see cref="MessageWindow.RegisterVolumeNotifications"/>), not a WMI query for a
/// disk model — a model match only ever works for the one reader it was written
/// against. The notification
/// arrives BEFORE Windows has finished mounting and lettering the volume, so every
/// reaction goes through <see cref="SettleDelay"/> first and the whole set of drives is
/// rescanned rather than the reported device being resolved back to a mount point.
/// </para>
/// <para>
/// Steam is only ever changed through its own front-end (see <c>docs\steam-cef.md</c>): registrations
/// are removed and added over the CEF bridge, never by hand-writing
/// <c>libraryfolders.vdf</c> under a live client. With Steam closed there is nothing to
/// reconcile — its next start reads the file and rebuilds the list correctly by itself.
/// </para>
/// </remarks>
internal sealed class CardVolumeMonitor : IDisposable
{
    /// <summary>How long to let Windows finish mounting before looking at drives.
    /// The notification fires while the volume is still arriving; reading drive
    /// letters immediately sees the state from before the change.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(3);

    /// <summary>Upper bound on one reconcile pass. Every step is a CEF round trip
    /// with its own timeout; this stops a wedged client pinning the worker.</summary>
    private static readonly TimeSpan PassTimeout = TimeSpan.FromSeconds(60);

    private readonly MessageWindow _window;
    private readonly Func<bool> _enabled;
    private readonly Func<Task> _afterReconcile;

    /// <summary>The one owner of what a detection means for Steam's library list.</summary>
    private readonly LibraryPolicy _policy;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _lifetimeGate = new();
    private readonly HashSet<Task> _activePasses = [];

    /// <summary>Library paths seen as live removable cards this session, normalized
    /// key to the path as it was discovered. Only touched inside a reconcile pass,
    /// which the gate serializes.</summary>
    private readonly Dictionary<string, string> _knownCardPaths =
        new(StringComparer.Ordinal);

    private Timer? _settle;
    private bool _disposed;
    private bool _waitingForSteamUi;

    private CardVolumeMonitor(
        MessageWindow window, Func<bool> enabled, Func<Task> afterReconcile, LibraryPolicy policy)
    {
        _window = window;
        _enabled = enabled;
        _afterReconcile = afterReconcile;
        _policy = policy;
    }

    /// <summary>Creates the monitor and subscribes it to volume notifications.</summary>
    /// <param name="window">The process message-only window.</param>
    /// <param name="enabled">Whether the CEF bridge may be driven right now. Read at
    /// every reaction, not captured once, so the master switch applies live.</param>
    /// <param name="afterReconcile">Runs after a pass that changed something — the
    /// hook that re-syncs library tabs and the in-page badge.</param>
    /// <param name="policy">
    /// The session's library policy, which owns every registration transition. This monitor
    /// detects and reports; what a detection means is the policy's to decide.
    /// </param>
    /// <returns>The started monitor, or null when the registration failed.</returns>
    internal static CardVolumeMonitor? StartNew(
        MessageWindow window, Func<bool> enabled, Func<Task> afterReconcile, LibraryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var monitor = new CardVolumeMonitor(window, enabled, afterReconcile, policy);
        if (!window.RegisterVolumeNotifications())
        {
            return null;
        }
        window.VolumeChanged += monitor.OnVolumeChanged;
        // Seed from the cards that are already in their readers. Without a first pass
        // the removal path is blind to anything inserted before WSGM started: it only
        // ever purges a path it saw as a live card, and nothing would have seen it.
        monitor.Kick("startup");
        return monitor;
    }

    /// <summary>Schedules a reconcile pass without a volume notification — used at
    /// startup and whenever Steam restarts, since a fresh client rebuilds its folder
    /// list from disk and may bring a departed card's library back with it.</summary>
    /// <param name="reason">What asked for the pass; appears in the log.</param>
    internal void Kick(string reason)
    {
        if (_disposed)
        {
            return;
        }
        Log.Info($"Card volumes: reconcile requested ({reason}).");
        Schedule();
    }

    private void OnVolumeChanged(bool arrived)
    {
        if (_disposed)
        {
            return;
        }
        Log.Info($"Card volumes: {(arrived ? "arrival" : "removal")} reported, "
            + $"reconciling in {SettleDelay.TotalSeconds:0}s.");
        Schedule();
    }

    /// <summary>Arms the settle timer. Restarting a one-shot timer collapses the burst
    /// a single card produces — a reader reports the interface, then the volume, then
    /// the mount — into one pass, and covers a user swapping several cards in a row.
    /// </summary>
    private void Schedule()
    {
        lock (_lifetimeGate)
        {
            if (_disposed)
            {
                return;
            }

            _settle ??= new Timer(_ => StartPass(), null, Timeout.Infinite, Timeout.Infinite);
            _settle.Change(SettleDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void StartPass()
    {
        Task pass;
        lock (_lifetimeGate)
        {
            if (_disposed)
            {
                return;
            }

            pass = RunPassAsync(_lifetime.Token);
            _activePasses.Add(pass);
        }

        _ = pass.ContinueWith(
            completed => CompletePass(completed),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void CompletePass(Task pass)
    {
        if (pass.Exception is { } failure)
        {
            Log.Warn($"Card volumes: reconcile worker failed during teardown: {failure.GetBaseException().Message}");
        }

        lock (_lifetimeGate)
        {
            _activePasses.Remove(pass);
            if (_disposed && _activePasses.Count == 0)
            {
                _gate.Dispose();
                _lifetime.Dispose();
            }
        }
    }

    private async Task RunPassAsync(CancellationToken lifetimeToken)
    {
        if (lifetimeToken.IsCancellationRequested)
        {
            return;
        }
        // One pass at a time. A second card arriving mid-pass simply waits; the scan
        // is cheap and the state it reads is whatever is true when it runs.
        if (!await _gate.WaitAsync(TimeSpan.Zero, lifetimeToken).ConfigureAwait(false))
        {
            return;
        }
        try
        {
            // Look at the reader before asking whether Steam may be changed, and remember what is
            // there regardless of the answer. This used to sit behind the Steam-running check, and
            // a session that started while Steam was still coming up bailed out of its startup
            // pass without ever recording the card already in the reader; when that card was
            // pulled later, the removal pass had nothing it knew about to purge and did nothing,
            // silently, with the library still in Steam's list (Claw, 2026-09-11). Remembering is
            // local and touches nothing; only the reconcile needs Steam.
            var present = ScanCardLibraryPaths();
            foreach (var card in present)
            {
                _knownCardPaths[SteamLibraryVdf.NormalizePath(card.LibraryPath)] = card.LibraryPath;
            }

            if (!_enabled() || !Steam.IsRunning)
            {
                // Not silent: this is the branch that hid the fault above. Same one-shot discipline
                // as the readiness wait, and the same retry, so the pass runs once Steam is up.
                if (!_waitingForSteamUi)
                {
                    _waitingForSteamUi = true;
                    Log.Info("Card volumes: card state captured; waiting for Steam before changing "
                        + "its library list.");
                }
                Schedule();
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            timeout.CancelAfter(PassTimeout);
            var changed = await ReconcileAsync(present, timeout.Token).ConfigureAwait(false);
            if (changed)
            {
                await _afterReconcile().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
            Log.Info("Card volumes: reconcile canceled during monitor shutdown.");
        }
        catch (OperationCanceledException)
        {
            Log.Warn("Card volumes: reconcile timed out; Steam's library list is unchanged.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Card volumes: reconcile failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Brings Steam's registrations in line with the cards that are actually
    /// in their readers — both the ones that arrived and the ones that left. Returns
    /// true when anything changed.</summary>
    /// <param name="present">
    /// The reader's contents as scanned by the caller, which has already remembered them in
    /// <see cref="_knownCardPaths"/>. Remembering the path while the card is here is the only way
    /// the removal pass can know it was a card at all: once the media is out, the volume is gone
    /// and nothing can be asked whether it was hot-pluggable. See <see cref="RemoveDepartedCardsAsync"/>.
    /// </param>
    /// <param name="cancellationToken">Bounds the pass.</param>
    private async Task<bool> ReconcileAsync(
        List<(string LibraryPath, string? ContentId, string Label)> present,
        CancellationToken cancellationToken)
    {
        var registered = ReadRegisteredContentIdsByPath();
        var changed = false;

        if (!SteamUiReadiness.IsReady)
        {
            if (!_waitingForSteamUi)
            {
                _waitingForSteamUi = true;
                Log.Info("Card volumes: card state captured; waiting for the Big Picture window "
                    + "before changing Steam's library list.");
            }
            Schedule();
            return false;
        }
        if (_waitingForSteamUi)
        {
            _waitingForSteamUi = false;
            Log.Info("Card volumes: Big Picture is ready; resuming deferred library reconcile.");
        }

        foreach (var (libraryPath, cardContentId, _) in present)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = SteamLibraryVdf.NormalizePath(libraryPath);
            var ids = registered.TryGetValue(key, out var at) ? at : [];

            // A library the user ejected on purpose stays out of Steam's list even though the
            // volume is mounted and looks exactly like a fresh insert. A media eject leaves the
            // card in the reader and Windows remounts it in seconds, so without this the next
            // pass puts back what the eject just removed.
            if (_policy.IsHeldEjected(libraryPath, cardContentId))
            {
                continue;
            }

            var action = LibraryPolicy.Decide(cardContentId, ids);
            if (action == LibraryTransition.None)
            {
                continue;
            }
            // Every earlier card in this pass cost a CEF round trip, so the scan can be
            // seconds old by now and the reader takes a new card in far less than that.
            // Re-read the marker and abandon the decision if the media changed: acting
            // on it would register the card that left, or hand the card that arrived
            // the previous one's label. The swap raised its own notification, so the
            // pass it schedules decides again on what is actually there.
            if (!StillInTheReader(libraryPath, cardContentId, out var cardLabel))
            {
                Schedule();
                continue;
            }
            Log.Info($"Card volumes: {libraryPath} needs {action} "
                + $"(card {cardContentId ?? "none"}, Steam has {ids.Count} registration(s)).");
            changed |= await LibraryPolicy.ApplyAsync(
                action, libraryPath, cardLabel, cancellationToken).ConfigureAwait(false);
        }

        var here = present
            .Select(card => SteamLibraryVdf.NormalizePath(card.LibraryPath))
            .ToHashSet(StringComparer.Ordinal);
        changed |= await RemoveDepartedCardsAsync(here, registered, cancellationToken)
            .ConfigureAwait(false);
        return changed;
    }

    /// <summary>Re-reads the marker at a card path and reports whether it still carries
    /// the identity the scan saw, handing back the label to apply with it.</summary>
    /// <param name="libraryPath">The card library, e.g. <c>E:\SteamLibrary</c>.</param>
    /// <param name="scannedContentId">What the scan read there, null for a volume that
    /// carried no library — for which "still true" means still no library.</param>
    /// <param name="label">The marker's current label, empty when it has none or when
    /// the media changed.</param>
    /// <returns>False when the media no longer matches the scan, or could not be read.
    /// </returns>
    private static bool StillInTheReader(
        string libraryPath, string? scannedContentId, out string label)
    {
        label = "";
        string? currentContentId;
        try
        {
            currentContentId =
                SteamLibraryVdf.TryReadMarker(libraryPath, out var id, out var current)
                    ? id
                    : null;
            label = current;
        }
        catch (Exception ex)
        {
            Log.Warn($"Card volumes: {libraryPath} became unreadable before Steam was "
                + $"changed; the decision is abandoned: {ex.Message}");
            label = "";
            return false;
        }
        if (string.Equals(currentContentId, scannedContentId, StringComparison.Ordinal))
        {
            return true;
        }
        Log.Info($"Card volumes: {libraryPath} now holds {currentContentId ?? "no library"} "
            + $"instead of {scannedContentId ?? "no library"}; the card changed mid-pass, so "
            + "this decision is abandoned and a fresh pass is scheduled.");
        label = "";
        return false;
    }

    /// <summary>Drops Steam's library for every card this session has seen that is no
    /// longer in its reader.</summary>
    /// <remarks>
    /// <para>
    /// Without this, taking a card out changes nothing Steam can see: its registration
    /// is persistent, so the card's games stay in the library as though the card were
    /// still in — reported for both a Safe Eject and pulling the card out.
    /// </para>
    /// <para>
    /// It cannot be driven off the present-volume scan, which is the bug this fixes:
    /// once the media is out, the letter reports not-ready (or disappears), so the path
    /// simply drops out of the scan and nothing ever concludes that it left. Nor can
    /// staleness be inferred from Steam's list alone — a registered path that is
    /// currently unreachable might be an external drive or a share the user unplugged
    /// on purpose, and purging that would throw away a library WSGM never created.
    /// </para>
    /// <para>
    /// So removal is driven off paths this monitor POSITIVELY identified as removable
    /// card libraries while they were mounted. That is deliberately conservative: a
    /// card that was already out when WSGM started is left alone, because nothing
    /// observed it as a card and Steam's own unmounted state is the honest answer.
    /// </para>
    /// </remarks>
    private async Task<bool> RemoveDepartedCardsAsync(
        HashSet<string> present,
        Dictionary<string, List<string>> registered,
        CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var (key, libraryPath) in _knownCardPaths.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (present.Contains(key))
            {
                continue;
            }
            // The media is actually gone, so any standing eject for it has been served: a card
            // that comes back is an ordinary insert and registers again.
            _policy.ClearEjected(libraryPath);

            if (!registered.ContainsKey(key))
            {
                // Gone, and Steam no longer lists it: nothing to do, and no reason to
                // keep watching a path that is not a card any more.
                _knownCardPaths.Remove(key);
                continue;
            }
            Log.Info($"Card volumes: {libraryPath} left the reader; "
                + "removing the library Steam still holds for it.");
            if (await LibraryPolicy.ApplyAsync(
                    LibraryTransition.Purge, libraryPath, "", cancellationToken)
                .ConfigureAwait(false))
            {
                changed = true;
                _knownCardPaths.Remove(key);
            }
            else
            {
                // Kept in _knownCardPaths so the next pass tries again. Said out loud because a
                // registration Steam would not drop is the state the user sees as "the card is
                // still in Steam", and the line above alone reads as if it had been handled.
                Log.Warn($"Card volumes: Steam did not drop {libraryPath}; it will be retried "
                    + "on the next pass.");
            }
        }
        return changed;
    }

    /// <summary>Every mounted card's <c>&lt;X&gt;:\SteamLibrary</c> path, paired with
    /// the content id and label its marker carries — a null id when the volume has no
    /// library on it, which is the state a freshly inserted blank card is in.</summary>
    /// <remarks>
    /// Only volumes that are removable AND not part of a system disk are considered,
    /// so a fixed second drive is never touched by an insert of something else. A
    /// path with no library still has to be reported: that is precisely the case where
    /// Steam is holding a registration for a card that has gone.
    /// </remarks>
    private static List<(string LibraryPath, string? ContentId, string Label)>
        ScanCardLibraryPaths()
    {
        var systemDisks = RemovableDriveManager.ResolveSystemDisks();
        var found = new List<(string, string?, string)>();
        foreach (var volume in NativeStorage.MountedVolumes())
        {
            if (volume.DriveType != DriveType.Removable || !volume.Ready
                || volume.DeviceType != NativeStorage.FileDeviceDisk || volume.Disk < 0
                || RemovableDriveManager.ClassifyDisk(volume.Disk, systemDisks) is null)
            {
                continue;
            }
            var libraryPath = $@"{volume.Letter}:\SteamLibrary";
            try
            {
                var read = SteamLibraryVdf.TryReadMarker(libraryPath, out var id, out var label);
                found.Add((libraryPath, read ? id : null, read ? label : ""));
            }
            catch (Exception ex)
            {
                // A card pulled mid-scan throws here — the volume is then SKIPPED, so
                // an unreadable marker never purges a library. The next notification
                // re-runs the scan.
                Log.Warn($@"Card volumes: could not inspect {volume.Letter}:\: {ex.Message}");
            }
        }
        return found;
    }

    /// <summary>Content ids Steam has registered, grouped by normalized library path.
    /// </summary>
    /// <remarks>
    /// Read from <c>config\libraryfolders.vdf</c> because Steam's live folder API does
    /// not expose content ids at all — <c>GetInstallFolders</c> returns path, capacity
    /// and mount state and nothing that identifies WHICH library a folder holds. Steam
    /// writes the file as soon as the list changes (live-verified), so it is current
    /// enough to answer "is the registration at this path this card's".
    /// </remarks>
    private static Dictionary<string, List<string>> ReadRegisteredContentIdsByPath()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        try
        {
            if (!Steam.TryReadLibraryFolders(out _, out var text) || text is null)
            {
                return map;
            }
            foreach (var entry in SteamLibraryVdf.ReadEntries(text))
            {
                var key = SteamLibraryVdf.NormalizePath(entry.Path);
                if (key.Length == 0 || entry.ContentId is null)
                {
                    continue;
                }
                if (!map.TryGetValue(key, out var list))
                {
                    map[key] = list = [];
                }
                list.Add(entry.ContentId);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Card volumes: could not read Steam's library list: {ex.Message}");
        }
        return map;
    }

    /// <summary>Unsubscribes and stops reacting to volume notifications.</summary>
    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _settle?.Dispose();
            _settle = null;
            _lifetime.Cancel();
            if (_activePasses.Count == 0)
            {
                _gate.Dispose();
                _lifetime.Dispose();
            }
        }
        _window.VolumeChanged -= OnVolumeChanged;
        _window.DeregisterVolumeNotifications();
    }
}
