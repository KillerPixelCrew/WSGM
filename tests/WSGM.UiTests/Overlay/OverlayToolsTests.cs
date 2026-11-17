using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.UiTests.Fakes;
using WSGM.UiTests.Infrastructure;
using WSGM.UiTests.Visual;

namespace WSGM.UiTests.Overlay;

/// <summary>Whole tool flows over explicit fakes; execution follows the maintainer's manual pass.</summary>
public sealed class OverlayToolsTests
{
    [AvaloniaFact]
    public void EachToolHasAReachablePopulatedPageAndNativeEditors()
    {
        using var fixture = new UiFixture();
        var window = PreviewTools.Create(fixture);
        foreach (var page in new[]
                 {
                     OverlayPage.SystemThemes, OverlayPage.SystemAnimations, OverlayPage.SystemArtwork,
                     OverlayPage.SteamGameLibrary
                 })
        {
            UiFixture.Click(window, UiFixture.Rail(window, page));
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(window.GetVisualDescendants().OfType<ActionButton>(),
                button => button.IsEffectivelyVisible);
        }

        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.SystemThemes));
        Click(window, "Browse");
        var editors = window.GetVisualDescendants().OfType<ComboBox>().Where(control => control.IsEffectivelyVisible)
            .ToArray();
        Assert.NotEmpty(editors);
        Assert.All(editors, editor => Assert.True(editor.Bounds.Height >= 36));
    }

    [AvaloniaFact]
    public void BackFromAThemeDetailReturnsToBrowseBeforeTheRail()
    {
        using var fixture = new UiFixture();
        var window = PreviewTools.Create(fixture);
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.SystemThemes));
        Click(window, "Browse");
        Click(window, "Midnight");
        Assert.Contains(Visible(window), button => button.Title == "Update");
        UiFixture.Key(window, Key.Escape);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(Visible(window), button => button.Title == "Search");
        Assert.Contains(Visible(window), button => button.Title == "Midnight");
        Assert.True(UiFixture.Named<ThemesView>(window, "ThemesHost").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void TurningAFeatureOffRetiresItsOpenPageAndRailEntry()
    {
        using var fixture = new UiFixture();
        var window = PreviewTools.Create(fixture);
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.SystemThemes));
        ((OverlayViewModel)window.DataContext!).ShowThemes = false;
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(UiFixture.Named<StackPanel>(window, "SectionRail").Children.OfType<Button>(),
            button => Equals(button.Tag, "rail.SystemThemes"));
        Assert.False(UiFixture.Named<ThemesView>(window, "ThemesHost").IsEffectivelyVisible);
    }

    [AvaloniaTheory]
    [InlineData("overlay-importer-rom-systems-1280", 1280, 800)]
    [InlineData("overlay-importer-rom-systems-980", 980, 640)]
    public void ARomLibrarysSystemsAreListedAndTickedLikeSources(string name, int width, int height)
    {
        using var fixture = new UiFixture();
        var library = OverlayToolFixtures.Library;
        var source = new FakeLibrarySource
        {
            State = library with
            {
                Sources =
                [
                    .. library.Sources,
                    new GameLibrarySource("retro", "Retro collection", LibrarySourceKind.Rom, true, true,
                        "Retro collection", 296, true, true),
                    new GameLibrarySource("retro/psx", "PlayStation", LibrarySourceKind.Rom, true, true, "psx", 212,
                        true, true, "retro"),
                    new GameLibrarySource("retro/snes", "Super Nintendo", LibrarySourceKind.Rom, true, true, "snes",
                        84, true, true, "retro"),
                    new GameLibrarySource("retro/gamecube", "Nintendo GameCube", LibrarySourceKind.Rom, true, false,
                        "Not scanned", -1, false, true, "retro")
                ]
            }
        };
        var window = PreviewTools.Create(fixture, width, height, source);
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.SteamGameLibrary));
        Click(window, $"Sources ({library.Reading.Count} on)");
        Dispatcher.UIThread.RunJobs();
        UiFixture.Tagged<Control>(window, "source:retro/gamecube").BringIntoView();
        Dispatcher.UIThread.RunJobs();
        VisualBaseline.Verify(window, name);

        Click(window, "Retro collection › Nintendo GameCube");
        Assert.Equal("SetSourceEnabledAsync", source.Commands[^1]);
    }

    [AvaloniaFact]
    public void SelectingVisibleTitlesDoesNotSelectAHiddenRemoval()
    {
        using var fixture = new UiFixture();
        var window = PreviewTools.Create(fixture);
        var source = new FakeLibrarySource();
        UiFixture.Named<GameLibraryView>(window, "GameLibraryHost").Attach(source);
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.SteamGameLibrary));
        Click(window, "Review (3)");
        Click(window, "New");
        Click(window, "Deselect visible");
        Assert.False(source.State.Entries.Single(entry => entry.Id == "new-one").Selected);
        Assert.False(source.State.Entries.Single(entry => entry.Action == "Remove").Selected);
        Assert.Single(source.Commands, name => name == "SetSelectedAsync");
        Assert.DoesNotContain("ToggleEntryAsync", source.Commands);
    }

    [AvaloniaFact]
    public void ArtworkRechecksAreReachableFromReviewAndTheTitlePicker()
    {
        using var fixture = new UiFixture();
        var source = new FakeLibrarySource();
        var window = PreviewTools.Create(fixture, library: source);
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.SteamGameLibrary));
        Click(window, "Review (3)");
        Click(window, "Recheck all artwork");
        Click(window, "New game");
        Click(window, "Staged artwork");
        Click(window, "Recheck artwork");

        Assert.Contains("RecheckArtworkAsync:", source.Commands);
        Assert.Contains("RecheckArtworkAsync:new-one", source.Commands);
    }

    [AvaloniaFact]
    public void MovieDeletionRequiresTheConfirmationAndLeavesOtherActionsAvailable()
    {
        using var fixture = new UiFixture();
        var window = PreviewTools.Create(fixture);
        var source = new FakeMoviesSource();
        UiFixture.Named<AnimationsView>(window, "AnimationsHost").AttachSession(source, source);
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.SystemAnimations));
        Click(window, "Aurora");
        Click(window, "Remove");
        Assert.DoesNotContain("DeleteAsync", source.Commands);
        Click(window, "Cancel");
        Assert.DoesNotContain("DeleteAsync", source.Commands);
        Click(window, "Remove");
        Click(window, "Confirm");
        Assert.Contains("DeleteAsync", source.Commands);
    }

    [AvaloniaFact]
    public void AServicePublicationRetainsTheFocusedDropdownInstance()
    {
        using var fixture = new UiFixture();
        var window = PreviewTools.Create(fixture);
        var source = new FakeThemesSource();
        var themes = UiFixture.Named<ThemesView>(window, "ThemesHost");
        themes.AttachSession(source, source);
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.SystemThemes));
        Click(window, "Browse");
        var editor = themes.GetVisualDescendants().OfType<ComboBox>()
            .Single(control => Equals(control.Tag, "choice:Target"));
        Assert.True(editor.IsEffectivelyVisible);
        Assert.True(editor.IsEffectivelyEnabled);
        Assert.True(editor.Focus());
        source.Publish();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(editor, window.FocusManager!.GetFocusedElement());
        Assert.Same(editor, themes.GetVisualDescendants().OfType<ComboBox>()
            .Single(control => Equals(control.Tag, "choice:Target")));
        Assert.Contains(editor, window.GetVisualDescendants());
    }

    private static IEnumerable<ActionButton> Visible(OverlayWindow window)
    {
        return window.GetVisualDescendants().OfType<ActionButton>().Where(button => button.IsEffectivelyVisible);
    }

    private static void Click(OverlayWindow window, string title)
    {
        UiFixture.Click(window, Visible(window).First(button => button.Title == title));
        Dispatcher.UIThread.RunJobs();
    }
}
