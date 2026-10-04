using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Overlay;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

public sealed class KeyboardEditingTests
{
    [AvaloniaFact]
    public void ClosingOneSheetLeavesTheOtherSheetsTextEntryWorking()
    {
        using UiFixture fixture = new();
        var first = fixture.Overlay(session: new OverlayWindow.SessionState());
        var second = fixture.Overlay(session: new OverlayWindow.SessionState());
        string? accepted = null;
        Assert.True(first.RequestText("First", "one", 0, _ => { }));
        Assert.True(second.RequestText("Second", "two", 0, value => accepted = value));
        first.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.True(second.HasActiveSurface);
        var keyboard = second.GetVisualDescendants().OfType<KeyboardPanel>().Single();
        UiFixture.Click(second, KeyButton(keyboard, Key.Enter));
        Assert.Equal("two", accepted);
        Assert.False(second.HasActiveSurface);
    }

    [AvaloniaFact]
    public void TypingReplacesTheSelectedTextAfterFocusMovesToAKey()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        var keyboard = new KeyboardPanel("Name", "before OLD after", 100);
        window.ShowKeyboardSurface(keyboard);
        Dispatcher.UIThread.RunJobs();
        var input = UiFixture.Named<TextBox>(keyboard, "Input");
        input.Focus();
        input.SelectionStart = 7;
        input.SelectionEnd = 10;
        UiFixture.Click(window, KeyButton(keyboard, Key.N));
        Assert.Equal("before n after", input.Text);
        Assert.Equal(8, input.CaretIndex);
    }

    [AvaloniaFact]
    public void NativeTypingSupportsUndoRedoAndTheFieldLengthLimit()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        var keyboard = new KeyboardPanel("Name", "1234", 6);
        window.ShowKeyboardSurface(keyboard);
        Dispatcher.UIThread.RunJobs();
        var input = UiFixture.Named<TextBox>(keyboard, "Input");
        Click(Key.D5);
        Assert.Equal("12345", input.Text);
        Click(Key.LeftCtrl);
        Click(Key.Z);
        Assert.Equal("1234", input.Text);
        Click(Key.LeftCtrl);
        Click(Key.Y);
        Assert.Equal("12345", input.Text);
        Click(Key.D6);
        Click(Key.D7);
        Assert.Equal("123456", input.Text);
        return;

        void Click(Key key)
        {
            UiFixture.Click(window, KeyButton(keyboard, key));
        }
    }

    [AvaloniaFact]
    public void AllPrintableWpaPassphraseCharactersExistInTheActualKeyLayout()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        var keyboard = new KeyboardPanel("Password", "", 100, true);
        window.ShowKeyboardSurface(keyboard);
        Dispatcher.UIThread.RunJobs();
        HashSet<char> characters = [' '];
        Collect();
        UiFixture.Click(window, KeyButton(keyboard, Key.LeftShift));
        Collect();
        Assert.All(Enumerable.Range(32, 95), value => Assert.Contains((char)value, characters));
        return;

        void Collect()
        {
            foreach (var button in keyboard.GetVisualDescendants().OfType<Button>()
                         .Where(button => button.IsEffectivelyVisible && button.Tag is Key))
            {
                if (button.Content is string { Length: 1 } text)
                {
                    characters.Add(text[0]);
                }
            }
        }
    }

    private static Button KeyButton(KeyboardPanel keyboard, Key key)
    {
        return keyboard.GetVisualDescendants().OfType<Button>()
            .Single(button => button.IsEffectivelyVisible && Equals(button.Tag, key));
    }
}
