using Microsoft.Data.Sqlite;
using WSGM.Core;
using WSGM.Testing;

namespace WSGM.Tests.Core;

/// <summary>itch discovery from butler's caves and their verdicts.</summary>
public sealed class ItchLibrarySourceTests
{
    private const string Database = @"C:\Users\Player\AppData\Roaming\itch\db\butler.db";

    private static string Verdict(string basePath, params (string Path, string Flavor)[] candidates)
    {
        var list = string.Join(",", candidates.Select(candidate =>
            $"{{\"path\":\"{candidate.Path}\",\"flavor\":\"{candidate.Flavor}\",\"depth\":1}}"));
        return $"{{\"basePath\":\"{basePath}\",\"totalSize\":1,\"candidates\":[{list}]}}";
    }

    private static ItchLibrarySource Source(IReadOnlyList<ItchCave> caves, params string[] files)
    {
        HashSet<string> existing = new(files, StringComparer.OrdinalIgnoreCase) { Database };
        return new ItchLibrarySource(Database, _ => caves, existing.Contains);
    }

    [Fact]
    public async Task TheFirstWindowsCandidateIsTheExecutableWithItsSlashesNormalised()
    {
        var cave = new ItchCave(
            "42", "Moonlit", "game",
            Verdict("C:/itch/apps/moonlit", ("linux/moonlit.x86_64", "linux"), ("win/Moonlit.exe", "windows")),
            string.Empty);
        var source = Source([cave], @"C:\itch\apps\moonlit\win\Moonlit.exe");

        var game = Assert.Single(await source.DiscoverAsync([], CancellationToken.None));

        Assert.Equal("itch", game.SourceId);
        Assert.Equal("42", game.Key);
        Assert.Equal("Moonlit", game.Name);
        Assert.Equal(@"C:\itch\apps\moonlit", game.InstallPath);
        var route = Assert.Single(game.CommandRoutes);
        Assert.Equal("direct", route.Id);
        Assert.Equal(@"C:\itch\apps\moonlit\win\Moonlit.exe", route.Target);
        Assert.Equal(@"C:\itch\apps\moonlit\win", route.StartDirectory);
        Assert.Equal(string.Empty, route.LaunchOptions);
    }

    [Fact]
    public async Task TheInstallFolderStandsInWhenTheVerdictHasNoBasePath()
    {
        var cave = new ItchCave(
            "7", "Tool", "tool", "{\"candidates\":[{\"path\":\"tool.exe\"}]}", @"D:\itch\tool");
        var source = Source([cave], @"D:\itch\tool\tool.exe");

        var route = Assert.Single(Assert.Single(await source.DiscoverAsync([], CancellationToken.None)).CommandRoutes);

        Assert.Equal(@"D:\itch\tool\tool.exe", route.Target);
    }

    [Fact]
    public async Task OnlyGamesToolsAndUnclassifiedInstallsAreOffered()
    {
        var source = Source(
        [
            new ItchCave("1", "Game", "game", Verdict("C:/g", ("g.exe", "windows")), string.Empty),
            new ItchCave("2", "Soundtrack", "soundtrack", Verdict("C:/s", ("s.exe", "windows")), string.Empty),
            new ItchCave("3", "Unknown", string.Empty, Verdict("C:/u", ("u.exe", "windows")), string.Empty)
        ], @"C:\g\g.exe", @"C:\s\s.exe", @"C:\u\u.exe");

        Assert.Equal(["1", "3"], (await source.DiscoverAsync([], CancellationToken.None)).Select(game => game.Key));
    }

    [Fact]
    public async Task AnInstallWhoseExecutableIsGoneIsSkipped()
    {
        var source = Source([
            new ItchCave("1", "Gone", "game", Verdict("C:/gone", ("g.exe", "windows")), string.Empty)
        ]);

        Assert.Empty(await source.DiscoverAsync([], CancellationToken.None));
    }

    [Fact]
    public async Task AnInstallWithNoWindowsCandidateOrNoVerdictIsSkipped()
    {
        var source = Source(
        [
            new ItchCave("1", "Linux", "game", Verdict("C:/l", ("run.sh", "linux")), string.Empty),
            new ItchCave("2", "Unscanned", "game", string.Empty, @"C:\x"),
            new ItchCave("3", "Broken", "game", "{not json", @"C:\x")
        ], @"C:\l\run.sh");

        Assert.Empty(await source.DiscoverAsync([], CancellationToken.None));
    }

    [Fact]
    public async Task AGameInstalledTwiceIsOfferedOnce()
    {
        var source = Source(
        [
            new ItchCave("5", "Twice", "game", Verdict("C:/a", ("a.exe", "windows")), string.Empty),
            new ItchCave("5", "Twice", "game", Verdict("C:/b", ("b.exe", "windows")), string.Empty)
        ], @"C:\a\a.exe", @"C:\b\b.exe");

        var game = Assert.Single(await source.DiscoverAsync([], CancellationToken.None));
        Assert.Equal(@"C:\a\a.exe", Assert.Single(game.CommandRoutes).Target);
    }

    [Fact]
    public async Task WithoutTheDatabaseNothingIsFound()
    {
        var source = new ItchLibrarySource(
            Database, _ => throw new InvalidOperationException("Must not be read."), _ => false);

        Assert.False(source.Detect([]).Installed);
        Assert.Empty(await source.DiscoverAsync([], CancellationToken.None));
    }

    [Fact]
    public async Task TheInstallFolderTheDatabaseNamesNowWinsOverTheOneTheVerdictRecorded()
    {
        // The verdict is taken at install time and keeps the old folder after the install is moved.
        var cave = new ItchCave(
            "9", "Moved", "game", Verdict("C:/itch/apps/old", ("Moved.exe", "windows")), @"E:\itch\moved\");
        var source = Source([cave], @"E:\itch\moved\Moved.exe");

        var game = Assert.Single(await source.DiscoverAsync([], CancellationToken.None));

        Assert.Equal(@"E:\itch\moved", game.InstallPath);
        Assert.Equal(@"E:\itch\moved\Moved.exe", Assert.Single(game.CommandRoutes).Target);
    }

    [Fact]
    public async Task AnUnreadableDatabaseFailsTheScanRatherThanListingNothing()
    {
        var source = new ItchLibrarySource(
            Database,
            _ => throw new LauncherDatabaseException("butler.db could not be read.", new IOException()),
            path => path == Database);

        await Assert.ThrowsAsync<LauncherDatabaseException>(() => source.DiscoverAsync([], CancellationToken.None));
    }

    [Fact]
    public void AnOlderSchemaIsReadWithTheQueryItCanAnswer()
    {
        using TemporaryDirectory folder = new();
        var path = folder.GetPath("butler.db");
        Execute(path,
            "create table games (id integer primary key, title text)",
            "create table caves (id integer primary key, game_id integer, verdict text)",
            "insert into games values (2, 'Second'), (1, 'First')",
            "insert into caves values (20, 2, '{}'), (10, 1, '{}')");

        var caves = ItchLibrarySource.ReadCaves(path);

        Assert.Equal(["1", "2"], caves.Select(cave => cave.GameId));
        Assert.Equal("First", caves[0].Title);
    }

    [Fact]
    public void ADatabaseWithNoCavesHasNoCaves()
    {
        using TemporaryDirectory folder = new();
        var path = folder.GetPath("butler.db");
        Execute(path,
            "create table games (id integer primary key, title text, classification text)",
            "create table install_locations (id integer primary key, path text)",
            "create table caves (id integer primary key, game_id integer, verdict text, "
            + "install_location_id integer, install_folder_name text, custom_install_folder text)");

        Assert.Empty(ItchLibrarySource.ReadCaves(path));
    }

    [Fact]
    public void AMissingDatabaseIsAFailureNotAnEmptyLibrary()
    {
        Assert.Throws<LauncherDatabaseException>(() =>
            ItchLibrarySource.ReadCaves(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "butler.db")));
    }

    [Fact]
    public void TheAppsUninstallEntryAlsoCountsAsInstalled()
    {
        var entry = new UninstallEntry("itch", "itch", @"C:\itch", "itch corp.", "uninstall.exe", string.Empty);
        var source = new ItchLibrarySource(Database, _ => [], _ => false);

        Assert.True(source.Detect([entry]).Installed);
    }

    private static void Execute(string path, params string[] statements)
    {
        SqliteConnectionStringBuilder builder = new() { DataSource = path, Pooling = false };
        using SqliteConnection connection = new(builder.ToString());
        connection.Open();
        foreach (var statement in statements)
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }
    }
}
