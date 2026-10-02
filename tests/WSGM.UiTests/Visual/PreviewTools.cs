using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using WSGM.Controls;
using WSGM.Input;
using WSGM.Overlay;
using WSGM.UiTests.Fakes;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Visual;

/// <summary>Populated tool captures over explicit local fakes, without Steam, network or installs.</summary>
internal static class PreviewTools
{
    internal static OverlayWindow Create(UiFixture fixture, int width = 1280, int height = 800)
    {
        var window = fixture.Overlay(width, height);
        UiFixture.Named<ThemesView>(window, "ThemesHost").AttachSession(new FakeThemesSource());
        UiFixture.Named<AnimationsView>(window, "AnimationsHost").AttachSession(new FakeMoviesSource());
        UiFixture.Named<ArtworkView>(window, "ArtworkHost").AttachSession(new FakeArtworkSource());
        UiFixture.Named<GameLibraryView>(window, "GameLibraryHost").Attach(new FakeLibrarySource());
        var model = (OverlayViewModel)window.DataContext!;
        model.ShowThemes = model.ShowAnimations = model.ShowArtwork = model.ShowGameLibrary = true;
        Dispatcher.UIThread.RunJobs();
        window.SelectNextTab();
        window.SelectNextTab();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    internal static void Export(string directory, int width, int height)
    {
        foreach (var scenario in new[]
                 {
                     "themes-installed", "themes-browse", "themes-profiles", "themes-settings", "movies-library",
                     "movies-browse", "movies-settings", "artwork", "artwork-manage", "artwork-logo", "importer",
                     "importer-review", "importer-artwork", "importer-all-artwork"
                 })
        {
            using var fixture = new UiFixture();
            var window = Create(fixture, width, height);
            var page = scenario.StartsWith("themes", StringComparison.Ordinal) ? OverlayPage.SystemThemes
                : scenario.StartsWith("movies", StringComparison.Ordinal) ? OverlayPage.SystemAnimations
                : scenario.StartsWith("artwork", StringComparison.Ordinal) ? OverlayPage.SystemArtwork
                : OverlayPage.SteamGameLibrary;
            UiFixture.Click(window, UiFixture.Rail(window, page.ToString()));
            Dispatcher.UIThread.RunJobs();
            if (scenario == "themes-browse" || scenario == "movies-browse")
            {
                Click("Browse");
            }

            if (scenario == "themes-profiles")
            {
                Click("Profiles");
            }

            if (scenario == "themes-settings" || scenario == "movies-settings")
            {
                Click("Settings");
            }

            if (scenario.StartsWith("artwork", StringComparison.Ordinal))
            {
                Click("Example game");
                if (scenario == "artwork-manage")
                {
                    Click("Manage");
                }

                if (scenario == "artwork-logo")
                {
                    Click("Manage");
                    Click("Adjust logo position");
                }
            }

            if (scenario.StartsWith("importer-", StringComparison.Ordinal))
            {
                Click("Review (3)");
                if (scenario == "importer-artwork")
                {
                    Click("New game");
                    Click("Staged artwork");
                }

                if (scenario == "importer-all-artwork")
                {
                    Click("All artwork");
                }
            }

            Dispatcher.UIThread.RunJobs();
            window.FocusManager!.Focus(null);
            window.MouseMove(new Point(-20, -20));
            using var image = window.CaptureRenderedFrame() ??
                              throw new InvalidOperationException("No tool preview was rendered.");
            image.Save(Path.Combine(directory, $"{scenario}-{width}x{height}.png"), new PngBitmapEncoderOptions());
            window.Close();
            continue;

            void Click(string title)
            {
                var button = FocusSearch.First<ActionButton>(window,
                                 button => button.Title == title && button.IsEffectivelyVisible)
                             ?? throw new InvalidOperationException("Preview action is missing: " + title);
                UiFixture.Click(window, button);
                Dispatcher.UIThread.RunJobs();
            }
        }
    }
}
