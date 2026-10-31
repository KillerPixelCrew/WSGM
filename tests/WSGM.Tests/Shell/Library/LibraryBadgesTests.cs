using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

/// <summary>The library badge reading built from the card model.</summary>
public sealed class LibraryBadgesTests
{
    [Fact]
    public void ARegisteredLibraryKeepsItsSteamNameWithoutReadingTheVolume()
    {
        var state = LibraryBadges.Build(
            new AppConfig(), new HashSet<string>(), RegistrationText("  Games  "),
            readVolumeLabel: _ => throw new InvalidOperationException("The Steam name is already available."));

        var library = Assert.Single(state.Libraries);
        Assert.Equal("Games", library.Name);
        Assert.True(library.Connected);
        Assert.Equal([70], library.AppIds);
    }

    [Fact]
    public void AnUnnamedRegistrationUsesItsVolumeName()
    {
        string? queriedRoot = null;
        var state = LibraryBadges.Build(
            new AppConfig(), new HashSet<string>(), RegistrationText(""),
            readVolumeLabel: root =>
            {
                queriedRoot = root;
                return "  NVME Games  ";
            });

        Assert.Equal(@"d:\", queriedRoot);
        Assert.Equal("NVME Games", Assert.Single(state.Libraries).Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void AVolumeWithoutANameGetsAFriendlyLibraryName(string? volumeLabel)
    {
        var state = LibraryBadges.Build(
            new AppConfig(), new HashSet<string>(), RegistrationText(""),
            readVolumeLabel: _ => volumeLabel);

        Assert.Equal("Library (D:)", Assert.Single(state.Libraries).Name);
    }

    [Fact]
    public void ARegisteredTrackedCardKeepsItsOwnName()
    {
        AppConfig config = new();
        config.CardLibraries.Add(new CardLibraryConfig
        {
            ContentId = "123",
            Name = "Blue card",
            AppIds = [70]
        });

        var state = LibraryBadges.Build(
            config, new HashSet<string> { "123" }, RegistrationText("Old registration"),
            readVolumeLabel: _ => throw new InvalidOperationException("The card already has its own name."));

        Assert.Equal("Blue card", Assert.Single(state.Libraries).Name);
    }

    [Fact]
    public void EveryNamedCardWithGamesIsListedWithItsConnection()
    {
        AppConfig config = new();
        config.CardLibraries.Add(new CardLibraryConfig
        {
            ContentId = "blue",
            Name = "Blue card",
            AppIds = [70, 400]
        });
        config.CardLibraries.Add(new CardLibraryConfig
        {
            ContentId = "red",
            Name = "Red card",
            AppIds = [220]
        });

        var state = LibraryBadges.Build(config, new HashSet<string> { "red" }, null, 3);

        Assert.Equal(3, state.Revision);
        Assert.Collection(
            state.Libraries,
            blue =>
            {
                Assert.Equal("Blue card", blue.Name);
                Assert.False(blue.Connected);
                Assert.Equal([70, 400], blue.AppIds);
            },
            red =>
            {
                Assert.Equal("Red card", red.Name);
                Assert.True(red.Connected);
            });
    }

    [Fact]
    public void AHiddenCardIsStillListedBecauseHidingGovernsTheTabNotWhereTheGameIs()
    {
        AppConfig config = new();
        config.CardLibraries.Add(new CardLibraryConfig
        {
            ContentId = "blue",
            Name = "Blue card",
            Hidden = true,
            Enabled = false,
            AppIds = [70]
        });

        var state = LibraryBadges.Build(config, new HashSet<string>(), null);

        var library = Assert.Single(state.Libraries);
        Assert.Equal("Blue card", library.Name);
        Assert.False(library.Connected);
    }

    [Fact]
    public void ACardWithNoGamesOrNoNameHasNothingToBadge()
    {
        AppConfig config = new();
        config.CardLibraries.Add(new CardLibraryConfig { ContentId = "empty", Name = "Empty" });
        config.CardLibraries.Add(new CardLibraryConfig { ContentId = "anon", Name = " ", AppIds = [1] });

        Assert.Empty(LibraryBadges.Build(config, new HashSet<string> { "empty", "anon" }, null).Libraries);
    }

    [Fact]
    public void UpdatingReplacesTheReadingAndRaisesChanged()
    {
        var raised = 0;
        LibraryBadges.Changed += OnChanged;
        try
        {
            AppConfig config = new();
            config.CardLibraries.Add(new CardLibraryConfig { ContentId = "c", Name = "Card", AppIds = [5] });

            LibraryBadges.Update(config, new HashSet<string> { "c" }, null);
            var first = LibraryBadges.Current!.Revision;
            LibraryBadges.Update(config, new HashSet<string>(), null);
            var second = LibraryBadges.Current!.Revision;
            // The same reading again publishes nothing new.
            LibraryBadges.Update(config, new HashSet<string>(), null);

            Assert.Equal(2, raised);
            Assert.True(second > first);
            Assert.Equal(second, LibraryBadges.Current.Revision);
            Assert.False(Assert.Single(LibraryBadges.Current.Libraries).Connected);
        }
        finally
        {
            LibraryBadges.Changed -= OnChanged;
        }

        return;

        void OnChanged()
        {
            raised++;
        }
    }

    [Fact]
    public async Task TheHomeLayoutReportIsAccepted()
    {
        LibraryBadgeBackend backend = new();

        Assert.True((await backend.HomeLayoutAsync(true, CancellationToken.None)).Succeeded);
        Assert.True((await backend.HomeLayoutAsync(true, CancellationToken.None)).Succeeded);
        Assert.True((await backend.HomeLayoutAsync(false, CancellationToken.None)).Succeeded);
    }

    private static string RegistrationText(string label)
    {
        return "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n"
               + "\t\t\"path\" \"d:\\\\SteamLibrary\"\n"
               + $"\t\t\"label\" \"{label}\"\n"
               + "\t\t\"contentid\" \"123\"\n\t\t\"apps\"\n\t\t{\n\t\t\t\"70\" \"100\"\n\t\t}\n\t}\n}";
    }
}
