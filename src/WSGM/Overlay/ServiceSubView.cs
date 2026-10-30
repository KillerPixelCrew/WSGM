using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using SteamUiToolkit;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Input;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     A sub-view over one session service: it redraws its current level when the service publishes,
///     keeps focus on the row the user was on through that redraw, and runs the service's commands
///     with their refusal shown as a toast. The Game Library and Themes views are two of these.
/// </summary>
/// <remarks>
///     A level that shows running work registers it with <see cref="SetLiveContent{TState}" />. A publication that
///     leaves the level's structure unchanged then updates its <see cref="OverlayProgress" /> element in place instead
///     of running the level again; only a structural change redraws it.
/// </remarks>
public abstract class ServiceSubView : OverlaySubView
{
    private LiveLevel? _live;
    private OverlayWindow? _owner;
    private int _refreshQueued;
    private bool _renderDeferred;
    private int _renderGeneration = -1;
    private IChangeSource? _source;

    /// <summary>Pairs deferred rendering with the owning window's modal lifetime.</summary>
    protected ServiceSubView()
    {
        AttachedToVisualTree += (_, _) =>
        {
            _owner = TopLevel.GetTopLevel(this) as OverlayWindow;
            if (_owner is not null)
            {
                _owner.SurfaceClosed += OnSurfaceClosed;
            }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_owner is not null)
            {
                _owner.SurfaceClosed -= OnSurfaceClosed;
            }

            _owner = null;
        };
    }

    private void OnSurfaceClosed()
    {
        if (_renderDeferred && IsEffectivelyVisible)
        {
            _renderDeferred = false;
            CurrentLevel?.Invoke();
        }
    }

    /// <summary>Whether the current level shows the service's state, so the service's changes redraw it.</summary>
    private protected virtual bool RedrawsOnSourceChange => true;

    /// <summary>Opens the view on its home level.</summary>
    public void Open()
    {
        NavigationStack.Clear();
        CurrentLevel = null;
        NavigationGeneration++;
        Navigate(RenderHome);
    }

    /// <summary>Draws the view's home level.</summary>
    private protected abstract void RenderHome();

    /// <summary>Follows a service's changes, or none with null.</summary>
    /// <param name="source">The service, or null when the overlay closes or the session has none.</param>
    private protected void AttachSource(IChangeSource? source)
    {
        if (_source is not null)
        {
            _source.Changed -= OnSourceChanged;
        }

        _source = source;
        if (source is not null)
        {
            source.Changed += OnSourceChanged;
        }
    }

    /// <summary>
    ///     Sets a level whose service state is partly live: later publications with the same structure update the
    ///     level's <see cref="OverlayProgress" /> element in place, and any other publication redraws the level.
    /// </summary>
    /// <typeparam name="TState">The service state the level was drawn from.</typeparam>
    /// <param name="stack">The level.</param>
    /// <param name="rendered">
    ///     The state the level was drawn from, read before drawing it, so a change published during the draw still
    ///     counts as a change.
    /// </param>
    /// <param name="read">Reads the service's current state on the UI thread; null redraws the level.</param>
    /// <param name="sameStructure">
    ///     Whether two states draw the same level apart from its live progress: the same rows, actions,
    ///     availability, enablement and messages.
    /// </param>
    /// <param name="progress">
    ///     The live progress element's state, or null for a level without one; its publications then change nothing
    ///     until the structure does.
    /// </param>
    /// <remarks>
    ///     The level draws at most one <see cref="OverlayProgress" />, mounted even while hidden so that work which
    ///     starts without a structural change can still be shown. Live updates also run behind an in-window surface,
    ///     since they replace no control and move no focus.
    /// </remarks>
    private protected void SetLiveContent<TState>(StackPanel stack, TState rendered, Func<TState?> read,
        Func<TState, TState, bool> sameStructure, Func<TState, OverlayProgressState?>? progress)
        where TState : class
    {
        SetContent(stack);
        if (CurrentLevel is not { } level)
        {
            return;
        }

        var element = Content is Control root
            ? root.GetLogicalDescendants().OfType<OverlayProgress>().FirstOrDefault()
            : null;
        _live = new LiveLevel<TState>(level, NavigationGeneration, element, rendered, read, sameStructure, progress);
    }

    /// <summary>Sets a level that shows no service state, so the service's publications leave it as drawn.</summary>
    /// <param name="stack">The level.</param>
    private protected void SetStaticContent(StackPanel stack)
    {
        SetLiveContent(stack, stack, () => stack, static (_, _) => true, null);
    }

    /// <inheritdoc />
    internal override void Leave()
    {
        _live = null;
        base.Leave();
    }

    /// <inheritdoc />
    private protected override void SetContent(StackPanel stack)
    {
        // Every draw replaces the previous level's registration; a live level registers again after this.
        _live = null;
        if (_renderGeneration != NavigationGeneration || Content is not Control old)
        {
            _renderGeneration = NavigationGeneration;
            base.SetContent(stack);
            return;
        }

        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        var ownedFocus = focused is not null && (ReferenceEquals(focused, old) || old.IsLogicalAncestorOf(focused));
        if (old is StackPanel retainedRoot)
        {
            Reconcile(retainedRoot, stack);
            stack = retainedRoot;
        }
        else
        {
            Content = stack;
        }

        if (ownedFocus && focused is not null && !stack.IsLogicalAncestorOf(focused))
        {
            var key = focused.Tag;
            var target = stack.GetLogicalDescendants().OfType<Control>()
                             .FirstOrDefault(control => Equals(control.Tag, key) && control.Focusable)
                         ?? FocusSearch.FirstNavigable(stack);
            target?.Focus(NavigationMethod.Directional);
        }

        return;

        static void Reconcile(Panel target, Panel next)
        {
            target.Width = next.Width;
            target.Height = next.Height;
            target.Margin = next.Margin;
            target.Opacity = next.Opacity;
            target.IsEnabled = next.IsEnabled;
            target.IsVisible = next.IsVisible;
            if (target is StackPanel stack && next is StackPanel nextStack)
            {
                stack.Spacing = nextStack.Spacing;
            }

            var desired = next.Children.ToArray();
            for (var index = 0; index < desired.Length; index++)
            {
                var fresh = desired[index];
                var existing = Key(fresh) is string key
                    ? target.Children.FirstOrDefault(child => Equals(Key(child), key))
                    : index < target.Children.Count
                        ? target.Children[index]
                        : null;
                if (existing?.GetType() == fresh.GetType() && Equals(Key(existing), Key(fresh)))
                {
                    // The fresh control is unstyled, so copy only an opacity it set itself; copying the default
                    // would override the retained control's styled value, such as a disabled row's dimming.
                    if (fresh.IsSet(OpacityProperty))
                    {
                        existing.Opacity = fresh.Opacity;
                    }
                    else
                    {
                        existing.ClearValue(OpacityProperty);
                    }

                    existing.IsEnabled = fresh.IsEnabled;
                    existing.IsVisible = fresh.IsVisible;
                    if (target.Children.IndexOf(existing) != index)
                    {
                        target.Children.Move(target.Children.IndexOf(existing), index);
                    }

                    if (existing is IOverlayRefreshable refreshable)
                    {
                        refreshable.RefreshFrom(fresh);
                        continue;
                    }

                    if (existing is TextBlock text && fresh is TextBlock replacement)
                    {
                        text.Text = replacement.Text;
                        text.IsVisible = replacement.IsVisible;
                        continue;
                    }

                    // Reconcile the mounted body, not the Grid's native Expander. Replacing that
                    // child would leave Body and Heading pointing at detached controls.
                    if (existing is CollapsibleSection section && fresh is CollapsibleSection nextSection
                                                               && section.Body is Panel body &&
                                                               nextSection.Body is Panel nextBody)
                    {
                        section.Summary = nextSection.Summary;
                        Reconcile(body, nextBody);
                        continue;
                    }

                    if (existing is Panel panel and not CollapsibleSection && fresh is Panel newPanel)
                    {
                        Reconcile(panel, newPanel);
                        continue;
                    }

                    if (existing.IsKeyboardFocusWithin && (existing is Slider or TextBox ||
                                                           existing.GetLogicalDescendants().OfType<Slider>().Any()))
                    {
                        continue;
                    }
                }

                next.Children.Remove(fresh);
                // A new status row can shift keyed controls that have not been matched yet.
                // Keep them mounted until their turn; discard unmatched children below.
                target.Children.Insert(index, fresh);
            }

            while (target.Children.Count > desired.Length)
            {
                target.Children.RemoveAt(target.Children.Count - 1);
            }
        }

        static object? Key(Control control)
        {
            return control is CollapsibleSection section ? section.Heading.Tag : control.Tag;
        }
    }

    /// <summary>A level's status: the shared progress element while working, then the error, else the notice.</summary>
    /// <param name="stack">The level.</param>
    /// <param name="busy">
    ///     Whether the service is working; it then shows an indeterminate <see cref="OverlayProgress" />, the same
    ///     element a level with measured progress shows.
    /// </param>
    /// <param name="error">The last refusal, or null.</param>
    /// <param name="notice">A line worth reading, or null.</param>
    /// <param name="tag">The error or notice line's stable tag, or null to leave it untagged.</param>
    private protected static void AddStatus(StackPanel stack, bool busy, string? error, string? notice,
        string? tag = null)
    {
        if (busy)
        {
            stack.Children.Add(new OverlayProgress("status.progress", new OverlayProgressState("Working…", true)));
        }

        var message = error is { Length: > 0 } ? error : notice is { Length: > 0 } ? notice : null;
        if (message is not null)
        {
            var caption = Caption(message);
            stack.Children.Add(tag is null ? caption : Tagged(caption, tag));
        }
    }

    /// <summary>
    ///     Adds the searchable, paged Steam library rows a view opens games from: search, refresh, the read's
    ///     state, one row per matching game, Load more and the empty text.
    /// </summary>
    /// <param name="body">The level.</param>
    /// <param name="list">The view's list state.</param>
    /// <param name="icon">The icon of each game row.</param>
    /// <param name="read">Reads the library; used by Refresh.</param>
    /// <param name="open">Opens a game.</param>
    /// <param name="render">Redraws the level after a search or Load more.</param>
    /// <remarks>Call <see cref="EnsureGameList" /> after the level's content is set.</remarks>
    private protected void AddGameList(StackPanel body, GameList list, Geometry icon,
        Func<Task<OverlayLibraryResult>> read, Action<SteamLibraryApp> open, Action render)
    {
        body.Children.Add(Tagged(Row("Search games", list.Query, Icons.ListLines,
            () => EditText("Search your Steam library", list.Query, 0, query =>
            {
                list.Query = query;
                list.Shown = GameList.Chunk;
                render();
            })), "games.search"));
        body.Children.Add(Tagged(Row("Refresh games", "Read the library from Steam again", Icons.Restart,
            list.Reading ? null : () => _ = RunSafelyAsync(ReadGameListAsync(list, read), "games")), "games.refresh"));
        if (list.Error is not null)
        {
            body.Children.Add(Tagged(Caption(list.Error), "games.error"));
        }

        if (list.Reading)
        {
            body.Children.Add(Tagged(Caption("Asking Steam…"), "games.reading"));
        }

        var games = list.Games.Where(game => game.Name.Contains(list.Query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var game in games.Take(list.Shown))
        {
            var chosen = game;
            body.Children.Add(Tagged(Row(game.Name, game.Shortcut ? "Non-Steam game" : "Steam game", icon,
                () => open(chosen)), "game:" + game.AppId));
        }

        if (games.Length > list.Shown)
        {
            var added = "game:" + games[list.Shown].AppId;
            body.Children.Add(Tagged(Row("Load more", $"{list.Shown} of {games.Length}", Icons.ArrowDown, () =>
            {
                list.Shown += GameList.Chunk;
                render();
                PagedRows.FocusAdded(this, added);
            }), "games.more"));
        }

        if (list.Loaded && !list.Reading && list.Error is null && games.Length == 0)
        {
            body.Children.Add(Tagged(Caption(list.Games.Count == 0
                ? "Your Steam library has no games."
                : "No games match the search."), "games.empty"));
        }
    }

    /// <summary>Starts the first library read once the list level is on screen.</summary>
    /// <param name="list">The view's list state.</param>
    /// <param name="read">Reads the library.</param>
    private protected void EnsureGameList(GameList list, Func<Task<OverlayLibraryResult>> read)
    {
        if (!list.Loaded && !list.Reading)
        {
            _ = RunSafelyAsync(ReadGameListAsync(list, read), "games");
        }
    }

    private async Task ReadGameListAsync(GameList list, Func<Task<OverlayLibraryResult>> read)
    {
        var generation = NavigationGeneration;
        list.Reading = true;
        CurrentLevel?.Invoke();
        OverlayLibraryResult result;
        try
        {
            result = await read();
        }
        finally
        {
            list.Reading = false;
            list.Loaded = true;
        }

        if (result.Succeeded)
        {
            list.Games = result.Games;
        }

        // A failed read keeps the last good list rather than presenting it as an empty library.
        list.Error = result.Error;
        if (generation == NavigationGeneration)
        {
            CurrentLevel?.Invoke();
        }
    }


    /// <summary>Tags a control so focus can find it again after the level is redrawn.</summary>
    /// <param name="control">The control.</param>
    /// <param name="tag">Its stable tag.</param>
    /// <returns>The control.</returns>
    private protected static T Tagged<T>(T control, string tag) where T : Control
    {
        control.Tag = tag;
        return control;
    }

    /// <summary>Runs one of the service's commands; a refusal is shown, a failure logged.</summary>
    /// <param name="operation">The command.</param>
    /// <param name="what">What it was, for the log.</param>
    private protected void Run(Func<CancellationToken, Task<SteamUiCommandResult>> operation, string what = "command")
    {
        _ = RunCommandAsync(operation, what);
    }

    /// <summary>Completes after the service command and its current-view notification have finished.</summary>
    /// <param name="onApplied">Updates the current view after the command is applied.</param>
    /// <param name="operation">
    ///     Service-owned operation; it receives CancellationToken.None so leaving the view does not cancel
    ///     durable work.
    /// </param>
    /// <param name="what">Operation name used for diagnostics.</param>
    /// <returns>
    ///     Completion after the operation and any current-generation refusal notification; safe-run error handling
    ///     remains in effect.
    /// </returns>
    internal Task RunCommandAsync(Func<CancellationToken, Task<SteamUiCommandResult>> operation,
        string what = "command", Action? onApplied = null)
    {
        return RunSafelyAsync(RunAsync(), what);

        async Task RunAsync()
        {
            var generation = NavigationGeneration;
            var result = await Task.Run(() => operation(CancellationToken.None));
            if (!result.Succeeded)
            {
                // The service owns its work and finishes it after the user leaves. A refusal that
                // arrives then is only logged: a left view keeps its notice for the next open.
                var message = result.Error ?? "That did not work.";
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (CurrentLevel is not null && generation == NavigationGeneration)
                    {
                        Toast(message);
                    }
                    else
                    {
                        Log.Info($"{LogScope}: {message}");
                    }
                });
            }
            else if (onApplied is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (CurrentLevel is not null && generation == NavigationGeneration)
                    {
                        onApplied();
                    }
                });
            }
        }
    }

    private protected void ConfirmCommand(string title, string message,
        Func<CancellationToken, Task<SteamUiCommandResult>> command)
    {
        Navigate(() =>
        {
            var body = NewStack(title);
            body.Children.Add(Caption(message));
            body.Children.Add(Tagged(Row("Cancel", "", Icons.ArrowLeft, () => Back()), "confirm.cancel"));
            body.Children.Add(Tagged(
                DangerRow("Confirm", "", Icons.Close, () => _ = RunSafelyAsync(CommitAsync(), "confirm")),
                "confirm.accept"));
            // The question is fixed text: a publication while it is open must not redraw it under the user.
            SetStaticContent(body);
        });
        return;

        async Task CommitAsync()
        {
            var generation = NavigationGeneration;
            var result = await Task.Run(() => command(CancellationToken.None));
            if (generation != NavigationGeneration)
            {
                return;
            }

            if (result.Succeeded)
            {
                Back();
                CurrentLevel?.Invoke();
            }
            else
            {
                Toast(result.Error ?? "The operation failed.");
            }
        }
    }

    private void OnSourceChanged()
    {
        // Raised from the service's own work, on whatever thread finished it, and in bursts. One
        // refresh is queued at a time, at background priority, and it renders whatever the state is
        // by then, so a burst costs one rebuild rather than one per change.
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (_source is not null && IsEffectivelyVisible && RedrawsOnSourceChange)
            {
                if (_live is { } live && live.TryUpdate(this))
                {
                    return;
                }

                if (_owner?.HasActiveSurface == true)
                {
                    _renderDeferred = true;
                }
                else
                {
                    CurrentLevel?.Invoke();
                }
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>A registered live level: what it was drawn from and how its progress element follows the service.</summary>
    private abstract class LiveLevel
    {
        /// <summary>Applies a publication in place when the level's structure is unchanged.</summary>
        /// <param name="view">The view that registered the level.</param>
        /// <returns>True when the level needs no redraw; false when the caller must redraw it.</returns>
        internal abstract bool TryUpdate(ServiceSubView view);
    }

    /// <summary>A live level over one kind of service state.</summary>
    /// <typeparam name="TState">The service state the level was drawn from.</typeparam>
    private sealed class LiveLevel<TState>(
        Action level,
        int generation,
        OverlayProgress? element,
        TState shown,
        Func<TState?> read,
        Func<TState, TState, bool> sameStructure,
        Func<TState, OverlayProgressState?>? progress) : LiveLevel
        where TState : class
    {
        private TState _shown = shown;

        /// <inheritdoc />
        internal override bool TryUpdate(ServiceSubView view)
        {
            if (generation != view.NavigationGeneration || !ReferenceEquals(level, view.CurrentLevel)
                                                        || read() is not { } next
                                                        || !sameStructure(_shown, next))
            {
                return false;
            }

            if (progress is not null)
            {
                // A progress element that left the tree falls back to a redraw, which registers the mounted one.
                if (element is null || !view.IsLogicalAncestorOf(element))
                {
                    return false;
                }

                element.Apply(progress(next));
            }

            _shown = next;
            return true;
        }
    }

    /// <summary>A view's Steam library list: the games read last, the search and how many rows are shown.</summary>
    private protected sealed class GameList
    {
        /// <summary>The rows each Load more adds.</summary>
        internal const int Chunk = 48;

        /// <summary>The last successfully read library.</summary>
        internal IReadOnlyList<SteamLibraryApp> Games { get; set; } = [];

        /// <summary>Why the last read failed, or null.</summary>
        internal string? Error { get; set; }

        /// <summary>Whether a read has finished.</summary>
        internal bool Loaded { get; set; }

        /// <summary>Whether a read is running.</summary>
        internal bool Reading { get; set; }

        /// <summary>The search text.</summary>
        internal string Query { get; set; } = "";

        /// <summary>How many matching games are shown.</summary>
        internal int Shown { get; set; } = Chunk;
    }
}
