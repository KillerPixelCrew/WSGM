using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Input;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.UiTests.Fakes;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Visual;

/// <summary>
///     Render-only captures of the Emulator Manager's and the Game Library's live progress. Explicit fakes publish a
///     progress tick and then the end of the work; each tick must update the level's progress element in place and
///     each end must redraw the level. Nothing is installed, scanned or written to Steam.
/// </summary>
public static class PreviewLiveProgress
{
    /// <summary>Exports every live progress case at one viewport.</summary>
    /// <param name="directory">The output folder.</param>
    /// <param name="width">The viewport width in DIPs.</param>
    /// <param name="height">The viewport height in DIPs.</param>
    public static void Export(string directory, int width, int height)
    {
        Directory.CreateDirectory(directory);
        ExportEmulators(directory, width, height);
        ExportLibrary(directory, width, height);
    }

    /// <summary>
    ///     The installed emulators while an install runs: <c>emulators-busy</c>, the status tick
    ///     <c>emulators-busy-tick</c> and the finished operation <c>emulators-done</c>.
    /// </summary>
    private static void ExportEmulators(string directory, int width, int height)
    {
        using var fixture = new UiFixture();
        var window = PreviewTools.Create(fixture, width, height);
        var source = PreviewEmulators.Create();
        source.State = source.State with { Busy = true, Status = "Downloading RPCS3" };
        UiFixture.Named<EmulatorManagerView>(window, "EmulatorManagerHost").Attach(source);
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.EmulatorManager));
        Dispatcher.UIThread.RunJobs();
        Save(window, directory, "emulators-busy", width, height);

        var progress = Single<OverlayProgress>(window, "emu.progress");
        var stop = Single<ActionButton>(window, "emu.stop");
        var refresh = Single<ActionButton>(window, "emu.refresh");
        Require(!refresh.IsEnabled && stop.IsEffectivelyVisible, "The busy manager did not disable Check for updates.");
        // A redraw reconciles every retained row and clears an opacity its fresh copy did not set, so a marked
        // opacity survives only while the level is not drawn again.
        refresh.Opacity = 0.5;

        source.Publish(source.State with { Status = "Extracting RPCS3" });
        Dispatcher.UIThread.RunJobs();
        Require(ReferenceEquals(progress, Single<OverlayProgress>(window, "emu.progress")),
            "The status tick replaced the progress element.");
        Require(ReferenceEquals(stop, Single<ActionButton>(window, "emu.stop")), "The status tick replaced Stop.");
        Require(ReferenceEquals(refresh, Single<ActionButton>(window, "emu.refresh")) && refresh.Opacity == 0.5,
            "The status tick redrew the level.");
        Require(progress.State?.Text == "Extracting RPCS3",
            "The status tick did not update the phase line: " + progress.State?.Text);
        refresh.ClearValue(Control.OpacityProperty);
        Save(window, directory, "emulators-busy-tick", width, height);

        refresh.Opacity = 0.5;
        source.Publish(source.State with { Busy = false, Status = "RPCS3 installed." });
        Dispatcher.UIThread.RunJobs();
        refresh = Single<ActionButton>(window, "emu.refresh");
        Require(refresh.IsEnabled && refresh.Opacity == 1, "The operation's end did not redraw the level.");
        Require(!Single<ActionButton>(window, "emu.stop").IsVisible, "Stop stayed after the operation ended.");
        Save(window, directory, "emulators-done", width, height);
        window.Close();
    }

    /// <summary>
    ///     The Game Library while an apply saves titles: the Sources level and its tick
    ///     (<c>library-sources-applying</c>, <c>library-sources-tick</c>), the home level with Stop in its progress
    ///     (<c>library-home-applying</c>) and the finished apply (<c>library-home-done</c>).
    /// </summary>
    private static void ExportLibrary(string directory, int width, int height)
    {
        using var fixture = new UiFixture();
        var source = new FakeLibrarySource();
        var idle = source.State;
        var window = PreviewTools.Create(fixture, width, height, source);
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.SteamGameLibrary));
        Dispatcher.UIThread.RunJobs();
        Click(window, "Sources (2 on)");

        var applying = idle with { Phase = GameLibraryPhases.Applying, Loading = true, Progress = 1, ProgressTotal = 3 };
        Publish(source, applying);
        Require(!Single<ActionButton>(window, "source:xbox").IsEnabled, "The apply did not disable the sources.");
        Save(window, directory, "library-sources-applying", width, height);

        var progress = Single<OverlayProgress>(window, "library.progress");
        var stop = Single<ActionButton>(window, "stop");
        var row = Single<ActionButton>(window, "source:xbox");
        row.Opacity = 0.5;
        // A saved title publishes the whole state again, with that title's entry changed.
        var saved = applying with
        {
            Progress = 2,
            Entries = [applying.Entries[0] with { Action = "Update", AppId = 44 }, .. applying.Entries.Skip(1)]
        };
        Publish(source, saved);
        Require(ReferenceEquals(progress, Single<OverlayProgress>(window, "library.progress")),
            "The saved title replaced the progress element.");
        Require(ReferenceEquals(stop, Single<ActionButton>(window, "stop")), "The saved title replaced Stop.");
        Require(ReferenceEquals(row, Single<ActionButton>(window, "source:xbox")) && row.Opacity == 0.5,
            "The saved title redrew the Sources level.");
        Require(progress.State?.Text == "Saving 2 of 3…",
            "The saved title did not update the phase line: " + progress.State?.Text);
        var bar = progress.GetVisualDescendants().OfType<ProgressBar>().Single();
        Require(Math.Abs(bar.Value - 2.0 / 3) < 0.0001 && !bar.IsIndeterminate,
            "The saved title did not move the bar: " + bar.Value);
        row.ClearValue(Control.OpacityProperty);
        Save(window, directory, "library-sources-tick", width, height);

        UiFixture.Key(window, Key.Escape);
        Dispatcher.UIThread.RunJobs();
        Require(Single<ActionButton>(window, "stop").IsEffectivelyVisible, "The home level has no Stop while saving.");
        var home = Single<OverlayProgress>(window, "library.progress");
        Publish(source, saved with { Progress = 3 });
        Require(ReferenceEquals(home, Single<OverlayProgress>(window, "library.progress"))
                && home.State?.Text == "Saving 3 of 3…", "The home level did not update its progress in place.");
        Save(window, directory, "library-home-applying", width, height);

        Publish(source, idle with { Phase = GameLibraryPhases.Done, Notice = "Saved 3 titles to Steam." });
        Require(FocusSearch.First<ActionButton>(window, button => button.Title == "Scan for games") is not null,
            "The apply's end did not redraw the home level.");
        Require(!Single<OverlayProgress>(window, "library.progress").IsVisible,
            "The progress element stayed visible after the apply ended.");
        Save(window, directory, "library-home-done", width, height);
        window.Close();
    }

    private static void Publish(FakeLibrarySource source, GameLibraryState state)
    {
        source.State = state;
        source.Publish();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Save(Window window, string directory, string name, int width, int height)
    {
        window.FocusManager!.Focus(null);
        window.MouseMove(new Point(-20, -20));
        Dispatcher.UIThread.RunJobs();
        using var image = window.CaptureRenderedFrame()
                          ?? throw new InvalidOperationException("No live progress preview was rendered: " + name);
        image.Save(Path.Combine(directory, $"{name}-{width}x{height}.png"), new PngBitmapEncoderOptions());
    }

    private static T Single<T>(Window window, string tag)
        where T : Control
    {
        return window.GetVisualDescendants().OfType<T>().SingleOrDefault(control => Equals(control.Tag, tag))
               ?? throw new InvalidOperationException("Preview control is missing: " + tag + ".");
    }

    private static void Require(bool condition, string failure)
    {
        if (!condition)
        {
            throw new InvalidOperationException(failure);
        }
    }

    private static void Click(Window window, string title)
    {
        var button = FocusSearch.First<ActionButton>(window,
                         button => button.Title == title && button.IsEffectivelyVisible)
                     ?? throw new InvalidOperationException("Preview action is missing: " + title + ". Visible: "
                         + string.Join(" | ", window.GetVisualDescendants().OfType<ActionButton>()
                             .Where(candidate => candidate.IsEffectivelyVisible).Select(candidate => candidate.Title)));
        UiFixture.Click(window, button);
        Dispatcher.UIThread.RunJobs();
    }
}
