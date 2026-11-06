using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Overlay;
using WSGM.UiTests.Infrastructure;
using WSGM.UiTests.Visual;
using Case = WSGM.UiTests.Visual.PreviewScaledResolutions.Case;

namespace WSGM.UiTests.Overlay;

public sealed class CustomResolutionsViewTests
{
    [AvaloniaFact]
    public void ToolsDisplayListsTheDriversSizesAndOnlyCustomOnesOpenRemove()
    {
        using var fixture = new UiFixture();
        var window = PreviewScaledResolutions.Create(fixture, 1024, Case.Intel, out var editor);

        Assert.True(UiFixture.Named<Control>(window, "CustomResolutionsHost").IsVisible);
        Assert.DoesNotContain("PCI\\FIXTURE", editor.AdapterText);
        Assert.True(Row(window, "resolutions.item:1760x1100").IsEffectivelyEnabled);
        Assert.False(Row(window, "resolutions.item:3840x2160").IsEffectivelyEnabled);
        Assert.True(Row(window, "resolutions.steps").IsEffectivelyEnabled);
        Assert.Null(Find(window, "resolutions.restart"));
        Assert.Null(Find(window, "resolutions.reapply"));
        Assert.Same(Row(window, "resolutions.add"), window.FocusManager!.GetFocusedElement());
        VisualBaseline.Verify(window, "overlay-custom-resolutions-1024");

        UiFixture.Key(window, Key.Escape);
        Dispatcher.UIThread.RunJobs();
        Assert.False(UiFixture.Named<Control>(window, "CustomResolutionsHost").IsVisible);
        Assert.True(window.PanelSystemDisplay.IsVisible);
    }

    [AvaloniaFact]
    public async Task RemovingASizeAsksFirstWithCancelFocused()
    {
        using var fixture = new UiFixture();
        var window = PreviewScaledResolutions.Create(fixture, 1024, Case.Intel, out var editor);
        var before = editor.Resolution;

        UiFixture.Click(window, Row(window, "resolutions.item:1760x1100"));
        Dispatcher.UIThread.RunJobs();
        var cancel = UiFixture.Tagged<Control>(window, "confirm.cancel");
        Assert.Same(cancel, window.FocusManager!.GetFocusedElement());
        await CloseAsync(window, () => UiFixture.Click(window, cancel));
        Assert.Same(before, editor.Resolution);

        UiFixture.Click(window, Row(window, "resolutions.item:1760x1100"));
        Dispatcher.UIThread.RunJobs();
        await CloseAsync(window, () => UiFixture.Click(window, UiFixture.Tagged<Control>(window, "confirm.accept")));
        Assert.Equal((1760, 1100), (editor.Resolution!.Width, editor.Resolution.Height));
    }

    [AvaloniaFact]
    public async Task AdapterWideListsWarnOfferTheDriverRestartAndAskBeforeIt()
    {
        using var fixture = new UiFixture();
        var window = PreviewScaledResolutions.Create(fixture, 1280, Case.Amd, out var editor);

        Assert.True(editor.NeedsRestart);
        Assert.NotNull(Find(window, "resolutions.activation"));
        Assert.True(Row(window, "resolutions.reapply").IsEffectivelyEnabled);
        VisualBaseline.Verify(window, "overlay-custom-resolutions-1280");

        UiFixture.Click(window, Row(window, "resolutions.restart"));
        Dispatcher.UIThread.RunJobs();
        Assert.Same(UiFixture.Tagged<Control>(window, "confirm.cancel"), window.FocusManager!.GetFocusedElement());
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text?.Contains("Displays may go blank and games may be interrupted.") == true
                     && block.IsEffectivelyVisible);
        VisualBaseline.Verify(window, "overlay-custom-resolutions-restart-1280");
        await CloseAsync(window, () => UiFixture.Key(window, Key.Escape));
        Assert.True(UiFixture.Named<Control>(window, "CustomResolutionsHost").IsVisible);
    }

    [AvaloniaFact]
    public void ADisplayWithoutAnEditableListSaysWhyAndOffersRefresh()
    {
        using var fixture = new UiFixture();
        var window = PreviewScaledResolutions.Create(fixture, 1024, Case.Unsupported, out var editor);

        Assert.False(editor.CanEdit);
        Assert.NotNull(Find(window, "resolutions.unavailable"));
        Assert.Null(Find(window, "resolutions.add"));
        Assert.True(Row(window, "resolutions.refresh").IsEffectivelyEnabled);
        VisualBaseline.Verify(window, "overlay-custom-resolutions-unavailable-1024");
    }

    [AvaloniaFact]
    public void WithoutAGraphicsOwnerTheDisplayPageDoesNotOfferCustomResolutions()
    {
        using var fixture = new UiFixture();
        var window = fixture.Overlay(1024, 640);

        Assert.False(window.PanelSystemDisplay.CustomResolutionsButton.IsVisible);
    }

    private static async Task CloseAsync(OverlayWindow window, Action close)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Surfaces.Closed += Closed;
        try
        {
            close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            window.Surfaces.Closed -= Closed;
        }

        Dispatcher.UIThread.RunJobs();
        Assert.False(window.Surfaces.HasActive);
        return;

        void Closed()
        {
            closed.TrySetResult();
        }
    }

    private static ActionButton Row(Window window, string tag)
    {
        return Assert.IsAssignableFrom<ActionButton>(Find(window, tag));
    }

    private static Control? Find(Window window, string tag)
    {
        return UiFixture.Named<Control>(window, "CustomResolutionsHost").GetVisualDescendants()
            .OfType<Control>().Concat(window.GetVisualDescendants().OfType<Control>())
            .FirstOrDefault(control => Equals(control.Tag, tag) && control.IsEffectivelyVisible);
    }
}
