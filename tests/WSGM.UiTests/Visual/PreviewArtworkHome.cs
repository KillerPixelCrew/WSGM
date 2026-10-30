using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SteamUiToolkit;
using WSGM.Controls;
using WSGM.Input;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.Testing;
using WSGM.UiTests.Fakes;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Visual;

/// <summary>
///     Render-only captures of the Steam Artwork Changer home, the searchable game list ArtworkView shares with
///     Game Tools. The shared artwork fake answers each library read from the case's script, so no Steam client
///     is asked and nothing is written.
/// </summary>
/// <remarks>
///     The view's existing session seam, ArtworkView.AttachSession, carries the library read, so
///     the view needs no separate reader. Searches go through the "Search games" row and the overlay keyboard,
///     and the failed read is a real Refresh, so each case reaches its state the way a user would.
/// </remarks>
public static class PreviewArtworkHome
{
    private static readonly string[] Scenarios =
    [
        "artwork-home", "artwork-home-long-name", "artwork-home-empty", "artwork-home-no-match",
        "artwork-home-failed", "artwork-home-reading"
    ];

    private static readonly SteamLibraryApp[] Library =
    [
        new(1245620, "ELDEN RING"),
        new(1091500, "Cyberpunk 2077"),
        new(2050650, "Resident Evil 4"),
        new(3000000001, "RetroArch", Shortcut: true),
        new(1174180, "Red Dead Redemption 2"),
        new(990080, "Hogwarts Legacy"),
        new(1086940, "Baldur's Gate 3")
    ];

    private static readonly SteamLibraryApp[] LongNames =
    [
        new(2668510,
            "The Legend of Heroes: Trails through Daybreak II, Digital Deluxe Edition with every bonus pack"),
        new(3000000002, "A non-Steam game with a very long name that should wrap or trim cleanly in the list",
            Shortcut: true),
        .. Library
    ];

    /// <summary>Exports every Artwork Changer home case at one viewport.</summary>
    /// <param name="directory">The output folder.</param>
    /// <param name="width">The viewport width in DIPs.</param>
    /// <param name="height">The viewport height in DIPs.</param>
    public static void Export(string directory, int width, int height)
    {
        Directory.CreateDirectory(directory);
        foreach (var scenario in Scenarios)
        {
            using var fixture = new UiFixture();
            var window = PreviewTools.Create(fixture, width, height);
            var session = new FakeArtworkSource { GameReads = Reads(scenario) };
            UiFixture.Named<ArtworkView>(window, "ArtworkHost").AttachSession(session);
            UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.SystemArtwork));
            Dispatcher.UIThread.RunJobs();
            switch (scenario)
            {
                case "artwork-home":
                    Expect(window, "game:1245620");
                    break;
                case "artwork-home-long-name":
                    Expect(window, "game:2668510");
                    Expect(window, "game:3000000002");
                    break;
                case "artwork-home-empty":
                    Expect(window, "games.empty");
                    break;
                case "artwork-home-no-match":
                    Search(window, "zelda");
                    Expect(window, "games.empty");
                    break;
                case "artwork-home-failed":
                    // The first read succeeded; Refresh fails and the last good list stays under the error.
                    Click(window, "Refresh games");
                    Expect(window, "games.error");
                    Expect(window, "game:1245620");
                    break;
                case "artwork-home-reading":
                    Expect(window, "games.reading");
                    break;
            }

            window.FocusManager!.Focus(null);
            window.MouseMove(new Point(-20, -20));
            Dispatcher.UIThread.RunJobs();
            using var image = window.CaptureRenderedFrame() ??
                              throw new InvalidOperationException("No artwork home preview was rendered.");
            image.Save(Path.Combine(directory, $"{scenario}-{width}x{height}.png"), new PngBitmapEncoderOptions());
            window.Close();
        }
    }

    /// <summary>The library reads one case answers, in order; the last answer repeats.</summary>
    private static Task<OverlayLibraryResult>[] Reads(string scenario)
    {
        var library = Task.FromResult(new OverlayLibraryResult(Library, Error: null));
        return scenario switch
        {
            "artwork-home" or "artwork-home-no-match" => [library],
            "artwork-home-long-name" => [Task.FromResult(new OverlayLibraryResult(LongNames, Error: null))],
            "artwork-home-empty" => [Task.FromResult(new OverlayLibraryResult([], Error: null))],
            "artwork-home-failed" =>
            [
                library,
                Task.FromResult(new OverlayLibraryResult([],
                    "Steam could not list the games: The request timed out."))
            ],
            // A read Steam never answers keeps the list on its reading state for the capture.
            "artwork-home-reading" => [new TaskCompletionSource<OverlayLibraryResult>().Task],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };
    }

    private static void Search(OverlayWindow window, string query)
    {
        Click(window, "Search games");
        var keyboard = window.GetVisualDescendants().OfType<KeyboardPanel>().Single();
        foreach (var character in query)
        {
            var key = Enum.Parse<Key>(char.ToUpperInvariant(character).ToString());
            UiFixture.Click(window, KeyButton(keyboard, key));
        }

        // The keyboard leaves after the overlay's close grace, which runs on a dispatcher timer.
        using var closed = new CancellationTokenSource(AsyncConditions.TimeLimit);
        window.SurfaceClosed += Stop;
        try
        {
            UiFixture.Click(window, KeyButton(keyboard, Key.Enter));
            Dispatcher.UIThread.MainLoop(closed.Token);
        }
        finally
        {
            window.SurfaceClosed -= Stop;
        }

        if (window.HasActiveSurface)
        {
            throw new TimeoutException("The search keyboard did not close.");
        }

        Dispatcher.UIThread.RunJobs();
        return;

        void Stop()
        {
            closed.Cancel();
        }
    }

    private static Button KeyButton(KeyboardPanel keyboard, Key key)
    {
        return keyboard.GetVisualDescendants().OfType<Button>()
                   .FirstOrDefault(button => button.IsEffectivelyVisible && Equals(button.Tag, key))
               ?? throw new InvalidOperationException("No visible keyboard key " + key + ".");
    }

    private static void Click(Window window, string title)
    {
        var button = FocusSearch.First<ActionButton>(window,
                         candidate => candidate.Title == title && candidate.IsEffectivelyVisible)
                     ?? throw new InvalidOperationException("Preview action is missing: " + title + ".");
        UiFixture.Click(window, button);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Expect(Window window, string tag)
    {
        if (!window.GetVisualDescendants().OfType<Control>()
                .Any(control => Equals(control.Tag, tag) && control.IsEffectivelyVisible))
        {
            throw new InvalidOperationException("The artwork home case did not reach its state: " + tag
                                                + " is not shown.");
        }
    }
}
