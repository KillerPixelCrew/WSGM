using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
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
public abstract class ServiceSubView : OverlaySubView
{
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

    /// <inheritdoc />
    private protected override void SetContent(StackPanel stack)
    {
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
                    existing.Opacity = fresh.Opacity;
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

    /// <summary>A level's status: working, then the error, else the notice.</summary>
    /// <param name="stack">The level.</param>
    /// <param name="busy">Whether the service is working.</param>
    /// <param name="error">The last refusal, or null.</param>
    /// <param name="notice">A line worth reading, or null.</param>
    private protected static void AddStatus(StackPanel stack, bool busy, string? error, string? notice)
    {
        if (busy)
        {
            stack.Children.Add(Caption("Working…"));
        }

        if (error is { Length: > 0 })
        {
            stack.Children.Add(Caption(error));
        }
        else if (notice is { Length: > 0 })
        {
            stack.Children.Add(Caption(notice));
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
            SetContent(body);
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
            if (_source is not null && IsEffectivelyVisible)
            {
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
}
