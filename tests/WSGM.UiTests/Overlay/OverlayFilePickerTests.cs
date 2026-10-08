using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SteamUiToolkit;
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
        var picker = Picker(false);
        var window = new Window { Content = picker, Width = 800, Height = 500 };
        try
        {
            window.Show();
            await EnterDirectoryAsync(window, picker, directory.Root);
            var entries = picker.Entries.Items.OfType<SteamFileEntry>().ToArray();
            Assert.Equal(450, entries.Length);
            Assert.Equal("entry-000.png", entries[0].Name);
            Assert.Same(picker.DefaultFocusTarget, window.FocusManager!.GetFocusedElement());
            Assert.True(picker.GetVisualDescendants().OfType<ListBoxItem>().Count() < 450,
                "the file list is virtualized");
            Assert.DoesNotContain(picker.GetLogicalDescendants().OfType<ActionButton>(),
                button => button.Title == "Show more");

            string? chosen = null;
            picker.Completed += path => chosen = path;
            UiFixture.Key(window, Key.Enter);
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
        var picker = Picker(true);
        var window = new Window { Content = picker, Width = 600, Height = 400 };
        try
        {
            window.Show();
            await EnterDirectoryAsync(window, picker, directory.Root);
            var entry = Assert.Single(picker.Entries.Items.OfType<SteamFileEntry>());
            Assert.Equal("child", entry.Name);
            Assert.Same(picker.DefaultFocusTarget, window.FocusManager!.GetFocusedElement());
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
        var picker = Picker(false);
        var selection = window.PickLocalPathAsync(picker, false);
        Dispatcher.UIThread.RunJobs();
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

        var entry = Assert.Single(picker.Entries.Items.OfType<SteamFileEntry>());
        Assert.Equal("first.png", entry.Name);
        Assert.Null(TopLevel.GetTopLevel(keyboard));
        Assert.Same(picker.DefaultFocusTarget, window.FocusManager.GetFocusedElement());
        Assert.False(selection.IsCompleted);

        var pickerClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.SurfaceClosed += () => pickerClosed.TrySetResult();
        UiFixture.Key(window, Key.Enter);
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
        var picker = Picker(false);
        var selection = window.PickLocalPathAsync(picker, false);
        Dispatcher.UIThread.RunJobs();
        UiFixture.Click(window, picker.GetLogicalDescendants().OfType<ActionButton>()
            .Single(button => button.Title == "Enter a path" || button.Description == "Enter a path"));
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

    [AvaloniaFact]
    public async Task ControllerNavigationReachesVirtualizedRowsWithCurrentNames()
    {
        using var directory = new TemporaryDirectory();
        for (var index = 0; index < 160; index++)
        {
            File.WriteAllText(directory.GetPath($"file-{index:000}.png"), "");
        }

        using var fixture = new UiFixture();
        var picker = Picker(false);
        var window = new Window { Content = picker, Width = 800, Height = 500 };
        try
        {
            window.Show();
            await EnterDirectoryAsync(window, picker, directory.Root);
            for (var index = 0; index < 120; index++)
            {
                Assert.True(picker.Navigate(NavigationDirection.Down));
            }

            Dispatcher.UIThread.RunJobs();
            Assert.Equal(120, picker.Entries.SelectedIndex);
            var container = Assert.IsType<ListBoxItem>(picker.DefaultFocusTarget);
            Assert.Contains(container.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "file-120.png");
            string? selected = null;
            picker.Completed += path => selected = path;
            UiFixture.Key(window, Key.Enter);
            Assert.Equal(directory.GetPath("file-120.png"), selected);
        }
        finally
        {
            window.Close();
        }
    }

    private static OverlayFilePicker Picker(bool folder)
    {
        return new OverlayFilePicker(folder, folder ? [] : [".png"],
            _ => Task.FromResult(new SteamFilePlaces([])));
    }

    private static Task DirectoryLoaded(OverlayFilePicker picker, string path)
    {
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        picker.DirectoryLoaded += listing =>
        {
            if (listing.Path == path)
            {
                loaded.TrySetResult();
            }
        };
        return loaded.Task;
    }
}
