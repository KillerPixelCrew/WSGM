using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Overlay;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

public sealed class UtilitySurfaceTests
{
    [AvaloniaFact]
    public async Task UnavailableUtilityContainsFocusAndReturnsToItsInvokerAt720p()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay(1280, 720);
        var invoker = UiFixture.Named<Button>(window, "BrightnessButton");
        invoker.Focus(NavigationMethod.Directional);
        var invokerBottom = invoker.TranslatePoint(new Point(0, invoker.Bounds.Height), window)!.Value;
        var dismissed = false;
        window.Dismissed += () => dismissed = true;

        // No brightness service is attached: this exercises the real shared surface owner
        // without constructing a manager that would enumerate or change the live machine.
        window.ShowBrightnessSurface();
        Dispatcher.UIThread.RunJobs();

        var close = Assert.IsType<Button>(window.ActiveSurfaceFocusTarget);
        Assert.Equal("Close Brightness", AutomationProperties.GetName(close));
        Assert.Same(close, window.FocusManager?.GetFocusedElement());
        Assert.Same(window, TopLevel.GetTopLevel(close));
        Assert.True(UiFixture.Named<Grid>(window, "DeckContent").IsEnabled);
        Assert.False(UiFixture.Named<Grid>(window, "DeckContent").IsHitTestVisible);
        var shield = Assert.IsType<Grid>(UiFixture.Named<Grid>(window, "SurfaceRoot").Children.Last());
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(shield.Background).Color);
        UiFixture.Click(window, UiFixture.Named<Button>(window, "CloseButton"));
        Assert.False(dismissed);
        Assert.True(window.HasActiveSurface);
        var surface = close.GetVisualAncestors().OfType<Border>()
            .First(border => border.Child is Grid && border.Padding.Left == 20);
        var origin = surface.TranslatePoint(default, window)!.Value;
        var far = surface.TranslatePoint(new Point(surface.Bounds.Width, surface.Bounds.Height), window)!.Value;
        Assert.InRange(origin.X, 0, window.Bounds.Width);
        Assert.InRange(origin.Y, invokerBottom.Y, window.Bounds.Height);
        Assert.InRange(far.X, 0, window.Bounds.Width);
        Assert.InRange(far.Y, 0, window.Bounds.Height);
        UiFixture.Key(window, Key.Tab);
        Assert.Same(close, window.FocusManager?.GetFocusedElement());

        var detached = 0;
        surface.DetachedFromVisualTree += (_, _) => detached++;
        var closed = new TaskCompletionSource();
        window.SurfaceClosed += () => closed.TrySetResult();
        UiFixture.Click(window, close);
        Assert.False(close.IsEffectivelyEnabled);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, detached);
        Assert.Null(TopLevel.GetTopLevel(surface));
        Assert.False(window.HasActiveSurface);
        Assert.True(UiFixture.Named<Grid>(window, "DeckContent").IsEnabled);
        Assert.True(UiFixture.Named<Grid>(window, "DeckContent").IsHitTestVisible);
        Assert.Same(invoker, window.FocusManager?.GetFocusedElement());
        Assert.True(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task KeyboardCancellationKeepsCoveredUtilityAttachedUntilFinalClose()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        var invoker = window.DefaultFocusTarget;
        invoker.Focus(NavigationMethod.Directional);
        window.ShowBrightnessSurface();
        Dispatcher.UIThread.RunJobs();
        var utilityClose = Assert.IsType<Button>(window.ActiveSurfaceFocusTarget);
        var utilityContent = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "Brightness unavailable for this display");
        var utilityDetachments = 0;
        utilityContent.DetachedFromVisualTree += (_, _) => utilityDetachments++;
        var accepted = 0;
        var keyboard = new KeyboardPanel("Name", "unchanged", 32);
        keyboard.Accepted += _ => accepted++;
        window.ShowKeyboardSurface(keyboard);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(window, TopLevel.GetTopLevel(utilityContent));
        Assert.False(utilityContent.IsEffectivelyVisible);
        Assert.Equal(0, utilityDetachments);
        var keyboardClosed = new TaskCompletionSource();
        window.SurfaceClosed += () => keyboardClosed.TrySetResult();
        Assert.True(window.CloseActiveSurface());
        await keyboardClosed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, accepted);
        Assert.Null(TopLevel.GetTopLevel(keyboard));
        Assert.True(utilityContent.IsEffectivelyVisible);
        Assert.Equal(0, utilityDetachments);
        Assert.True(window.HasActiveSurface);
        Assert.Same(utilityClose, window.FocusManager?.GetFocusedElement());
        Assert.True(UiFixture.Named<Grid>(window, "DeckContent").IsEnabled);
        Assert.False(UiFixture.Named<Grid>(window, "DeckContent").IsHitTestVisible);
        var utilityClosed = new TaskCompletionSource();
        window.SurfaceClosed += () => utilityClosed.TrySetResult();
        Assert.True(window.CloseActiveSurface());
        await utilityClosed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, utilityDetachments);
        Assert.Null(TopLevel.GetTopLevel(utilityContent));
        Assert.False(window.HasActiveSurface);
        Assert.True(UiFixture.Named<Grid>(window, "DeckContent").IsEnabled);
        Assert.True(UiFixture.Named<Grid>(window, "DeckContent").IsHitTestVisible);
        Assert.Same(invoker, window.FocusManager?.GetFocusedElement());
    }
}
