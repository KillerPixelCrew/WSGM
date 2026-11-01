using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Overlay;
using WSGM.Testing;
using WSGM.UiTests.Fakes;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Visual;

/// <summary>
///     Render-only Game Tools scenes. An explicit fake owner publishes each case's state, a fixed library stands in
///     for Steam and synthetic capsules stand in for its artwork, so no game folder is scanned, nothing is installed
///     and nothing is downloaded.
/// </summary>
public static class PreviewGameTools
{
    /// <summary>Every scene, in the order the page reaches them.</summary>
    internal static readonly string[] Scenes =
        ["list", "game", "installed", "installing", "program", "anticheat", "nightly"];

    /// <summary>Exports every Game Tools scene at one viewport.</summary>
    /// <param name="directory">The output folder.</param>
    /// <param name="width">The viewport width in DIPs.</param>
    /// <param name="height">The viewport height in DIPs.</param>
    public static void Export(string directory, int width, int height)
    {
        Directory.CreateDirectory(directory);
        foreach (var scene in Scenes)
        {
            using var fixture = new UiFixture();
            var (window, _) = Open(fixture, scene, width, height);
            window.FocusManager!.Focus(null);
            window.MouseMove(new Point(-20, -20));
            using var image = window.CaptureRenderedFrame() ??
                              throw new InvalidOperationException("No Game Tools preview was rendered.");
            image.Save(Path.Combine(directory, $"game-tools-{scene}-{width}x{height}.png"),
                new PngBitmapEncoderOptions());
            window.Close();
        }
    }

    /// <summary>Opens the Overlay on Steam › Game Tools and drives the page to a scene the way a user would.</summary>
    /// <param name="fixture">The fixture owning the window.</param>
    /// <param name="scene">One of <see cref="Scenes" />.</param>
    /// <param name="width">The viewport width in DIPs.</param>
    /// <param name="height">The viewport height in DIPs.</param>
    /// <returns>The window, with every visible capsule decoded, and the fake it reads.</returns>
    internal static (OverlayWindow Window, FakeGameToolsSource Source) Open(UiFixture fixture, string scene, int width,
        int height)
    {
        var source = new FakeGameToolsSource(GameToolsFixtures.States, GameToolsFixtures.Library);
        var window = fixture.Overlay(width, height);
        var view = UiFixture.Named<GameToolsView>(window, "GameToolsHost");
        // Without a Steam client the page finds no open game and shows this library.
        view.LibraryReader = () => Task.FromResult(GameToolsFixtures.SteamLibrary);
        view.CapsuleReader = GameToolsFixtures.CapsuleAsync;
        view.Attach(source);
        ((OverlayViewModel)window.DataContext!).ShowGameTools = true;
        Dispatcher.UIThread.RunJobs();
        UiFixture.Click(window, UiFixture.Tab(window, 1));
        Dispatcher.UIThread.RunJobs();
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.SteamGameTools));
        Dispatcher.UIThread.RunJobs();
        switch (scene)
        {
            case "list":
                break;
            case "game" or "nightly":
                Open(window, "game:" + GameToolsFixtures.AlanWake);
                break;
            case "installed":
                Open(window, "tools:" + GameToolsFixtures.Cyberpunk);
                break;
            case "installing":
                Open(window, "game:" + GameToolsFixtures.AlanWake);
                source.Publish(GameToolsFixtures.Installing);
                break;
            case "program":
                Open(window, "game:" + GameToolsFixtures.RedDead);
                Open(window, "game.change-program");
                break;
            case "anticheat":
                Open(window, "game:" + GameToolsFixtures.EldenRing);
                Open(window, "tool:optiscaler.primary");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scene), scene, null);
        }

        if (scene == "nightly")
        {
            // A closed dropdown commits a changed selection, as a keyboard or touch choice on it does.
            UiFixture.Tagged<ComboBox>(window, "tool:optiscaler.channel.editor").SelectedIndex = 1;
        }

        Dispatcher.UIThread.RunJobs();
        if (scene != "anticheat")
        {
            ScrollToTop(window);
        }

        WaitForCapsules(window);
        return (window, source);
    }

    private static void Open(OverlayWindow window, string tag)
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

    // Each on-screen capsule decodes on a worker once it is laid out; the capture waits for all of them.
    private static void WaitForCapsules(Window window)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            var pending = window.GetVisualDescendants().OfType<OverlayPreviewImage>()
                .Where(preview => preview.IsEffectivelyVisible && preview.Child is not Image
                                                               && OnScreen(window, preview))
                .ToArray();
            if (pending.Length == 0)
            {
                return;
            }

            if (Stopwatch.GetElapsedTime(started) > AsyncConditions.TimeLimit)
            {
                throw new TimeoutException(pending.Length + " Game Tools capsules did not decode.");
            }

            Thread.Yield();
        }
    }

    // Only a preview inside the controls pane's viewport loads; one scrolled out of it stays a placeholder.
    private static bool OnScreen(Window window, Avalonia.Visual preview)
    {
        var pane = window.GetVisualDescendants().OfType<ScrollViewer>()
            .FirstOrDefault(scroller => scroller.Name == "ContentScroller" && scroller.IsVisualAncestorOf(preview));
        if (pane is null)
        {
            return false;
        }

        var origin = preview.TranslatePoint(default, pane);
        return origin is { } point && point.Y < pane.Bounds.Height && point.Y + preview.Bounds.Height > 0;
    }
}
