using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     The one owner of the SD Card Manager, the Library Tab Manager and Steam's library-tab syncs, on the
///     overlay and in Steam's Quick Access. It projects the <see cref="SdCardLibraryCatalog" />'s card
///     model, the custom tabs and the tab order into two states, answers both surfaces' operations
///     through the same persistence, and runs every <see cref="LibraryTabManager" /> sync, so its stop,
///     active operations and completion cover syncs, order writes and pushes alike.
/// </summary>
/// <remarks>
///     Reading state is cheap and lock-free: the session host reads it on every publication. Drive
///     scans run only on an explicit refresh and at start, never on a timer; a change rereads the
///     configuration, and a finished sync hands over the scan it already made. Every operation
///     validates once here, so neither surface carries its own copy of the rules.
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

    private readonly SdCardLibraryCatalog _cards;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<AppConfig> _config;
    private readonly Lock _gate = new();
    private readonly LibraryTabOrderWriter _orderWriter;
    private readonly Func<SteamClient?> _steam;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConfigStore _store;
    private readonly LibraryTabManager _tabs;
    private int _activeOperations;
    private int _busy;
    private bool _disposed;
    private Task? _refreshRun;
    private bool _rereadPending;
    private long _revision;

    // What the next refresh run does: a drive scan, a configuration reread, or the reading a
    // finished sync handed over. A scan covers both of the others.
    private bool _scanPending;
    private LibraryManagerSnapshot _snapshot = LibraryManagerSnapshot.Empty;
    private LibraryReading? _syncReading;

    /// <summary>Creates the service over the session's persistence and Steam client.</summary>
    /// <param name="store">The configuration the cards and tabs are kept in.</param>
    /// <param name="steam">Returns the session's Steam client, or null on a surface without one.</param>
    /// <param name="config">Returns the live configuration, for the feature switches.</param>
    internal LibraryManagerService(ConfigStore store, Func<SteamClient?> steam, Func<AppConfig> config)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _steam = steam ?? throw new ArgumentNullException(nameof(steam));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _cards = new SdCardLibraryCatalog(store);
        _tabs = new LibraryTabManager(store);
        _orderWriter = new LibraryTabOrderWriter(_gate, _tabs, steam, SyncQuietlyAsync, _stop.Token);
        CardsSection = new LibraryManagerSection(this, CardsId, OpenCardsId, SteamCardManagerSurface.Route,
            "SD Card Manager", "Open SD Card Manager", service => service.CardManagerAvailable,
            service => service.CardsDetail());
        TabsSection = new LibraryManagerSection(this, TabsId, OpenTabsId, SteamLibraryTabsSurface.Route,
            "Library Tab Manager", "Open Library Tab Manager", service => service.TabManagerAvailable,
            service => service.TabsDetail());
    }

    /// <summary>The revision both states carry.</summary>
    internal long Revision => Interlocked.Read(ref _revision);

    /// <summary>Whether a command is in flight.</summary>
    internal bool Busy => Volatile.Read(ref _busy) > 0;

    /// <summary>Whether the card manager may be offered: CEF on and the card manager switch on.</summary>
    internal bool CardManagerAvailable => _config().Cef is { Enabled: true, CardManager: true };

    /// <summary>Whether the tab manager may be offered: CEF on and the library tabs switch on.</summary>
    internal bool TabManagerAvailable => _config().Cef is { Enabled: true, LibraryTabs: true };

    /// <summary>The Quick Access Extensions entry that opens the card manager, absent while unavailable.</summary>
    internal IExtensionsTabSection CardsSection { get; }

    /// <summary>The Quick Access Extensions entry that opens the tab manager, absent while unavailable.</summary>
    internal IExtensionsTabSection TabsSection { get; }

    /// <summary>
    ///     Completes after admission closes and every admitted read, command, tab sync and order write
    ///     finishes.
    /// </summary>
    internal Task Completion => _completion.Task;

    /// <summary>Releases the owner after its session has joined Completion.</summary>
    public void Dispose()
    {
        CloseAdmission();
        if (Completion.IsCompleted)
        {
            _stop.Dispose();
        }
    }

    /// <summary>Raised after either state changed; handlers must not block.</summary>
    public event Action? Changed;

    /// <summary>Closes admission and cancels admitted cancellable work, tab syncs included.</summary>
    internal void CloseAdmission()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_activeOperations == 0)
            {
                _completion.TrySetResult();
            }
        }

        _stop.Cancel();
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
            snapshot.Cards, snapshot.Loaded, Busy, snapshot.Error, snapshot.Notice, snapshot.CardGames, Revision);
    }

    /// <summary>The tab manager as it is now.</summary>
    /// <returns>The state; cheap, no I/O.</returns>
    internal SteamLibraryTabsState ReadTabsState()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return new SteamLibraryTabsState(
            snapshot.Tabs, snapshot.Order, LibraryTabFilterKind.All, snapshot.CardChoices, snapshot.Tags,
            snapshot.Collections, snapshot.Games, snapshot.Loaded, Busy, snapshot.Error, snapshot.Notice,
            Revision);
    }

    /// <inheritdoc />
    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        return QueueRefresh(scan: true).WaitAsync(cancellationToken);
    }

    /// <summary>Schedules a refresh without waiting for it; the session's first one takes the first reading.</summary>
    internal void RequestRefresh()
    {
        Log.Observe(RefreshAsync(), "Library manager refresh", true);
    }

    /// <summary>Synchronizes Steam's library tabs now, for a library on a card that changed.</summary>
    /// <param name="cancellationToken">Cancels the sync; the manager's stop cancels it too.</param>
    /// <returns>The sync's outcome; a refusal once the manager stops, or without a Steam client.</returns>
    internal async Task<LibraryTabSyncResult> SyncTabsAsync(CancellationToken cancellationToken)
    {
        if (_steam() is not { } steam)
        {
            return new LibraryTabSyncResult("Steam is not available here.", false);
        }

        if (!TryBeginOperation())
        {
            return new LibraryTabSyncResult("Library tab sync cancelled.", false);
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            return await SyncCoreAsync(steam, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>
    ///     Syncs once the Steam UI transport opens after a cold boot, a Steam restart or a return to
    ///     game mode, so tabs appear without any user action. In game mode the session's transport gate
    ///     opens when the Big Picture window is up; inside Steam, the sync then waits for the library
    ///     stores and WSGM's tab claim. A sync that does not place the tabs waits for the transport's
    ///     next ready edge, and any card or builder change syncs in the meantime.
    /// </summary>
    /// <param name="readiness">The session's Steam UI readiness the sync waits on.</param>
    /// <param name="cancellationToken">Cancels the wait; the manager's stop cancels it too.</param>
    /// <returns>Completion after a successful ready-state sync, or when the readiness policy or the stop ended it.</returns>
    internal async Task SyncTabsOnBootAsync(SteamUiReadiness readiness, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        if (_steam() is not { } steam || !TryBeginOperation())
        {
            return;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            _ = await readiness.RunWhenReadyAsync(
                "Library tabs (boot)",
                async token =>
                {
                    var result = await SyncCoreAsync(steam, token).ConfigureAwait(false);
                    Log.Info($"Library tabs (boot): {result.Summary}");
                    // A half-initialized appStore can be reachable but reject a filter; only a sync that
                    // reached Steam and placed the tabs is done. The badge needs no retry of its own: its
                    // reading is published through the patch lifecycle, which reaches Steam when the
                    // bridge does.
                    return result.Success;
                }, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested
                                                 && !cancellationToken.IsCancellationRequested)
        {
            // The manager is stopping; the session joins its completion.
        }
        finally
        {
            EndOperation();
        }
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
            var (renamed, note) = await _cards.RenameCardAsync(_steam(), contentId, trimmed, token)
                .ConfigureAwait(false);
            if (renamed)
            {
                StartQuietSync("card manager");
            }

            await ReprojectAsync(token).ConfigureAwait(false);
            if (note is null)
            {
                return SteamUiCommandResult.Applied;
            }

            // The catalog answers a refusal and a partial write with the same kind of note; the stored
            // name tells them apart, and a refusal is reported as the failure it is.
            var stored = Volatile.Read(ref _snapshot).Cards.Any(card =>
                card.ContentId == contentId && string.Equals(card.Name, trimmed, StringComparison.Ordinal));
            if (!stored)
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
            await _cards.SetCardEnabledAsync(contentId, enabled, token).ConfigureAwait(false);
            StartQuietSync("card manager");
            await ReprojectAsync(token).ConfigureAwait(false);
            return SteamUiCommandResult.Applied;
        }, "tab switch", cancellationToken);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetCardHiddenAsync(string contentId, bool hidden,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(async token =>
        {
            await _cards.SetCardHiddenAsync(contentId, hidden, token).ConfigureAwait(false);
            StartQuietSync("card manager");
            await ReprojectAsync(token).ConfigureAwait(false);
            return SteamUiCommandResult.Applied;
        }, "hide", cancellationToken);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ForgetCardAsync(string contentId, CancellationToken cancellationToken = default)
    {
        return RunAsync(async token =>
        {
            await _cards.ForgetCardAsync(contentId, token).ConfigureAwait(false);
            StartQuietSync("card manager");
            // The inspected card's game list dies with the card.
            Mutate(snapshot => snapshot.CardGames?.ContentId == contentId
                ? snapshot with { CardGames = null }
                : snapshot);
            await ReprojectAsync(token).ConfigureAwait(false);
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
        Mutate(snapshot => snapshot with { CardGames = LibraryPickerReader.Unnamed(contentId, ids) });
        var loaded = await LibraryPickerReader.ReadCardGamesAsync(_steam(), contentId, ids, cancellationToken)
            .ConfigureAwait(false);
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
            var saved = tab.Copy();
            saved.Id = existing?.Id ?? (string.IsNullOrWhiteSpace(tab.Id) ? Guid.NewGuid().ToString("N") : tab.Id);
            saved.Name = name;
            saved.Position = existing?.Position ?? (stored.Count == 0 ? 0 : stored.Max(t => t.Position) + 1);
            await _tabs.SaveCustomTabAsync(saved).ConfigureAwait(false);
            StartQuietSync("builder");
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

            await _tabs.DeleteCustomTabAsync(id).ConfigureAwait(false);
            StartQuietSync("builder");
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
        return Task.FromResult(ReorderTabs(order =>
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
        return Task.FromResult(ReorderTabs(order =>
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
        return WorkAsync(async token =>
        {
            Mutate(snapshot => snapshot with { Tags = snapshot.Tags with { Loading = true, Error = null } });
            var result = await LibraryPickerReader.ReadTagsAsync(_steam(), () => Volatile.Read(ref _snapshot).Tags,
                token).ConfigureAwait(false);
            Mutate(snapshot => snapshot with { Tags = result });
            return result;
        }, cancellationToken);
    }

    /// <summary>Reads Steam's collections for the Collection filter.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The picker, also published; a failed read keeps the previous items and names the error.</returns>
    internal Task<LibraryCollectionPicker> LoadCollectionsAsync(CancellationToken cancellationToken = default)
    {
        return WorkAsync(async token =>
        {
            Mutate(snapshot => snapshot with
            {
                Collections = snapshot.Collections with { Loading = true, Error = null }
            });
            var result = await LibraryPickerReader.ReadCollectionsAsync(_steam(),
                () => Volatile.Read(ref _snapshot).Collections, token).ConfigureAwait(false);
            Mutate(snapshot => snapshot with { Collections = result });
            return result;
        }, cancellationToken);
    }

    /// <summary>Reads the library for the whitelist and blacklist filters.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The picker, also published; a failed read keeps the previous items and names the error.</returns>
    internal Task<LibraryGamePicker> LoadGamesAsync(CancellationToken cancellationToken = default)
    {
        return WorkAsync(async token =>
        {
            Mutate(snapshot => snapshot with { Games = snapshot.Games with { Loading = true, Error = null } });
            var result = await LibraryPickerReader.ReadGamesAsync(_steam(), () => Volatile.Read(ref _snapshot).Games,
                token).ConfigureAwait(false);
            Mutate(snapshot => snapshot with { Games = result });
            return result;
        }, cancellationToken);
    }

    private SteamUiCommandResult ReorderTabs(Func<List<LibraryTabOrderView>, bool> apply)
    {
        bool startWriter;
        lock (_gate)
        {
            if (_disposed)
            {
                return SteamUiCommandResult.Refused;
            }

            List<LibraryTabOrderView> order = [.. _snapshot.Order];
            if (!apply(order))
            {
                return new SteamUiCommandResult(false, "That tab cannot be moved there.");
            }

            _snapshot = _snapshot with { Order = order, Error = null };
            // Each press replaces the pending order; the one writer saves whichever is newest.
            startWriter = _orderWriter.Press(new LibraryTabOrderPress(
                [.. order.Select(entry => entry.Key)],
                [.. order.Where(entry => entry is { Kind: "native", Hidden: true }).Select(entry => entry.Key)]));
            if (startWriter)
            {
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

    private async Task WriteTabOrderAsync()
    {
        try
        {
            await _orderWriter.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            EndOperation();
        }

        // The projection follows the stored order again, or shows that a failed save left it unchanged.
        Log.Observe(QueueRefresh(reread: true), "Library manager refresh", true);
    }

    // A sync that saved a fresh drive reading hands it to the projection instead of a second scan, so
    // both the overlay and Steam follow a card insertion or the sync a change on either surface started.
    private async Task<LibraryTabSyncResult> SyncCoreAsync(SteamClient steam, CancellationToken cancellationToken)
    {
        var result = await _tabs.SyncAsync(steam, cancellationToken).ConfigureAwait(false);
        if (result.Reading is { } reading)
        {
            Log.Observe(QueueRefresh(reading: reading), "Library manager refresh", true);
        }

        return result;
    }

    // A change to what Steam should show re-materializes the tabs in the background, as an admitted
    // operation the session joins at shutdown; a failure waits for the next sync. A surface without a
    // Steam client only saves.
    private void StartQuietSync(string origin)
    {
        if (_steam() is not { } steam || !TryBeginOperation())
        {
            return;
        }

        _ = RunQuietSyncAsync(steam, origin);
    }

    private async Task RunQuietSyncAsync(SteamClient steam, string origin)
    {
        try
        {
            await SyncQuietlyAsync(steam, origin, _stop.Token).ConfigureAwait(false);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task SyncQuietlyAsync(SteamClient steam, string origin, CancellationToken cancellationToken)
    {
        try
        {
            var result = await SyncCoreAsync(steam, cancellationToken).ConfigureAwait(false);
            Log.Info($"Library tabs ({origin}): {result.Summary}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The session is closing.
        }
        catch (Exception ex)
        {
            Log.Warn($"Library-tab sync failed ({origin}): {ex.Message}");
        }
    }

    private bool TryBeginOperation()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _activeOperations++;
            return true;
        }
    }

    private async Task<T> WorkAsync<T>(Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        if (!TryBeginOperation())
        {
            throw new OperationCanceledException("Library management is stopping.");
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
                ? await _cards.ListCardsAsync(cancellationToken).ConfigureAwait(false)
                : null;
            var config = scan || reread || reading is null
                ? await Task.Run(() => _store.Read().Config, cancellationToken).ConfigureAwait(false)
                : reading.Config;
            if (config is null)
            {
                Mutate(snapshot => snapshot with { Error = "Couldn't read your configuration." });
                return;
            }

            var views = cards ?? SdCardLibraryCatalog.ProjectCards(config, reading?.Present ?? PresentCards());
            var tabs = config.CustomTabs.OrderBy(tab => tab.Position).Select(tab => tab.Copy()).ToList();
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
                Order = _orderWriter.Running ? snapshot.Order : order,
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

    private void Mutate(Func<LibraryManagerSnapshot, LibraryManagerSnapshot> change)
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
}
