using System.Text.Json;
using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class ScreensaverContentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WSGM.ScreensaverTests", Guid.NewGuid().ToString("N"));

    public ScreensaverContentTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void DiscoverySkipsCorruptImagesAndRefreshesMissingFoldersInPlace()
    {
        var source = new ScreensaverSourceConfig { Folder = _root };
        File.WriteAllBytes(Path.Combine(_root, "broken.png"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_root, "valid.PNG"), Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aL1sAAAAASUVORK5CYII="));
        var first = ScreensaverContent.Read(source, new Version(2, 4, 0));
        Assert.True(first.Available, first.Detail);
        Assert.Equal(["valid.PNG"], first.Playback!.Images);
        File.Delete(Path.Combine(_root, "valid.PNG"));
        Assert.False(ScreensaverContent.Read(source, new Version(2, 4, 0)).Available);
        source.Folder = Path.Combine(_root, "removed-drive");
        Assert.False(ScreensaverContent.Read(source, new Version(2, 4, 0)).Available);
        Directory.CreateDirectory(source.Folder);
        File.WriteAllBytes(Path.Combine(source.Folder, "one.gif"), Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw=="));
        Assert.True(ScreensaverContent.Read(source, new Version(2, 4, 0)).Available);
    }

    [Theory]
    [InlineData("../outside.js")]
    [InlineData("C:/outside.js")]
    [InlineData("file:///outside.js")]
    [InlineData("sub/%2e%2e/outside.js")]
    [InlineData("sub/../outside.js")]
    [InlineData("asset.js:alternate")]
    public void ManifestCannotAuthorizeOutsideOrAlternateStreamAssets(string asset)
    {
        WriteScene(new[] { asset });
        var result = ScreensaverContent.Read(new ScreensaverSourceConfig { Folder = _root, Pack = true }, new Version(2, 4, 0));
        Assert.False(result.Available);
    }

    [Fact]
    public void SceneRequiresCompatibleManifestAndDeclaredLocalAssets()
    {
        WriteScene(["scene.js"]);
        File.WriteAllText(Path.Combine(_root, "scene.js"), "document.body.textContent='scene';");
        var source = new ScreensaverSourceConfig { Folder = _root, Pack = true, FramesPerSecond = 15 };
        var result = ScreensaverContent.Read(source, new Version(2, 4, 0));
        Assert.True(result.Available, result.Detail);
        Assert.Equal("scene", result.Playback!.Type);
        Assert.Contains("index.html", result.Playback.Assets);
        Assert.Contains("scene.js", result.Playback.Assets);
        var revision = result.Playback.Revision;
        File.AppendAllText(Path.Combine(_root, "scene.js"), "// changed");
        Assert.NotEqual(revision, ScreensaverContent.Read(source, new Version(2, 4, 0)).Playback!.Revision);
        Assert.False(ScreensaverContent.Read(source, new Version(1, 0)).Available);
        File.Delete(Path.Combine(_root, "scene.js"));
        Assert.False(ScreensaverContent.Read(source, new Version(2, 4, 0)).Available);
    }

    [Fact]
    public async Task WireRejectsOversizedAndTruncatedMessages()
    {
        using var oversized = new MemoryStream(BitConverter.GetBytes(ScreensaverWire.MaximumPacketBytes + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => ScreensaverWire.ReadAsync(oversized, CancellationToken.None));
        using var truncated = new MemoryStream([10, 0, 0, 0, 123]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => ScreensaverWire.ReadAsync(truncated, CancellationToken.None));
    }

    private void WriteScene(string[] assets)
    {
        File.WriteAllText(Path.Combine(_root, "index.html"), "<!doctype html><script src='scene.js'></script>");
        var manifest = new ScreensaverManifest
        {
            Id = "test.scene", Name = "Test scene", Author = "WSGM", Version = "1.0.0", Type = "scene",
            Entry = "index.html", Assets = [.. assets], MinimumWsgm = "2.3.2"
        };
        File.WriteAllText(Path.Combine(_root, "screensaver.json"), JsonSerializer.Serialize(manifest, ScreensaverJson.Default.ScreensaverManifest));
    }

    public void Dispose()
    {
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WSGM.ScreensaverTests")) + Path.DirectorySeparatorChar;
        Assert.StartsWith(expected, Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase);
        Directory.Delete(_root, recursive: true);
    }
}
