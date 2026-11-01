using WSGM.Core;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Core;

/// <summary>Where Prism Launcher's instances are found and how each one is started.</summary>
public sealed class PrismLauncherSourceTests
{
    private const string Executable = @"C:\Users\u\AppData\Local\Programs\PrismLauncher\prismlauncher.exe";
    private const string Instances = @"C:\Users\u\AppData\Roaming\PrismLauncher\instances";

    private static PrismLauncherSource Source(LibraryFakeDisk disk)
    {
        return new PrismLauncherSource(
            LibraryFakeDisk.SpecialFolder, disk.FileExists, disk.DirectoryExists, disk.Directories, disk.ReadText);
    }

    [Fact]
    public async Task EachInstanceIsStartedThroughPrismByItsFolderName()
    {
        var disk = new LibraryFakeDisk()
            .With(Executable)
            .With($@"{Instances}\1.20 Fabric\instance.cfg", "[General]\r\nname=Fabric Survival\r\n");

        var game = Assert.Single(await Source(disk).DiscoverAsync([], CancellationToken.None));

        Assert.Equal("prism", game.SourceId);
        Assert.Equal("1.20 Fabric", game.Key);
        Assert.Equal("Fabric Survival", game.Name);
        Assert.True(game.IsGame);
        Assert.True(game.Launch.Validated);
        var route = Assert.Single(game.CommandRoutes);
        Assert.Equal("launcher", route.Id);
        Assert.Equal(Executable, route.Target);
        Assert.Equal(Path.GetDirectoryName(Executable), route.StartDirectory);
        Assert.Equal("--launch \"1.20 Fabric\"", route.LaunchOptions);
        Assert.Equal($@"{Instances}\1.20 Fabric", route.FollowMarker);
    }

    [Fact]
    public async Task PrismsOwnScratchFoldersAndFoldersWithoutAnInstanceAreSkipped()
    {
        var disk = new LibraryFakeDisk()
            .With(Executable)
            .With($@"{Instances}\_LAUNCHER_TEMP\instance.cfg", "name=Temp")
            .With($@"{Instances}\.tmp\instance.cfg", "name=Hidden")
            .With($@"{Instances}\Loose\readme.txt", "not an instance")
            .With($@"{Instances}\Vanilla\instance.cfg", "InstanceType=OneSix");

        var game = Assert.Single(await Source(disk).DiscoverAsync([], CancellationToken.None));

        Assert.Equal("Vanilla", game.Key);
        // No name in the instance's config: the folder name stands in for it.
        Assert.Equal("Vanilla", game.Name);
    }

    [Fact]
    public async Task APortableInstallReadsItsConfigBesideTheExecutableAndFollowsAMovedInstanceFolder()
    {
        var disk = new LibraryFakeDisk()
            .With(@"D:\Prism\prismlauncher.exe")
            .With(@"D:\Prism\portable.txt")
            .With(@"D:\Prism\prismlauncher.cfg", "InstanceDir=E:/Minecraft/instances\n")
            .With(@"E:\Minecraft\instances\Modded\instance.cfg", "[General]\nname=\"Modded, \\\"heavy\\\"\"\n");

        var game = Assert.Single(
            await Source(disk).DiscoverAsync([Entry("Prism Launcher 9.2", @"D:\Prism")], CancellationToken.None));

        Assert.Equal("Modded, \"heavy\"", game.Name);
        Assert.Equal(@"D:\Prism\prismlauncher.exe", game.CommandRoutes[0].Target);
    }

    [Fact]
    public async Task WithoutPrismNothingIsDetectedOrFound()
    {
        var disk = new LibraryFakeDisk().With($@"{Instances}\Vanilla\instance.cfg", "name=Vanilla");
        var source = Source(disk);
        UninstallEntry[] programs = [Entry("Some Other Launcher", @"C:\Other")];

        Assert.False(source.Detect(programs).Installed);
        Assert.Empty(await source.DiscoverAsync(programs, CancellationToken.None));
    }

    private static UninstallEntry Entry(string displayName, string location)
    {
        return new UninstallEntry("key", displayName, location, "", "", "");
    }
}
