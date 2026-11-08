using Avalonia.Controls;
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

public sealed class ProgramPickerTests
{
    [AvaloniaTheory]
    [InlineData("overlay-programs-picker-populated-980", "populated", 980, 640)]
    [InlineData("overlay-programs-picker-populated-1280", "populated", 1280, 800)]
    [InlineData("overlay-programs-picker-searching-980", "searching", 980, 640)]
    [InlineData("overlay-programs-picker-searching-1280", "searching", 1280, 800)]
    [InlineData("overlay-programs-picker-empty-980", "empty", 980, 640)]
    [InlineData("overlay-programs-picker-empty-1280", "empty", 1280, 800)]
    [InlineData("overlay-programs-picker-failure-980", "failure", 980, 640)]
    [InlineData("overlay-programs-picker-failure-1280", "failure", 1280, 800)]
    [InlineData("overlay-programs-picker-loading-980", "loading", 980, 640)]
    [InlineData("overlay-programs-picker-full-980", "full", 980, 640)]
    [InlineData("overlay-programs-picker-full-1280", "full", 1280, 800)]
    [InlineData("overlay-programs-list-980", "list", 980, 640)]
    [InlineData("overlay-programs-list-1280", "list", 1280, 800)]
    public async Task Scene(string name, string scene, int width, int height)
    {
        using UiFixture fixture = new();
        ProgramsOverlaySource source = new();
        switch (scene)
        {
            case "failure":
                source.Installed = Task.FromResult(new InstalledProgramList([], "Windows could not list the installed apps."));
                break;
            case "loading":
                source.Installed = new TaskCompletionSource<InstalledProgramList>().Task;
                break;
            case "full":
                source.Installed = Task.FromResult(new InstalledProgramList(ManyPrograms()));
                break;
        }

        var window = Open(fixture, source, width, height);
        if (scene != "list")
        {
            Click(window, "program.add");
        }

        switch (scene)
        {
            case "searching":
                await SearchAsync(window, "note");
                break;
            case "empty":
                await SearchAsync(window, "no such app");
                break;
        }

        Dispatcher.UIThread.RunJobs();
        foreach (var scroller in window.GetVisualDescendants().OfType<ScrollViewer>()
                     .Where(scroller => scroller.Name == "ContentScroller"))
        {
            scroller.Offset = default;
        }

        Dispatcher.UIThread.RunJobs();
        VisualBaseline.Verify(window, name);
    }

    [AvaloniaFact]
    public void PickingAnAppAddsItAtOnceAndReturnsToTheList()
    {
        using UiFixture fixture = new();
        ProgramsOverlaySource source = new();
        var window = Open(fixture, source);
        Click(window, "program.add");

        Click(window, "program.pick:app:xbox");

        var added = Assert.Single(source.Added);
        Assert.Equal("Xbox", added.Name);
        Assert.Equal("Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App", added.AppUserModelId);
        Assert.Equal("", added.Executable);
        Assert.NotNull(UiFixture.Tagged<Button>(window, "program.add"));
    }

    [AvaloniaFact]
    public void TheWholeListIsDrawnAndALetterJumpsToIt()
    {
        using UiFixture fixture = new();
        ProgramsOverlaySource source = new() { Installed = Task.FromResult(new InstalledProgramList(ManyPrograms())) };
        var window = Open(fixture, source, 980, 640);
        Click(window, "program.add");

        Assert.Equal(ManyPrograms().Length, window.GetVisualDescendants().OfType<IconActionRow>().Count(row =>
            (row.Tag as string)?.StartsWith("program.pick:", StringComparison.Ordinal) == true));
        Assert.Equal(ManyPrograms().Length, source.Icons.Count);
        var scroller = window.GetVisualDescendants().OfType<ScrollViewer>()
            .Single(item => item.Name == "ContentScroller");
        Assert.Equal(0, scroller.Offset.Y);
        Assert.False(UiFixture.Tagged<Button>(window, "alphabet:Q").IsEffectivelyEnabled);

        UiFixture.Tagged<Button>(window, "alphabet:M").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.True(scroller.Offset.Y > 0);
        Assert.True(UiFixture.Tagged<Button>(window, "program.pick:app:m0").IsFocused);
        var bar = window.GetVisualDescendants().OfType<AlphabetJumpBar>().Single();
        Assert.Equal(scroller.Offset.Y, ((Avalonia.Media.TranslateTransform)bar.RenderTransform!).Y);
    }

    [Theory]
    [InlineData("Xbox", 'X')]
    [InlineData("Ägypten", 'A')]
    [InlineData("7-Zip", '#')]
    [InlineData("  notepad", 'N')]
    public void ANameFilesUnderItsLetter(string name, char letter)
    {
        Assert.Equal(letter, AlphabetJumpBar.Bucket(name));
    }

    [AvaloniaFact]
    public void EachProgramHasItsCommandsBesideIt()
    {
        using UiFixture fixture = new();
        var window = Open(fixture, new ProgramsOverlaySource());

        Click(window, "program.edit:monitor");
        Assert.NotNull(UiFixture.Tagged<Button>(window, "program.name"));
        Assert.True(window.TryCancelSubView());
        Dispatcher.UIThread.RunJobs();

        Assert.IsType<RowCommandButton>(UiFixture.Tagged<Button>(window, "program.pin:monitor"));
        Click(window, "program.remove:monitor");
        Assert.True(UiFixture.Tagged<ActionButton>(window, "confirm.cancel").IsFocused);
    }
    [AvaloniaFact]
    public async Task AnAlreadyAddedAppIsMarkedAndAnEmptySearchOffersToClearIt()
    {
        using UiFixture fixture = new();
        var window = Open(fixture, new ProgramsOverlaySource());
        Click(window, "program.add");
        Assert.Equal("Added", UiFixture.Tagged<IconActionRow>(window, "program.pick:app:monitor").TrailingText);

        await SearchAsync(window, "no such app");
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<IconActionRow>(), row => row.IsEffectivelyVisible
            && (row.Tag as string)?.StartsWith("program.pick:", StringComparison.Ordinal) == true);

        Click(window, "program.search.clear");
        Assert.Equal(5, window.GetVisualDescendants().OfType<IconActionRow>().Count(row =>
            (row.Tag as string)?.StartsWith("program.pick:", StringComparison.Ordinal) == true));
    }

    [AvaloniaFact]
    public void AFailedListingStillOffersTheManualChoiceAndARetry()
    {
        using UiFixture fixture = new();
        ProgramsOverlaySource source = new()
        {
            Installed = Task.FromResult(new InstalledProgramList([], "Windows could not list the installed apps."))
        };
        var window = Open(fixture, source);
        Click(window, "program.add");

        Assert.True(UiFixture.Tagged<Button>(window, "program.browse").IsEffectivelyEnabled);
        source.Installed = Task.FromResult(new InstalledProgramList(
            [new InstalledProgram("app:xbox", "Xbox", "Microsoft.GamingApp_8wekyb3d8bbwe!App", null, "")]));
        Click(window, "program.retry");
        Assert.NotNull(UiFixture.Tagged<IconActionRow>(window, "program.pick:app:xbox"));
    }

    [AvaloniaFact]
    public void TheDeviceBackKeyPopsThePickerAndReleasesItsIcons()
    {
        using UiFixture fixture = new();
        ProgramsOverlaySource source = new();
        var window = Open(fixture, source);
        Click(window, "program.add");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(5, source.Icons.Count);

        Assert.True(window.TryCancelSubView());
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(UiFixture.Tagged<Button>(window, "program.add"));
        // A disposed WriteableBitmap refuses to lock its framebuffer.
        Assert.All(source.Icons, icon => Assert.ThrowsAny<Exception>(() => icon.Lock()));
    }

    /// <summary>More apps than fit on a page, in sorted order, leaving some letters without any.</summary>
    private static InstalledProgram[] ManyPrograms()
    {
        return "ABCDEFGHIJKLMNOPRSTUVW".SelectMany(letter => Enumerable.Range(0, 4).Select(index =>
            new InstalledProgram($"app:{char.ToLowerInvariant(letter)}{index}", $"{letter}pp number {index}", null,
                $@"C:\Tools\{letter}{index}.exe", ""))).ToArray();
    }

    private static OverlayWindow Open(UiFixture fixture, ProgramsOverlaySource source, int width = 1280,
        int height = 800)
    {
        var window = fixture.Overlay(width, height);
        window.ProgramsHost.Attach(source);
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        Dispatcher.UIThread.RunJobs();
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.Programs));
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Click(OverlayWindow window, string tag)
    {
        UiFixture.Click(window, UiFixture.Tagged<Button>(window, tag));
        Dispatcher.UIThread.RunJobs();
    }

    private static Task SearchAsync(OverlayWindow window, string text)
    {
        return EnterAsync(window, UiFixture.Tagged<Button>(window, "program.search"), text);
    }

    private static async Task EnterAsync(OverlayWindow window, Button row, string text)
    {
        UiFixture.Click(window, row);
        Dispatcher.UIThread.RunJobs();
        var keyboard = window.GetVisualDescendants().OfType<KeyboardPanel>().Single();
        UiFixture.Named<TextBox>(keyboard, "Input").Text = text;
        UiFixture.Click(window, keyboard.GetVisualDescendants().OfType<Button>()
            .Single(button => button.IsEffectivelyVisible && Equals(button.Tag, Key.Enter)));
        var started = Environment.TickCount64;
        while (window.GetVisualDescendants().OfType<KeyboardPanel>().Any())
        {
            Assert.True(Environment.TickCount64 - started < 5000, "The keyboard did not close.");
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
        }

        Dispatcher.UIThread.RunJobs();
    }
}
