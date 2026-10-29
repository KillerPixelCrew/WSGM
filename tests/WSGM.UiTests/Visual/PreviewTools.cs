using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using WSGM.Controls;
using WSGM.Input;
using WSGM.Overlay;
using WSGM.UiTests.Fakes;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Visual;

/// <summary>Populated tool captures over explicit local fakes, without Steam, network or installs.</summary>
public static class PreviewTools
{
    /// <summary>Exports only the affected Display settings and emulator dependency previews.</summary>
    public static void ExportFeatureCases(string directory, int width, int height)
    {
        Directory.CreateDirectory(directory);
        PreviewScaledResolutions.Export(directory);
        PreviewDisplaySettings.Export(directory);
        Export(directory, width, height, ["emulators-detail", "emulators-install", "importer-rom-source"]);
    }

    /// <summary>Exports only the removable storage library levels and the removed-title return path.</summary>
    public static void ExportStorageCases(string directory, int width, int height)
    {
        Directory.CreateDirectory(directory);
        Export(directory, width, height, StorageScenarios);
    }

    /// <summary>Storage cases render only when a filter names them, so ordinary exports are unchanged.</summary>
    private static readonly string[] StorageScenarios =
    [
        "importer-storage-home", "importer-storage-list", "importer-storage-lower", "importer-storage-entry",
        "importer-storage-empty", "importer-storage-checking", "importer-storage-error", "importer-entry-removed",
        "importer-entry-loading"
    ];

    internal static OverlayWindow Create(UiFixture fixture, int width = 1280, int height = 800,
        FakeLibrarySource? library = null)
    {
        var window = fixture.Overlay(width, height);
        UiFixture.Named<ThemesView>(window, "ThemesHost").AttachSession(new FakeThemesSource());
        UiFixture.Named<AnimationsView>(window, "AnimationsHost").AttachSession(new FakeMoviesSource());
        UiFixture.Named<ArtworkView>(window, "ArtworkHost").AttachSession(new FakeArtworkSource());
        UiFixture.Named<GameLibraryView>(window, "GameLibraryHost").Attach(library ?? new FakeLibrarySource());
        UiFixture.Named<EmulatorManagerView>(window, "EmulatorManagerHost").Attach(PreviewEmulators.Create());
        window.AttachPrograms(new ProgramsOverlaySource());
        var model = (OverlayViewModel)window.DataContext!;
        model.ShowThemes = model.ShowAnimations = model.ShowArtwork = model.ShowGameLibrary = true;
        model.ShowEmulators = true;
        Dispatcher.UIThread.RunJobs();
        window.SelectNextTab();
        window.SelectNextTab();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    internal static void Export(string directory, int width, int height, IReadOnlyCollection<string>? scenarioFilter = null)
    {
        foreach (var scenario in new[]
                 {
                     "themes-installed", "themes-browse", "themes-profiles", "themes-settings", "movies-library",
                     "movies-browse", "movies-settings", "artwork", "artwork-manage", "artwork-logo", "importer",
                     "importer-review", "importer-artwork", "importer-all-artwork", "importer-rom-source", "emulators-installed",
                     "emulators-detail", "emulators-install", "emulators-cores", "emulators-defaults", "emulators-bios",
                     "programs", "programs-detail", "programs-unavailable", "programs-pins"
                 }.Concat(scenarioFilter is null ? Array.Empty<string>() : StorageScenarios))
        {
            if (scenarioFilter is not null && !scenarioFilter.Contains(scenario, StringComparer.Ordinal))
            {
                continue;
            }

            using var fixture = new UiFixture();
            var library = StorageScenarios.Contains(scenario, StringComparer.Ordinal)
                ? new FakeLibrarySource { State = OverlayToolFixtures.Storage }
                : null;
            var window = Create(fixture, width, height, library);
            var keepFocus = false;
            var page = scenario.StartsWith("themes", StringComparison.Ordinal) ? OverlayPage.SystemThemes
                : scenario.StartsWith("programs", StringComparison.Ordinal) ? OverlayPage.Programs
                : scenario.StartsWith("emulators", StringComparison.Ordinal) ? OverlayPage.EmulatorManager
                : scenario.StartsWith("movies", StringComparison.Ordinal) ? OverlayPage.SystemAnimations
                : scenario.StartsWith("artwork", StringComparison.Ordinal) ? OverlayPage.SystemArtwork
                : OverlayPage.SteamGameLibrary;
            UiFixture.Click(window, UiFixture.Rail(window, page.ToString()));
            Dispatcher.UIThread.RunJobs();
            if (scenario == "programs-detail")
            {
                Click("Edit Performance monitor");
            }

            if (scenario == "programs-unavailable")
            {
                Click("Edit Window utility");
            }

            if (scenario == "programs-pins")
            {
                window.SetPins(["program:monitor", "program:missing"]);
                UiFixture.Click(window, UiFixture.Tab(window, 0));
                Dispatcher.UIThread.RunJobs();
            }

            if (scenario == "emulators-detail")
            {
                Click("PCSX2");
            }

            if (scenario == "emulators-install")
            {
                Click("Available 2");
                Click("Eden");
            }

            if (scenario == "emulators-cores")
            {
                Click("RetroArch (all cores)");
                Click("Installed cores (3)");
            }

            if (scenario == "emulators-defaults")
            {
                Click("System defaults");
            }

            if (scenario == "emulators-bios")
            {
                Click("BIOS & firmware");
            }

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

            if (library is not null)
            {
                if (scenario == "importer-storage-empty")
                {
                    library.State = library.State with { StorageLibraries = [], Shortcuts = [] };
                    library.Publish();
                    Dispatcher.UIThread.RunJobs();
                }

                if (scenario == "importer-storage-checking")
                {
                    library.State = library.State with { StorageChecked = false, Shortcuts = [] };
                    library.Publish();
                    Dispatcher.UIThread.RunJobs();
                }

                if (scenario == "importer-storage-error")
                {
                    library.State = library.State with
                    {
                        Error = "Shortcut availability is pending: Steam's account could not be confirmed."
                    };
                    library.Publish();
                    Dispatcher.UIThread.RunJobs();
                }

                if (scenario != "importer-storage-home")
                {
                    Click("Storage");
                }

                if (scenario == "importer-storage-lower")
                {
                    // The bottom of the list: a fixed disk's group with a long name and path.
                    var last = FocusSearch.First<ActionButton>(window,
                                   button => button.Title?.StartsWith("Chronicle", StringComparison.Ordinal) == true
                                             && button.IsEffectivelyVisible)
                               ?? throw new InvalidOperationException("The last shortcut is not listed.");
                    last.BringIntoView();
                    last.Focus(NavigationMethod.Directional);
                    keepFocus = true;
                }

                if (scenario == "importer-storage-entry")
                {
                    Click("Lantern Saga");
                }

                if (scenario is "importer-entry-removed" or "importer-entry-loading")
                {
                    // An applied removal finished while the title was open: the host publishes a state without
                    // it, and the production view leaves the title's level.
                    Click("Deleted Platformer");
                    library.State = library.State with
                    {
                        Loading = scenario == "importer-entry-loading",
                        Entries = library.State.Entries.Where(entry => entry.Id != "retro-deleted").ToArray(),
                        Shortcuts = library.State.Shortcuts!.Where(shortcut => shortcut.AppId != 102).ToArray()
                    };
                    library.Publish();
                    keepFocus = true;
                }

                Dispatcher.UIThread.RunJobs();
            }
            else if (scenario == "importer-rom-source")
            {
                Click($"Sources ({OverlayToolFixtures.Library.Reading.Count} on)");
                Click("Add a ROM library");
            }
            else if (scenario.StartsWith("importer-", StringComparison.Ordinal))
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
            if (!keepFocus)
            {
                window.FocusManager!.Focus(null);
            }

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
