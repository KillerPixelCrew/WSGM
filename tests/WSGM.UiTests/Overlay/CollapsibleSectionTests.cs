using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.Testing;
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
        // ReSharper disable ArgumentsStyleLiteral
        var window = fixture.Overlay(width: 980, height: 640);
        // ReSharper restore ArgumentsStyleLiteral
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

    [AvaloniaFact]
    public async Task SoundLibraryKeepsExpansionAndHeaderFocusAcrossServiceRefresh()
    {
        using TemporaryDirectory temporary = new();
        var root = temporary.GetPath("sounds");
        using var fixture = new UiFixture();
        var window = fixture.Overlay();
        await using var service = new SoundPackService(new SoundPackLibrary(root), () => "", _ => { },
            () => null, _ => { });
        await service.RefreshAsync(CancellationToken.None);
        var view = UiFixture.Named<SoundsView>(window, "SoundsHost");
        view.Attach(service, new HashSet<string>());
        UiFixture.Named<StackPanel>(window, "PanelQuickAccess").IsVisible = false;
        view.IsVisible = true;
        view.Open();
        Dispatcher.UIThread.RunJobs();
        var initial = view.GetVisualDescendants().OfType<CollapsibleSection>().Single();
        initial.IsExpanded = true;
        initial.Heading.Focus(NavigationMethod.Directional);
        await service.RefreshAsync(CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
        var current = view.GetVisualDescendants().OfType<CollapsibleSection>().Single();
        Assert.NotSame(initial, current);
        Assert.True(current.IsExpanded);
        Assert.Same(current.Heading, window.FocusManager!.GetFocusedElement());
        view.Attach(null);
    }
}
