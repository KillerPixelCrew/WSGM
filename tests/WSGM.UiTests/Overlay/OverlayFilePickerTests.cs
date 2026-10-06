using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Overlay;
using WSGM.Testing;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

public sealed class OverlayFilePickerTests
{
    [AvaloniaFact]
    public async Task ListsEveryMatchingFileWithoutPagingAndFocusesTheFirstEntry()
    {
        using var directory = new TemporaryDirectory();
        for (var index = 0; index < 450; index++)
        {
            File.WriteAllText(directory.GetPath($"entry-{index:D3}.png"), string.Empty);
        }

        File.WriteAllText(directory.GetPath("ignored.txt"), string.Empty);
        using var fixture = new UiFixture();
        var picker = new OverlayFilePicker(false, [".png"]);
        var window = new Window { Content = new ScrollViewer { Content = picker }, Width = 600, Height = 400 };
        try
        {
            window.Show();
            await EnterDirectoryAsync(window, picker, directory.Root);
            var entries = picker.GetLogicalDescendants().OfType<ActionButton>()
                .Where(button => button.Tag is string).ToArray();
            Assert.Equal(450, entries.Length);
            Assert.Equal("entry-000.png", entries[0].Title);
            Assert.Same(entries[0], window.FocusManager!.GetFocusedElement());
            Assert.DoesNotContain(picker.GetLogicalDescendants().OfType<ActionButton>(),
                button => button.Title == "Show more");

            string? chosen = null;
            picker.Completed += path => chosen = path;
            UiFixture.Click(window, entries[0]);
            Assert.Equal(directory.GetPath("entry-000.png"), chosen);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task FolderNavigationFocusesTheFirstChildAndChoosesTheCurrentFolder()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.GetPath("child"));
        File.WriteAllText(directory.GetPath("hidden.png"), string.Empty);
        using var fixture = new UiFixture();
        var picker = new OverlayFilePicker(true, []);
        var window = new Window { Content = picker, Width = 600, Height = 400 };
        try
        {
            window.Show();
            await EnterDirectoryAsync(window, picker, directory.Root);
            var entry = Assert.Single(picker.GetLogicalDescendants().OfType<ActionButton>(),
                button => button.Tag is string);
            Assert.Equal("child", entry.Title);
            Assert.Same(entry, window.FocusManager!.GetFocusedElement());
            string? chosen = null;
            picker.Completed += path => chosen = path;
            UiFixture.Click(window, picker.GetLogicalDescendants().OfType<ActionButton>()
                .Single(button => button.Title == "Choose this folder"));
            Assert.Equal(directory.Root, chosen);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task EnteringDirectoryThroughTheKeyboardReturnsFocusToItsFirstFile()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(directory.GetPath("first.png"), string.Empty);
        using var fixture = new UiFixture();
        var window = fixture.Overlay();
        var selection = window.PickLocalPathAsync(false, ".png");
        Dispatcher.UIThread.RunJobs();
        var picker = window.GetVisualDescendants().OfType<OverlayFilePicker>().Single();
        var loaded = DirectoryLoaded(picker, directory.Root);
        UiFixture.Click(window, picker.GetLogicalDescendants().OfType<ActionButton>()
            .Single(button => button.Title == "Enter a path"));
        var keyboard = window.GetVisualDescendants().OfType<KeyboardPanel>().Single();
        Assert.Same(window, TopLevel.GetTopLevel(picker));
        Assert.Same(window, TopLevel.GetTopLevel(keyboard));
        UiFixture.Named<TextBox>(keyboard, "Input").Text = directory.Root;
        var keyboardClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.SurfaceClosed += () => keyboardClosed.TrySetResult();
        UiFixture.Click(window, keyboard.GetVisualDescendants().OfType<Button>()
            .Single(button => button.IsEffectivelyVisible && Equals(button.Tag, Key.Enter)));
        await loaded.WaitAsync(TimeSpan.FromSeconds(5));
        await keyboardClosed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Dispatcher.UIThread.RunJobs();

        var entry = Assert.Single(picker.GetLogicalDescendants().OfType<ActionButton>(),
            button => button.Tag is string);
        Assert.Equal("first.png", entry.Title);
        Assert.Null(TopLevel.GetTopLevel(keyboard));
        Assert.Same(picker.DefaultFocusTarget, entry);
        Assert.Same(entry, window.FocusManager.GetFocusedElement());
        Assert.False(selection.IsCompleted);

        var pickerClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.SurfaceClosed += () => pickerClosed.TrySetResult();
        UiFixture.Click(window, entry);
        Assert.Equal(directory.GetPath("first.png"), await selection.WaitAsync(TimeSpan.FromSeconds(2)));
        await pickerClosed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(window.HasActiveSurface);
        Assert.Null(TopLevel.GetTopLevel(picker));
    }

    [AvaloniaFact]
    public async Task EnteringAFilePathClosesTheKeyboardAndItsCoveredPickerOnce()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.GetPath("chosen.png");
        File.WriteAllText(path, string.Empty);
        using var fixture = new UiFixture();
        var window = fixture.Overlay();
        var selection = window.PickLocalPathAsync(false, ".png");
        Dispatcher.UIThread.RunJobs();
        var picker = window.GetVisualDescendants().OfType<OverlayFilePicker>().Single();
        UiFixture.Click(window, picker.GetLogicalDescendants().OfType<ActionButton>()
            .Single(button => button.Title == "Enter a path"));
        var keyboard = window.GetVisualDescendants().OfType<KeyboardPanel>().Single();
        UiFixture.Named<TextBox>(keyboard, "Input").Text = path;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closures = 0;
        var pickerDetachments = 0;
        picker.DetachedFromVisualTree += (_, _) => pickerDetachments++;
        window.SurfaceClosed += () =>
        {
            closures++;
            closed.TrySetResult();
        };

        UiFixture.Click(window, keyboard.GetVisualDescendants().OfType<Button>()
            .Single(button => button.IsEffectivelyVisible && Equals(button.Tag, Key.Enter)));
        Assert.Equal(path, await selection.WaitAsync(TimeSpan.FromSeconds(2)));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(window.HasActiveSurface);
        Assert.Null(TopLevel.GetTopLevel(keyboard));
        Assert.Null(TopLevel.GetTopLevel(picker));
        Assert.Equal(1, pickerDetachments);
        Assert.Equal(1, closures);
        Assert.True(UiFixture.Named<Grid>(window, "DeckContent").IsHitTestVisible);
    }

    private static async Task EnterDirectoryAsync(Window window, OverlayFilePicker picker, string path)
    {
        var loaded = DirectoryLoaded(picker, path);
        picker.TextEntryRequested += (_, accept) => accept(path);
        UiFixture.Click(window, picker.GetLogicalDescendants().OfType<ActionButton>()
            .Single(button => button.Title == "Enter a path"));
        await loaded.WaitAsync(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();
    }

    private static Task DirectoryLoaded(OverlayFilePicker picker, string path)
    {
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        picker.PropertyChanged += (_, change) =>
        {
            if (change.Property == ContentControl.ContentProperty && picker.Content is StackPanel body
                                                                  && body.Children.OfType<TextBlock>().FirstOrDefault()
                                                                      ?.Text == path)
            {
                body.Children.CollectionChanged += (_, _) =>
                {
                    if (!body.Children.OfType<TextBlock>().Any(text => text.Text == "Reading folders…"))
                    {
                        loaded.TrySetResult();
                    }
                };
            }
        };
        return loaded.Task;
    }
}
