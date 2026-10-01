using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using WSGM.Core;
using WSGM.Testing;

namespace WSGM.Tests.Core.Themes;

/// <summary>Installing from the store unpacks the package over the themes folder and fetches what it needs.</summary>
public sealed class ThemeInstallerTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();

    private string Root => _temporary.GetPath("themes");

    public void Dispose()
    {
        _temporary.Dispose();
    }

    private static byte[] Zip(params (string Path, string Content)[] entries)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, true))
        {
            foreach (var (path, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    private static string Details(string id, string name, string dependencies = "[]", int manifestVersion = 9)
    {
        return $$"""
                 { "id": "{{id}}", "name": "{{name}}", "displayName": "{{name}}", "version": "v1", "target": "Steam Deck",
                   "targets": [], "manifestVersion": {{manifestVersion}}, "specifiedAuthor": "a", "images": [],
                   "download": { "id": "zip-{{id}}", "downloadCount": 1 }, "dependencies": {{dependencies}} }
                 """;
    }

    [Fact]
    public async Task InstallsTheThemeAndTheDependencyItLacksButNotOneAlreadyThere()
    {
        var packages = new Dictionary<string, byte[]>
        {
            ["zip-top"] = Zip(("Top/theme.json", """{ "name": "Top" }"""), ("Top/theme.css", ".t{}")),
            ["zip-dep"] = Zip(("Dep/theme.json", """{ "name": "Dep" }""")),
            ["zip-have"] = Zip(("Have/theme.json", """{ "name": "Have" }"""))
        };
        var handler = new ThemeStoreClientTests.StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/themes/top" => Ok(Details("top", "Top",
                """[{ "id": "dep", "name": "Dep", "displayName": "Dep", "version": "v1" }, { "id": "have", "name": "Have", "displayName": "Have", "version": "v1" }]""")),
            "/themes/dep" => Ok(Details("dep", "Dep")),
            "/themes/have" => Ok(Details("have", "Have")),
            var path when path.StartsWith("/blobs/", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode
                    .OK)
                { Content = new ByteArrayContent(packages[path["/blobs/".Length..]]) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var installer = new ThemeInstaller(new ThemeStoreClient(handler, "https://store.example"), Root);

        var installed = await installer.InstallAsync("top", ["Have"], CancellationToken.None);

        Assert.Equal(["Top", "Dep"], installed);
        Assert.Equal(".t{}", File.ReadAllText(Path.Combine(Root, "Top", "theme.css")));
        Assert.True(File.Exists(Path.Combine(Root, "Dep", "theme.json")));
        Assert.False(Directory.Exists(Path.Combine(Root, "Have")), "a dependency already installed is not fetched");
    }

    [Fact]
    public async Task RefusesAManifestNewerThanTheLoaderReads()
    {
        var handler = new ThemeStoreClientTests.StubHandler(_ => Ok(Details("new", "New", manifestVersion: 10)));
        var installer = new ThemeInstaller(new ThemeStoreClient(handler, "https://store.example"), Root);

        var refused = await Assert.ThrowsAsync<ThemeStoreException>(() =>
            installer.InstallAsync("new", [], CancellationToken.None));

        Assert.Contains("unsupported by this version", refused.Message);
        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public void APackageThatWouldWriteOutsideTheFolderIsRefusedWhole()
    {
        var refused = Assert.Throws<ThemeStoreException>(() =>
            ThemeInstaller.Unpack(new MemoryStream(Zip(("../escape.css", ".x{}"), ("Ok/theme.css", ".y{}"))), Root));

        Assert.Contains("could not be unpacked", refused.Message);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(Root)!, "escape.css")));
    }

    [Fact]
    public void InvalidLaterThemeCannotPartiallyReplaceAnEarlierOne()
    {
        var previous = Path.Combine(Root, "Good", "theme.css");
        Directory.CreateDirectory(Path.GetDirectoryName(previous)!);
        File.WriteAllText(previous, ".old{}");
        Assert.Throws<ThemeStoreException>(() => ThemeInstaller.Unpack(new MemoryStream(Zip(
            ("Good/theme.css", ".new{}"), ("Bad/theme.json", "{broken"))), Root));
        Assert.Equal(".old{}", File.ReadAllText(previous));
        Assert.False(Directory.Exists(Path.Combine(Root, "Bad")));
    }

    [Fact]
    public void PlainStylesheetUpdateRetainsExistingUserStateAndUnmentionedFiles()
    {
        var folder = Path.Combine(Root, "Plain");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "theme.css"), ".old{}");
        File.WriteAllText(Path.Combine(folder, "config.json"), "user-state");
        File.WriteAllText(Path.Combine(folder, "custom.css"), ".user{}");
        ThemeInstaller.Unpack(new MemoryStream(Zip(("Plain/theme.css", ".new{}"))), Root);
        Assert.Equal(".new{}", File.ReadAllText(Path.Combine(folder, "theme.css")));
        Assert.Equal("user-state", File.ReadAllText(Path.Combine(folder, "config.json")));
        Assert.Equal(".user{}", File.ReadAllText(Path.Combine(folder, "custom.css")));
    }

    [Fact]
    public void InterruptedPromotionRestoresOldFoldersAndRemovesOnlyNewlyPromotedOnes()
    {
        var id = Guid.NewGuid().ToString("N");
        var backup = Root + ".wsgm-backup-" + id;
        var stage = Root + ".wsgm-stage-" + id;
        Directory.CreateDirectory(Path.Combine(backup, "Old"));
        Directory.CreateDirectory(Path.Combine(Root, "Old"));
        Directory.CreateDirectory(Path.Combine(Root, "New"));
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(backup, "Old", "theme.css"), ".old{}");
        File.WriteAllText(Path.Combine(Root, "Old", "theme.css"), ".new{}");
        File.WriteAllText(Root + ".wsgm-update.json", JsonSerializer.Serialize(new
        {
            Id = id, Names = new[] { "Old", "New" }, Existing = new[] { "Old" }, Committed = false
        }));
        ThemeInstaller.Recover(Root);
        Assert.Equal(".old{}", File.ReadAllText(Path.Combine(Root, "Old", "theme.css")));
        Assert.False(Directory.Exists(Path.Combine(Root, "New")));
        Assert.False(Directory.Exists(backup));
        Assert.False(Directory.Exists(stage));
    }

    private static HttpResponseMessage Ok(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
    }
}
