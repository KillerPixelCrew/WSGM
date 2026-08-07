using System.Text.Json;
using WSGM.Core;

namespace WSGM.Tests;

public sealed class BootManifestTests
{
    [Fact]
    public void ManifestRoundTripsThroughSourceGeneratedJson()
    {
        var original = new BootManifest
        {
            GameModeBoot = true,
            Elevate = true,
            ExePath = @"C:\Users\me\AppData\Local\WSGM\bin\WSGM.exe",
        };

        var json = JsonSerializer.Serialize(original, BootManifestJsonContext.Default.BootManifest);
        var restored = BootManifestStore.TryParse(json);

        Assert.NotNull(restored);
        Assert.True(restored.GameModeBoot);
        Assert.True(restored.Elevate);
        Assert.Equal(original.ExePath, restored.ExePath);
        Assert.Equal(1, restored.SchemaVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{ \"GameModeBoot\": tru")]
    [InlineData("null")]
    [InlineData("[]")]
    public void GarbageParsesToNullInsteadOfThrowing(string json)
    {
        Assert.Null(BootManifestStore.TryParse(json));
    }

    [Fact]
    public void MissingExePathIsUnusable()
    {
        Assert.Null(BootManifestStore.TryParse(
            "{ \"SchemaVersion\": 1, \"GameModeBoot\": true, \"ExePath\": \"\" }"));
    }

    [Fact]
    public void UnknownSchemaVersionIsUnusable()
    {
        Assert.Null(BootManifestStore.TryParse(
            "{ \"SchemaVersion\": 2, \"GameModeBoot\": true, \"ExePath\": \"C:\\\\x.exe\" }"));
    }

    [Fact]
    public void SaveAndTryLoadRoundTripOnDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wsgm-boot-test-{Environment.ProcessId}.json");
        try
        {
            BootManifestStore.Save(path, new BootManifest
            {
                GameModeBoot = false,
                Elevate = true,
                ExePath = @"C:\x\WSGM.exe",
            });
            var loaded = BootManifestStore.TryLoad(path);

            Assert.NotNull(loaded);
            Assert.False(loaded.GameModeBoot);
            Assert.True(loaded.Elevate);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingFileLoadsAsNull()
    {
        Assert.Null(BootManifestStore.TryLoad(
            Path.Combine(Path.GetTempPath(), "wsgm-boot-test-does-not-exist.json")));
    }

    [Fact]
    public void OversizedFileLoadsAsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wsgm-boot-test-big-{Environment.ProcessId}.json");
        try
        {
            File.WriteAllText(path, new string(' ', 65 * 1024) + "{}");
            Assert.Null(BootManifestStore.TryLoad(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
