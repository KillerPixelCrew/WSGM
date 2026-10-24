using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Overlay;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

public sealed class FullKeyboardTests
{
    [AvaloniaTheory]
    [InlineData(1280, 720, 1.0)]
    [InlineData(3840, 2160, 2.0)]
    [InlineData(980, 720, 1.0)]
    [InlineData(980, 640, 1.0)]
    public void EveryKeyboardPageFitsAtSupportedScales(int width, int height, double scale)
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay(width, height, scale);
        var keyboard = new KeyboardPanel("Name", "", 100);
        window.ShowKeyboardSurface(keyboard);
        Dispatcher.UIThread.RunJobs();
        var buttons = keyboard.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Tag is Key && button.IsEffectivelyVisible).ToArray();
        Assert.True(keyboard.Bounds.Height <= 240, "The touch keyboard should fit within 240 DIPs.");
        foreach (var key in new[] { Key.Q, Key.A, Key.Z, Key.LeftCtrl, Key.LeftShift, Key.Back, Key.Enter, Key.Space })
        {
            var button = buttons.Single(button => button.IsEffectivelyVisible && Equals(button.Tag, key));
            Assert.True(button.IsEffectivelyVisible);
            Assert.True(button.Bounds.Width >= 44);
            Assert.True(button.Bounds.Height >= 44);
            AssertFits(button);
        }

        foreach (var button in buttons)
        {
            AssertFits(button);
        }

        Page(window, keyboard, "More");
        foreach (var key in Enumerable.Range(0, 12).Select(index => Key.F1 + index)
                     .Concat([Key.LeftAlt, Key.LWin, Key.CapsLock, Key.Tab, Key.Home, Key.End, Key.Delete, Key.Up, Key.Down,
                         Key.Insert, Key.PrintScreen, Key.Scroll, Key.Pause]))
        {
            var button = keyboard.GetVisualDescendants().OfType<Button>()
                .Single(button => button.IsEffectivelyVisible && Equals(button.Tag, key));
            AssertFits(button);
        }

        Page(window, keyboard, "Num pad");
        foreach (var button in keyboard.GetVisualDescendants().OfType<Button>()
                     .Where(button => button.Tag is Key && button.IsEffectivelyVisible))
        {
            AssertFits(button);
        }

        foreach (var key in new[]
                 {
                     Key.NumLock,
                     Key.NumPad0, Key.NumPad1, Key.NumPad2, Key.NumPad3, Key.NumPad4, Key.NumPad5,
                     Key.NumPad6, Key.NumPad7, Key.NumPad8, Key.NumPad9, Key.Add, Key.Subtract,
                     Key.Multiply, Key.Divide, Key.Decimal
                 })
        {
            Assert.Contains(keyboard.GetVisualDescendants().OfType<Button>(),
                button => button.IsEffectivelyVisible && Equals(button.Tag, key));
        }

        return;

        void AssertFits(Button button)
        {
            Assert.True(button.Bounds.Width >= 44);
            Assert.True(button.Bounds.Height >= 44);
            var origin = button.TranslatePoint(default, window)!.Value;
            var far = button.TranslatePoint(new Point(button.Bounds.Width, button.Bounds.Height), window)!.Value;
            Assert.InRange(origin.X, 0, window.Bounds.Width);
            Assert.InRange(origin.Y, 0, window.Bounds.Height);
            Assert.InRange(far.X, 0, window.Bounds.Width);
            Assert.InRange(far.Y, 0, window.Bounds.Height);
        }
    }

    [AvaloniaFact]
    public void NumericPadTypesAndNumLockSelectsNavigation()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        var keyboard = new KeyboardPanel("Name", "", 100);
        window.ShowKeyboardSurface(keyboard);
        Dispatcher.UIThread.RunJobs();
        var input = UiFixture.Named<TextBox>(keyboard, "Input");
        Page(window, keyboard, "More");
        Page(window, keyboard, "Num pad");
        Click(Key.NumPad1);
        Click(Key.NumPad2);
        Click(Key.NumPad3);
        Assert.Equal("123", input.Text);
        Click(Key.NumLock);
        Click(Key.NumPad7);
        Click(Key.Decimal);
        Assert.Equal("23", input.Text);
        Page(window, keyboard, "ABC");
        Assert.True(keyboard.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Tag, Key.Q)).IsEffectivelyVisible);
        return;

        void Click(Key key)
        {
            UiFixture.Click(window, keyboard.GetVisualDescendants().OfType<Button>()
                .Single(button => button.IsEffectivelyVisible && Equals(button.Tag, key)));
        }
    }

    [AvaloniaFact]
    public void CtrlAReplacesSelectionAndOneShotModifiersClear()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        var keyboard = new KeyboardPanel("Name", "old", 100);
        window.ShowKeyboardSurface(keyboard);
        Dispatcher.UIThread.RunJobs();
        var input = UiFixture.Named<TextBox>(keyboard, "Input");
        Click(Key.LeftCtrl);
        Assert.Contains("latched", KeyButton(Key.LeftCtrl).Classes);
        Click(Key.A);
        Assert.Equal(0, Math.Min(input.SelectionStart, input.SelectionEnd));
        Assert.Equal(3, Math.Max(input.SelectionStart, input.SelectionEnd));
        Assert.DoesNotContain("latched", KeyButton(Key.LeftCtrl).Classes);
        Click(Key.X);
        Assert.Equal("x", input.Text);
        Page(window, keyboard, "More");
        Click(Key.CapsLock);
        Page(window, keyboard, "ABC");
        Click(Key.Y);
        Click(Key.Z);
        Assert.Equal("xYZ", input.Text);
        Page(window, keyboard, "More");
        Assert.Contains("latched", KeyButton(Key.CapsLock).Classes);

        return;

        Button KeyButton(Key key)
        {
            return keyboard.GetVisualDescendants().OfType<Button>()
                .Single(button => button.IsEffectivelyVisible && Equals(button.Tag, key));
        }

        void Click(Key key)
        {
            UiFixture.Click(window, KeyButton(key));
        }
    }

    [AvaloniaFact]
    public void ShiftArrowHomeEndAndDeleteEditTheLocalField()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        var keyboard = new KeyboardPanel("Name", "abcd", 100);
        window.ShowKeyboardSurface(keyboard);
        Dispatcher.UIThread.RunJobs();
        var input = UiFixture.Named<TextBox>(keyboard, "Input");
        Page(window, keyboard, "More");
        Click(Key.End);
        Click(Key.LeftShift);
        Click(Key.Left);
        Assert.Equal(1, Math.Abs(input.SelectionEnd - input.SelectionStart));
        Click(Key.Delete);
        Assert.Equal("abc", input.Text);
        Click(Key.Home);
        Click(Key.Delete);
        Assert.Equal("bc", input.Text);
        Click(Key.End);
        Click(Key.Back);
        Assert.Equal("b", input.Text);

        return;

        void Click(Key key)
        {
            UiFixture.Click(window, keyboard.GetVisualDescendants().OfType<Button>()
                .Single(button => button.IsEffectivelyVisible && Equals(button.Tag, key)));
        }
    }

    [AvaloniaFact]
    public void FunctionKeysDeliverRealLocalKeyEventsWithLatchedModifiers()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        var keyboard = new KeyboardPanel("Name", "unchanged", 100);
        window.ShowKeyboardSurface(keyboard);
        Dispatcher.UIThread.RunJobs();
        var input = UiFixture.Named<TextBox>(keyboard, "Input");
        var received = new List<(Key, KeyModifiers)>();
        input.KeyDown += (_, args) => received.Add((args.Key, args.KeyModifiers));
        Page(window, keyboard, "More");
        Click(Key.LeftCtrl);
        Click(Key.LeftAlt);
        Click(Key.F5);
        Assert.Contains((Key.F5, KeyModifiers.Control | KeyModifiers.Alt), received);
        Assert.Equal("unchanged", input.Text);
        Assert.True(window.HasActiveSurface);

        return;

        void Click(Key key)
        {
            UiFixture.Click(window, keyboard.GetVisualDescendants().OfType<Button>()
                .Single(button => button.IsEffectivelyVisible && Equals(button.Tag, key)));
        }
    }

    [AvaloniaFact]
    public async Task ClipboardChordsCopyCutAndPasteThroughTheLocalEditor()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        var keyboard = new KeyboardPanel("Name", "copy me", 100);
        window.ShowKeyboardSurface(keyboard);
        Dispatcher.UIThread.RunJobs();
        var input = UiFixture.Named<TextBox>(keyboard, "Input");
        Click(Key.LeftCtrl);
        Click(Key.A);
        Click(Key.LeftCtrl);
        Click(Key.C);
        Dispatcher.UIThread.RunJobs();
        Click(Key.LeftCtrl);
        Click(Key.X);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("", input.Text);
        var pasted = new TaskCompletionSource();
        input.TextChanged += (_, _) =>
        {
            if (input.Text == "copy me")
            {
                pasted.TrySetResult();
            }
        };
        Click(Key.LeftCtrl);
        Click(Key.V);
        await pasted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("copy me", input.Text);

        return;

        void Click(Key key)
        {
            UiFixture.Click(window, keyboard.GetVisualDescendants().OfType<Button>()
                .Single(button => button.IsEffectivelyVisible && Equals(button.Tag, key)));
        }
    }

    private static void Page(Window window, KeyboardPanel keyboard, string label)
    {
        UiFixture.Click(window, keyboard.GetVisualDescendants().OfType<Button>()
            .Single(button => button.IsEffectivelyVisible && Equals(button.Content, label)));
    }
}
