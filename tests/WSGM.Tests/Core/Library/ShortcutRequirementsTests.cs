using WSGM.Core;

namespace WSGM.Tests.Core.Library;

/// <summary>
///     What each Steam shortcut needs on disk, and what a literal drive path's observation means, decided without
///     touching a file, volume or Steam.
/// </summary>
public sealed class ShortcutRequirementsTests
{
    private const string Helper = @"C:\Program Files\WSGM\WSGM.PackagedLaunch.exe";
    private const string ContentId = "0123456789abcdef0123456789abcdef";
    private const string CardVolume = @"\\?\Volume{6f1c5e2a-0d4b-4c39-9a51-3f2b8f1e7c10}\";
    private const string OtherVolume = @"\\?\Volume{0b6f1c2e-5a4d-4c39-9a51-3f2b8f1e7c11}\";
    private const string GamePath = @"D:\SteamLibrary\Games\Foo\Foo.exe";

    private static readonly ObservedVolume Card4 = new(CardVolume, @"D:\", "SDCard4", "4071319129913660490", true);
    private static readonly ObservedVolume Card5 = new(OtherVolume, @"D:\", "SDCard5", "1111111111111111111", true);
    private static readonly ObservedVolume Fixed = new(OtherVolume, @"D:\", "Data", "", false);
    private static readonly ShortcutBinding Bound = new(7, 42, GamePath, CardVolume, "4071319129913660490", "SDCard4");

    [Fact]
    public void AManagedTargetNamesItsContentRecordAndNoLiteralPath()
    {
        var shortcut = new ExistingShortcut(42, PackagedLaunchCommand.ComposeManagedTarget(Helper, ContentId), "",
            @"""C:\Program Files\WSGM""");

        var requirement = ShortcutRequirements.Resolve(shortcut);

        Assert.NotNull(requirement);
        Assert.True(requirement.Managed);
        Assert.Equal(ContentId, requirement.ContentId);
        Assert.Empty(requirement.LiteralPaths);
    }

    [Fact]
    public void AFollowRouteNeedsOnlyItsInstallFolderAsWritten()
    {
        var options = PackagedLaunchCommand.ComposeFollow(new PackagedFollowRequest(
            @"C:\Program Files\Epic Games\Launcher\EpicGamesLauncher.exe", "-launch", @"D:\Games\Hades", ""));
        var shortcut = new ExistingShortcut(42, $"\"{Helper}\"", options, @"""C:\Program Files\WSGM""");

        var requirement = ShortcutRequirements.Resolve(shortcut);

        Assert.NotNull(requirement);
        Assert.False(requirement.Managed);
        var folder = Assert.Single(requirement.LiteralPaths);
        Assert.Equal(@"D:\Games\Hades", folder.AbsolutePath);
        Assert.True(folder.Directory);
        Assert.Empty(folder.VolumeId);
    }

    [Fact]
    public void AnOrdinaryShortcutNeedsItsUnquotedTargetAndStartIn()
    {
        var shortcut = new ExistingShortcut(42, $"\"{GamePath}\"", "-windowed", @"""D:\SteamLibrary\Games\Foo""");

        var requirement = ShortcutRequirements.Resolve(shortcut);

        Assert.NotNull(requirement);
        Assert.Equal([(GamePath, false), (@"D:\SteamLibrary\Games\Foo", true)],
            requirement.LiteralPaths.Select(path => (path.AbsolutePath, path.Directory)));
    }

    [Theory]
    [InlineData("steam://rungameid/123", "")]
    [InlineData("com.epicgames.launcher://apps/Hades?action=launch", "")]
    [InlineData(@"\\server\share\Game.exe", "")]
    [InlineData("Game.exe", "")]
    [InlineData(Helper, "--aumid Publisher.Game_abc!App --mode steam-overlay")]
    public void AShortcutWithNothingLocalToCheckIsUntracked(string target, string options)
    {
        Assert.Null(ShortcutRequirements.Resolve(new ExistingShortcut(42, target, options)));
    }

    [Fact]
    public void AFilePresentOnTheCardInTheDriveIsAvailableAndBindsToThatCard()
    {
        var verdict = ShortcutRequirements.DecideLiteral(42, GamePath, ContentPathState.Present, Card4, null, Card4);

        Assert.Equal(ContentAvailability.Available, verdict.State);
        Assert.Equal("SDCard4", verdict.Location);
        Assert.Equal((CardVolume, "4071319129913660490", "SDCard4"),
            (verdict.Binding!.VolumeId, verdict.Binding.CardId, verdict.Binding.Name));
    }

    [Fact]
    public void TheSamePathFoundOnAnotherCardRebindsToIt()
    {
        var verdict = ShortcutRequirements.DecideLiteral(42, GamePath, ContentPathState.Present, Card5, Bound, null);

        Assert.Equal(ContentAvailability.Available, verdict.State);
        Assert.Equal((OtherVolume, "SDCard5"), (verdict.Binding!.VolumeId, verdict.Binding.Name));
    }

    [Fact]
    public void ABoundCardThatIsOutIsLibraryMissingNamingTheCard()
    {
        var verdict = ShortcutRequirements.DecideLiteral(42, GamePath, ContentPathState.VolumeAbsent, null, Bound, null);

        Assert.Equal(ContentAvailability.LibraryMissing, verdict.State);
        Assert.Equal("SDCard4", verdict.Location);
        Assert.Equal("Insert SDCard4.", verdict.Detail);
        Assert.Same(Bound, verdict.Binding);
    }

    [Fact]
    public void AnotherCardInTheSameDriveIsLibraryMissingNotDeletedFiles()
    {
        var verdict = ShortcutRequirements.DecideLiteral(42, GamePath, ContentPathState.Missing, Card5, Bound, null);

        Assert.Equal(ContentAvailability.LibraryMissing, verdict.State);
        Assert.Equal("Insert SDCard4.", verdict.Detail);
    }

    [Fact]
    public void TheBoundCardAtAnotherLetterSaysWhichLetterTheShortcutNeeds()
    {
        var elsewhere = Card4 with { Mount = @"E:\" };

        var verdict = ShortcutRequirements.DecideLiteral(42, GamePath, ContentPathState.VolumeAbsent, null, Bound,
            elsewhere);

        Assert.Equal(ContentAvailability.LibraryMissing, verdict.State);
        Assert.Contains("SDCard4 is in E:", verdict.Detail);
        Assert.Contains("D:", verdict.Detail);
    }

    [Fact]
    public void TheBoundCardPresentWithoutTheFileIsFileMissing()
    {
        var verdict = ShortcutRequirements.DecideLiteral(42, GamePath, ContentPathState.Missing, Card4, Bound, Card4);

        Assert.Equal(ContentAvailability.FileMissing, verdict.State);
        Assert.Equal("SDCard4", verdict.Location);
        Assert.Contains(GamePath, verdict.Detail);
    }

    [Fact]
    public void TheBoundCardIsRecognisedByItsOwnIdentityUnderANewVolume()
    {
        var renamed = Card4 with { VolumeId = OtherVolume };

        var verdict = ShortcutRequirements.DecideLiteral(42, GamePath, ContentPathState.Missing, renamed, Bound, renamed);

        Assert.Equal(ContentAvailability.FileMissing, verdict.State);
        Assert.Equal(OtherVolume, verdict.Binding!.VolumeId);
    }

    [Fact]
    public void ANeverBoundFileMissingFromARemovableDriveIsLibraryMissing()
    {
        var verdict = ShortcutRequirements.DecideLiteral(42, GamePath, ContentPathState.Missing, Card5, null, null);

        Assert.Equal(ContentAvailability.LibraryMissing, verdict.State);
        Assert.Equal("D:", verdict.Location);
        Assert.Equal("Not on the card in D:. Insert the card that holds it.", verdict.Detail);
        Assert.Null(verdict.Binding);
    }

    [Fact]
    public void ANeverBoundFileMissingFromAFixedDiskIsFileMissing()
    {
        var verdict = ShortcutRequirements.DecideLiteral(42, GamePath, ContentPathState.Missing, Fixed, null, null);

        Assert.Equal(ContentAvailability.FileMissing, verdict.State);
        Assert.Equal("Data", verdict.Location);
    }

    [Fact]
    public void ANeverBoundPathOnAnEmptyDriveIsLibraryMissing()
    {
        var verdict = ShortcutRequirements.DecideLiteral(42, GamePath, ContentPathState.VolumeAbsent, null, null, null);

        Assert.Equal(ContentAvailability.LibraryMissing, verdict.State);
        Assert.Equal("D:", verdict.Location);
    }

    [Fact]
    public void UnreadableStorageIsLibraryMissingAndKeepsItsBinding()
    {
        var verdict = ShortcutRequirements.DecideLiteral(42, GamePath, ContentPathState.Unreadable, Card4, Bound, Card4);

        Assert.Equal(ContentAvailability.LibraryMissing, verdict.State);
        Assert.Same(Bound, verdict.Binding);
    }

    [Theory]
    [InlineData(ContentAvailability.Available, "Available")]
    [InlineData(ContentAvailability.LibraryMissing, "Unavailable · library missing")]
    [InlineData(ContentAvailability.FileMissing, "Unavailable · files missing")]
    public void EachStateHasOneLabel(ContentAvailability state, string label)
    {
        Assert.Equal(label, ShortcutStatusPresentation.Label(state));
    }
}
