using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Overlay;
using WSGM.UiTests.Fakes;
using WSGM.UiTests.Infrastructure;
using WSGM.UiTests.Visual;

namespace WSGM.UiTests.Overlay;

public sealed class HotkeysViewTests
{
    [AvaloniaTheory]
    [InlineData("overlay-hotkeys-list-1280", "list", 1280, 800)]
    [InlineData("overlay-hotkeys-list-980", "list", 980, 640)]
    [InlineData("overlay-hotkeys-empty-1280", "empty", 1280, 800)]
    [InlineData("overlay-hotkeys-empty-980", "empty", 980, 640)]
    [InlineData("overlay-hotkeys-editor-1280", "editor", 1280, 800)]
    [InlineData("overlay-hotkeys-editor-980", "editor", 980, 640)]
    [InlineData("overlay-hotkeys-keys-1280", "keys", 1280, 800)]
    [InlineData("overlay-hotkeys-keys-980", "keys", 980, 640)]
    [InlineData("overlay-hotkeys-pinned-1280", "pinned", 1280, 800)]
    [InlineData("overlay-hotkeys-pinned-980", "pinned", 980, 640)]
    public void Scene(string name, string scene, int width, int height)
    {
        using UiFixture fixture = new();
        var (window, _) = Open(fixture, scene == "empty", width, height);
        switch (scene)
        {
            case "editor":
                Click(window, "hotkey.edit:lossless");
                break;
            case "keys":
                Click(window, "hotkey.edit:lossless");
                Click(window, "editor.key");
                break;
            case "pinned":
                window.SetPins(["home.steam", "home.desktop", "hotkey:menu", "hotkey:lossless"]);
                HidePinToast(window);
                UiFixture.Click(window, UiFixture.Tab(window, 0));
                Dispatcher.UIThread.RunJobs();
                break;
        }

        ScrollToTop(window);
        Assert.Equal(width, window.ClientSize.Width);
        Assert.Equal(height, window.ClientSize.Height);
        VisualBaseline.Verify(window, name);
    }

    [AvaloniaFact]
    public void AReadyMadeHotkeyOpensItsEditorAndIsStoredOnlyOnSave()
    {
        using UiFixture fixture = new();
        var (window, source) = Open(fixture, true);

        Click(window, "hotkeys.preset:lossless");
        Assert.Equal("Lossless Scaling", UiFixture.Tagged<ActionButton>(window, "editor.name").Description);
        Assert.Empty(source.Commands);

        Click(window, "editor.save");
        var saved = Assert.IsType<HotkeyDefinition>(Assert.Single(source.Commands).Argument);
        Assert.Equal("Lossless Scaling", saved.Name);
        Assert.Equal([HotkeyKeys.Control, HotkeyKeys.Alt, HotkeyKeys.S], saved.Keys);
        Assert.NotNull(UiFixture.Tagged<HotkeyActionRow>(window, "hotkey:" + saved.Id));
    }

    [AvaloniaFact]
    public void TheEditorDraftSurvivesBackUntilItIsSavedOrDiscarded()
    {
        using UiFixture fixture = new();
        var (window, source) = Open(fixture);

        Click(window, "hotkey.edit:menu");
        Click(window, "editor.modifier:Ctrl");
        Click(window, "editor.key");
        Click(window, "key:" + 0x70);
        Assert.Equal("F1", UiFixture.Tagged<ActionButton>(window, "editor.key").Description);
        Assert.Equal("Sends Ctrl+F1", UiFixture.Tagged<ActionButton>(window, "editor.save").Description);

        Assert.True(window.HotkeysHost.Back());
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Unsaved changes in the editor",
            UiFixture.Tagged<HotkeyActionRow>(window, "hotkey:menu").Description);
        Assert.Empty(source.Commands);

        Click(window, "hotkey.edit:menu");
        Assert.Equal("Sends Ctrl+F1", UiFixture.Tagged<ActionButton>(window, "editor.save").Description);
        Click(window, "editor.discard");
        Assert.Null(UiFixture.Tagged<HotkeyActionRow>(window, "hotkey:menu").Description);
        Assert.Empty(source.Commands);
    }

    [AvaloniaFact]
    public async Task DeletingAsksWithTheNameFocusesCancelAndRemovesOnlyWhenAccepted()
    {
        using UiFixture fixture = new();
        var (window, source) = Open(fixture);
        Click(window, "hotkey.edit:lossless");

        Click(window, "editor.delete");
        Assert.True(window.Surfaces.HasActive);
        Assert.True(UiFixture.Tagged<ActionButton>(window, "confirm.cancel").IsFocused);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.IsEffectivelyVisible && text.Text == "Delete “Lossless Scaling”?");

        await CloseAsync(window, () => Click(window, "confirm.cancel"));
        Assert.Empty(source.Commands);

        Click(window, "editor.delete");
        await CloseAsync(window, () => Click(window, "confirm.accept"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(("remove", (object)"lossless"), Assert.Single(source.Commands));
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<HotkeyActionRow>(),
            row => Equals(row.Tag, "hotkey:lossless"));
    }

    [AvaloniaFact]
    public void APinnedTileSendsItsHotkeyAndXUnpinsIt()
    {
        using UiFixture fixture = new();
        var (window, _) = Open(fixture);
        List<string> sent = [];
        List<string> toggled = [];
        window.HotkeysHost.SendRequested += sent.Add;
        window.PinToggleRequested += toggled.Add;

        // X on the page's hotkey pins it, as on any pinnable row.
        window.RequestSecondaryAction(UiFixture.Tagged<HotkeyActionRow>(window, "hotkey:menu"));
        Assert.Equal(["hotkey:menu"], toggled);

        window.SetPins(["home.steam", "home.desktop", "hotkey:menu"]);
        UiFixture.Click(window, UiFixture.Tab(window, 0));
        Dispatcher.UIThread.RunJobs();
        var tile = UiFixture.Tagged<HotkeyActionRow>(window, "pin:hotkey:menu");
        var cap = Assert.Single(tile.GetVisualDescendants().OfType<Border>(),
            border => border.Classes.Contains("keycap"));
        Assert.Equal("Insert", Assert.IsType<TextBlock>(cap.Child).Text);

        UiFixture.Click(window, tile);
        Assert.Equal(["menu"], sent);

        window.RequestSecondaryAction(tile);
        Assert.Equal(["hotkey:menu", "hotkey:menu"], toggled);
    }

    [AvaloniaFact]
    public void TheCaptureFieldRecordsHeldKeysInPressOrderAndConsumesEnter()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        var field = new HotkeyCapture(TimeSpan.Zero);
        var cancel = new ActionButton { Title = "Cancel" };
        var clicked = false;
        cancel.Click += (_, _) => clicked = true;
        var content = new StackPanel();
        content.Children.Add(field);
        content.Children.Add(cancel);
        IReadOnlyList<int>? captured = null;
        field.Captured += keys => captured = keys;
        window.Surfaces.ShowConfirm(content, "Press the keys", field);
        Dispatcher.UIThread.RunJobs();

        window.KeyPress(Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.ControlLeft, null);
        window.KeyPress(Key.Enter, RawInputModifiers.Control, PhysicalKey.Enter, null);
        window.KeyRelease(Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.ControlLeft, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(captured);
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(clicked);
        Assert.Equal([HotkeyKeys.Control, 0x0D], captured);
    }

    [Theory]
    [InlineData(Key.LeftCtrl, HotkeyKeys.Control)]
    [InlineData(Key.RightAlt, HotkeyKeys.Alt)]
    [InlineData(Key.RightShift, HotkeyKeys.Shift)]
    [InlineData(Key.LWin, HotkeyKeys.Windows)]
    [InlineData(Key.Insert, HotkeyKeys.Insert)]
    [InlineData(Key.S, HotkeyKeys.S)]
    [InlineData(Key.D7, 0x37)]
    [InlineData(Key.F24, 0x87)]
    [InlineData(Key.NumPad3, 0x63)]
    [InlineData(Key.OemTilde, 0xC0)]
    public void CapturedKeysMapToTheVirtualKeysTheyAreSentAs(Key key, int virtualKey)
    {
        Assert.Equal(virtualKey, HotkeyCapture.VirtualKey(key));
    }

    private static (OverlayWindow Window, HotkeysOverlaySource Source) Open(UiFixture fixture, bool empty = false,
        int width = 1280, int height = 800)
    {
        var source = new HotkeysOverlaySource(empty);
        var window = fixture.Overlay(width, height);
        window.HotkeysHost.Attach(source);
        window.SetPins(["home.steam", "home.desktop", "hotkey:menu"]);
        HidePinToast(window);
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        Dispatcher.UIThread.RunJobs();
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.Hotkeys));
        Dispatcher.UIThread.RunJobs();
        return (window, source);
    }

    // Changing the fixture's pins confirms with the transient pin toast, which is not part of these scenes.
    private static void HidePinToast(OverlayWindow window)
    {
        Dispatcher.UIThread.RunJobs();
        UiFixture.Named<Control>(window, "PinToast").IsVisible = false;
    }

    private static void Click(OverlayWindow window, string tag)
    {
        UiFixture.Click(window, UiFixture.Tagged<Button>(window, tag));
        Dispatcher.UIThread.RunJobs();
    }

    private static void ScrollToTop(Window window)
    {
        foreach (var scroller in window.GetVisualDescendants().OfType<ScrollViewer>()
                     .Where(scroller => scroller.Name == "ContentScroller"))
        {
            scroller.Offset = default;
        }

        Dispatcher.UIThread.RunJobs();
    }

    // A surface closes after its touch grace, which the dispatcher's timer runs while the test awaits.
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

        Assert.False(window.Surfaces.HasActive);
        return;

        void Closed()
        {
            closed.TrySetResult();
        }
    }
}
