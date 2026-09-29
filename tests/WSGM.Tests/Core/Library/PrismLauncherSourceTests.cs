using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>Where Prism Launcher's instances are found and how each one is started.</summary>
public sealed class PrismLauncherSourceTests
{
    private const string Executable = @"C:\Users\u\AppData\Local\Programs\PrismLauncher\prismlauncher.exe";
    private const string Instances = @"C:\Users\u\AppData\Roaming\PrismLauncher\instances";

    private static PrismLauncherSource Source(LibraryFakeDisk disk, params UninstallEntry[] entries)
    {
        return new PrismLauncherSource(
            () => entries, LibraryFakeDisk.SpecialFolder, disk.FileExists, disk.DirectoryExists,
            disk.Directories, disk.ReadText);
    }

    [Fact]
    public async Task EachInstanceIsStartedThroughPrismByItsFolderName()
    {
        var disk = new LibraryFakeDisk()
            .With(Executable)
            .With($@"{Instances}\1.20 Fabric\instance.cfg", "[General]\r\nname=Fabric Survival\r\n");

        var game = Assert.Single(await Source(disk).DiscoverAsync(CancellationToken.None));

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

        var game = Assert.Single(await Source(disk).DiscoverAsync(CancellationToken.None));

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

        var source = Source(disk, Entry("Prism Launcher 9.2", @"D:\Prism"));
        var game = Assert.Single(await source.DiscoverAsync(CancellationToken.None));

        Assert.Equal("Modded, \"heavy\"", game.Name);
        Assert.Equal(@"D:\Prism\prismlauncher.exe", game.CommandRoutes[0].Target);
    }

    [Fact]
    public async Task WithoutPrismNothingIsDetectedOrFound()
    {
        var disk = new LibraryFakeDisk().With($@"{Instances}\Vanilla\instance.cfg", "name=Vanilla");
        var source = Source(disk, Entry("Some Other Launcher", @"C:\Other"));

        Assert.False(source.Detect().Installed);
        Assert.Empty(await source.DiscoverAsync(CancellationToken.None));
    }

    private static UninstallEntry Entry(string displayName, string location)
    {
        return new UninstallEntry("key", displayName, location, "", "", "");
    }
}

/// <summary>An in-memory file system for the Game Library's folder-based sources.</summary>
/// <remarks>Folders exist when a file is under them, as they would after a real install.</remarks>
internal sealed class LibraryFakeDisk
{
    private readonly Dictionary<string, FileAttributes> _attributes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The special folders of a user called <c>u</c>.</summary>
    public static string SpecialFolder(Environment.SpecialFolder folder)
    {
        return folder switch
        {
            Environment.SpecialFolder.ApplicationData => @"C:\Users\u\AppData\Roaming",
            // wsgm-allow-live-data-path: names the folder only to answer with the fake user's path;
            // nothing here resolves the real one.
            Environment.SpecialFolder.LocalApplicationData => @"C:\Users\u\AppData\Local",
            _ => ""
        };
    }

    /// <summary>Adds a file.</summary>
    public LibraryFakeDisk With(string path, string text = "")
    {
        _files[path] = text;
        return this;
    }

    /// <summary>Gives a file or folder attributes such as hidden.</summary>
    public LibraryFakeDisk Mark(string path, FileAttributes attributes)
    {
        _attributes[path] = attributes;
        return this;
    }

    public bool FileExists(string path)
    {
        return _files.ContainsKey(path);
    }

    public string? ReadText(string path)
    {
        return _files.GetValueOrDefault(path);
    }

    public bool DirectoryExists(string path)
    {
        var prefix = Prefix(path);
        return _files.Keys.Any(file => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<string> Directories(string path)
    {
        var prefix = Prefix(path);
        return _files.Keys
            .Where(file => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(file => file[prefix.Length..])
            .Where(rest => rest.Contains('\\'))
            .Select(rest => prefix + rest[..rest.IndexOf('\\')])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<FolderEntry> List(string path)
    {
        var prefix = Prefix(path);
        var files = _files.Keys
            .Where(file => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                           && !file[prefix.Length..].Contains('\\'))
            .Select(file => new FolderEntry(file, false, _attributes.GetValueOrDefault(file, FileAttributes.Normal)));
        var folders = Directories(path)
            .Select(folder => new FolderEntry(
                folder, true, _attributes.GetValueOrDefault(folder, FileAttributes.Directory)));
        return files.Concat(folders).ToList();
    }

    private static string Prefix(string path)
    {
        return path.TrimEnd('\\') + "\\";
    }
}
