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
/// against, and WMI is COM, which this NativeAOT binary cannot use. The notification
/// arrives BEFORE Windows has finished mounting and lettering the volume, so every
/// reaction goes through <see cref="SettleDelay"/> first and the whole set of drives is
/// rescanned rather than the reported device being resolved back to a mount point.
/// </para>
/// <para>
/// Steam is only ever changed through its own front-end (invariant 8): registrations
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
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Library paths seen as live removable cards this session, normalized
    /// key to the path as it was discovered. Only touched inside a reconcile pass,
    /// which the gate serializes.</summary>
    private readonly Dictionary<string, string> _knownCardPaths =
        new(StringComparer.Ordinal);

    private Timer? _settle;
    private bool _disposed;

    private CardVolumeMonitor(MessageWindow window, Func<bool> enabled, Func<Task> afterReconcile)
    {
        _window = window;
        _enabled = enabled;
        _afterReconcile = afterReconcile;
    }

    /// <summary>Creates the monitor and subscribes it to volume notifications.</summary>
    /// <param name="window">The process message-only window.</param>
    /// <param name="enabled">Whether the CEF bridge may be driven right now. Read at
    /// every reaction, not captured once, so the master switch applies live.</param>
    /// <param name="afterReconcile">Runs after a pass that changed something — the
    /// hook that re-syncs library tabs and the in-page badge.</param>
    /// <returns>The started monitor, or null when the registration failed.</returns>
    internal static CardVolumeMonitor? StartNew(
        MessageWindow window, Func<bool> enabled, Func<Task> afterReconcile)
    {
        var monitor = new CardVolumeMonitor(window, enabled, afterReconcile);
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
        _settle ??= new Timer(_ => _ = RunPassAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _settle.Change(SettleDelay, Timeout.InfiniteTimeSpan);
    }

    private async Task RunPassAsync()
    {
        if (_disposed || !_enabled() || !Steam.IsRunning)
        {
            return;
        }
        // One pass at a time. A second card arriving mid-pass simply waits; the scan
        // is cheap and the state it reads is whatever is true when it runs.
        if (!await _gate.WaitAsync(TimeSpan.Zero).ConfigureAwait(false))
        {
            return;
        }
        try
        {
            using var timeout = new CancellationTokenSource(PassTimeout);
            var changed = await ReconcileAsync(timeout.Token).ConfigureAwait(false);
            if (changed)
            {
                await _afterReconcile().ConfigureAwait(false);
            }
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
    private async Task<bool> ReconcileAsync(CancellationToken cancellationToken)
    {
        var registered = ReadRegisteredContentIdsByPath();
        var present = ScanCardLibraryPaths();
        var changed = false;

        foreach (var (libraryPath, cardContentId) in present)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = SteamLibraryVdf.NormalizePath(libraryPath);
            // Remembering the path while the card is HERE is the only way the
            // removal pass below can know it was a card at all: once the media is
            // out, the volume is gone and nothing can be asked whether it was
            // hot-pluggable. See RemoveDepartedCardsAsync.
            _knownCardPaths[key] = libraryPath;
            var ids = registered.TryGetValue(key, out var at) ? at : [];
            var action = CardLibraryDecision.Decide(cardContentId, ids);
            if (action == CardLibraryAction.None)
            {
                continue;
            }
            Log.Info($"Card volumes: {libraryPath} needs {action} "
                + $"(card {cardContentId ?? "none"}, Steam has {ids.Count} registration(s)).");
            changed |= await ApplyAsync(action, libraryPath, cancellationToken)
                .ConfigureAwait(false);
        }

        var here = present
            .Select(card => SteamLibraryVdf.NormalizePath(card.LibraryPath))
            .ToHashSet(StringComparer.Ordinal);
        changed |= await RemoveDepartedCardsAsync(here, registered, cancellationToken)
            .ConfigureAwait(false);
        return changed;
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
            if (!registered.ContainsKey(key))
            {
                // Gone, and Steam no longer lists it: nothing to do, and no reason to
                // keep watching a path that is not a card any more.
                _knownCardPaths.Remove(key);
                continue;
            }
            Log.Info($"Card volumes: {libraryPath} left the reader; "
                + "removing the library Steam still holds for it.");
            if (await ApplyAsync(CardLibraryAction.Purge, libraryPath, cancellationToken)
                .ConfigureAwait(false))
            {
                changed = true;
                _knownCardPaths.Remove(key);
            }
        }
        return changed;
    }

    private static async Task<bool> ApplyAsync(
        CardLibraryAction action, string libraryPath, CancellationToken cancellationToken)
    {
        if (action == CardLibraryAction.Purge)
        {
            var removal = await SteamCdp.RemoveLibrariesAtPathAsync(libraryPath, cancellationToken)
                .ConfigureAwait(false);
            return removal.Status == SteamLibraryRemoveStatus.Removed;
        }
        // Replace and Add both end in an add. `replaceExisting` makes the add drop
        // whatever is registered at the path first, which is exactly Replace; for Add
        // there is nothing there to drop, so one call covers both.
        var add = await SteamCdp.AddLibraryAsync(
            libraryPath, label: null, replaceExisting: action == CardLibraryAction.Replace,
            cancellationToken).ConfigureAwait(false);
        return add.Status is SteamLibraryAddStatus.Added or SteamLibraryAddStatus.AlreadyPresent;
    }

    /// <summary>Every mounted card's <c>&lt;X&gt;:\SteamLibrary</c> path, paired with
    /// the content id its marker carries — null when the volume has no library on it,
    /// which is the state a freshly inserted blank card is in.</summary>
    /// <remarks>
    /// Only volumes that are removable AND not part of a system disk are considered,
    /// so a fixed second drive is never touched by an insert of something else. A
    /// path with no library still has to be reported: that is precisely the case where
    /// Steam is holding a registration for a card that has gone.
    /// </remarks>
    private static List<(string LibraryPath, string? ContentId)> ScanCardLibraryPaths()
    {
        var systemDisks = RemovableDriveManager.ResolveSystemDisks();
        var found = new List<(string, string?)>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Removable || !drive.IsReady
                    || !IsRemovableCardVolume(drive, systemDisks))
                {
                    continue;
                }
                var libraryPath = Path.Combine(drive.Name, "SteamLibrary");
                var marker = Path.Combine(libraryPath, "libraryfolder.vdf");
                var contentId = File.Exists(marker)
                    ? SteamLibraryVdf.ValuesOf(File.ReadAllText(marker), "contentid")
                        .FirstOrDefault()
                    : null;
                found.Add((libraryPath.TrimEnd('\\'), contentId));
            }
            catch (Exception ex)
            {
                // A card pulled mid-scan throws here; the next notification re-runs.
                Log.Warn($"Card volumes: could not inspect {drive.Name}: {ex.Message}");
            }
        }
        return found;
    }

    private static bool IsRemovableCardVolume(DriveInfo drive, HashSet<int> systemDisks)
    {
        var letter = char.ToUpperInvariant(drive.Name[0]);
        using var volume = NativeStorage.OpenVolumeForQuery(letter);
        if (volume.IsInvalid
            || !NativeStorage.TryGetDeviceNumber(volume, out var type, out var disk)
            || type != NativeStorage.FileDeviceDisk || disk < 0
            || systemDisks.Contains(disk))
        {
            return false;
        }
        // Query access only, for the same reason the card scan uses it: a read handle
        // on \\.\PhysicalDriveN needs elevation, so it would find nothing when WSGM
        // runs unelevated.
        using var physical = NativeStorage.OpenDiskForQuery(disk);
        return !physical.IsInvalid
            && NativeStorage.TryGetHotplugInfo(physical, out var media, out var hotplug)
            && RemovableDriveManager.Classify(hotplug, media) is not null;
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
        var steamExe = Steam.ExePath;
        if (steamExe is null)
        {
            return map;
        }
        try
        {
            var configPath = Path.Combine(
                Path.GetDirectoryName(steamExe)!, "config", "libraryfolders.vdf");
            if (!File.Exists(configPath))
            {
                return map;
            }
            var text = File.ReadAllText(configPath);
            // Each entry lists path and contentid in order, so the two value lists
            // align by entry — the same pairing the card label lookup relies on.
            var paths = SteamLibraryVdf.ValuesOf(text, "path");
            var ids = SteamLibraryVdf.ValuesOf(text, "contentid");
            for (var i = 0; i < paths.Count && i < ids.Count; i++)
            {
                var key = SteamLibraryVdf.NormalizePath(paths[i]);
                if (key.Length == 0)
                {
                    continue;
                }
                if (!map.TryGetValue(key, out var list))
                {
                    map[key] = list = [];
                }
                list.Add(ids[i]);
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
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _window.VolumeChanged -= OnVolumeChanged;
        _window.DeregisterVolumeNotifications();
        _settle?.Dispose();
        _settle = null;
        _gate.Dispose();
    }
}
