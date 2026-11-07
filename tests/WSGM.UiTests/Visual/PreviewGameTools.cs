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
        ["list", "game", "installed", "addons", "installing", "program", "anticheat", "nightly", "injectors", "disabled",
            "config-dgvoodoo", "config-directx", "config-glide", "config-reshade", "config-special-k", "config-dxvk", "hotkey", "dlls"];

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
            case "config-dgvoodoo" or "config-directx" or "config-glide" or "config-reshade" or "config-special-k" or "config-dxvk" or "hotkey" or "dlls":
                Open(window, "game:" + GameToolsFixtures.AlanWake);
                var id = scene switch { "config-reshade" or "hotkey" => "reshade", "config-special-k" => "special-k",
                    "config-dxvk" or "dlls" => "dxvk", _ => "dgvoodoo2" };
                var publication = GameToolsFixtures.Configuration(id);
                if (scene == "dlls")
                {
                    publication = publication with { Libraries =
                    [new WSGM.Core.GameToolBinary("dxgi.dll", "reshade", "ReShade", "x64", ["Direct3D 11"], "6.8.0", "Publisher/export identity"),
                     new WSGM.Core.GameToolBinary("ddraw.dll", "dgvoodoo2", "dgVoodoo2", "x86", ["DirectDraw / Direct3D 1–7"], "2.87.6", "Publisher/export identity"),
                     new WSGM.Core.GameToolBinary("custom.dll", "", "Unknown DLL", "x64", [], "", "Ownership unproven")] };
                }
                source.Publish(publication);
                Dispatcher.UIThread.RunJobs();
                foreach (var expander in window.GetVisualDescendants().OfType<Expander>())
                {
                    expander.ContentTransition = null;
                }
                if (scene == "dlls")
                {
                    UiFixture.Click(window, UiFixture.Tagged<Button>(window, "game.dlls"));
                }
                else
                {
                    var group = scene switch { "config-directx" => "DirectX", "config-glide" => "Glide",
                        "config-dgvoodoo" => "General", "config-special-k" => "Steam", "config-dxvk" => "Rendering", _ => "Input" };
                    UiFixture.Click(window, UiFixture.Tagged<Button>(window, "tool:" + id + ".group:" + group));
                    if (scene == "config-reshade")
                    {
                        UiFixture.Click(window, UiFixture.Tagged<Button>(window, "tool:reshade.group:Effects"));
                    }
                    if (scene == "config-special-k")
                    {
                        UiFixture.Click(window, UiFixture.Tagged<Button>(window, "tool:special-k.group:All Special K games"));
                    }
                    if (scene == "hotkey")
                    {
                        Dispatcher.UIThread.RunJobs();
                        UiFixture.Click(window, UiFixture.Tagged<Button>(window, "tool:reshade.setting:overlayKey.editor"));
                    }
                    else
                    {
                        Dispatcher.UIThread.RunJobs();
                        var heading = UiFixture.Tagged<Control>(window, "tool:" + id + ".group:" + group);
                        var scroller = window.GetVisualDescendants().OfType<ScrollViewer>()
                            .First(control => control.Name == "ContentScroller");
                        if (heading.TranslatePoint(default, scroller) is { } point)
                        {
                            scroller.Offset = new Vector(0, scroller.Offset.Y + point.Y - 12);
                        }
                    }
                }
                break;
            case "list":
                break;
            case "game" or "nightly" or "injectors" or "disabled":
                Open(window, "game:" + GameToolsFixtures.AlanWake);
                if (scene is "injectors" or "disabled")
                {
                    source.Publish(GameToolsFixtures.Injectors(scene == "disabled"));
                }
                break;
            case "installed" or "addons":
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
        if (scene == "addons")
        {
            // The add-on row and the actions under it sit below the settings, past the first screen.
            UiFixture.Tagged<Control>(window, "tool:optiscaler.actions").BringIntoView();
            Dispatcher.UIThread.RunJobs();
        }
        else if (scene != "anticheat" && !scene.StartsWith("config-", StringComparison.Ordinal) && scene is not ("hotkey" or "dlls"))
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
