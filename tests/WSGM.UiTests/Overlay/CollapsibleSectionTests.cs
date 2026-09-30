using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using WSGM.Controls;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

public sealed class CollapsibleSectionTests
{
    [AvaloniaFact]
    public void CollapseFocusedBodyReturnsToHeaderAndRetainsEditorValue()
    {
        using var fixture = new UiFixture();
        var window = fixture.Overlay();
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = 37 };
        var body = new StackPanel { Children = { slider } };
        var section = new CollapsibleSection("Performance", body) { IsExpanded = true };
        UiFixture.Named<StackPanel>(window, "PinnedSectionsGrid").Children.Add(section);
        Dispatcher.UIThread.RunJobs();
        slider.Focus(NavigationMethod.Directional);
        Assert.Same(slider, window.FocusManager!.GetFocusedElement());
        section.IsExpanded = false;
        Assert.Same(section.Heading, window.FocusManager.GetFocusedElement());
        Assert.False(slider.IsEffectivelyVisible);
        section.IsExpanded = true;
        Assert.True(slider.IsEffectivelyVisible);
        Assert.Equal(37, slider.Value);
        Assert.Same(body, section.Body);
    }

    [AvaloniaFact]
    public void HeaderUsesTheSameStateForPointerAndControllerDirections()
    {
        using var fixture = new UiFixture();
        var window = fixture.Overlay(width: 980, height: 640);
        var section = new CollapsibleSection("Display", new Button { Content = "Brightness" }) { IsExpanded = true };
        UiFixture.Named<StackPanel>(window, "PinnedSectionsGrid").Children.Add(section);
        Dispatcher.UIThread.RunJobs();
        UiFixture.Click(window, section.Heading);
        Assert.False(section.IsExpanded);
        section.Heading.Focus(NavigationMethod.Directional);
        Assert.True(window.NavigateWorkspace(NavigationDirection.Right));
        Assert.True(section.IsExpanded);
        Assert.True(window.NavigateWorkspace(NavigationDirection.Left));
        Assert.False(section.IsExpanded);
        Assert.Same(section.Heading, window.FocusManager!.GetFocusedElement());
    }
}
