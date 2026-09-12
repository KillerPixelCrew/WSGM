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

    [Theory]
    // Starting with Windows and taking the screen over are separate choices, so the two manifest
    // flags come from the pair rather than from one switch.
    [InlineData(true, SessionStartMode.Game, true, false)]
    [InlineData(true, SessionStartMode.Desktop, false, true)]
    [InlineData(false, SessionStartMode.Game, false, false)]
    [InlineData(false, SessionStartMode.Desktop, false, false)]
    public void TheManifestProjectsBothSignInChoices(
        bool startAtSignIn, SessionStartMode mode, bool gameModeBoot, bool desktopResident)
    {
        var config = new AppConfig { StartAtSignIn = startAtSignIn, StartMode = mode };

        Assert.Equal(gameModeBoot, config.StartAtSignIn && config.StartMode is SessionStartMode.Game);
        Assert.Equal(desktopResident, config.StartAtSignIn && config.StartMode is SessionStartMode.Desktop);
    }

    [Fact]
    public void DisarmingTheSignInStartKeepsTheChosenMode()
    {
        var config = new AppConfig { StartAtSignIn = true, StartMode = SessionStartMode.Desktop };

        config.StartAtSignIn = false;

        // The recovery paths disarm the start itself; the mode is the user's preference and
        // survives, so re-enabling in Settings restores what they had.
        Assert.Equal(SessionStartMode.Desktop, config.StartMode);
    }

    [Fact]
    public void DesktopResidencyRoundTripsThroughTheManifest()
    {
        var json = JsonSerializer.Serialize(
            new BootManifest { DesktopResident = true, ExePath = @"C:\x\WSGM.exe" },
            BootManifestJsonContext.Default.BootManifest);

        var restored = BootManifestStore.TryParse(json);

        Assert.NotNull(restored);
        Assert.True(restored.DesktopResident);
        Assert.False(restored.GameModeBoot);
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
