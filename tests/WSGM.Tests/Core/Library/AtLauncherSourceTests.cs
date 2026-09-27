using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>Where ATLauncher's instances are found and how each one is started.</summary>
public sealed class AtLauncherSourceTests
{
    private const string Roaming = @"C:\Users\u\AppData\Roaming\ATLauncher";

    private static AtLauncherSource Source(LibraryFakeDisk disk, params UninstallEntry[] entries)
    {
        return new AtLauncherSource(
            () => entries, LibraryFakeDisk.SpecialFolder, disk.FileExists, disk.DirectoryExists,
            disk.Directories, disk.ReadText);
    }

    [Fact]
    public async Task EachInstanceIsStartedThroughAtLauncherByItsNameAndFollowed()
    {
        var disk = new LibraryFakeDisk()
            .With($@"{Roaming}\ATLauncher.exe")
            .With($@"{Roaming}\instances\VanillaMinecraft1201\instance.json",
                """{"id":"1.20.1","launcher":{"name":"Vanilla Minecraft 1.20.1","pack":"Vanilla"}}""");

        var source = Source(disk);
        Assert.True(source.Detect().Installed);
        var game = Assert.Single(await source.DiscoverAsync(CancellationToken.None));

        Assert.Equal("atlauncher", game.SourceId);
        Assert.Equal("VanillaMinecraft1201", game.Key);
        Assert.Equal("Vanilla Minecraft 1.20.1", game.Name);
        var route = Assert.Single(game.CommandRoutes);
        Assert.Equal("launcher", route.Id);
        Assert.Equal($@"{Roaming}\ATLauncher.exe", route.Target);
        Assert.Equal(Roaming, route.StartDirectory);
        Assert.Equal("--launch \"Vanilla Minecraft 1.20.1\" --close-launcher --no-launcher-update",
            route.LaunchOptions);
        Assert.Equal($@"{Roaming}\instances\VanillaMinecraft1201", route.FollowMarker);
    }

    [Fact]
    public async Task AnInstallWithoutInstancesBesideItReadsTheRoamingDataFolder()
    {
        var disk = new LibraryFakeDisk()
            .With(@"D:\ATLauncher\ATLauncher.exe")
            .With($@"{Roaming}\instances\Skyblock\instance.json", """{"launcher":{"name":"Sky Block!"}}""");

        var source = Source(disk, new UninstallEntry("ATLauncher", "ATLauncher", @"D:\ATLauncher", "", "", ""));
        var game = Assert.Single(await source.DiscoverAsync(CancellationToken.None));

        Assert.Equal("Sky Block!", game.Name);
        Assert.Equal(@"D:\ATLauncher\ATLauncher.exe", game.CommandRoutes[0].Target);
        Assert.Equal("--launch \"Sky Block!\" --close-launcher --no-launcher-update",
            game.CommandRoutes[0].LaunchOptions);
    }

    [Fact]
    public async Task AnUnreadableInstanceFileFallsBackToTheFolderName()
    {
        var disk = new LibraryFakeDisk()
            .With($@"{Roaming}\ATLauncher.exe")
            .With($@"{Roaming}\instances\Broken\instance.json", "{ half written")
            .With($@"{Roaming}\instances\NoInstance\notes.txt");

        var game = Assert.Single(await Source(disk).DiscoverAsync(CancellationToken.None));

        Assert.Equal("Broken", game.Name);
        Assert.Equal("--launch \"Broken\" --close-launcher --no-launcher-update", game.CommandRoutes[0].LaunchOptions);
    }

    [Fact]
    public async Task WithoutAtLauncherNothingIsDetectedOrFound()
    {
        var source = Source(new LibraryFakeDisk());

        Assert.Equal(SourceAvailability.NotFound, source.Detect());
        Assert.Empty(await source.DiscoverAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("Vanilla Minecraft 1.20.1", "Vanilla Minecraft 1.20.1")]
    [InlineData("Say \"Hi\"", "SayHi")]
    [InlineData("Überleben", "berleben")]
    [InlineData("Ü", "Folder")]
    [InlineData(null, "Folder")]
    [InlineData("", "Folder")]
    public void TheLaunchArgumentIsTheNameOnlyWhenItSurvivesTheCommandLine(string? name, string expected)
    {
        // Java reads its command line in the ANSI code page, so a name outside ASCII would arrive
        // mangled; ATLauncher also matches the safe name.
        Assert.Equal(expected, AtLauncherSource.LaunchArgument(name, "Folder"));
    }

    [Fact]
    public async Task AnInstanceNameEndingInASeparatorDoesNotSwallowTheNextArgument()
    {
        var disk = new LibraryFakeDisk()
            .With($@"{Roaming}\ATLauncher.exe")
            .With($@"{Roaming}\instances\Pack\instance.json", """{"launcher":{"name":"Pack\\"}}""");

        var game = Assert.Single(await Source(disk).DiscoverAsync(CancellationToken.None));

        Assert.Equal(
            @"--launch ""Pack\\"" --close-launcher --no-launcher-update", game.CommandRoutes[0].LaunchOptions);
    }

    [Theory]
    [InlineData("Vanilla Minecraft 1.20.1", "VanillaMinecraft1201")]
    [InlineData("Sky-Block: Ünïcode", "SkyBlockncode")]
    [InlineData("!!!", "")]
    public void TheSafeNameKeepsOnlyAsciiLettersAndDigits(string name, string expected)
    {
        Assert.Equal(expected, AtLauncherSource.SafeName(name));
    }
}
