using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>One tracked card as the SD Card Manager shows it, on the overlay and in Steam.</summary>
/// <param name="ContentId">The card's stable identity (its library content id).</param>
/// <param name="Name">The display name, from the card's own marker.</param>
/// <param name="Enabled">Whether a Steam library tab is maintained for it.</param>
/// <param name="Hidden">Whether it is kept out of tab creation while still tracked.</param>
/// <param name="Inserted">Whether the card is mounted right now.</param>
/// <param name="GameCount">How many games the card holds, remembered while it is ejected.</param>
/// <param name="AppIds">The remembered installed app ids.</param>
/// <param name="CapacityBytes">The volume's size from its last mount, 0 when never read.</param>
/// <param name="FreeBytes">The free space from that same reading.</param>
internal sealed record LibraryCardView(
    string ContentId,
    string Name,
    bool Enabled,
    bool Hidden,
    bool Inserted,
    int GameCount,
    IReadOnlyList<long> AppIds,
    long CapacityBytes,
    long FreeBytes)
{
    /// <summary>Whether the card ever reported its size.</summary>
    public bool HasCapacity => CapacityBytes > 0;

    /// <summary>The used space from the last reading, never negative.</summary>
    public long UsedBytes => HasCapacity ? Math.Max(0, CapacityBytes - FreeBytes) : 0;
}

/// <summary>One game on a card, named from Steam's library when it answered.</summary>
/// <param name="AppId">The app id.</param>
/// <param name="Name">Steam's title, or the id as text when the library was not readable.</param>
internal sealed record LibraryCardGame(long AppId, string Name);

/// <summary>The games of the card last inspected.</summary>
/// <param name="ContentId">Which card the list belongs to.</param>
/// <param name="Games">The games, in the card's order.</param>
/// <param name="Loading">Whether the names are still being read.</param>
/// <param name="Error">Why the names could not be read, or null; the ids are still listed.</param>
internal sealed record LibraryCardGames(
    string ContentId,
    IReadOnlyList<LibraryCardGame> Games,
    bool Loading,
    string? Error);

/// <summary>Everything the SD Card Manager draws.</summary>
/// <param name="Cards">Every tracked card, inserted or remembered.</param>
/// <param name="Loaded">Whether a drive scan has completed since the service started.</param>
/// <param name="Busy">Whether a scan or a change is in flight.</param>
/// <param name="Error">Why the last operation failed, or null.</param>
/// <param name="Notice">Something the user should read, such as a partial rename, or null.</param>
/// <param name="Games">The games of the card last inspected, or null.</param>
/// <param name="Revision">Monotonic observation revision, shared with the tab manager.</param>
internal sealed record SteamCardManagerState(
    IReadOnlyList<LibraryCardView> Cards,
    bool Loaded,
    bool Busy,
    string? Error,
    string? Notice,
    LibraryCardGames? Games,
    long Revision);

/// <summary>One row of the tab strip as the Library Tab Manager orders it.</summary>
/// <param name="Key">The native id (<c>AllGames</c>) or the injected id (<c>wsgm-…</c>).</param>
/// <param name="Title">The title Steam shows.</param>
/// <param name="Kind"><c>native</c>, <c>card</c> or <c>custom</c>.</param>
/// <param name="Hidden">Whether a native tab is currently left out of the strip.</param>
internal sealed record LibraryTabOrderView(string Key, string Title, string Kind, bool Hidden);

/// <summary>One filter type the tab builder offers.</summary>
/// <param name="Kind">The <see cref="FilterKind" /> name.</param>
/// <param name="Label">The short name.</param>
/// <param name="Description">What it matches.</param>
/// <param name="CanInvert">Whether its NOT toggle means anything.</param>
internal sealed record LibraryTabFilterKind(string Kind, string Label, string Description, bool CanInvert);

/// <summary>A card the SD Card filter can name.</summary>
/// <param name="ContentId">The card's identity.</param>
/// <param name="Name">Its display name.</param>
internal sealed record LibraryTabCardChoice(string ContentId, string Name);

/// <summary>A store tag the Tag filter can pick.</summary>
/// <param name="TagId">Steam's tag id.</param>
/// <param name="Name">The tag's name.</param>
/// <param name="Count">How many library games carry it.</param>
internal sealed record LibraryTagChoice(int TagId, string Name, int Count);

/// <summary>A Steam collection the Collection filter can pick.</summary>
/// <param name="Id">The collection id.</param>
/// <param name="Name">Its name.</param>
/// <param name="GameCount">How many games it holds.</param>
internal sealed record LibraryCollectionChoice(string Id, string Name, int GameCount);

/// <summary>A library game the whitelist and blacklist filters can pick.</summary>
/// <param name="AppId">The unsigned app id, the form the filters store.</param>
/// <param name="Name">Steam's title.</param>
internal sealed record LibraryGameChoice(long AppId, string Name);

/// <summary>The store tags, as last read from Steam.</summary>
/// <param name="Items">The tags, empty before the first read and after a failed one.</param>
/// <param name="Loaded">Whether a read succeeded at least once.</param>
/// <param name="Loading">Whether a read is in flight.</param>
/// <param name="Error">Why the last read failed, or null.</param>
internal sealed record LibraryTagPicker(
    IReadOnlyList<LibraryTagChoice> Items, bool Loaded, bool Loading, string? Error);

/// <summary>The collections, as last read from Steam.</summary>
/// <param name="Items">The collections, empty before the first read and after a failed one.</param>
/// <param name="Loaded">Whether a read succeeded at least once.</param>
/// <param name="Loading">Whether a read is in flight.</param>
/// <param name="Error">Why the last read failed, or null.</param>
internal sealed record LibraryCollectionPicker(
    IReadOnlyList<LibraryCollectionChoice> Items, bool Loaded, bool Loading, string? Error);

/// <summary>The library games, as last read from Steam.</summary>
/// <param name="Items">The games, empty before the first read and after a failed one.</param>
/// <param name="Loaded">Whether a read succeeded at least once.</param>
/// <param name="Loading">Whether a read is in flight.</param>
/// <param name="Error">Why the last read failed, or null.</param>
internal sealed record LibraryGamePicker(
    IReadOnlyList<LibraryGameChoice> Items, bool Loaded, bool Loading, string? Error);

/// <summary>Everything the Library Tab Manager draws.</summary>
/// <param name="Tabs">The custom tabs, by position.</param>
/// <param name="Order">The whole tab strip, left to right, hidden native tabs included.</param>
/// <param name="FilterKinds">The filter types the builder offers.</param>
/// <param name="Cards">The tracked cards the SD Card filter can name.</param>
/// <param name="Tags">The store tags for the Tag filter.</param>
/// <param name="Collections">The collections for the Collection filter.</param>
/// <param name="Games">The library for the whitelist and blacklist filters.</param>
/// <param name="Loaded">Whether configuration has been read since the service started.</param>
/// <param name="Busy">Whether a save or a read is in flight.</param>
/// <param name="Error">Why the last operation failed, or null.</param>
/// <param name="Notice">Something the user should read, or null.</param>
/// <param name="Revision">Monotonic observation revision, shared with the card manager.</param>
internal sealed record SteamLibraryTabsState(
    IReadOnlyList<CustomTabConfig> Tabs,
    IReadOnlyList<LibraryTabOrderView> Order,
    IReadOnlyList<LibraryTabFilterKind> FilterKinds,
    IReadOnlyList<LibraryTabCardChoice> Cards,
    LibraryTagPicker Tags,
    LibraryCollectionPicker Collections,
    LibraryGamePicker Games,
    bool Loaded,
    bool Busy,
    string? Error,
    string? Notice,
    long Revision);

/// <summary>
///     The one owner of what the SD Card Manager and the Library Tab Manager show, on the overlay and
///     in Steam's Quick Access. It projects <see cref="LibraryTabManager" />'s card model, custom
///     tabs and tab order into two states, answers both surfaces' operations through the same
///     persistence and Steam synchronization, and follows a change made on the other surface or by a
///     card insertion through <see cref="LibraryTabManager.Changed" />.
/// </summary>
/// <remarks>
///     Reading state is cheap and lock-free: the session host reads it on every publication. Drive
///     scans run only on an explicit refresh and at start, never on a timer; a change rereads the
///     configuration, and a finished sync hands over the scan it already made. Every operation
///     validates once here, so neither surface carries its own copy of the rules.
/// </remarks>
internal sealed class LibraryManagerService : IDisposable
{
    /// <summary>The Extensions-tab item and page namespace of the card manager.</summary>
    internal const string CardsId = "wsgm.cards";

    /// <summary>The action that opens the card manager page.</summary>
    internal const string OpenCardsId = "wsgm.cards.open";

    /// <summary>The Extensions-tab item and page namespace of the tab manager.</summary>
    internal const string TabsId = "wsgm.library-tabs";

    /// <summary>The action that opens the tab manager page.</summary>
    internal const string OpenTabsId = "wsgm.library-tabs.open";

    // How long the tab order waits after the last move press before it is saved and shown in Steam.
    private static readonly TimeSpan TabOrderWriteDelay = TimeSpan.FromMilliseconds(600);

    /// <summary>The filter types the builder offers, in the order both surfaces list them.</summary>
    internal static IReadOnlyList<LibraryTabFilterKind> FilterKinds { get; } =
    [
        Kind(FilterKind.Tag, "Tag / Genre", "Games with a store tag"),
        Kind(FilterKind.Installed, "Installed", "Installed or not"),
        Kind(FilterKind.Collection, "Collection", "In a Steam collection"),
        Kind(FilterKind.Regex, "Title", "Title matches a pattern"),
        Kind(FilterKind.SdCard, "SD Card", "Installed on a card"),
        Kind(FilterKind.TimePlayed, "Playtime", "Above/below hours played"),
        Kind(FilterKind.SizeOnDisk, "Size", "Above/below install size"),
        Kind(FilterKind.ReviewScore, "Review score", "Above/below a score"),
        Kind(FilterKind.ReleaseDate, "Release date", "Before/after a date"),
        Kind(FilterKind.LastPlayed, "Last played", "Before/after a date"),
        Kind(FilterKind.Platform, "Platform", "Steam or non-Steam"),
        Kind(FilterKind.Whitelist, "Whitelist", "Only these games"),
        Kind(FilterKind.Blacklist, "Blacklist", "Exclude these games"),
        Kind(FilterKind.Merge, "Merge group", "Nested AND/OR of filters")
    ];

    private readonly Func<AppConfig> _config;
    private readonly Lock _gate = new();
    private readonly Func<SteamClient?> _steam;
    private readonly ConfigStore _store;
    private int _busy;
    private bool _disposed;
    private int _activeOperations;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The order the user last moved to and not yet saved, and whether its one writer runs. While it
    // does, a refresh keeps the pressed-to order rather than publishing an older stored one over it.
    private TabOrderPress? _pendingOrder;
    private bool _orderWriterRunning;

    // What the next refresh run does: a drive scan, a configuration reread, or the reading a
    // finished sync handed over. A scan covers both of the others.
    private bool _scanPending;
    private bool _rereadPending;
    private LibraryReading? _syncReading;
    private Task? _refreshRun;
    private long _revision;
    private Snapshot _snapshot = Snapshot.Empty;
    private bool _started;

    /// <summary>Creates the service over the session's persistence and Steam client.</summary>
    /// <param name="store">The configuration the cards and tabs are kept in.</param>
    /// <param name="steam">Returns the session's Steam client, or null on a surface without one.</param>
    /// <param name="config">Returns the live configuration, for the feature switches.</param>
    internal LibraryManagerService(ConfigStore store, Func<SteamClient?> steam, Func<AppConfig> config)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _steam = steam ?? throw new ArgumentNullException(nameof(steam));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        CardsSection = new ExtensionsSection(this, CardsId, OpenCardsId, SteamCardManagerSurface.Route,
            "SD Card Manager", "Open SD Card Manager", service => service.CardManagerAvailable,
            service => service.CardsDetail());
        TabsSection = new ExtensionsSection(this, TabsId, OpenTabsId, SteamLibraryTabsSurface.Route,
            "Library Tab Manager", "Open Library Tab Manager", service => service.TabManagerAvailable,
            service => service.TabsDetail());
    }

    /// <summary>Raised after either state changed; handlers must not block.</summary>
    public event Action? Changed;

    /// <summary>The revision both states carry.</summary>
    internal long Revision => Interlocked.Read(ref _revision);

    /// <summary>Whether the card manager may be offered: CEF on and the card manager switch on.</summary>
    internal bool CardManagerAvailable => _config().Cef is { Enabled: true, CardManager: true };

    /// <summary>Whether the tab manager may be offered: CEF on and the library tabs switch on.</summary>
    internal bool TabManagerAvailable => _config().Cef is { Enabled: true, LibraryTabs: true };

    /// <summary>The Quick Access Extensions entry that opens the card manager, absent while unavailable.</summary>
    internal IExtensionsTabSection CardsSection { get; }

    /// <summary>The Quick Access Extensions entry that opens the tab manager, absent while unavailable.</summary>
    internal IExtensionsTabSection TabsSection { get; }

    /// <summary>Completes after admission closes and every admitted read, command and order write finishes.</summary>
    internal Task Completion => _completion.Task;

    /// <summary>Closes admission, cancels admitted cancellable work and stops library observation.</summary>
    internal void CloseAdmission()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_started)
            {
                LibraryTabManager.Changed -= OnLibraryChanged;
                _started = false;
            }

            if (_activeOperations == 0)
            {
                _completion.TrySetResult();
            }
        }

        _stop.Cancel();
    }

    /// <summary>Releases the owner after its session has joined Completion.</summary>
    public void Dispose()
    {
        CloseAdmission();
        if (Completion.IsCompleted)
        {
            _stop.Dispose();
        }
    }

    /// <summary>Follows the manager's changes and takes the first reading. The session calls this once.</summary>
    internal void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
            LibraryTabManager.Changed += OnLibraryChanged;
        }

        RequestRefresh();
    }

    /// <summary>The configuration was reloaded from disk; the projection follows it.</summary>
    internal void ConfigurationChanged()
    {
        RequestRefresh();
    }

    /// <summary>The card manager as it is now.</summary>
    /// <returns>The state; cheap, no I/O.</returns>
    internal SteamCardManagerState ReadCardState()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return new SteamCardManagerState(
            snapshot.Cards, snapshot.Loaded, Volatile.Read(ref _busy) > 0, snapshot.Error, snapshot.Notice,
            snapshot.CardGames, Revision);
    }

    /// <summary>The tab manager as it is now.</summary>
    /// <returns>The state; cheap, no I/O.</returns>
    internal SteamLibraryTabsState ReadTabsState()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return new SteamLibraryTabsState(
            snapshot.Tabs, snapshot.Order, FilterKinds, snapshot.CardChoices, snapshot.Tags, snapshot.Collections,
            snapshot.Games, snapshot.Loaded, Volatile.Read(ref _busy) > 0, snapshot.Error, snapshot.Notice,
            Revision);
    }

    /// <summary>Scans the drives and rereads the configuration; concurrent requests share one run.</summary>
    /// <param name="cancellationToken">Cancels waiting for the run, not the run itself.</param>
    /// <returns>Completion of a refresh that started at or after this call.</returns>
    internal Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        return QueueRefresh(scan: true).WaitAsync(cancellationToken);
    }

    /// <summary>Schedules a refresh without waiting for it.</summary>
    internal void RequestRefresh()
    {
        Log.Observe(RefreshAsync(), "Library manager refresh", true);
    }

    // After a command: its write is in configuration, and the drives have not changed because of it,
    // so the projection rereads the configuration and keeps the last reading of which cards are in.
    private Task ReprojectAsync(CancellationToken cancellationToken)
    {
        return QueueRefresh(reread: true).WaitAsync(cancellationToken);
    }

    private Task QueueRefresh(bool scan = false, bool reread = false, LibraryReading? reading = null)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            _scanPending |= scan;
            _rereadPending |= reread;
            _syncReading = reading ?? _syncReading;
            if (_refreshRun is null)
            {
                _activeOperations++;
                _refreshRun = Task.Run(async () =>
                {
                    try
                    {
                        await RefreshLoopAsync(_stop.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        EndOperation();
                    }
                }, CancellationToken.None);
            }

            return _refreshRun;
        }
    }

    /// <summary>Clears the error and the notice.</summary>
    /// <param name="cancellationToken">Unused; the change is immediate.</param>
    /// <returns>Applied.</returns>
    internal Task<SteamUiCommandResult> DismissAsync(CancellationToken cancellationToken = default)
    {
        Mutate(static snapshot => snapshot with { Error = null, Notice = null });
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    // ---- Cards ----

    /// <summary>Renames a card everywhere it has a name, through the manager's one rename path.</summary>
    /// <param name="contentId">The card.</param>
    /// <param name="name">The new name.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>
    ///     Failure with the manager's reason when nothing was renamed; success otherwise, with any
    ///     partial-write note published as the notice.
    /// </returns>
    internal Task<SteamUiCommandResult> RenameCardAsync(string contentId, string name,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(async token =>
        {
            var trimmed = name.Trim();
            var note = await LibraryTabManager.RenameCardAsync(_store, _steam(), contentId, trimmed, token)
                .ConfigureAwait(false);
            await ReprojectAsync(token).ConfigureAwait(false);
            if (note is null)
            {
                return SteamUiCommandResult.Applied;
            }

            // The manager answers a refusal and a partial write with the same note; the stored name
            // tells them apart, and a refusal is reported as the failure it is.
            var renamed = Volatile.Read(ref _snapshot).Cards.Any(card =>
                card.ContentId == contentId && string.Equals(card.Name, trimmed, StringComparison.Ordinal));
            if (!renamed)
            {
                return new SteamUiCommandResult(false, note);
            }

            Mutate(snapshot => snapshot with { Notice = note });
            return SteamUiCommandResult.Applied;
        }, "rename", cancellationToken);
    }

    /// <summary>Keeps or drops the card's Steam tab.</summary>
    /// <param name="contentId">The card.</param>
    /// <param name="enabled">Whether to maintain a tab.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether the preference was saved.</returns>
    internal Task<SteamUiCommandResult> SetCardEnabledAsync(string contentId, bool enabled,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(async token =>
        {
            await LibraryTabManager.SetCardEnabledAsync(_store, _steam(), contentId, enabled, token)
                .ConfigureAwait(false);
            await ReprojectAsync(token).ConfigureAwait(false);
            return SteamUiCommandResult.Applied;
        }, "tab switch", cancellationToken);
    }

    /// <summary>Hides a card from tab creation, or shows it again.</summary>
    /// <param name="contentId">The card.</param>
    /// <param name="hidden">Whether to hide it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether the preference was saved.</returns>
    internal Task<SteamUiCommandResult> SetCardHiddenAsync(string contentId, bool hidden,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(async token =>
        {
            await LibraryTabManager.SetCardHiddenAsync(_store, _steam(), contentId, hidden, token)
                .ConfigureAwait(false);
            await ReprojectAsync(token).ConfigureAwait(false);
            return SteamUiCommandResult.Applied;
        }, "hide", cancellationToken);
    }

    /// <summary>Forgets a card: its tab and its tracking. A reinserted card is rediscovered fresh.</summary>
    /// <param name="contentId">The card.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether the card was removed from tracking.</returns>
    internal Task<SteamUiCommandResult> ForgetCardAsync(string contentId, CancellationToken cancellationToken = default)
    {
        return RunAsync(async token =>
        {
            await LibraryTabManager.ForgetCardAsync(_store, _steam(), contentId, token)
                .ConfigureAwait(false);
            // The inspected card's game list dies with the card.
            Mutate(snapshot => snapshot.CardGames?.ContentId == contentId
                ? snapshot with { CardGames = null }
                : snapshot);
            await ReprojectAsync(token).ConfigureAwait(false);
            return SteamUiCommandResult.Applied;
        }, "forget", cancellationToken);
    }

    /// <summary>Names the games on a card from Steam's library; the ids alone when it did not answer.</summary>
    /// <param name="contentId">The card.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The list, also published as the card state's <see cref="SteamCardManagerState.Games" />.</returns>
    internal Task<LibraryCardGames> LoadCardGamesAsync(string contentId,
        CancellationToken cancellationToken = default)
    {
        return WorkAsync(token => LoadCardGamesCoreAsync(contentId, token), cancellationToken);
    }

    private async Task<LibraryCardGames> LoadCardGamesCoreAsync(string contentId,
        CancellationToken cancellationToken)
    {
        var card = Volatile.Read(ref _snapshot).Cards.FirstOrDefault(c => c.ContentId == contentId);
        if (card is null)
        {
            var missing = new LibraryCardGames(contentId, [], false, "That card is no longer tracked.");
            Mutate(snapshot => snapshot with { CardGames = missing });
            return missing;
        }

        var ids = card.AppIds;
        Mutate(snapshot => snapshot with
        {
            CardGames = new LibraryCardGames(contentId, Named(ids, null), true, null)
        });
        var read = await OverlayLibraryLookup.ReadAsync(_steam(), cancellationToken).ConfigureAwait(false);
        var names = read.Games.ToDictionary(game => (long)game.AppId, game => game.Name);
        var loaded = new LibraryCardGames(contentId, Named(ids, names), false, read.Error);
        Mutate(snapshot => snapshot with { CardGames = loaded });
        return loaded;
    }

    // ---- Tabs ----

    /// <summary>Saves a custom tab, new or edited, then rebuilds Steam's tabs in the background.</summary>
    /// <param name="tab">The tab as the editor holds it. A blank id makes a new tab.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Failure with the reason for an incomplete tab; success once it is stored.</returns>
    internal Task<SteamUiCommandResult> SaveTabAsync(CustomTabConfig tab, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return RunAsync(async token =>
        {
            var name = tab.Name.Trim();
            if (name.Length == 0)
            {
                return new SteamUiCommandResult(false, "A tab needs a name.");
            }

            var filters = tab.FilterTree.Children;
            if (filters.Count == 0 || !filters.All(LibraryFilter.IsValid))
            {
                return new SteamUiCommandResult(false, "Finish every filter first.");
            }

            var stored = Volatile.Read(ref _snapshot).Tabs;
            var existing = string.IsNullOrWhiteSpace(tab.Id)
                ? null
                : stored.FirstOrDefault(t => string.Equals(t.Id, tab.Id, StringComparison.Ordinal));
            var saved = new CustomTabConfig
            {
                Id = existing?.Id ?? (string.IsNullOrWhiteSpace(tab.Id) ? Guid.NewGuid().ToString("N") : tab.Id),
                Name = name,
                Enabled = tab.Enabled,
                Position = existing?.Position ?? (stored.Count == 0 ? 0 : stored.Max(t => t.Position) + 1),
                Categories = tab.Categories,
                FilterTree = tab.FilterTree.Clone()
            };
            await LibraryTabManager.SaveCustomTabsAsync(_store, _steam(), [saved], new HashSet<string>())
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await ReprojectAsync(token).ConfigureAwait(false);
            return SteamUiCommandResult.Applied;
        }, "save tab", cancellationToken);
    }

    /// <summary>Deletes a custom tab, then rebuilds Steam's tabs in the background.</summary>
    /// <param name="id">The tab's id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Success once it is gone; failure for an id that is not stored.</returns>
    internal Task<SteamUiCommandResult> DeleteTabAsync(string id, CancellationToken cancellationToken = default)
    {
        return RunAsync(async token =>
        {
            if (Volatile.Read(ref _snapshot).Tabs.All(t => !string.Equals(t.Id, id, StringComparison.Ordinal)))
            {
                return new SteamUiCommandResult(false, "That tab is no longer there.");
            }

            await LibraryTabManager.SaveCustomTabsAsync(_store, _steam(), [],
                new HashSet<string>(StringComparer.Ordinal) { id }).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await ReprojectAsync(token).ConfigureAwait(false);
            return SteamUiCommandResult.Applied;
        }, "delete tab", cancellationToken);
    }

    /// <summary>Moves a tab one place in the strip; the order is saved and Steam follows, debounced.</summary>
    /// <param name="key">The tab's key.</param>
    /// <param name="delta">-1 for earlier, +1 for later.</param>
    /// <param name="cancellationToken">Unused; the order is applied at once and the latest one is saved when the presses pause.</param>
    /// <returns>Failure when the tab is not in the strip or cannot move that way.</returns>
    internal Task<SteamUiCommandResult> MoveTabAsync(string key, int delta, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ReorderTabs(key, order =>
        {
            var index = order.FindIndex(entry => string.Equals(entry.Key, key, StringComparison.Ordinal));
            var target = index + Math.Sign(delta);
            if (index < 0 || target < 0 || target >= order.Count)
            {
                return false;
            }

            (order[index], order[target]) = (order[target], order[index]);
            return true;
        }));
    }

    /// <summary>Hides one of Steam's own tabs from the strip, or puts it back.</summary>
    /// <param name="key">The native tab's id.</param>
    /// <param name="hidden">Whether to hide it.</param>
    /// <param name="cancellationToken">Unused; the order is applied at once and the latest one is saved when the presses pause.</param>
    /// <returns>Failure for a tab that is not native or not in the strip.</returns>
    internal Task<SteamUiCommandResult> SetNativeTabHiddenAsync(string key, bool hidden,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ReorderTabs(key, order =>
        {
            var index = order.FindIndex(entry => string.Equals(entry.Key, key, StringComparison.Ordinal));
            if (index < 0 || order[index].Kind != "native")
            {
                return false;
            }

            order[index] = order[index] with { Hidden = hidden };
            return true;
        }));
    }

    /// <summary>Reads Steam's store tags for the Tag filter.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The picker, also published; a failed read keeps the previous items and names the error.</returns>
    internal Task<LibraryTagPicker> LoadTagsAsync(CancellationToken cancellationToken = default)
    {
        return WorkAsync(LoadTagsCoreAsync, cancellationToken);
    }

    private async Task<LibraryTagPicker> LoadTagsCoreAsync(CancellationToken cancellationToken)
    {
        Mutate(snapshot => snapshot with { Tags = snapshot.Tags with { Loading = true, Error = null } });
        LibraryTagPicker result;
        if (_steam() is { } steam)
        {
            try
            {
                var read = await steam.Library.ReadStoreTagsAsync(cancellationToken).ConfigureAwait(false);
                var previous = Volatile.Read(ref _snapshot).Tags;
                result = read is { Succeeded: true, Value: { } tags }
                    ? new LibraryTagPicker([.. tags.Select(t => new LibraryTagChoice(t.TagId, t.Name, t.Count))],
                        true, false, null)
                    : previous with { Loading = false, Error = read.Error ?? "Steam did not list its tags." };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = Volatile.Read(ref _snapshot).Tags with { Loading = false, Error = ex.Message };
            }
        }
        else
        {
            result = Volatile.Read(ref _snapshot).Tags with { Loading = false, Error = "Steam is not available here." };
        }

        Mutate(snapshot => snapshot with { Tags = result });
        return result;
    }

    /// <summary>Reads Steam's collections for the Collection filter.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The picker, also published; a failed read keeps the previous items and names the error.</returns>
    internal Task<LibraryCollectionPicker> LoadCollectionsAsync(CancellationToken cancellationToken = default)
    {
        return WorkAsync(LoadCollectionsCoreAsync, cancellationToken);
    }

    private async Task<LibraryCollectionPicker> LoadCollectionsCoreAsync(CancellationToken cancellationToken)
    {
        Mutate(snapshot => snapshot with
        {
            Collections = snapshot.Collections with { Loading = true, Error = null }
        });
        LibraryCollectionPicker result;
        if (_steam() is { } steam)
        {
            try
            {
                var read = await steam.Library.ReadCollectionsAsync(cancellationToken).ConfigureAwait(false);
                var previous = Volatile.Read(ref _snapshot).Collections;
                result = read is { Succeeded: true, Value: { } collections }
                    ? new LibraryCollectionPicker(
                        [.. collections.Select(c => new LibraryCollectionChoice(c.Id, c.Name, c.AppIds.Count))],
                        true, false, null)
                    : previous with
                    {
                        Loading = false, Error = read.Error ?? "Steam did not list its collections."
                    };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = Volatile.Read(ref _snapshot).Collections with { Loading = false, Error = ex.Message };
            }
        }
        else
        {
            result = Volatile.Read(ref _snapshot).Collections with
            {
                Loading = false, Error = "Steam is not available here."
            };
        }

        Mutate(snapshot => snapshot with { Collections = result });
        return result;
    }

    /// <summary>Reads the library for the whitelist and blacklist filters.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The picker, also published; a failed read keeps the previous items and names the error.</returns>
    internal Task<LibraryGamePicker> LoadGamesAsync(CancellationToken cancellationToken = default)
    {
        return WorkAsync(LoadGamesCoreAsync, cancellationToken);
    }

    private async Task<LibraryGamePicker> LoadGamesCoreAsync(CancellationToken cancellationToken)
    {
        Mutate(snapshot => snapshot with { Games = snapshot.Games with { Loading = true, Error = null } });
        var read = await OverlayLibraryLookup.ReadAsync(_steam(), cancellationToken).ConfigureAwait(false);
        var previous = Volatile.Read(ref _snapshot).Games;
        var result = read.Succeeded
            ? new LibraryGamePicker([.. read.Games.Select(g => new LibraryGameChoice(g.AppId, g.Name))], true,
                false, null)
            : previous with { Loading = false, Error = read.Error };
        Mutate(snapshot => snapshot with { Games = result });
        return result;
    }

    /// <summary>Formats a byte count the way both managers print capacity.</summary>
    /// <param name="bytes">The count.</param>
    /// <returns>Gigabytes with one decimal, or megabytes below one gigabyte.</returns>
    internal static string FormatBytes(long bytes)
    {
        const double gigabyte = 1024d * 1024 * 1024;
        return bytes >= gigabyte
            ? (bytes / gigabyte).ToString("0.#", CultureInfo.InvariantCulture) + " GB"
            : (bytes / (1024d * 1024)).ToString("0", CultureInfo.InvariantCulture) + " MB";
    }

    private static LibraryTabFilterKind Kind(FilterKind kind, string label, string description)
    {
        return new LibraryTabFilterKind(kind.ToString(), label, description, LibraryFilter.CanInvert(kind));
    }

    private static IReadOnlyList<LibraryCardGame> Named(IReadOnlyList<long> ids, Dictionary<long, string>? names)
    {
        return [.. ids.Select(id => new LibraryCardGame(id,
            names is not null && names.TryGetValue(id, out var name)
                ? name
                : id.ToString(CultureInfo.InvariantCulture)))];
    }

    private SteamUiCommandResult ReorderTabs(string key, Func<List<LibraryTabOrderView>, bool> apply)
    {
        List<LibraryTabOrderView> order;
        bool startWriter;
        lock (_gate)
        {
            if (_disposed)
            {
                return SteamUiCommandResult.Refused;
            }

            order = [.. _snapshot.Order];
            if (!apply(order))
            {
                return new SteamUiCommandResult(false, "That tab cannot be moved there.");
            }

            _snapshot = _snapshot with { Order = order, Error = null };
            // Each press replaces the pending order; the one writer saves whichever is newest.
            _pendingOrder = new TabOrderPress(
                [.. order.Select(entry => entry.Key)],
                [.. order.Where(entry => entry is { Kind: "native", Hidden: true }).Select(entry => entry.Key)]);
            startWriter = !_orderWriterRunning;
            if (startWriter)
            {
                _orderWriterRunning = true;
                _activeOperations++;
            }

            Interlocked.Increment(ref _revision);
        }

        if (startWriter)
        {
            Log.Observe(WriteTabOrderAsync(), "Library tab order write", true);
        }

        RaiseChanged();
        return SteamUiCommandResult.Applied;
    }

    // The one tab-order writer: waits until the presses pause, saves the newest order once and shows
    // it in Steam, and runs again while newer presses arrived meanwhile. Closing skips the wait and the
    // push, so the last press is still saved.
    private async Task WriteTabOrderAsync()
    {
        try
        {
            while (true)
            {
                TabOrderPress? waited;
                lock (_gate)
                {
                    waited = _pendingOrder;
                }

                try
                {
                    await Task.Delay(TabOrderWriteDelay, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                TabOrderPress press;
                lock (_gate)
                {
                    if (!ReferenceEquals(_pendingOrder, waited) && !_stop.IsCancellationRequested)
                    {
                        // A newer press restarts the wait.
                        continue;
                    }

                    press = _pendingOrder!;
                    _pendingOrder = null;
                }

                var saved = false;
                try
                {
                    await LibraryTabManager.SaveTabOrderAsync(_store, press.Keys, press.Hidden).ConfigureAwait(false);
                    saved = true;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Library tab order save failed: {ex.Message}");
                }

                if (saved && !_stop.IsCancellationRequested && _steam() is { } steam)
                {
                    await LibraryTabManager.PushTabOrderAsync(_store, steam, press.Keys, press.Hidden, _stop.Token)
                        .ConfigureAwait(false);
                }

                lock (_gate)
                {
                    if (_pendingOrder is null)
                    {
                        _orderWriterRunning = false;
                        break;
                    }
                }
            }
        }
        finally
        {
            EndOperation();
        }

        // The projection follows the stored order again, or shows that a failed save left it unchanged.
        Log.Observe(QueueRefresh(reread: true), "Library manager refresh", true);
    }

    private async Task<T> WorkAsync<T>(Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                throw new OperationCanceledException("Library management is stopping.");
            }

            _activeOperations++;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            linked.Token.ThrowIfCancellationRequested();
            return await operation(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            EndOperation();
        }
    }

    private void EndOperation()
    {
        lock (_gate)
        {
            _activeOperations--;
            if (_disposed && _activeOperations == 0)
            {
                _completion.TrySetResult();
            }
        }
    }

    private Task<SteamUiCommandResult> RunAsync(Func<CancellationToken, Task<SteamUiCommandResult>> operation,
        string name, CancellationToken cancellationToken)
    {
        return WorkAsync(token => RunCommandAsync(operation, name, token), cancellationToken);
    }

    private async Task<SteamUiCommandResult> RunCommandAsync(
        Func<CancellationToken, Task<SteamUiCommandResult>> operation, string name, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _busy);
        Interlocked.Increment(ref _revision);
        RaiseChanged();
        try
        {
            var result = await operation(cancellationToken).ConfigureAwait(false);
            Mutate(snapshot => snapshot with { Error = result.Succeeded ? null : result.Error });
            return result;
        }
        catch (OperationCanceledException)
        {
            return new SteamUiCommandResult(false, "The change was cancelled.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Library manager {name} failed: {ex.Message}");
            var message = "Could not save the change. Try again.";
            Mutate(snapshot => snapshot with { Error = message });
            return new SteamUiCommandResult(false, message);
        }
        finally
        {
            Interlocked.Decrement(ref _busy);
            Interlocked.Increment(ref _revision);
            RaiseChanged();
        }
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            bool scan;
            bool reread;
            LibraryReading? reading;
            lock (_gate)
            {
                if ((!_scanPending && !_rereadPending && _syncReading is null) || _disposed)
                {
                    _refreshRun = null;
                    return;
                }

                scan = _scanPending;
                reread = _rereadPending;
                reading = _syncReading;
                _scanPending = false;
                _rereadPending = false;
                _syncReading = null;
            }

            await ReadOnceAsync(scan, reread, reading, cancellationToken).ConfigureAwait(false);
        }
    }

    // A scan merges the drives into configuration first. Otherwise the projection comes from the
    // configuration, reread or as a sync saved it, and from the sync's or the last reading's present cards.
    private async Task ReadOnceAsync(bool scan, bool reread, LibraryReading? reading,
        CancellationToken cancellationToken)
    {
        try
        {
            var cards = scan
                ? await LibraryTabManager.ListCardsAsync(_store, cancellationToken).ConfigureAwait(false)
                : null;
            var config = scan || reread || reading is null
                ? await Task.Run(() => _store.Read().Config, cancellationToken).ConfigureAwait(false)
                : reading.Config;
            if (config is null)
            {
                Mutate(snapshot => snapshot with { Error = "Couldn't read your configuration." });
                return;
            }

            var views = cards ?? LibraryTabManager.ProjectCards(config, reading?.Present ?? PresentCards());
            var tabs = config.CustomTabs.OrderBy(tab => tab.Position).Select(Clone).ToList();
            var order = LibraryTabManager.BuildTabOrder(config);
            var choices = config.CardLibraries.Select(card => new LibraryTabCardChoice(card.ContentId, card.Name))
                .ToList();
            var loaded = scan || reading is not null;
            Mutate(snapshot => snapshot with
            {
                Cards = views,
                Tabs = tabs,
                // While the order writer runs, the stored order is older than the one the user
                // already pressed to; the writer refreshes again when it is done.
                Order = _orderWriterRunning ? snapshot.Order : order,
                CardChoices = choices,
                Loaded = snapshot.Loaded || loaded,
                CardGames = snapshot.CardGames is { } games && views.All(card => card.ContentId != games.ContentId)
                    ? null
                    : snapshot.CardGames
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Library manager refresh failed: {ex.Message}");
            // Never dress a read failure up as "no cards": that invites re-formatting a card that
            // is in fact still tracked.
            Mutate(snapshot => snapshot with { Error = "Couldn't read your cards. Try again." });
        }
    }

    private static CustomTabConfig Clone(CustomTabConfig tab)
    {
        return new CustomTabConfig
        {
            Id = tab.Id,
            Name = tab.Name,
            Enabled = tab.Enabled,
            Position = tab.Position,
            Categories = tab.Categories,
            FilterTree = tab.FilterTree.Clone()
        };
    }

    private void Mutate(Func<Snapshot, Snapshot> change)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var next = change(_snapshot);
            if (ReferenceEquals(next, _snapshot))
            {
                return;
            }

            _snapshot = next;
            Interlocked.Increment(ref _revision);
        }

        RaiseChanged();
    }

    // A sync just scanned and saved: project its reading instead of scanning the drives again.
    private void OnLibraryChanged(AppConfig config, IReadOnlySet<string> present)
    {
        Log.Observe(QueueRefresh(reading: new LibraryReading(config, present)), "Library manager refresh", true);
    }

    // The cards the last reading found mounted, for a projection that did not scan.
    private HashSet<string> PresentCards()
    {
        return Volatile.Read(ref _snapshot).Cards.Where(static card => card.Inserted)
            .Select(static card => card.ContentId).ToHashSet(StringComparer.Ordinal);
    }

    private void RaiseChanged()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        // Listeners run outside the gate, so a slow one never holds up a command or a refresh.
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn($"Library manager: a change listener failed: {ex.Message}");
        }
    }

    private string CardsDetail()
    {
        var cards = Volatile.Read(ref _snapshot).Cards;
        if (cards.Count == 0)
        {
            return "No SD-card libraries tracked yet.";
        }

        var inserted = cards.Count(card => card.Inserted);
        return $"{cards.Count} tracked · {inserted} connected";
    }

    private string TabsDetail()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var custom = snapshot.Tabs.Count;
        var cardTabs = snapshot.Order.Count(entry => entry.Kind == "card");
        var hidden = snapshot.Order.Count(entry => entry is { Kind: "native", Hidden: true });
        return $"{custom} custom · {cardTabs} card · {hidden} Steam tabs hidden";
    }

    /// <summary>The whole projection, replaced as one so a reader never sees half a change.</summary>
    private sealed record Snapshot(
        IReadOnlyList<LibraryCardView> Cards,
        IReadOnlyList<CustomTabConfig> Tabs,
        IReadOnlyList<LibraryTabOrderView> Order,
        IReadOnlyList<LibraryTabCardChoice> CardChoices,
        LibraryTagPicker Tags,
        LibraryCollectionPicker Collections,
        LibraryGamePicker Games,
        LibraryCardGames? CardGames,
        bool Loaded,
        string? Error,
        string? Notice)
    {
        internal static Snapshot Empty { get; } = new(
            [], [], [], [],
            new LibraryTagPicker([], false, false, null),
            new LibraryCollectionPicker([], false, false, null),
            new LibraryGamePicker([], false, false, null),
            null, false, null, null);
    }

    /// <summary>A tab order the user pressed to, waiting for the writer.</summary>
    /// <param name="Keys">Every tab key, left to right.</param>
    /// <param name="Hidden">The native tabs left out of the strip.</param>
    private sealed record TabOrderPress(List<string> Keys, List<string> Hidden);

    /// <summary>What a finished sync saved and found, projected without another scan.</summary>
    /// <param name="Config">The configuration the sync saved.</param>
    /// <param name="Present">The content ids of the cards its scan found mounted.</param>
    private sealed record LibraryReading(AppConfig Config, IReadOnlySet<string> Present);

    /// <summary>One Quick Access Extensions entry: a row with the action that opens its page.</summary>
    private sealed class ExtensionsSection(
        LibraryManagerService service,
        string sectionId,
        string openId,
        string route,
        string name,
        string openLabel,
        Func<LibraryManagerService, bool> available,
        Func<LibraryManagerService, string> detail) : IExtensionsTabSection
    {
        public string SectionId => sectionId;

        public SteamExtensionsTabItem? ReadExtensionsItem()
        {
            if (!available(service))
            {
                return null;
            }

            return new SteamExtensionsTabItem(
                sectionId,
                name,
                string.Empty,
                Volatile.Read(ref service._busy) > 0 ? "Working…" : "Ready",
                detail(service),
                [new SteamExtensionsTabAction(openId, openLabel)]);
        }

        public Task<SteamUiCommandResult> ActivateExtensionAsync(string id, CancellationToken cancellationToken)
        {
            if (id != openId || !available(service))
            {
                return Task.FromResult(new SteamUiCommandResult(false, "That entry is no longer available."));
            }

            // The page shows what the last reading had and a fresh scan replaces it on arrival.
            service.RequestRefresh();
            return Task.FromResult(SteamUiCommandResult.Route(route));
        }

        public Task<SteamUiCommandResult> ConfigureExtensionAsync(string key, JsonElement value,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new SteamUiCommandResult(false, "That setting is no longer available."));
        }
    }
}

/// <summary>Serializes the two manager states for Steam and reads a custom tab sent back from its editor.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SteamCardManagerState))]
[JsonSerializable(typeof(SteamLibraryTabsState))]
[JsonSerializable(typeof(CustomTabConfig))]
internal sealed partial class LibraryManagerJsonContext : JsonSerializerContext;
