using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Overlay;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

public sealed class OverlaySurfaceTests
{
    [AvaloniaFact]
    public async Task PowerCancellationRestoresInvokerWithoutDismissingOverlay()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        var invoker = window.DefaultFocusTarget;
        invoker.Focus(NavigationMethod.Directional);
        var dismissed = false;
        window.Dismissed += () => dismissed = true;
        window.ShowPowerMenu();
        Dispatcher.UIThread.RunJobs();

        var keepPlaying = Assert.IsType<Button>(window.ActiveSurfaceFocusTarget);
        Assert.Equal("Keep playing", keepPlaying.Content);
        Assert.Same(keepPlaying, window.FocusManager?.GetFocusedElement());
        Assert.True(UiFixture.Named<Grid>(window, "DeckContent").IsEnabled);
        Assert.False(UiFixture.Named<Grid>(window, "DeckContent").IsHitTestVisible);
        Assert.True(window.IsPowerMenuOpen);
        var closed = new TaskCompletionSource();
        window.SurfaceClosed += () => closed.TrySetResult();
        UiFixture.Click(window, keepPlaying);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(window.HasActiveSurface);
        Assert.True(UiFixture.Named<Grid>(window, "DeckContent").IsEnabled);
        Assert.True(UiFixture.Named<Grid>(window, "DeckContent").IsHitTestVisible);
        Assert.Same(invoker, window.FocusManager?.GetFocusedElement());
        Assert.False(dismissed);
        Assert.True(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task KeyboardReturnsToCoveredSurfaceAndCommitsOnce()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        window.ShowPowerMenu();
        Dispatcher.UIThread.RunJobs();
        var invoker = window.ActiveSurfaceFocusTarget;
        var keyboard = new KeyboardPanel("Name", "sample", 32);
        var accepted = new List<string>();
        keyboard.Accepted += accepted.Add;
        window.ShowKeyboardSurface(keyboard);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsPowerMenuOpen);
        Assert.Same(window, TopLevel.GetTopLevel(keyboard));
        Assert.True(keyboard.Bounds.Width > window.Bounds.Width * 0.8);
        var closed = new TaskCompletionSource();
        window.SurfaceClosed += () => closed.TrySetResult();
        var accept = UiFixture.Named<Button>(keyboard, "AcceptButton");
        UiFixture.Click(window, accept);
        Assert.False(accept.IsEffectivelyEnabled);
        UiFixture.Key(window, Key.Enter);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["sample"], accepted);
        Assert.True(window.IsPowerMenuOpen);
        Assert.Same(invoker, window.FocusManager?.GetFocusedElement());
        Assert.True(UiFixture.Named<Grid>(window, "DeckContent").IsEnabled);
        Assert.False(UiFixture.Named<Grid>(window, "DeckContent").IsHitTestVisible);
    }

    [AvaloniaFact]
    public void ReplacingKeyboardDropsTheSupersededEditorAndReturnsToOriginalInvoker()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        var invoker = window.DefaultFocusTarget;
        invoker.Focus(NavigationMethod.Directional);
        var first = new KeyboardPanel("First", "old", 32);
        var second = new KeyboardPanel("Second", "new", 32);
        window.ShowKeyboardSurface(first);
        window.ShowKeyboardSurface(second);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(TopLevel.GetTopLevel(first));
        Assert.Same(window, TopLevel.GetTopLevel(second));
        window.CloseAllSurfaces();
        Assert.False(window.HasActiveSurface);
        Assert.Same(invoker, window.FocusManager?.GetFocusedElement());
    }

    [AvaloniaFact]
    public void PowerConfirmationIsClearedWhenMenuIsReplaced()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        window.ShowPowerMenu();
        Dispatcher.UIThread.RunJobs();
        var restart = window.GetVisualDescendants().OfType<Button>()
            .Single(button => button.IsEffectivelyVisible && button.Tag is ActionButton { Name: "RestartButton" });
        UiFixture.Click(window, restart);
        Assert.Equal("Really?", ((ActionButton)restart.Tag!).Title);
        Assert.Contains(restart.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Really?");
        window.CloseAllSurfaces();
        window.ShowPowerMenu();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Restart", UiFixture.Named<ActionButton>(window, "RestartButton").Title);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>()
            .Where(button => button.IsEffectivelyVisible && button.Tag is ActionButton)
            .SelectMany(button => button.GetVisualDescendants().OfType<TextBlock>()), text => text.Text == "Really?");
        Assert.Equal("Keep playing", Assert.IsType<Button>(window.ActiveSurfaceFocusTarget).Content);
    }

    [AvaloniaFact]
    public void PowerMenuProjectsEveryAvailableSourceActionIntoTwoColumnsAt720p()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay(1280, 720);
        window.ShowPowerMenu();
        Dispatcher.UIThread.RunJobs();
        var menuActions = window.GetVisualDescendants().OfType<Button>()
            .Where(button => button.IsEffectivelyVisible && button.Tag is ActionButton).ToArray();
        var sources = UiFixture.Named<StackPanel>(window, "PanelPowerActions").Children.OfType<ActionButton>()
            .Where(button => button.IsVisible).ToList();
        var desktop = UiFixture.Named<ActionButton>(window, "DesktopButton");
        if (desktop.IsVisible)
        {
            sources.Add(desktop);
        }

        Assert.Equal(sources.Count, menuActions.Length);
        foreach (var source in sources)
        {
            var action = Assert.Single(menuActions, button => ReferenceEquals(button.Tag, source));
            Assert.Contains(action.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == source.Title);
            Assert.True(action.Bounds.Width >= 44);
            Assert.True(action.Bounds.Height >= 44);
            var origin = action.TranslatePoint(default, window)!.Value;
            var far = action.TranslatePoint(new Point(action.Bounds.Width, action.Bounds.Height), window)!.Value;
            Assert.InRange(origin.X, 0, window.Bounds.Width);
            Assert.InRange(origin.Y, 0, window.Bounds.Height);
            Assert.InRange(far.X, 0, window.Bounds.Width);
            Assert.InRange(far.Y, 0, window.Bounds.Height);
        }

        Assert.Equal(2,
            menuActions.Select(button => button.TranslatePoint(default, window)!.Value.X).Distinct().Count());
        var keepPlaying = Assert.IsType<Button>(window.ActiveSurfaceFocusTarget);
        Assert.Equal("Keep playing", keepPlaying.Content);
        Assert.Same(keepPlaying, window.FocusManager?.GetFocusedElement());
        var desktopInvocations = 0;
        window.DesktopRequested += () => desktopInvocations++;
        UiFixture.Click(window, Assert.Single(menuActions, button => ReferenceEquals(button.Tag, desktop)));
        Assert.Equal(1, desktopInvocations);
    }

    [AvaloniaFact]
    public void CredentialKeyboardMasksTextAndKeepsEntryInTheOverlay()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        var keyboard = new KeyboardPanel("Network password", "secret", 63, true);
        window.ShowKeyboardSurface(keyboard);
        Dispatcher.UIThread.RunJobs();

        var input = UiFixture.Named<TextBox>(keyboard, "Input");
        Assert.Equal('●', input.PasswordChar);
        Assert.Equal("secret", input.Text);
        Assert.Same(window, TopLevel.GetTopLevel(input));
        Assert.IsNotType<TextBox>(window.FocusManager?.GetFocusedElement());
        window.CloseAllSurfaces();
        Assert.False(window.HasActiveSurface);
    }
}
