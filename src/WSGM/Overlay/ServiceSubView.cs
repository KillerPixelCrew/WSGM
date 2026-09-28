using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using WSGM.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     A sub-view over one session service: it redraws its current level when the service publishes,
///     keeps focus on the row the user was on through that redraw, and runs the service's commands
///     with their refusal shown as a toast. The Game Library and Themes views are two of these.
/// </summary>
public abstract class ServiceSubView : OverlaySubView
{
    private int _refreshQueued;
    private IChangeSource? _source;

    /// <summary>Opens the view on its home level.</summary>
    public void Open()
    {
        _stack.Clear();
        _current = null;
        _navigationGeneration++;
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
        // The service republishes on every change, the user's own toggles included. Rebuilding the
        // level would otherwise throw focus back to the top of a list the user is working down.
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        var tag = focused?.Tag as string;
        base.SetContent(stack);
        if (tag is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            var match = stack.GetLogicalDescendants().OfType<Control>()
                .FirstOrDefault(control => Equals(control.Tag, tag) && control.Focusable);
            match?.Focus(NavigationMethod.Directional);
        });
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

    /// <summary>The row that continues on the service's page in Steam.</summary>
    /// <param name="description">What the page offers that the overlay does not.</param>
    /// <param name="open">Hands over to the page.</param>
    /// <returns>The row.</returns>
    private protected static Control OpenInSteamRow(string description, Action open)
    {
        return Tagged(Row("Open in Steam", description, Icons.SteamLike, open), "open-in-steam");
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
        _ = RunSafelyAsync(RunAsync(), what);

        async Task RunAsync()
        {
            var result = await operation(CancellationToken.None);
            if (!result.Succeeded)
            {
                Dispatcher.UIThread.Post(() => Toast(result.Error ?? "That did not work."));
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
            if (_source is not null && IsVisible)
            {
                _current?.Invoke();
            }
        }, DispatcherPriority.Background);
    }
}
