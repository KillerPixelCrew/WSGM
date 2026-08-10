using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Builds Steam library tabs by materializing them as WSGM-owned Steam
/// collections over the CEF port (<see cref="SteamCollections"/>):
/// <list type="bullet">
/// <item>one tab per removable Steam library (MicroSD card / external drive),
/// keyed by its <c>libraryfolder.vdf</c> content id and remembered while ejected;</item>
/// <item>one tab per top store-tag genre (category), recomputed from the library.</item>
/// </list>
/// Steam renders each collection as a tab with no restart and no UI injection.
/// Every collection is tracked by the id WSGM created, so a sync only ever updates
/// or prunes WSGM's own collections — never a user's or Steam ROM Manager's.</summary>
public sealed class LibraryTabManager
{
    // Static so every trigger (boot, overlay open, each builder change) coalesces even
    // across separate manager instances — concurrent syncs would race the config.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Recomputes every WSGM library tab and injects them into Steam's tab
    /// strip (see <see cref="SteamLibraryTabs"/>): custom filter tabs, then per-card
    /// tabs, then genre tabs. Reactive — called after any change in the builder and on
    /// overlay open. Returns a short user-facing summary; coalesces concurrent calls.</summary>
    /// <param name="cancellationToken">Cancels the run.</param>
    public async Task<string> SyncAllAsync(CancellationToken cancellationToken = default)
    {
        if (!await Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return "A library-tab sync is already running.";
        }
        try
        {
            var discovered = await Task.Run(ScanLibraries, cancellationToken).ConfigureAwait(false);
            var config = await Task.Run(ConfigStore.Load, cancellationToken).ConfigureAwait(false);
            MergeDiscovery(config, discovered);

            var (tabs, reachable) = await BuildTabsAsync(config, discovered, cancellationToken)
                .ConfigureAwait(false);

            var ok = reachable
                && await SteamLibraryTabs.SyncTabsAsync(tabs, cancellationToken).ConfigureAwait(false);

            // One-time migration off the old collection approach: delete any collections
            // WSGM created before and clear their stored ids.
            if (ok)
            {
                await CleanupLegacyCollectionsAsync(config, cancellationToken).ConfigureAwait(false);
            }

            await Task.Run(() => ConfigStore.Save(config), cancellationToken).ConfigureAwait(false);

            if (!reachable || !ok)
            {
                return "Saved the tabs — Steam isn't reachable yet; they'll appear when it's open.";
            }

            await PushCardBadgesAsync(config, cancellationToken).ConfigureAwait(false);
            Log.Info($"Library tabs: {tabs.Count} injected.");
            return tabs.Count == 0
                ? "No library tabs yet — add a custom tab or insert a card library."
                : $"Synced {tabs.Count} library tabs.";
        }
        catch (Exception ex)
        {
            Log.Error("Library tabs: sync failed.", ex);
            return "Could not sync library tabs — see the log.";
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Waits (with backoff) for Steam's library UI to finish loading after a
    /// cold boot, then syncs once — so tabs appear without the user opening the overlay.
    /// Best-effort and self-limiting; falls back to the on-open sync if Steam never
    /// becomes reachable.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public async Task SyncOnBootAsync(CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 30 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await Task.Delay(attempt == 0 ? 8000 : 5000, cancellationToken).ConfigureAwait(false);
                var probe = await SteamCef.EvaluateAsync(
                    "JSON.stringify(!!window.webpackChunksteamui&&!!window.collectionStore)",
                    TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
                if (probe.Reachable && probe.Value == "true")
                {
                    var summary = await SyncAllAsync(cancellationToken).ConfigureAwait(false);
                    Log.Info($"Library tabs (boot): {summary}");
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Library tabs (boot) probe failed: {ex.Message}");
            }
        }
        Log.Info("Library tabs (boot): Steam UI not reachable in time — will sync on overlay open.");
    }

    /// <summary>Builds the ordered injected-tab list: custom filter tabs (evaluated over
    /// the library), then per-card tabs. The bool is false when Steam was unreachable
    /// during filter evaluation.</summary>
    private static async Task<(List<InjectedTab> Tabs, bool Reachable)> BuildTabsAsync(
        AppConfig config, List<Discovered> discovered, CancellationToken cancellationToken)
    {
        var tabs = new List<InjectedTab>();
        var resolver = new CardResolver(config, discovered);

        foreach (var tab in config.CustomTabs
            .Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Name))
            .OrderBy(t => t.Position))
        {
            var categories = tab.Categories == 0
                ? LibraryFilter.Categories.Games
                : (LibraryFilter.Categories)tab.Categories;
            var js = LibraryFilter.BuildEvaluation(
                tab.FilterTree ?? new FilterNode { Kind = FilterKind.Merge }, categories, resolver);
            var eval = await SteamCollections.EvaluateFilterAsync(js, cancellationToken)
                .ConfigureAwait(false);
            if (!eval.Reachable)
            {
                return (tabs, false);
            }
            if (eval.AppIds.Count > 0)
            {
                tabs.Add(new InjectedTab($"wsgm-custom-{Slug(tab.Name)}", tab.Name, eval.AppIds));
            }
        }

        // Only the cards the user has enabled — never auto-generated genre tabs. A user
        // who wants a genre tab makes a custom tab with a Tag filter (same engine).
        var present = new HashSet<string>(
            discovered.Select(d => d.ContentId), StringComparer.Ordinal);
        foreach (var card in config.CardLibraries.Where(c => c is { Enabled: true, Hidden: false }))
        {
            var keep = present.Contains(card.ContentId) || config.KeepEjectedCardTabs;
            if (keep && card.AppIds.Count > 0)
            {
                tabs.Add(new InjectedTab($"wsgm-card-{card.ContentId}", card.Name, card.AppIds));
            }
        }

        return (tabs, true);
    }

    /// <summary>Deletes any Steam collections WSGM created under the previous
    /// collection-based approach and clears their stored ids, so switching to injected
    /// tabs leaves no orphaned collections behind.</summary>
    private static async Task CleanupLegacyCollectionsAsync(
        AppConfig config, CancellationToken cancellationToken)
    {
        async Task Drop(string id, Action clear)
        {
            if (!string.IsNullOrEmpty(id)
                && await SteamCollections.DeleteByIdAsync(id, cancellationToken).ConfigureAwait(false))
            {
                clear();
            }
        }
        foreach (var card in config.CardLibraries)
        {
            await Drop(card.CollectionId, () => card.CollectionId = "").ConfigureAwait(false);
        }
        foreach (var tab in config.CustomTabs)
        {
            await Drop(tab.CollectionId, () => tab.CollectionId = "").ConfigureAwait(false);
        }
        foreach (var cat in config.CategoryTabs.ToList())
        {
            await Drop(cat.CollectionId, () => cat.CollectionId = "").ConfigureAwait(false);
            config.CategoryTabs.Remove(cat);
        }
    }

    /// <summary>Makes a name id-safe (lowercase alphanumerics + dashes) for an injected
    /// tab id.</summary>
    private static string Slug(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }
        return sb.Length == 0 ? "x" : sb.ToString().Trim('-');
    }

    /// <summary>Pushes the per-game card badge map (app id → card name) into Steam's
    /// library page and (re)installs the resident badge observer. Best-effort — a badge
    /// failure never affects tab syncing.</summary>
    private static async Task PushCardBadgesAsync(AppConfig config, CancellationToken cancellationToken)
    {
        try
        {
            var map = new Dictionary<int, string>();
            foreach (var card in config.CardLibraries.Where(c => c is { Enabled: true, Hidden: false }))
            {
                foreach (var id in card.AppIds)
                {
                    map[id] = card.Name;
                }
            }
            await SteamPageBridge.UpdateCardBadgesAsync(map, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Card badge push failed: {ex.Message}");
        }
    }

    /// <summary>Resolves <see cref="FilterKind.SdCard"/> membership from WSGM's card
    /// model: "inserted" = union of currently-present cards, "any" = union of all
    /// tracked cards, "specific" = one card's remembered app ids.</summary>
    private sealed class CardResolver(AppConfig config, List<Discovered> discovered) : ISdCardResolver
    {
        private readonly HashSet<string> _present = new(
            discovered.Select(d => d.ContentId), StringComparer.Ordinal);

        public IReadOnlyCollection<int> Resolve(SdCardScope scope, string contentId)
        {
            IEnumerable<CardLibraryConfig> cards = scope switch
            {
                SdCardScope.Inserted => config.CardLibraries.Where(c => _present.Contains(c.ContentId)),
                SdCardScope.Any => config.CardLibraries,
                _ => config.CardLibraries.Where(
                    c => string.Equals(c.ContentId, contentId, StringComparison.Ordinal)),
            };
            var ids = new HashSet<int>();
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

    /// <summary>A card as shown in the manager: identity, name, tab/hidden state, game
    /// count, and whether it is currently inserted.</summary>
    /// <param name="ContentId">Stable card identity (its library content id).</param>
    /// <param name="Name">Display name.</param>
    /// <param name="Enabled">Whether a Steam tab is maintained.</param>
    /// <param name="Hidden">Whether it is hidden from tab creation.</param>
    /// <param name="GameCount">Remembered installed app count.</param>
    /// <param name="Inserted">Whether the card is currently mounted.</param>
    /// <param name="AppIds">Remembered installed app ids.</param>
    public sealed record CardView(
        string ContentId, string Name, bool Enabled, bool Hidden, int GameCount, bool Inserted,
        IReadOnlyList<int> AppIds);

    /// <summary>Scans drives, refreshes the card DB, and returns the current cards with
    /// live inserted state — the card manager's data source.</summary>
    /// <param name="cancellationToken">Cancels the scan.</param>
    public async Task<IReadOnlyList<CardView>> ListCardsAsync(
        CancellationToken cancellationToken = default)
    {
        var discovered = await Task.Run(ScanLibraries, cancellationToken).ConfigureAwait(false);
        var present = new HashSet<string>(
            discovered.Select(d => d.ContentId), StringComparer.Ordinal);
        return await MutateConfigAsync(config =>
        {
            MergeDiscovery(config, discovered);
            return config.CardLibraries
                .Select(c => new CardView(
                    c.ContentId, c.Name, c.Enabled, c.Hidden, c.AppIds.Count,
                    present.Contains(c.ContentId), [.. c.AppIds]))
                .ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Renames a tracked card (and its tab).</summary>
    /// <param name="contentId">The card's content id.</param>
    /// <param name="name">The new name.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public Task RenameCardAsync(string contentId, string name,
        CancellationToken cancellationToken = default)
        => UpdateCardAsync(contentId, c => c.Name = name.Trim(), cancellationToken);

    /// <summary>Enables or disables a card's Steam tab.</summary>
    /// <param name="contentId">The card's content id.</param>
    /// <param name="enabled">Whether to maintain a tab.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public Task SetCardEnabledAsync(string contentId, bool enabled,
        CancellationToken cancellationToken = default)
        => UpdateCardAsync(contentId, c => c.Enabled = enabled, cancellationToken);

    /// <summary>Hides or unhides a card in the manager.</summary>
    /// <param name="contentId">The card's content id.</param>
    /// <param name="hidden">Whether to hide it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public Task SetCardHiddenAsync(string contentId, bool hidden,
        CancellationToken cancellationToken = default)
        => UpdateCardAsync(contentId, c => c.Hidden = hidden, cancellationToken);

    /// <summary>Forgets a card: removes its tab (if any) and its DB entry. If the card
    /// is reinserted later it is rediscovered fresh.</summary>
    /// <param name="contentId">The card's content id.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task ForgetCardAsync(string contentId,
        CancellationToken cancellationToken = default)
    {
        var collectionId = await MutateConfigAsync(config =>
        {
            var card = config.CardLibraries.FirstOrDefault(
                c => string.Equals(c.ContentId, contentId, StringComparison.Ordinal));
            if (card is null)
            {
                return "";
            }
            config.CardLibraries.Remove(card);
            return card.CollectionId;
        }, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(collectionId))
        {
            await SteamCollections.DeleteByIdAsync(collectionId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private Task UpdateCardAsync(string contentId, Action<CardLibraryConfig> apply,
        CancellationToken cancellationToken)
        => MutateConfigAsync<object?>(config =>
        {
            var card = config.CardLibraries.FirstOrDefault(
                c => string.Equals(c.ContentId, contentId, StringComparison.Ordinal));
            if (card is not null)
            {
                apply(card);
            }
            return null;
        }, cancellationToken);

    /// <summary>Loads the config, applies <paramref name="mutate"/>, and saves — the
    /// whole read-modify-write held under the cross-process config lock so a concurrent
    /// WSGM process (Settings window) can neither interleave nor lose fields.</summary>
    /// <typeparam name="T">The value the mutation returns to the caller.</typeparam>
    /// <param name="mutate">Applies changes and returns a snapshot value.</param>
    /// <param name="cancellationToken">Cancels the off-thread work.</param>
    internal static Task<T> MutateConfigAsync<T>(Func<AppConfig, T> mutate,
        CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            using var _ = ConfigStore.AcquireLock();
            var config = ConfigStore.Load();
            var result = mutate(config);
            ConfigStore.Save(config);
            return result;
        }, cancellationToken);

    /// <summary>Loads the current custom tabs and cards for the builder UI (no scan;
    /// pair with <see cref="ListCardsAsync"/> for live inserted state).</summary>
    public static AppConfig LoadConfig() => ConfigStore.Load();

    /// <summary>A removable Steam library found on a mounted drive.</summary>
    private sealed record Discovered(string ContentId, string Name, List<int> AppIds, char Letter);

    /// <summary>Scans every ready drive for a <c>&lt;X&gt;:\SteamLibrary</c> marker and
    /// reads its identity, label and installed app ids. The primary Steam install
    /// has no such subfolder marker, so it is naturally excluded.</summary>
    private static List<Discovered> ScanLibraries()
    {
        var configLabels = ReadConfigLabels();
        var found = new List<Discovered>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }
                var root = Path.Combine(drive.Name, "SteamLibrary");
                var marker = Path.Combine(root, "libraryfolder.vdf");
                if (!File.Exists(marker))
                {
                    continue;
                }
                var text = File.ReadAllText(marker);
                var contentId = SteamLibraryVdf.ValuesOf(text, "contentid").FirstOrDefault();
                if (string.IsNullOrEmpty(contentId))
                {
                    continue;
                }
                var letter = char.ToUpperInvariant(drive.Name[0]);
                var label = SteamLibraryVdf.ValuesOf(text, "label").FirstOrDefault() ?? "";
                var name = ResolveName(label, contentId, configLabels, drive, letter);
                var appIds = ReadAcfAppIds(Path.Combine(root, "steamapps"));
                found.Add(new Discovered(contentId, name, appIds, letter));
            }
            catch (Exception ex)
            {
                Log.Warn($"Library tabs: could not read {drive.Name}: {ex.Message}");
            }
        }
        return found;
    }

    /// <summary>Maps content id → the Steam-side library label from
    /// <c>config\libraryfolders.vdf</c> (each entry lists <c>label</c> then
    /// <c>contentid</c> in order, so the value lists align by entry). Lets a card
    /// whose on-disk marker has no label still get its real Steam name.</summary>
    private static Dictionary<string, string> ReadConfigLabels()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var steamExe = Steam.ExePath;
        if (steamExe is null)
        {
            return map;
        }
        var configPath = Path.Combine(
            Path.GetDirectoryName(steamExe)!, "config", "libraryfolders.vdf");
        if (!File.Exists(configPath))
        {
            return map;
        }
        try
        {
            var text = File.ReadAllText(configPath);
            var labels = SteamLibraryVdf.ValuesOf(text, "label");
            var ids = SteamLibraryVdf.ValuesOf(text, "contentid");
            for (var i = 0; i < ids.Count && i < labels.Count; i++)
            {
                if (!string.IsNullOrEmpty(ids[i]))
                {
                    map[ids[i]] = labels[i];
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Library tabs: could not read config labels: {ex.Message}");
        }
        return map;
    }

    /// <summary>Picks a tab name: the library's own marker label, else its
    /// Steam-side label from config, else the volume label, else a drive-letter
    /// fallback.</summary>
    private static string ResolveName(
        string markerLabel, string contentId, Dictionary<string, string> configLabels,
        DriveInfo drive, char letter)
    {
        if (!string.IsNullOrWhiteSpace(markerLabel))
        {
            return markerLabel.Trim();
        }
        if (configLabels.TryGetValue(contentId, out var steamLabel)
            && !string.IsNullOrWhiteSpace(steamLabel))
        {
            return steamLabel.Trim();
        }
        try
        {
            if (!string.IsNullOrWhiteSpace(drive.VolumeLabel)
                && !string.Equals(drive.VolumeLabel, "Games", StringComparison.OrdinalIgnoreCase))
            {
                return drive.VolumeLabel.Trim();
            }
        }
        catch (IOException)
        {
            // No volume label available.
        }
        return $"Library ({letter}:)";
    }

    /// <summary>Reads app ids from <c>appmanifest_&lt;appid&gt;.acf</c> file names — the
    /// id is in the name, so no VDF parsing is needed for membership.</summary>
    private static List<int> ReadAcfAppIds(string steamAppsDir)
    {
        var ids = new List<int>();
        if (!Directory.Exists(steamAppsDir))
        {
            return ids;
        }
        foreach (var file in Directory.EnumerateFiles(steamAppsDir, "appmanifest_*.acf"))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            var idText = stem["appmanifest_".Length..];
            if (int.TryParse(idText, out var id) && id > 0)
            {
                ids.Add(id);
            }
        }
        return ids;
    }

    /// <summary>Upserts the scan into the persisted card DB: a new card is added
    /// (enabled), a known one has its name, app ids, last-seen and letter refreshed.
    /// Cards not currently discovered are left untouched (remembered while ejected).</summary>
    private static void MergeDiscovery(AppConfig config, List<Discovered> discovered)
    {
        var db = config.CardLibraries;
        var now = DateTime.UtcNow.Ticks;
        foreach (var card in discovered)
        {
            var existing = db.FirstOrDefault(
                c => string.Equals(c.ContentId, card.ContentId, StringComparison.Ordinal));
            if (existing is null)
            {
                existing = new CardLibraryConfig { ContentId = card.ContentId, Enabled = true };
                db.Add(existing);
            }
            existing.Name = card.Name;
            existing.AppIds = card.AppIds;
            existing.LastSeenTicks = now;
            existing.LastLetter = card.Letter.ToString();
        }
    }
}
