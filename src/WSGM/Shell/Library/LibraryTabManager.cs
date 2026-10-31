using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Builds Steam library tabs as injected in-memory definitions over CEF:
///     <list type="bullet">
///         <item>
///             one tab per removable Steam library (MicroSD card / external drive) the
///             <see cref="SdCardLibraryCatalog" /> tracks;
///         </item>
///         <item>user-built filter tabs evaluated against Steam's app store.</item>
///     </list>
///     Steam renders fake in-memory collections through its own grid; no real collection
///     is created or modified except one-time cleanup of IDs from older WSGM builds.
/// </summary>
/// <remarks>
///     Owned by <see cref="LibraryManagerService" />, whose lifetime admits, cancels and joins every
///     sync; this type keeps only the serialization of the syncs themselves.
/// </remarks>
/// <param name="store">The configuration persistence the tabs and cards are kept in.</param>
internal sealed class LibraryTabManager(ConfigStore store)
{
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

    /// <summary>How long the boot sync lets Steam's library finish loading after Big Picture is up.</summary>
    private static readonly TimeSpan LibraryReadyBudget = TimeSpan.FromSeconds(60);

    // Each CEF operation is bounded to 30 seconds. Keep the overall startup allowance in the
    // host and leave five seconds for transport discovery around each in-page wait.
    private static readonly TimeSpan LibraryReadyProbeBudget = TimeSpan.FromSeconds(25);

    // Every trigger (boot, card change, each builder change) serializes here; concurrent syncs
    // would race the config.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    ///     Answers <c>true</c> once Steam's webpack registry, its library stores and WSGM's tab claim
    ///     exist, or <c>false</c> when they did not within <see cref="LibraryReadyProbeBudget" />.
    /// </summary>
    /// <remarks>
    ///     Steam raises nothing when its stores are assigned, and the claim arrives with the bridge
    ///     the patch host installs once the transport opens, so the wait runs inside Steam rather than
    ///     as a round trip per check. It settles on its own before the evaluation's deadline.
    /// </remarks>
    private static string LibraryReadyProbe(bool requireTabClaim) =>
        "(async()=>{const ns=" + SteamCef.JsString(SteamUiBridgeIdentity.Namespace) + ";"
        + "const ready=()=>!!window.webpackChunksteamui&&window.appStore?.m_bIsInitialized===true"
        + (requireTabClaim ? "&&!!window.collectionStore&&!!window[ns]?.gate('wsgmLibraryTabs')" : "") + ";"
        + "const until=Date.now()+" + LibraryReadyProbeBudget.TotalMilliseconds.ToString(CultureInfo.InvariantCulture) +
        ";"
        + "while(!ready()){if(Date.now()>until)return 'false';await new Promise(r=>setTimeout(r,250));}"
        + "return 'true';})()";

    /// <summary>
    ///     Recomputes every WSGM library tab and injects them into Steam's tab
    ///     strip (see <see cref="SteamLibraryTabs" />): custom filter tabs, then per-card
    ///     tabs, then genre tabs. Reactive: called after any change in the builder or on a
    ///     card. Concurrent calls are serialized, not coalesced — every queued caller runs a full sync.
    /// </summary>
    /// <param name="steam">The session's Steam client.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>
    ///     The tab publication outcome and retry state; local discovery may already be saved when Steam
    ///     publication fails. Cancellation after admission is reported as a summary; cancellation while
    ///     waiting for the gate can propagate.
    /// </returns>
    internal async Task<LibraryTabSyncResult> SyncAsync(SteamClient steam, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var discovered = await Task.Run(SdCardLibraryCatalog.ScanLibraries, cancellationToken)
                .ConfigureAwait(false);
            var present = discovered.Select(static card => card.ContentId).ToHashSet(StringComparer.Ordinal);
            var config = await store.UpdateAsync(fresh =>
            {
                SdCardLibraryCatalog.MergeDiscovery(fresh, discovered);
                return fresh;
            }, cancellationToken).ConfigureAwait(false);

            // Card membership and badges come from disk, independently of Steam's tab claim.
            // Publish them before waiting, so an unready library cannot hide a newly inserted card.
            LibraryBadges.Update(config, present);

            var tabsEnabled = config.Cef is { Enabled: true, LibraryTabs: true };
            if (tabsEnabled && !await WaitForLibraryAsync(steam, cancellationToken).ConfigureAwait(false))
            {
                return new LibraryTabSyncResult(
                    "Steam's library did not finish loading; card membership and badges were refreshed.", false);
            }

            var (tabs, reachable, filterFailed) = await BuildTabsAsync(steam, config, discovered, cancellationToken)
                .ConfigureAwait(false);

            // CEF library-tabs feature gate (master + sub-toggle): when off, the tab
            // strip is never pushed. Discovery, badges, and config merge below still
            // run so the SD-card manager and its badges remain independent.
            TabSyncResult sync;
            if (reachable != false && !filterFailed && tabsEnabled)
            {
                sync = await SteamLibraryTabs.SyncTabsAsync(
                        steam, tabs, config.LibraryTabOrder, config.HiddenNativeTabs, cancellationToken)
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
                    var retraction = await SteamLibraryTabs.DisableAsync(steam, cancellationToken)
                        .ConfigureAwait(false);
                    reachable ??= retraction.Answered;
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
            config = await store.UpdateAsync(fresh =>
            {
                SdCardLibraryCatalog.MergeDiscovery(fresh, discovered);
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
            LibraryBadges.Update(config, present);
            // The merge above may have added a card, renamed one from its marker or refreshed its
            // capacity: the manager projections follow it from this reading, without another scan.
            var reading = new LibraryReading(config, present);
            if (!tabsEnabled)
            {
                // The tab strip is switched off, so there is nothing left to push and
                // nothing pending: report success, or the boot sync tries again at every
                // later ready edge of the Steam UI transport.
                return new LibraryTabSyncResult("Library tabs are turned off.", true, reading);
            }

            if (!reachedSteam || !ok)
            {
                if (filterFailed)
                {
                    return new LibraryTabSyncResult(
                        "Saved the tabs, but one filter failed in Steam; existing tabs were preserved.",
                        false, reading);
                }

                return new LibraryTabSyncResult(
                    "Saved the tabs — Steam isn't reachable yet; they'll appear when it's open.",
                    false, reading);
            }

            Log.Info($"Library tabs: {tabs.Count} injected.");
            var summary = tabs.Count == 0
                ? "No library tabs yet — add a custom tab or insert a card library."
                : $"Synced {tabs.Count} library tabs.";
            return new LibraryTabSyncResult(summary, true, reading);
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
            _gate.Release();
        }
    }

    /// <summary>Waits for library readiness using requests within the transport's operation bound.</summary>
    /// <param name="steam">The session's Steam client.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <param name="requireTabClaim">Whether this caller also needs the injected library tab claim.</param>
    /// <returns>Whether the library became ready within the budget.</returns>
    internal static async Task<bool> WaitForLibraryAsync(SteamClient steam, CancellationToken cancellationToken,
        bool requireTabClaim = true)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(LibraryReadyBudget);
        while (!deadline.IsCancellationRequested)
        {
            var probe = await steam.EvaluateAsync(
                    SteamUiTargetRole.SharedJsContext, LibraryReadyProbe(requireTabClaim),
                    LibraryReadyProbeBudget + TimeSpan.FromSeconds(5), deadline.Token)
                .ConfigureAwait(false);
            if (!probe.Answered || probe.Error is not null)
            {
                Log.Info($"Library tabs: readiness probe did not answer: {probe.Error}");
                return false;
            }

            if (probe.Value == "true")
            {
                return true;
            }

            if (probe.Value != "false")
            {
                Log.Warn("Library tabs: readiness probe returned an unexpected result.");
                return false;
            }
        }

        return false;
    }

    /// <summary>
    ///     Builds the ordered injected-tab list: custom filter tabs (evaluated over
    ///     the library), then per-card tabs. Reachable is false when Steam was unreachable
    ///     during filter evaluation and null when no filter was evaluated at all — nothing
    ///     probed Steam then, so the caller must not read it as "reachable".
    /// </summary>
    private static async Task<(List<InjectedTab> Tabs, bool? Reachable, bool FilterFailed)> BuildTabsAsync(
        SteamClient steam, AppConfig config, List<DiscoveredCard> discovered, CancellationToken cancellationToken)
    {
        var tabs = new List<InjectedTab>();
        var resolver = new CardLibraryResolver(config, discovered);

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
                ? LibraryFilterCategories.Games
                : (LibraryFilterCategories)tab.Categories, resolver)).ToList();
        var evaluations = await LibraryFilter.EvaluateAsync(steam, expressions, cancellationToken)
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

    /// <summary>Saves the tab-strip order and the hidden native tabs.</summary>
    /// <param name="order">Every tab key, left to right.</param>
    /// <param name="hidden">The native tabs left out of the strip.</param>
    /// <returns>Completion of the configuration write.</returns>
    internal Task SaveTabOrderAsync(List<string> order, List<string> hidden)
    {
        return store.UpdateAsync<object?>(config =>
        {
            config.LibraryTabOrder = order;
            config.HiddenNativeTabs = hidden;
            return null;
        });
    }

    /// <summary>Writes one custom tab, replacing the stored tab with its id or adding it after the others.</summary>
    /// <param name="tab">The tab to store.</param>
    /// <returns>Completion after the tab is saved.</returns>
    internal Task SaveCustomTabAsync(CustomTabConfig tab)
    {
        return store.UpdateAsync<object?>(config =>
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

            return null;
        });
    }

    /// <summary>Removes one custom tab from the configuration.</summary>
    /// <param name="id">The tab's id.</param>
    /// <returns>Completion after the removal is saved.</returns>
    internal Task DeleteCustomTabAsync(string id)
    {
        return store.UpdateAsync<object?>(config =>
        {
            config.CustomTabs.RemoveAll(tab => tab.Id == id);
            return null;
        });
    }

    /// <summary>
    ///     Builds the full tab-strip list the way Steam will render it: keys from
    ///     <see cref="AppConfig.LibraryTabOrder" /> first, then unlisted tabs in natural
    ///     order (native tabs, custom tabs by position, card tabs). Hidden native tabs
    ///     stay in the list — marked hidden — so they can be moved and unhidden.
    /// </summary>
    /// <param name="config">The loaded configuration.</param>
    /// <returns>A new ordered list including hidden native tabs; the input configuration is not changed.</returns>
    internal static List<LibraryTabOrderView> BuildTabOrder(AppConfig config)
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

        var pool = new List<LibraryTabOrderView>();
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
            .Select(tab => new LibraryTabOrderView($"wsgm-custom-{tab.Id}", tab.Name, "custom", false)));
        pool.AddRange(config.CardLibraries
            .Where(c => c is { Enabled: true, Hidden: false })
            .Where(card => pooled.Add($"wsgm-card-{card.ContentId}"))
            .Select(card => new LibraryTabOrderView($"wsgm-card-{card.ContentId}", card.Name, "card", false)));

        var byKey = pool.ToDictionary(e => e.Key, StringComparer.Ordinal);
        var result = new List<LibraryTabOrderView>();
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
                pool.Add(new LibraryTabOrderView(
                    id, titles.GetValueOrDefault(id, id), "native", hidden.Contains(id)));
            }
        }
    }
}
