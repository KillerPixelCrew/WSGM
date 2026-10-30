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
///     scans run only on an explicit refresh, after a change and at start, never on a timer. Every
///     operation validates once here, so neither surface carries its own copy of the rules.
/// </remarks>
internal sealed class LibraryManagerService : ICardManagerSource, IDisposable
{
    /// <summary>The Extensions-tab item and page namespace of the card manager.</summary>
    internal const string CardsId = "wsgm.cards";

    /// <summary>The action that opens the card manager page.</summary>
    internal const string OpenCardsId = "wsgm.cards.open";

    /// <summary>The Extensions-tab item and page namespace of the tab manager.</summary>
    internal const string TabsId = "wsgm.library-tabs";

    /// <summary>The action that opens the tab manager page.</summary>
    internal const string OpenTabsId = "wsgm.library-tabs.open";

    private const string CardTabPrefix = "wsgm-card-";

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

    // The order the user has moved to but whose configuration writes have not all landed yet; a
    // refresh in between keeps it rather than publishing an older order back over the user's press.
    private int _pendingOrderWrites;
    private bool _refreshPending;
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

    /// <inheritdoc />
    public SteamCardManagerState ReadCardState()
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

    /// <inheritdoc />
    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Task run;
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            _refreshPending = true;
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
            run = _refreshRun;
        }

        return run.WaitAsync(cancellationToken);
    }

    /// <summary>Schedules a refresh without waiting for it.</summary>
    internal void RequestRefresh()
    {
        Log.Observe(RefreshAsync(), "Library manager refresh", true);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> DismissAsync(CancellationToken cancellationToken = default)
    {
        Mutate(static snapshot => snapshot with { Error = null, Notice = null });
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    // ---- Cards ----

    /// <inheritdoc />
    public Task<SteamUiCommandResult> RenameCardAsync(string contentId, string name,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(async token =>
        {
            var trimmed = name.Trim();
            var note = await LibraryTabManager.RenameCardAsync(_store, _steam(), contentId, trimmed, token)
                .ConfigureAwait(false);
            await RefreshAsync(token).ConfigureAwait(false);
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

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetCardEnabledAsync(string contentId, bool enabled,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(async token =>
        {
            await LibraryTabManager.SetCardEnabledAsync(_store, _steam(), contentId, enabled, token)
                .ConfigureAwait(false);
            await RefreshAsync(token).ConfigureAwait(false);
            return SteamUiCommandResult.Applied;
        }, "tab switch", cancellationToken);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetCardHiddenAsync(string contentId, bool hidden,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(async token =>
        {
            await LibraryTabManager.SetCardHiddenAsync(_store, _steam(), contentId, hidden, token)
                .ConfigureAwait(false);
            await RefreshAsync(token).ConfigureAwait(false);
            return SteamUiCommandResult.Applied;
        }, "hide", cancellationToken);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ForgetCardAsync(string contentId, CancellationToken cancellationToken = default)
    {
        return RunAsync(async token =>
        {
            await LibraryTabManager.ForgetCardAsync(_store, _steam(), contentId, token)
                .ConfigureAwait(false);
            // The inspected card's game list dies with the card.
            Mutate(snapshot => snapshot.CardGames?.ContentId == contentId
                ? snapshot with { CardGames = null }
                : snapshot);
            await RefreshAsync(token).ConfigureAwait(false);
            return SteamUiCommandResult.Applied;
        }, "forget", cancellationToken);
    }

    /// <inheritdoc />
    public Task<LibraryCardGames> LoadCardGamesAsync(string contentId,
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
            await RefreshAsync(token).ConfigureAwait(false);
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
            await RefreshAsync(token).ConfigureAwait(false);
            return SteamUiCommandResult.Applied;
        }, "delete tab", cancellationToken);
    }

    /// <summary>Moves a tab one place in the strip and saves the order; Steam follows debounced.</summary>
    /// <param name="key">The tab's key.</param>
    /// <param name="delta">-1 for earlier, +1 for later.</param>
    /// <param name="cancellationToken">Unused; the order is applied at once and written in press order.</param>
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
    /// <param name="cancellationToken">Unused; the order is applied at once and written in press order.</param>
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

    /// <summary>The kind the strip shows for a tab key: Steam's own, a card's or a custom one.</summary>
    /// <param name="entry">The manager's order entry.</param>
    /// <returns><c>native</c>, <c>card</c> or <c>custom</c>.</returns>
    internal static string KindOf(LibraryTabManager.TabOrderEntry entry)
    {
        return entry.IsNative ? "native"
            : entry.Key.StartsWith(CardTabPrefix, StringComparison.Ordinal) ? "card" : "custom";
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
            _pendingOrderWrites++;
            _activeOperations++;
            Interlocked.Increment(ref _revision);
        }

        var keys = order.Select(entry => entry.Key).ToList();
        var hidden = order.Where(entry => entry is { Kind: "native", Hidden: true }).Select(entry => entry.Key).ToList();
        Task write;
        try
        {
            write = LibraryTabManager.SaveTabOrder(_store, _steam(), keys, hidden);
        }
        catch
        {
            FinishOrderWrite();
            throw;
        }

        Log.Observe(CompleteOrderWriteAsync(write), "Library tab order write", true);
        RaiseChanged();
        return SteamUiCommandResult.Applied;
    }

    private async Task CompleteOrderWriteAsync(Task write)
    {
        try
        {
            await write.ConfigureAwait(false);
        }
        finally
        {
            FinishOrderWrite();
        }
    }

    private void FinishOrderWrite()
    {
        lock (_gate)
        {
            _pendingOrderWrites--;
        }

        EndOperation();
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
            lock (_gate)
            {
                if (!_refreshPending || _disposed)
                {
                    _refreshRun = null;
                    return;
                }

                _refreshPending = false;
            }

            await ReadOnceAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReadOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var cards = await LibraryTabManager.ListCardsAsync(_store, cancellationToken).ConfigureAwait(false);
            var config = await Task.Run(() => _store.Read().Config, cancellationToken).ConfigureAwait(false);
            if (config is null)
            {
                Mutate(snapshot => snapshot with { Error = "Couldn't read your configuration." });
                return;
            }

            var views = cards.Select(card => new LibraryCardView(
                    card.ContentId, card.Name, card.Enabled, card.Hidden, card.Inserted, card.GameCount,
                    card.AppIds, card.CapacityBytes, card.FreeBytes))
                .ToList();
            var tabs = config.CustomTabs.OrderBy(tab => tab.Position).Select(Clone).ToList();
            var order = LibraryTabManager.BuildTabOrder(config)
                .Select(entry => new LibraryTabOrderView(entry.Key, entry.Title, KindOf(entry), entry.Hidden))
                .ToList();
            var choices = config.CardLibraries.Select(card => new LibraryTabCardChoice(card.ContentId, card.Name))
                .ToList();
            Mutate(snapshot => snapshot with
            {
                Cards = views,
                Tabs = tabs,
                // A refresh between two chained order writes must not put an older order back
                // over the one the user already pressed to.
                Order = _pendingOrderWrites > 0 ? snapshot.Order : order,
                CardChoices = choices,
                Loaded = true,
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

    private void OnLibraryChanged()
    {
        RequestRefresh();
    }

    private void RaiseChanged()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Warn($"Library manager: a change listener failed: {ex.Message}");
            }
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
