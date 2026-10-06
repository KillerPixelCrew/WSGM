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
    public async Task ClosingOneSheetLeavesTheOtherSheetsTextEntryWorking()
    {
        using UiFixture fixture = new();
        var first = fixture.Overlay(session: new OverlayWindow.SessionState());
        var second = fixture.Overlay(session: new OverlayWindow.SessionState());
        string? accepted = null;
        Assert.True(first.RequestText("First", "one", 0, _ => { }));
        Assert.True(second.RequestText("Second", "two", 0, value => accepted = value));
        var firstKeyboard = first.GetVisualDescendants().OfType<KeyboardPanel>().Single();
        var keyboard = second.GetVisualDescendants().OfType<KeyboardPanel>().Single();
        Assert.Same(first, TopLevel.GetTopLevel(firstKeyboard));
        Assert.Same(second, TopLevel.GetTopLevel(keyboard));
        Dispatcher.UIThread.RunJobs();
        Assert.Same(keyboard.DefaultFocusTarget, second.FocusManager.GetFocusedElement());
        first.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.False(first.HasActiveSurface);
        Assert.Null(TopLevel.GetTopLevel(firstKeyboard));
        Assert.True(second.HasActiveSurface);
        Assert.Same(keyboard, second.GetVisualDescendants().OfType<KeyboardPanel>().Single());
        Assert.Same(second, TopLevel.GetTopLevel(keyboard));
        Assert.Same(keyboard.DefaultFocusTarget, second.FocusManager.GetFocusedElement());
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closures = 0;
        second.SurfaceClosed += () =>
        {
            closures++;
            closed.TrySetResult();
        };
        UiFixture.Click(second, KeyButton(keyboard, Key.Enter));
        Assert.Equal("two", accepted);
        Assert.False(keyboard.IsEffectivelyEnabled);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(second.HasActiveSurface);
        Assert.Null(TopLevel.GetTopLevel(keyboard));
        Assert.Equal(1, closures);
    }

    [AvaloniaFact]
    public async Task OpeningTextEntrySettlesThePendingUtilityCloseBeforeCoveringAnotherSurface()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        window.ShowBrightnessSurface();
        var utility = Assert.IsAssignableFrom<Control>(window.ActiveSurfaceNavigationRoot);
        var closures = 0;
        window.SurfaceClosed += () => closures++;
        Assert.True(window.CloseActiveSurface());
        Assert.False(utility.IsEffectivelyEnabled);

        string? accepted = null;
        Assert.True(window.RequestText("Name", "replacement", 0, value => accepted = value));
        Dispatcher.UIThread.RunJobs();
        var keyboard = window.GetVisualDescendants().OfType<KeyboardPanel>().Single();
        Assert.Null(TopLevel.GetTopLevel(utility));
        Assert.Equal(1, closures);
        Assert.Same(window, TopLevel.GetTopLevel(keyboard));
        Assert.Same(keyboard.DefaultFocusTarget, window.FocusManager.GetFocusedElement());

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.SurfaceClosed += () => closed.TrySetResult();
        UiFixture.Click(window, KeyButton(keyboard, Key.Enter));
        Assert.Equal("replacement", accepted);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, closures);
        Assert.False(window.HasActiveSurface);
        Assert.Null(TopLevel.GetTopLevel(keyboard));
        Assert.True(UiFixture.Named<Grid>(window, "DeckContent").IsHitTestVisible);
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
