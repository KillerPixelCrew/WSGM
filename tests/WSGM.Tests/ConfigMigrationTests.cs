using System.Text.Json;
using System.Text.Json.Nodes;
using WSGM.Core;

namespace WSGM.Tests;

public sealed class ConfigMigrationTests
{
    private static AppConfig Migrate(string json)
    {
        Assert.True(ConfigMigrations.MayNeedMigration(json));
        return ConfigStore.DeserializeConfig(json);
    }

    [Fact]
    public void GameModeBootTrue_BecomesASignInGameStart()
    {
        var config = Migrate("""{ "GameModeBootEnabled": true }""");

        Assert.True(config.StartAtSignIn);
        Assert.Equal(SessionStartMode.Game, config.StartMode);
    }

    [Fact]
    public void RouteAutomationWithoutGameModeBoot_BecomesASignInDesktopStart()
    {
        var config = Migrate("""
            { "GameModeBootEnabled": false, "DisplayRoutes": { "Enabled": true } }
            """);

        Assert.True(config.StartAtSignIn);
        Assert.Equal(SessionStartMode.Desktop, config.StartMode);
    }

    [Fact]
    public void NeitherBootNorResidency_LeavesTheSignInAlone()
    {
        var config = Migrate("""
            { "GameModeBootEnabled": false, "DisplayRoutes": { "Enabled": false } }
            """);

        Assert.False(config.StartAtSignIn);
        Assert.Equal(SessionStartMode.Game, config.StartMode);
    }

    [Fact]
    public void CurrentKeysWin_WhenTheRetiredKeyIsAlsoPresent()
    {
        // A newer build's save beside a leftover key: the retired one must not undo it.
        var config = Migrate("""
            { "GameModeBootEnabled": true, "StartAtSignIn": false, "StartMode": "Desktop" }
            """);

        Assert.False(config.StartAtSignIn);
        Assert.Equal(SessionStartMode.Desktop, config.StartMode);
    }

    [Fact]
    public void MigrationIsIdempotent()
    {
        var root = JsonNode.Parse("""{ "GameModeBootEnabled": false }""")!.AsObject();

        Assert.True(ConfigMigrations.Apply(root));
        var once = root.ToJsonString();
        Assert.False(ConfigMigrations.Apply(root));

        Assert.Equal(once, root.ToJsonString());
    }

    [Fact]
    public void AMigratedDocumentKeepsItsOtherSettingsAndDropsTheRetiredKey()
    {
        var config = Migrate("""
            {
              "GameModeBootEnabled": false,
              "PreviousShellValue": "explorer.exe",
              "ExplorerLogonSettleMs": 250
            }
            """);

        Assert.Equal("explorer.exe", config.PreviousShellValue);
        Assert.Equal(250, config.ExplorerLogonSettleMs);

        var json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig);
        Assert.DoesNotContain("GameModeBootEnabled", json, StringComparison.Ordinal);
        Assert.False(ConfigMigrations.MayNeedMigration(json));
    }

    [Fact]
    public void ADocumentWithoutTheRetiredKeyNeedsNoPass()
        => Assert.False(ConfigMigrations.MayNeedMigration("""{ "StartAtSignIn": true }"""));
}
