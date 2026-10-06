using System.IO.Compression;
using System.Text;
using WSGM.Core;
using WSGM.Testing;

namespace WSGM.Tests.Core.Sounds;

public sealed class SoundPackLibraryTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();

    private string Root => _temporary.GetPath("sounds");

    public void Dispose()
    {
        _temporary.Dispose();
    }

    [Fact]
    public void ExactMappingsIgnoreAndMissingAssetsKeepUnknownEventsUnmapped()
    {
        var library = new SoundPackLibrary(Root);
        using var zip = Archive(("pack/pack.json", """
                                                   {"name":"Test","mappings":{"navigation.wav":["custom.wav"],"unknown.wav":["custom.wav"]},"ignore":["ignored.wav"]}
                                                   """), ("pack/custom.wav", "wave"), ("pack/ignored.wav", "wave"));
        var id = library.Install(zip);
        var pack = library.ReadPack(id);
        var map = library.BuildOverrides(pack, ["navigation.wav", "missing.wav", "ignored.wav"], out var detail);
        Assert.Single(map);
        Assert.StartsWith("data:audio/wav;base64,", map["navigation.wav"][0]);
        Assert.Contains("1 unknown mappings", detail);
        Assert.Contains("1 missing", detail);
    }

    [Fact]
    public void UpdatingLocalPackPreservesItsIdentityAndReplacesRemovedAssets()
    {
        var library = new SoundPackLibrary(Root);
        using var first = Archive(("pack.json", """{"name":"Test","version":"1"}"""), ("old.wav", "old"));
        var id = library.Install(first);
        using var second = Archive(("pack/pack.json", """{"name":"Test","version":"2"}"""), ("pack/new.wav", "new"));
        Assert.Equal(id, library.Install(second));
        Assert.Equal("2", library.ReadPack(id).Version);
        Assert.False(File.Exists(library.AssetPath(id, "old.wav")));
        Assert.True(File.Exists(library.AssetPath(id, "new.wav")));
        Assert.Single(library.Read());
    }

    [Fact]
    public void TraversingArchiveAndMusicPackAreRejectedWithoutReplacingTheInstalledPack()
    {
        var library = new SoundPackLibrary(Root);
        using var first = Archive(("pack.json", """{"name":"Test"}"""));
        var id = library.Install(first);
        using var traversing = Archive(("../escaped.wav", "bad"), ("pack.json", """{"name":"Test"}"""));
        Assert.Throws<InvalidDataException>(() => library.Install(traversing));
        using var music = Archive(("pack.json", """{"name":"Test","music":true}"""));
        Assert.Throws<InvalidDataException>(() => library.Install(music));
        Assert.Equal(id, Assert.Single(library.Read()).Id);
        Assert.DoesNotContain(Directory.EnumerateDirectories(Root), path => Path.GetFileName(path).StartsWith('.'));
    }

    [Fact]
    public void InvalidManifestIsListedAsUnavailableRatherThanBreakingOtherPacks()
    {
        var library = new SoundPackLibrary(Root);
        Directory.CreateDirectory(Path.Combine(Root, "broken"));
        File.WriteAllText(Path.Combine(Root, "broken", "pack.json"), "{");
        var broken = Assert.Single(library.Read());
        Assert.NotNull(broken.Error);
    }

    [Fact]
    public void PackAssetsCannotEscapeTheirInstalledFolder()
    {
        var library = new SoundPackLibrary(Root);
        Assert.Throws<InvalidDataException>(() => library.AssetPath("test", "../other.wav"));
        Assert.Throws<InvalidDataException>(() => library.PackPath("../other"));
    }

    [Fact]
    public void EcosystemIdentityKeepsRenamedUpdatesAndSameNamedDistinctPacksSeparate()
    {
        var library = new SoundPackLibrary(Root);
        using var first = Archive(("pack.json", """{"name":"First","id":"author.pack","version":"1"}"""));
        var id = library.Install(first);
        using var renamed = Archive(("pack.json", """{"name":"Renamed","id":"author.pack","version":"2"}"""));
        Assert.Equal(id, library.Install(renamed));
        Assert.Equal("Renamed", Assert.Single(library.Read()).Name);
        using var distinct = Archive(("pack.json", """{"name":"Renamed","id":"other.pack"}"""));
        Assert.NotEqual(id, library.Install(distinct));
        Assert.Equal(2, library.Read().Length);
    }

    [Fact]
    public void AddingManifestIdentityPreservesExistingSelectionAndReadsNestedPreviews()
    {
        var library = new SoundPackLibrary(Root);
        using var original = Archive(("pack.json", """{"name":"Test"}"""));
        var id = library.Install(original);
        using var updated = Archive(("pack.json", """
                                                  {"name":"Test","id":"author.test","source":"https://example.invalid/source","preview":"clips/preview.wav"}
                                                  """), ("clips/preview.wav", "wave"));
        Assert.Equal(id, library.Install(updated));
        var pack = library.ReadPack(id);
        Assert.Equal("author.test", pack.ManifestId);
        Assert.Equal("https://example.invalid/source", pack.Source);
        Assert.Equal("clips/preview.wav", Assert.Single(pack.Assets).Replace('\\', '/'));
    }

    [Fact]
    public void CompatibilityNamesUnknownEventsMissingVariantsAndIgnoredStockResources()
    {
        var library = new SoundPackLibrary(Root);
        using var archive = Archive(("pack.json", """
                                                  {"name":"Test","mappings":{"navigation.wav":["valid.wav","missing.wav"],"other-client.wav":["valid.wav"]},"ignore":["ignored.wav"]}
                                                  """), ("valid.wav", "wave"));
        var id = library.Install(archive);
        var compatibility = library.InspectCompatibility(library.ReadPack(id),
            ["navigation.wav", "absent.wav", "ignored.wav"]);
        Assert.Equal("navigation.wav", Assert.Single(compatibility.SupportedResources));
        Assert.Equal("absent.wav", Assert.Single(compatibility.MissingResources));
        Assert.Equal("ignored.wav", Assert.Single(compatibility.IgnoredResources));
        Assert.Equal("other-client.wav", Assert.Single(compatibility.UnknownMappings));
        Assert.Contains(compatibility.AssetProblems, problem => problem.Contains("navigation.wav: missing.wav"));
    }

    [Fact]
    public void OneUnreadableAssetLeavesOtherEventsAndVariantsAvailable()
    {
        var library = new SoundPackLibrary(Root);
        using var archive = Archive(("pack.json", """
                                                  {"name":"Test","mappings":{"navigation.wav":["locked.wav","valid.wav"]}}
                                                  """), ("locked.wav", "wave"), ("valid.wav", "wave"),
            ("other.wav", "wave"));
        var id = library.Install(archive);
        var pack = library.ReadPack(id);
        using var locked = new FileStream(library.AssetPath(id, "locked.wav"), FileMode.Open, FileAccess.Read,
            FileShare.None);
        var map = library.BuildOverrides(pack, ["navigation.wav", "other.wav"], out var detail);
        Assert.Equal(2, map.Count);
        Assert.Single(map["navigation.wav"]);
        Assert.Contains("1 missing or unsupported assets", detail);
    }

    internal static MemoryStream Archive(params (string Path, string Contents)[] entries)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var (path, contents) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), Encoding.UTF8);
                writer.Write(contents);
            }
        }

        stream.Position = 0;
        return stream;
    }
}
