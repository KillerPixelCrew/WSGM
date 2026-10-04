using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Structured outcome for tab synchronization and retry policy.</summary>
/// <param name="Summary">User-facing summary.</param>
/// <param name="Success">Whether the tab definitions synchronized.</param>
public readonly record struct LibraryTabSyncResult(string Summary, bool Success);

/// <summary>
///     Builds Steam library tabs as injected in-memory definitions over CEF:
///     <list type="bullet">
///         <item>
///             one tab per removable Steam library (MicroSD card / external drive),
///             keyed by its <c>libraryfolder.vdf</c> content id and remembered while ejected;
///         </item>
///         <item>user-built filter tabs evaluated against Steam's app store.</item>
///     </list>
///     Steam renders fake in-memory collections through its own grid; no real collection
///     is created or modified except one-time cleanup of IDs from older WSGM builds.
/// </summary>
public static class LibraryTabManager
{
    // Shared so every trigger (boot, card change, each builder change) serializes;
    // concurrent syncs would race the config.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    // Windows Big Picture's native tabs in Steam's default order — the pre-capture
    // fallback so the order UI works before the first sync has observed the strip.
    // Captured KnownNativeTabs entries override these titles and extend the list.
    private static readonly (string Id, string Title)[] DefaultNativeTabs =
    [
        ("AllGames", "All Games"),
        ("Installed", "Installed"),
        ("Favorites", "Favorites"),
        ("Collections", "Collections"),
        ("DesktopApps", "Non-Steam"),
        ("Soundtracks", "Soundtracks")
    ];

    private static readonly TimeSpan TabOrderPushDelay = TimeSpan.FromMilliseconds(600);

    /// <summary>How long the boot sync lets Steam's library finish loading after Big Picture is up.</summary>
    private static readonly TimeSpan LibraryReadyBudget = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     Answers <c>true</c> once Steam's webpack registry, its library stores and WSGM's tab claim
    ///     exist, or <c>false</c> when they did not within <see cref="LibraryReadyBudget" />.
    /// </summary>
    /// <remarks>
    ///     Steam raises nothing when its stores are assigned, and the claim arrives with the bridge
    ///     the patch host installs once the transport opens, so the wait runs inside Steam rather than
    ///     as a round trip per check. It settles on its own before the evaluation's deadline.
    /// </remarks>
    private static readonly string LibraryReadyProbe =
        "(async()=>{const ns=" + SteamCef.JsString(SteamUiBridgeIdentity.Namespace) + ";"
        + "const ready=()=>!!window.webpackChunksteamui&&!!window.collectionStore&&!!window.appStore"
        + "&&!!window[ns]?.gate('wsgmLibraryTabs');"
        + "const until=Date.now()+" + LibraryReadyBudget.TotalMilliseconds.ToString(CultureInfo.InvariantCulture) + ";"
        + "while(!ready()){if(Date.now()>until)return 'false';await new Promise(r=>setTimeout(r,250));}"
        + "return 'true';})()";

    // The builder's order writes, chained in press order. Only the UI thread appends.
    private static Task _tabOrderWrites = Task.CompletedTask;

    // The pending live push of the newest order, replaced by each write in the chain.
    private static CancellationTokenSource? _tabOrderPush;

    /// <summary>
    ///     Recomputes every WSGM library tab and injects them into Steam's tab
    ///     strip (see <see cref="SteamLibraryTabs" />): custom filter tabs, then per-card
    ///     tabs, then genre tabs. Reactive: called after any change in the builder or on a
    ///     card. Returns a short user-facing summary; concurrent calls are
    ///     serialized, not coalesced — every queued caller runs a full sync.
    /// </summary>
    /// <param name="store">The configuration persistence the tabs and cards are kept in.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public static async Task<string> SyncAllAsync(ConfigStore store, CancellationToken cancellationToken = default)
    {
        return (await SyncAllDetailedAsync(store, cancellationToken).ConfigureAwait(false)).Summary;
    }

    /// <summary>Synchronizes tabs and returns machine-readable retry state.</summary>
    public static async Task<LibraryTabSyncResult> SyncAllDetailedAsync(ConfigStore store, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var discovered = await Task.Run(ScanLibraries, cancellationToken).ConfigureAwait(false);
            var config = await Task.Run((() => store.Read().RequireConfig()), cancellationToken).ConfigureAwait(false);
            MergeDiscovery(config, discovered);

            var (tabs, reachable, filterFailed) = await BuildTabsAsync(config, discovered, cancellationToken)
                .ConfigureAwait(false);

            // CEF library-tabs feature gate (master + sub-toggle): when off, the tab
            // strip is never pushed. Discovery, badges, and config merge below still
            // run so the SD-card manager and its badges remain independent.
            var tabsEnabled = config.Cef is { Enabled: true, LibraryTabs: true };
            TabSyncResult sync;
            if (reachable != false && !filterFailed && tabsEnabled)
            {
                sync = await SteamLibraryTabs.SyncTabsAsync(
                        tabs, config.LibraryTabOrder, config.HiddenNativeTabs, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                sync = new TabSyncResult(false, []);
                if (reachable != false && !tabsEnabled)
                {
                    // Turning the sub-toggle off has to retract, not merely stop
                    // pushing: the resident script keeps rendering the tabs that were
                    // already injected, so without this the setting appears to do
                    // nothing until a desktop trip or a Steam restart clears them.
                    var retraction = await SteamLibraryTabs.DisableAsync(cancellationToken)
                        .ConfigureAwait(false);
                    reachable ??= retraction.Reachable;
                }
            }

            var ok = sync.Ok;
            // BuildTabsAsync leaves reachability unknown when it evaluated no filter
            // (a card-tabs-only or empty configuration never talks to Steam), so the
            // reported value comes from whatever actually reached the CEF target.
            var reachedSteam = reachable ?? ok;

            // CEF work above may take seconds. Merge only this sync's discovery into a
            // freshly loaded config under the cross-process read-modify-write lock;
            // never save the stale snapshot.
            config = await MutateConfigAsync(store, fresh =>
            {
                MergeDiscovery(fresh, discovered);
                // Union, not replace: dynamic native tabs (Soundtracks, Favorites)
                // drop out of Steam's array while empty, and must keep their entry
                // so the order UI can still place and unhide them.
                foreach (var native in sync.NativeTabs)
                {
                    var known = fresh.KnownNativeTabs.FirstOrDefault(k =>
                        string.Equals(k.Id, native.Id, StringComparison.Ordinal));
                    if (known is null)
                    {
                        fresh.KnownNativeTabs.Add(native);
                    }
                    else if (!string.IsNullOrEmpty(native.Title))
                    {
                        known.Title = native.Title;
                    }
                }

                return fresh;
            }, cancellationToken).ConfigureAwait(false);

            // The badge is a toolkit surface fed from the same card model: the reading is replaced
            // here and the session host publishes it. Whether Steam shows it is the host's switch,
            // so this never talks to Steam and cannot fail the sync.
            LibraryBadges.Update(config, discovered.Select(static card => card.ContentId)
                .ToHashSet(StringComparer.Ordinal));
            if (!tabsEnabled)
            {
                // The tab strip is switched off, so there is nothing left to push and
                // nothing pending: report success, or the boot sync tries again at every
                // later ready edge of the Steam UI transport.
                return new LibraryTabSyncResult("Library tabs are turned off.", true);
            }

            if (!reachedSteam || !ok)
            {
                if (filterFailed)
                {
                    return new LibraryTabSyncResult(
                        "Saved the tabs, but one filter failed in Steam; existing tabs were preserved.",
                        false);
                }

                return new LibraryTabSyncResult(
                    "Saved the tabs — Steam isn't reachable yet; they'll appear when it's open.",
                    false);
            }

            Log.Info($"Library tabs: {tabs.Count} injected.");
            var summary = tabs.Count == 0
                ? "No library tabs yet — add a custom tab or insert a card library."
                : $"Synced {tabs.Count} library tabs.";
            return new LibraryTabSyncResult(summary, true);
        }
        catch (OperationCanceledException)
        {
            // Expected: a desktop transition cancels the shared token mid-evaluation.
            // Not a failure, and it must not put a stack trace into the device log.
            Log.Info("Library tabs: sync cancelled.");
            return new LibraryTabSyncResult("Library tab sync cancelled.", false);
        }
        catch (Exception ex)
        {
            Log.Error("Library tabs: sync failed.", ex);
            return new LibraryTabSyncResult("Could not sync library tabs — see the log.", false);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    ///     Syncs once the Steam UI transport opens after a cold boot, a Steam restart or a return to
    ///     game mode, so tabs appear without any user action. In game mode the session's transport gate
    ///     opens when the Big Picture window is up; inside Steam, the sync then waits for the library
    ///     stores and WSGM's tab claim. A sync that does not place the tabs waits for the transport's
    ///     next ready edge, and any card or builder change syncs in the meantime.
    /// </summary>
    /// <param name="store">The configuration persistence the tabs and cards are kept in.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public static async Task SyncOnBootAsync(ConfigStore store, CancellationToken cancellationToken = default)
    {
        _ = await SteamUiReadiness.RunWhenReadyAsync(
            "Library tabs (boot)",
            async token =>
            {
                var probe = await SteamUiTransportSession.EvaluateAsync(
                        LibraryReadyProbe, LibraryReadyBudget + TimeSpan.FromSeconds(5), token)
                    .ConfigureAwait(false);
                if (!probe.Reachable || probe.Value != "true")
                {
                    Log.Info("Library tabs (boot): Steam's library did not finish loading.");
                    return false;
                }

                var result = await SyncAllDetailedAsync(store, token).ConfigureAwait(false);
                Log.Info($"Library tabs (boot): {result.Summary}");
                // A half-initialized appStore can be reachable but reject a filter; only a sync that
                // reached Steam and placed the tabs is done. The badge needs no retry of its own: its
                // reading is published through the patch lifecycle, which reaches Steam when the
                // bridge does.
                return result.Success;
            }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Builds the ordered injected-tab list: custom filter tabs (evaluated over
    ///     the library), then per-card tabs. Reachable is false when Steam was unreachable
    ///     during filter evaluation and null when no filter was evaluated at all — nothing
    ///     probed Steam then, so the caller must not read it as "reachable".
    /// </summary>
    private static async Task<(List<InjectedTab> Tabs, bool? Reachable, bool FilterFailed)> BuildTabsAsync(
        AppConfig config, List<Discovered> discovered, CancellationToken cancellationToken)
    {
        var tabs = new List<InjectedTab>();
        var resolver = new CardResolver(config, discovered);

        var customTabs = config.CustomTabs
            .Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Name))
            .OrderBy(t => t.Position)
            .Where(tab =>
            {
                var valid = LibraryFilter.IsValid(tab.FilterTree);
                if (!valid)
                {
                    Log.Warn($"Library tabs: skipped invalid custom tab '{tab.Name}'.");
                }

                return valid;
            }).ToList();
        var expressions = customTabs.Select(tab => LibraryFilter.BuildEvaluation(
            tab.FilterTree, tab.Categories == 0
                ? LibraryFilter.Categories.Games
                : (LibraryFilter.Categories)tab.Categories, resolver)).ToList();
        var evaluations = await LibraryFilter.EvaluateAsync(expressions, cancellationToken)
            .ConfigureAwait(false);
        for (var i = 0; i < customTabs.Count; i++)
        {
            var tab = customTabs[i];
            var eval = evaluations[i];
            if (!eval.Reachable)
            {
                return (tabs, false, false);
            }

            if (!eval.Ok)
            {
                Log.Warn($"Library tabs: Steam filter evaluation failed for '{tab.Name}'.");
                return (tabs, true, true);
            }

            if (eval.AppIds.Count > 0)
            {
                tabs.Add(new InjectedTab($"wsgm-custom-{tab.Id}", tab.Name, eval.AppIds));
            }
        }

        // Only the cards the user has enabled — never auto-generated genre tabs. A user
        // who wants a genre tab makes a custom tab with a Tag filter (same engine).
        var present = new HashSet<string>(
            discovered.Select(d => d.ContentId), StringComparer.Ordinal);
        tabs.AddRange(config.CardLibraries
            .Where(c => c is { Enabled: true, Hidden: false })
            .Where(card => (present.Contains(card.ContentId) || config.KeepEjectedCardTabs)
                           && card.AppIds.Count > 0)
            .Select(card => new InjectedTab($"wsgm-card-{card.ContentId}", card.Name, card.AppIds)));

        // Only a filter evaluation talks to Steam here; with none, reachability is
        // unknown rather than proven.
        bool? reachable = customTabs.Count > 0 ? true : null;
        return (tabs, reachable, false);
    }

    /// <summary>
    ///     Scans drives, refreshes the card DB, and returns the current cards with
    ///     live inserted state — the card manager's data source.
    /// </summary>
    /// <param name="store">The configuration persistence the tabs and cards are kept in.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    public static async Task<IReadOnlyList<CardView>> ListCardsAsync(ConfigStore store, CancellationToken cancellationToken = default)
    {
        var discovered = await Task.Run(ScanLibraries, cancellationToken).ConfigureAwait(false);
        var present = new HashSet<string>(
            discovered.Select(d => d.ContentId), StringComparer.Ordinal);
        return await MutateConfigAsync(store, config =>
        {
            MergeDiscovery(config, discovered);
            return config.CardLibraries
                .Select(c => new CardView(
                    c.ContentId, c.Name, c.Enabled, c.Hidden, c.AppIds.Count,
                    present.Contains(c.ContentId), [.. c.AppIds]))
                .ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Renames a tracked card everywhere it has a name: the card's own
    ///     <c>libraryfolder.vdf</c> marker, the WSGM tab, the Steam library label (live
    ///     via CEF when Steam runs, else a byte-preserving vdf edit), and the Windows
    ///     volume label. Identity is always the content id, and every write addresses the
    ///     volume resolved from it — never a drive letter.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The marker write is the one that matters: it is the only copy that travels with
    ///         the medium, so it is what the next scan reads the name back from. It happens
    ///         FIRST and everything else is conditional on it, because a name that did not
    ///         reach the drive is reverted by the next scan, and a rename that stopped at the
    ///         tracked cache would appear to work and then silently undo itself.
    ///     </para>
    ///     <para>
    ///         A library that is not mounted therefore cannot be renamed. Nothing can reach its
    ///         marker, so the rename could only live in the tracked cache until the drive came
    ///         back and the marker overwrote it.
    ///     </para>
    ///     <para>
    ///         See <see cref="FindMountedVolume" /> for why the letter is resolved to a volume
    ///         once and never used again: the Steam label is selected by content id and is safe
    ///         either way, but the marker and the Windows volume label are file-system writes,
    ///         and a letter can name different media by the time the Steam round trip between
    ///         them returns.
    ///     </para>
    /// </remarks>
    /// <param name="store">The configuration persistence the tabs and cards are kept in.</param>
    /// <param name="contentId">The card's content id.</param>
    /// <param name="name">The new name.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>
    ///     Null when every side applied; otherwise a short user-facing note
    ///     describing what did not.
    /// </returns>
    public static async Task<string?> RenameCardAsync(ConfigStore store, string contentId, string name,
        CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return "The name cannot be empty.";
        }

        var mounted = await Task.Run(() => FindMountedVolume(contentId), cancellationToken)
            .ConfigureAwait(false);
        if (mounted is not { } volume)
        {
            Log.Info($"Card rename: {contentId} is not mounted; nothing can write its name "
                     + "onto the drive, so the rename is refused.");
            return "Connect the drive to rename it.";
        }

        var library = Path.Combine(volume.Root, SteamLibraryVdf.CardFolderName);
        var markerNote = await Task.Run(
                () => TrySetMarkerLabel(library, contentId, trimmed), cancellationToken)
            .ConfigureAwait(false);
        if (markerNote is not null)
        {
            return markerNote;
        }

        await UpdateCardAsync(store, contentId, c => c.Name = trimmed, cancellationToken)
            .ConfigureAwait(false);

        var notes = new List<string>();
        var steamNote = await PushLabelToSteamAsync(contentId, trimmed, cancellationToken)
            .ConfigureAwait(false);
        if (steamNote is not null)
        {
            notes.Add(steamNote);
        }

        var volumeNote = await Task.Run(
            () => TrySetVolumeLabel(volume, trimmed), cancellationToken).ConfigureAwait(false);
        if (volumeNote is not null)
        {
            notes.Add(volumeNote);
        }

        _ = SyncQuietlyAsync(store, "card manager");
        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    /// <summary>
    ///     Writes the new label into the card's own <c>libraryfolder.vdf</c>,
    ///     preserving every other byte, and reads it back to confirm the media now says
    ///     what the caller asked for.
    /// </summary>
    /// <remarks>
    ///     Safe against a running Steam: the marker is read at mount time, not
    ///     held open, and WSGM already writes it at format time under a live client.
    /// </remarks>
    /// <param name="libraryPath">
    ///     The card library root, e.g. <c>E:\SteamLibrary</c>.
    /// </param>
    /// <param name="contentId">The identity the marker must still carry.</param>
    /// <param name="label">The new label.</param>
    /// <returns>
    ///     Null when the marker now carries the label, else a user-facing note.
    /// </returns>
    internal static string? TrySetMarkerLabel(
        string libraryPath, string contentId, string label)
    {
        const string markerBehind = "The drive still carries its old name.";
        var marker = Path.Combine(libraryPath, "libraryfolder.vdf");
        try
        {
            if (!File.Exists(marker))
            {
                Log.Warn($"Card rename: {marker} is gone; the card keeps its old name.");
                return markerBehind;
            }

            // Selected by content id even though the path already matched it: the card
            // can be swapped between FindMountedLetter and here, and relabelling the
            // wrong card is exactly the failure this rename exists to stop.
            if (!SteamLibraryVdf.TrySetLabel(
                    File.ReadAllText(marker), contentId, label, out var updated)
                || updated is null)
            {
                Log.Warn($"Card rename: {marker} does not carry content id {contentId}; "
                         + "the card in the reader changed. Its name is unchanged.");
                return markerBehind;
            }

            AtomicFile.WriteText(marker, updated, true);
            // Read back rather than trust the write: a replace that half-applied, or a
            // volume that went away underneath it, must not be reported as a rename the
            // next scan will contradict.
            if (SteamLibraryVdf.TryReadMarker(libraryPath, out var written, out var name)
                && string.Equals(written, contentId, StringComparison.Ordinal)
                && string.Equals(name, label, StringComparison.Ordinal))
            {
                return null;
            }

            Log.Warn($"Card rename: {marker} reads back as "
                     + $"'{name}' (content id {written ?? "none"}) after the write; "
                     + "the card's name is not what was asked for.");
            return markerBehind;
        }
        catch (Exception ex)
        {
            Log.Warn($"Card rename: could not write {marker}: {ex.Message}");
            return markerBehind;
        }
    }

    /// <summary>
    ///     Finds the volume carrying this content id, verified by reading the
    ///     marker, and returns it as a volume GUID path.
    /// </summary>
    /// <remarks>
    ///     Two separate reasons this cannot answer with a drive letter. The letter is
    ///     shared by every card a reader has ever held, so it is not identity; and it is
    ///     a mount point that the system can re-point at other media on its own — a
    ///     reconnecting iSCSI target or USB device bringing several volumes back at once
    ///     has the mount manager assigning letters in whatever order it processes them,
    ///     with no user action and no human timescale. Everything the rename writes
    ///     afterwards therefore addresses the volume that was validated here, so a letter
    ///     that moves in between cannot redirect a write onto a different library.
    /// </remarks>
    /// <param name="contentId">The library identity to look for.</param>
    private static MountedVolume? FindMountedVolume(string contentId)
    {
        foreach (var letter in ExternalVolumeLetters())
        {
            try
            {
                // Resolve the volume BEFORE reading the marker, and read through the
                // volume: validating a letter and then writing to that letter is the
                // race this exists to close.
                if (!NativeStorage.TryGetVolumeGuidPath(letter, out var root) || root is null)
                {
                    Log.Warn($"Card rename: {letter}: has no volume path (Win32 error "
                             + $"{NativeStorage.LastWin32Error()}); it cannot be written safely.");
                    continue;
                }

                if (SteamLibraryVdf.TryReadMarker(
                        Path.Combine(root, SteamLibraryVdf.CardFolderName), out var id, out _)
                    && string.Equals(id, contentId, StringComparison.Ordinal))
                {
                    return new MountedVolume(root, letter);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($@"Card rename: could not probe {letter}:\: {ex.Message}");
            }
        }

        return null;
    }

    // Pushes the new label to Steam's own view of the library, so its storage page
    // agrees with the name WSGM shows. Live client: SetFolderLabel over CEF, which
    // Steam persists itself. Closed client: a byte-preserving edit of
    // config\libraryfolders.vdf. The card's own marker is NOT written here — that is
    // TrySetMarkerLabel's job and it runs whether or not Steam is reachable, because
    // it is the copy the next scan reads the name back from.
    private static async Task<string?> PushLabelToSteamAsync(
        string contentId, string label, CancellationToken cancellationToken)
    {
        const string steamBehind = "Steam still shows the old name.";
        var steamExe = Steam.ExePath;
        if (steamExe is null)
        {
            return null;
        }

        var configPath = Path.Combine(
            Path.GetDirectoryName(steamExe)!, "config", "libraryfolders.vdf");

        if (Steam.IsRunning)
        {
            string configText;
            try
            {
                configText = File.Exists(configPath)
                    ? await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false)
                    : "";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"Card rename: could not read Steam config: {ex.Message}");
                return steamBehind;
            }

            var result = await SteamLibraryFolders.SetLibraryLabelByContentIdAsync(
                contentId, configText, label, cancellationToken).ConfigureAwait(false);
            if (result.Status is SteamLibraryLabelStatus.Applied
                or SteamLibraryLabelStatus.NotPresent)
            {
                return null;
            }

            Log.Warn($"Card rename: live relabel {result.Status} "
                     + $"({result.Detail ?? "no detail"}).");
            return steamBehind;
        }

        var edited = await Task.Run(() =>
        {
            try
            {
                if (!File.Exists(configPath)
                    || !SteamLibraryVdf.TrySetLabel(
                        File.ReadAllText(configPath), contentId, label, out var updatedConfig)
                    || updatedConfig is null)
                {
                    return true;
                }

                if (Steam.IsRunning)
                {
                    // Started between the check and the write; its exit rewrite
                    // would clobber ours (and ours could corrupt its view).
                    return false;
                }

                AtomicFile.WriteText(configPath, updatedConfig, true);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"Card rename: vdf label edit failed: {ex.Message}");
                return false;
            }
        }, cancellationToken).ConfigureAwait(false);
        return edited ? null : steamBehind;
    }

    // Windows volume labels are capped by filesystem: 32 chars on NTFS, 11 on
    // FAT32/exFAT — truncate rather than fail, and strip FAT-hostile characters.
    // Addressed by volume GUID path, not by DriveInfo: this runs after a Steam round
    // trip that can take seconds, and a drive letter can name different media by then.
    private static string? TrySetVolumeLabel(MountedVolume volume, string name)
    {
        const string labelBehind = "The Windows volume label could not be changed.";
        try
        {
            if (!NativeStorage.TryGetVolumeInformation(
                    volume.Root, out var current, out var fileSystem))
            {
                Log.Warn($"Card rename: {volume.Letter}: could not be queried for its label "
                         + $"(Win32 error {NativeStorage.LastWin32Error()}); it is unchanged.");
                return labelBehind;
            }

            var isNtfs = string.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase);
            var invalid = "*?/\\|,;:+=<>[]\".".ToCharArray();
            var cleaned = new string([.. name.Where(c => Array.IndexOf(invalid, c) < 0)]).Trim();
            if (cleaned.Length == 0)
            {
                return null;
            }

            var capped = cleaned.Length > (isNtfs ? 32 : 11)
                ? cleaned[..(isNtfs ? 32 : 11)]
                : cleaned;
            if (string.Equals(current, capped, StringComparison.Ordinal))
            {
                return null;
            }

            if (!NativeStorage.TrySetVolumeLabel(volume.Root, capped))
            {
                Log.Warn($"Card rename: could not label {volume.Letter}: '{capped}' "
                         + $"(Win32 error {NativeStorage.LastWin32Error()}).");
                return labelBehind;
            }

            Log.Info($"Card rename: volume {volume.Letter}: labeled '{capped}'.");
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Card rename: could not set the volume label on "
                     + $"{volume.Letter}:: {ex.Message}");
            return labelBehind;
        }
    }

    /// <summary>Enables or disables a card's Steam tab, then rebuilds Steam's tabs in the background.</summary>
    /// <param name="store">The configuration persistence the tabs and cards are kept in.</param>
    /// <param name="contentId">The card's content id.</param>
    /// <param name="enabled">Whether to maintain a tab.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static async Task SetCardEnabledAsync(ConfigStore store, string contentId, bool enabled,
        CancellationToken cancellationToken = default)
    {
        await UpdateCardAsync(store, contentId, c => c.Enabled = enabled, cancellationToken).ConfigureAwait(false);
        _ = SyncQuietlyAsync(store, "card manager");
    }

    /// <summary>Hides or unhides a card in the manager, then rebuilds Steam's tabs in the background.</summary>
    /// <param name="store">The configuration persistence the tabs and cards are kept in.</param>
    /// <param name="contentId">The card's content id.</param>
    /// <param name="hidden">Whether to hide it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static async Task SetCardHiddenAsync(ConfigStore store, string contentId, bool hidden,
        CancellationToken cancellationToken = default)
    {
        await UpdateCardAsync(store, contentId, c => c.Hidden = hidden, cancellationToken).ConfigureAwait(false);
        _ = SyncQuietlyAsync(store, "card manager");
    }

    /// <summary>
    ///     Forgets a card: removes its tab (if any) and its DB entry, then rebuilds Steam's tabs in
    ///     the background. If the card is reinserted later it is rediscovered fresh.
    /// </summary>
    /// <param name="store">The configuration persistence the tabs and cards are kept in.</param>
    /// <param name="contentId">The card's content id.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static async Task ForgetCardAsync(ConfigStore store, string contentId,
        CancellationToken cancellationToken = default)
    {
        await MutateConfigAsync<object?>(store, config =>
        {
            var card = config.CardLibraries.FirstOrDefault(c =>
                string.Equals(c.ContentId, contentId, StringComparison.Ordinal));
            if (card is null)
            {
                return null;
            }

            config.CardLibraries.Remove(card);
            if (!config.ForgottenInsertedCardIds.Contains(contentId, StringComparer.Ordinal))
            {
                config.ForgottenInsertedCardIds.Add(contentId);
            }

            return null;
        }, cancellationToken).ConfigureAwait(false);
        _ = SyncQuietlyAsync(store, "card manager");
    }

    private static Task<object?> UpdateCardAsync(ConfigStore store, string contentId, Action<CardLibraryConfig> apply,
        CancellationToken cancellationToken)
    {
        return MutateConfigAsync<object?>(store, config =>
        {
            var card = config.CardLibraries.FirstOrDefault(c =>
                string.Equals(c.ContentId, contentId, StringComparison.Ordinal));
            if (card is not null)
            {
                apply(card);
            }

            return null;
        }, cancellationToken);
    }

    /// <summary>Saves the tab-strip order and the hidden native tabs, then shows them in the running Steam.</summary>
    /// <param name="store">The configuration persistence the tabs and cards are kept in.</param>
    /// <param name="order">Every tab key, left to right.</param>
    /// <param name="hidden">The native tabs left out of the strip.</param>
    /// <remarks>
    ///     The builder calls this on every move press. Writes are chained so they commit in press
    ///     order; a slow earlier write must not clobber a newer one. The push into Steam is debounced
    ///     and cheap (no filter re-evaluation), so the strip follows while the user is still tapping
    ///     move, and it falls back to a full sync when the resident script is not installed in this
    ///     Steam session yet.
    /// </remarks>
    internal static void SaveTabOrder(ConfigStore store, List<string> order, List<string> hidden)
    {
        _tabOrderWrites = _tabOrderWrites
            .ContinueWith(_ => SaveTabOrderAsync(store, order, hidden), TaskScheduler.Default)
            .Unwrap();
    }

    private static async Task SaveTabOrderAsync(ConfigStore store, List<string> order, List<string> hidden)
    {
        try
        {
            await MutateConfigAsync<object?>(store, config =>
            {
                config.LibraryTabOrder = order;
                config.HiddenNativeTabs = hidden;
                return null;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Library tab order save failed: {ex.Message}");
            return;
        }

        var previous = _tabOrderPush;
        var push = _tabOrderPush = new CancellationTokenSource();
        previous?.Cancel();
        previous?.Dispose();
        _ = PushTabOrderAsync(store, order, hidden, push.Token);
    }

    private static async Task PushTabOrderAsync(ConfigStore store, List<string> order, List<string> hidden,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TabOrderPushDelay, cancellationToken).ConfigureAwait(false);
            if (!await SteamLibraryTabs.PushOrderAsync(order, hidden, cancellationToken).ConfigureAwait(false))
            {
                await SyncQuietlyAsync(store, "builder").ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Library tab order push failed: {ex.Message}");
        }
    }

    /// <summary>Writes the builder's custom tabs, then rebuilds Steam's tabs in the background.</summary>
    /// <param name="store">The configuration persistence the tabs and cards are kept in.</param>
    /// <param name="tabs">Every tab the builder holds.</param>
    /// <param name="baseline">
    ///     Ids of the tabs the builder loaded. One missing from <paramref name="tabs" /> was deleted
    ///     there; a tab added elsewhere since is kept.
    /// </param>
    internal static async Task SaveCustomTabsAsync(ConfigStore store, IReadOnlyList<CustomTabConfig> tabs, IReadOnlySet<string> baseline)
    {
        await MutateConfigAsync<object?>(store, config =>
        {
            var wanted = tabs.Select(static tab => tab.Id).ToHashSet(StringComparer.Ordinal);
            config.CustomTabs.RemoveAll(tab => baseline.Contains(tab.Id) && !wanted.Contains(tab.Id));
            foreach (var tab in tabs)
            {
                var index = config.CustomTabs.FindIndex(existing => existing.Id == tab.Id);
                if (index >= 0)
                {
                    config.CustomTabs[index] = tab;
                }
                else
                {
                    config.CustomTabs.Add(tab);
                }
            }

            return null;
        }).ConfigureAwait(false);
        _ = SyncQuietlyAsync(store, "builder");
    }

    // A change to what Steam should show re-materializes the tabs in the background; a failure
    // waits for the next sync.
    private static async Task SyncQuietlyAsync(ConfigStore store, string origin)
    {
        try
        {
            var summary = await SyncAllAsync(store).ConfigureAwait(false);
            Log.Info($"Library tabs ({origin}): {summary}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Library-tab sync failed ({origin}): {ex.Message}");
        }
    }

    /// <summary>
    ///     Loads the config, applies <paramref name="mutate" />, and saves — the
    ///     whole read-modify-write held under the cross-process config lock so a concurrent
    ///     WSGM process (Settings window) can neither interleave nor lose fields.
    /// </summary>
    /// <typeparam name="T">The value the mutation returns to the caller.</typeparam>
    /// <param name="store">The configuration persistence the tabs and cards are kept in.</param>
    /// <param name="mutate">Applies changes and returns a snapshot value.</param>
    /// <param name="cancellationToken">Cancels the off-thread work.</param>
    internal static Task<T> MutateConfigAsync<T>(ConfigStore store, Func<AppConfig, T> mutate,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            T result = default!;
            store.Update(config => { result = mutate(config); return true; });
            return result;
        }, cancellationToken);
    }

    /// <summary>The content ids of the removable libraries attached right now.</summary>
    /// <remarks>
    ///     What the library badge needs beside the card model, for a reading before the first sync:
    ///     a sync also refreshes the reading, but the badge must not wait for one.
    /// </remarks>
    internal static IReadOnlySet<string> PresentCardContentIds()
    {
        return ScanLibraries().Select(static card => card.ContentId).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    ///     Builds the full tab-strip list the way Steam will render it: keys from
    ///     <see cref="AppConfig.LibraryTabOrder" /> first, then unlisted tabs in natural
    ///     order (native tabs, custom tabs by position, card tabs). Hidden native tabs
    ///     stay in the list — marked hidden — so they can be moved and unhidden.
    /// </summary>
    /// <param name="config">The loaded configuration.</param>
    public static List<TabOrderEntry> BuildTabOrder(AppConfig config)
    {
        var hidden = new HashSet<string>(config.HiddenNativeTabs, StringComparer.Ordinal);
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, title) in DefaultNativeTabs)
        {
            titles[id] = title;
        }

        foreach (var native in config.KnownNativeTabs.Where(n => !string.IsNullOrEmpty(n.Title)))
        {
            titles[native.Id] = native.Title;
        }

        var pool = new List<TabOrderEntry>();
        var pooled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, _) in DefaultNativeTabs)
        {
            AddNative(id);
        }

        foreach (var native in config.KnownNativeTabs)
        {
            AddNative(native.Id);
        }

        foreach (var id in config.HiddenNativeTabs)
        {
            AddNative(id);
        }

        pool.AddRange(config.CustomTabs
            .Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Name))
            .OrderBy(t => t.Position)
            .Where(tab => pooled.Add($"wsgm-custom-{tab.Id}"))
            .Select(tab => new TabOrderEntry($"wsgm-custom-{tab.Id}", tab.Name, false, false)));
        pool.AddRange(config.CardLibraries
            .Where(c => c is { Enabled: true, Hidden: false })
            .Where(card => pooled.Add($"wsgm-card-{card.ContentId}"))
            .Select(card => new TabOrderEntry($"wsgm-card-{card.ContentId}", card.Name, false, false)));

        var byKey = pool.ToDictionary(e => e.Key, StringComparer.Ordinal);
        var result = new List<TabOrderEntry>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in config.LibraryTabOrder)
        {
            if (byKey.TryGetValue(key, out var entry) && used.Add(key))
            {
                result.Add(entry);
            }
        }

        result.AddRange(pool.Where(entry => used.Add(entry.Key)));
        return result;

        void AddNative(string id)
        {
            if (!string.IsNullOrEmpty(id) && pooled.Add(id))
            {
                pool.Add(new TabOrderEntry(
                    id, titles.GetValueOrDefault(id, id), true, hidden.Contains(id)));
            }
        }
    }

    /// <summary>
    ///     Scans every ready external volume for a <c>&lt;X&gt;:\SteamLibrary</c> marker and
    ///     reads its identity, label and installed app ids. The primary Steam install
    ///     has no such subfolder marker, so it is naturally excluded.
    /// </summary>
    /// <remarks>
    ///     Run when a sync, the card manager or a rename needs the answer, never on a timer. It is not
    ///     answered from a snapshot kept since the last volume notification: media slipped into a
    ///     reader whose volume already exists raises none, so such a snapshot would miss the card that
    ///     was just inserted.
    /// </remarks>
    private static List<Discovered> ScanLibraries()
    {
        var found = new List<Discovered>();
        foreach (var letter in ExternalVolumeLetters())
        {
            try
            {
                var root = $@"{letter}:\{SteamLibraryVdf.CardFolderName}";
                if (!SteamLibraryVdf.TryReadMarker(root, out var contentId, out var label)
                    || string.IsNullOrEmpty(contentId))
                {
                    continue;
                }

                var name = ResolveName(label, letter);
                var appIds = ReadAcfAppIds(Path.Combine(root, "steamapps"));
                found.Add(new Discovered(contentId, name, appIds, label));
            }
            catch (Exception ex)
            {
                Log.Warn($@"Library tabs: could not read {letter}:\: {ex.Message}");
            }
        }

        return found;
    }

    /// <summary>
    ///     The letters of every ready volume on external storage, classified as the eject list and the
    ///     card volume monitor classify it: a disk volume whose disk is hot-pluggable or holds removable
    ///     media, and is not one Windows or WSGM runs from.
    /// </summary>
    /// <remarks>
    ///     Query access only: GENERIC_READ on <c>\\.\PhysicalDriveN</c> requires elevation and WSGM is
    ///     asInvoker, so a read handle would be invalid for every disk in a desktop-launched process and
    ///     no card would ever be discovered. <see cref="RemovableDriveManager.ClassifyDisk" /> opens the
    ///     disk that way.
    /// </remarks>
    private static List<char> ExternalVolumeLetters()
    {
        // Resolved once per scan: each call opens two volume handles and issues two
        // IOCTLs, and the answer cannot change while a single scan runs.
        var systemDisks = RemovableDriveManager.ResolveSystemDisks();
        return
        [
            .. NativeStorage.MountedVolumes()
                .Where(volume => volume is { Ready: true, DeviceType: NativeStorage.FileDeviceDisk, Disk: >= 0 }
                                 && RemovableDriveManager.ClassifyDisk(volume.Disk, systemDisks) is not null)
                .Select(volume => volume.Letter)
        ];
    }

    /// <summary>
    ///     Picks a name for a card nothing has tracked yet: its marker label,
    ///     else the volume label, else a drive-letter fallback. Every candidate here is
    ///     read from the media itself. <c>config\libraryfolders.vdf</c> is deliberately
    ///     not consulted — its <c>label</c> belongs to a PATH registration, and a card
    ///     reader hands every card the same path, so Steam carries the previous card's
    ///     label onto the new card's content id (see <c>docs\sd-cards.md</c>).
    /// </summary>
    private static string ResolveName(string markerLabel, char letter)
    {
        if (!string.IsNullOrWhiteSpace(markerLabel))
        {
            return markerLabel.Trim();
        }

        if (NativeStorage.TryGetVolumeInformation($@"{letter}:\", out var volumeLabel, out _)
            && !string.IsNullOrWhiteSpace(volumeLabel)
            && !string.Equals(volumeLabel, SdFormatManager.DefaultLabel, StringComparison.OrdinalIgnoreCase))
        {
            return volumeLabel.Trim();
        }

        return $"Library ({letter}:)";
    }

    /// <summary>
    ///     Reads app ids from <c>appmanifest_&lt;appid&gt;.acf</c> file names — the
    ///     id is in the name, so no VDF parsing is needed for membership.
    /// </summary>
    private static List<long> ReadAcfAppIds(string steamAppsDir)
    {
        var ids = new List<long>();
        if (!Directory.Exists(steamAppsDir))
        {
            return ids;
        }

        foreach (var file in Directory.EnumerateFiles(steamAppsDir, "appmanifest_*.acf"))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            var idText = stem["appmanifest_".Length..];
            if (long.TryParse(idText, out var id) && id > 0 && id != 228980)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    ///     Upserts the scan into the persisted card DB: a new card is added
    ///     (enabled), a known one has its name and app ids refreshed from the media.
    ///     Cards not currently discovered are left untouched (remembered while ejected).
    /// </summary>
    /// <remarks>
    ///     The name follows the card's OWN marker label and nothing else. Steam's
    ///     <c>config\libraryfolders.vdf</c> label was the previous source and is not a
    ///     per-card value: it belongs to the registration at a PATH, and a card reader
    ///     gives every card the same path, so swapping cards left the new card's content
    ///     id carrying the previous card's label — which this merge then adopted, renaming
    ///     one card to the other (device-observed, 2026-09-05; see
    ///     <c>docs\sd-cards.md</c>). A WSGM-side rename writes the marker, so following
    ///     the marker still reflects a rename made here, and there is nothing left for a
    ///     two-way sync to reconcile.
    /// </remarks>
    /// <param name="config">The configuration to upsert into.</param>
    /// <param name="discovered">Cards found mounted by this scan.</param>
    internal static void MergeDiscovery(AppConfig config, List<Discovered> discovered)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(discovered);
        var db = config.CardLibraries;
        var presentIds = discovered.Select(static card => card.ContentId)
            .ToHashSet(StringComparer.Ordinal);
        config.ForgottenInsertedCardIds.RemoveAll(id => !presentIds.Contains(id));
        foreach (var card in discovered)
        {
            if (config.ForgottenInsertedCardIds.Contains(card.ContentId, StringComparer.Ordinal))
            {
                continue;
            }

            var existing = db.FirstOrDefault(c => string.Equals(c.ContentId, card.ContentId, StringComparison.Ordinal));
            if (existing is null)
            {
                existing = new CardLibraryConfig { ContentId = card.ContentId, Enabled = true };
                db.Add(existing);
            }

            // A labelled marker is the card's name. Only a card that carries no label
            // of its own falls back to the scan's guess, and only while nothing has
            // named it yet — otherwise an unlabelled card would lose a rename made
            // here every time the reader handed it a different drive letter.
            var name = string.IsNullOrWhiteSpace(card.MarkerLabel)
                ? existing.Name
                : card.MarkerLabel;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = card.Name;
            }

            if (!string.Equals(existing.Name, name, StringComparison.Ordinal))
            {
                // Change, not Info: one sync merges twice (once into the snapshot it
                // builds tabs from, once into the freshly locked config it saves), so
                // an unconditional line would report every rename twice.
                Log.Change($"card-name-{card.ContentId}",
                    $"Card {card.ContentId}: name '{existing.Name}' -> '{name}' "
                    + "(from the card's own marker).");
                existing.Name = name;
            }

            existing.AppIds = card.AppIds;
        }
    }

    /// <summary>
    ///     Resolves <see cref="FilterKind.SdCard" /> membership from WSGM's card
    ///     model: "inserted" = union of currently-present cards, "any" = union of all
    ///     tracked cards, "specific" = one card's remembered app ids.
    /// </summary>
    private sealed class CardResolver(AppConfig config, List<Discovered> discovered) : ISdCardResolver
    {
        private readonly HashSet<string> _present = new(
            discovered.Select(d => d.ContentId), StringComparer.Ordinal);

        public IReadOnlyCollection<long> Resolve(SdCardScope scope, string contentId)
        {
            var cards = scope switch
            {
                SdCardScope.Inserted => config.CardLibraries.Where(c => _present.Contains(c.ContentId)),
                SdCardScope.Any => config.CardLibraries,
                _ => config.CardLibraries.Where(c => string.Equals(c.ContentId, contentId, StringComparison.Ordinal))
            };
            var ids = new HashSet<long>();
            foreach (var card in cards)
            {
                foreach (var id in card.AppIds)
                {
                    ids.Add(id);
                }
            }

            return ids;
        }
    }

    // ---- Card-manager API (drives the overlay card sub-view) ----

    /// <summary>
    ///     A card as shown in the manager: identity, name, tab/hidden state, game
    ///     count, and whether it is currently inserted.
    /// </summary>
    /// <param name="ContentId">Stable card identity (its library content id).</param>
    /// <param name="Name">Display name.</param>
    /// <param name="Enabled">Whether a Steam tab is maintained.</param>
    /// <param name="Hidden">Whether it is hidden from tab creation.</param>
    /// <param name="GameCount">Remembered installed app count.</param>
    /// <param name="Inserted">Whether the card is currently mounted.</param>
    /// <param name="AppIds">Remembered installed app ids.</param>
    public sealed record CardView(
        string ContentId,
        string Name,
        bool Enabled,
        bool Hidden,
        int GameCount,
        bool Inserted,
        IReadOnlyList<long> AppIds);

    /// <summary>The volume a tracked library currently lives on.</summary>
    /// <param name="Root">
    ///     The volume GUID path with its trailing separator, which is
    ///     what every subsequent write addresses.
    /// </param>
    /// <param name="Letter">
    ///     The drive letter it was found through, for log lines
    ///     only — it is not a durable name for the volume.
    /// </param>
    private readonly record struct MountedVolume(string Root, char Letter);

    /// <summary>
    ///     One row of the tab-order UI: a tab key (a native Steam id or an
    ///     injected <c>wsgm-…</c> id), its display title, and its visibility. Only native
    ///     tabs can be hidden here — WSGM tabs are hidden by disabling them.
    /// </summary>
    /// <param name="Key">Native id (<c>AllGames</c>) or injected id (<c>wsgm-…</c>).</param>
    /// <param name="Title">Display title for the UI.</param>
    /// <param name="IsNative">Whether this is one of Steam's own tabs.</param>
    /// <param name="Hidden">Whether a native tab is currently hidden.</param>
    public sealed record TabOrderEntry(string Key, string Title, bool IsNative, bool Hidden);

    /// <summary>A removable Steam library found on a mounted drive.</summary>
    /// <param name="ContentId">The stable identity from the card's own marker.</param>
    /// <param name="Name">
    ///     The display name to use when the card has never been
    ///     tracked and its marker carries no label.
    /// </param>
    /// <param name="AppIds">The app ids currently installed on the card.</param>
    /// <param name="MarkerLabel">
    ///     The label in the card's own
    ///     <c>libraryfolder.vdf</c>, empty when it has none. Authoritative: it is the
    ///     only name that travels with the media.
    /// </param>
    internal sealed record Discovered(
        string ContentId,
        string Name,
        List<long> AppIds,
        string MarkerLabel);
}
