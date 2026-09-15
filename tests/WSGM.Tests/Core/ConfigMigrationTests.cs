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

    [Theory]
    [InlineData("Off")]
    [InlineData("DpiOnly")]
    [InlineData("AutomaticProfiles")]
    public void TheThreeNonFixedDisplayModesAllBecomeDefault(string mode)
    {
        // Off becomes Default rather than "nothing at all": Game Mode has always needed the
        // scaling handling, and the old Off left a handheld running desktop scaling in Big Picture.
        var config = Migrate($$"""{ "DisplayManagement": "{{mode}}" }""");

        Assert.Equal(GameModeLaunchKind.Default, config.GameModeLaunch.Kind);
        Assert.Null(config.GameModeLaunch.GameLayout);
    }

    [Fact]
    public void FixedProfilesBecomeACustomLayoutLaidOutLeftToRightAwaitingConfirmation()
    {
        var config = Migrate("""
            {
              "DisplayManagement": "FixedProfiles",
              "DisplayProfiles": [
                {
                  "MonitorId": "\\Registry\\Machine\\...\\0001",
                  "DeviceName": "\\\\.\\DISPLAY1",
                  "DisplayName": "Internal panel",
                  "HdrAvailable": true,
                  "Desktop": { "Width": 1920, "Height": 1080, "RefreshRate": 60, "DpiPercent": 150, "HdrEnabled": false },
                  "Game": { "Width": 1280, "Height": 720, "RefreshRate": 120, "DpiPercent": 100, "HdrEnabled": true }
                },
                {
                  "DisplayName": "Living room TV",
                  "HdrAvailable": false,
                  "Desktop": { "Width": 2560, "Height": 1440, "RefreshRate": 60, "DpiPercent": 100 },
                  "Game": { "Width": 3840, "Height": 2160, "RefreshRate": 120, "DpiPercent": 100 }
                }
              ]
            }
            """);

        Assert.Equal(GameModeLaunchKind.Custom, config.GameModeLaunch.Kind);
        var game = config.GameModeLaunch.GameLayout!.Outputs;
        Assert.Equal(2, game.Count);
        Assert.Equal((0, 1280, 720), (game[0].X, game[0].Width, game[0].Height));
        Assert.Equal((1280, 3840, 2160), (game[1].X, game[1].Width, game[1].Height));
        Assert.True(game[0].Hdr);
        Assert.Null(game[1].Hdr);

        var desktop = config.GameModeLaunch.DesktopLayout!.Outputs;
        Assert.Equal((1920, 150), (desktop[0].Width, desktop[0].DpiPercent));
        Assert.Equal(GameModeReturn.DesktopLayout, config.GameModeLaunch.Return);

        // The old shape recorded a GDI name and a registry key, neither of which Windows can
        // resolve back to a monitor, so nothing may claim to know which display these are.
        Assert.All(game, output => Assert.Empty(output.Target.DevicePath));
        Assert.Empty(config.GameModeLaunch.KnownDisplays);
    }

    [Fact]
    public void AProfileWithoutAResolutionIsLeftOutRatherThanMigratedAsZeroByZero()
    {
        var config = Migrate("""
            {
              "DisplayManagement": "FixedProfiles",
              "DisplayProfiles": [
                { "DisplayName": "Never captured", "Desktop": {}, "Game": {} }
              ]
            }
            """);

        Assert.Equal(GameModeLaunchKind.Default, config.GameModeLaunch.Kind);
        Assert.Null(config.GameModeLaunch.GameLayout);
    }

    [Fact]
    public void EnabledRoutesBecomeOneStepActionListsAndADisplayWait()
    {
        var config = Migrate("""
            {
              "DisplayRoutes": {
                "Enabled": true,
                "EnterGameMode": {
                  "Plugin": { "PluginId": "wsgm.ir", "InstanceId": "blaster" },
                  "ActionId": "remote-run",
                  "Arguments": { "remote": { "Text": "hdmi-switch" } },
                  "Target": {
                    "DevicePath": "\\\\?\\DISPLAY#TV0001",
                    "FriendlyName": "Living room TV",
                    "AdapterLowPart": 0, "AdapterHighPart": 0, "TargetId": 3
                  },
                  "TimeoutSeconds": 45
                },
                "LeaveGameMode": {
                  "Plugin": { "PluginId": "wsgm.ir", "InstanceId": "blaster" },
                  "ActionId": "remote-press",
                  "TimeoutSeconds": 500
                },
                "DesktopWake": {
                  "Plugin": { "PluginId": "wsgm.ir", "InstanceId": "blaster" },
                  "ActionId": "remote-refresh"
                }
              }
            }
            """);

        var enter = Assert.Single(config.GameModeLaunch.EnterActions);
        Assert.Equal("remote-run", enter.ActionId);
        Assert.Equal("hdmi-switch", enter.Arguments["remote"].Text);
        Assert.Equal(45, enter.TimeoutSeconds);
        Assert.Equal(120, Assert.Single(config.GameModeLaunch.LeaveActions).TimeoutSeconds);
        Assert.Equal("remote-refresh", Assert.Single(config.GameModeLaunch.DesktopWakeActions).ActionId);
        Assert.Empty(config.GameModeLaunch.DesktopStartupActions);
        Assert.Equal("Living room TV", config.GameModeLaunch.WaitForDisplay!.FriendlyName);
        // A resolvable wait target seeds the catalog, so the editor can show it while it is unplugged.
        Assert.Equal("Living room TV", Assert.Single(config.GameModeLaunch.KnownDisplays).Target!.FriendlyName);
    }

    [Fact]
    public void ABindingWithNoPluginContributesNoStep()
    {
        var config = Migrate("""
            { "DisplayRoutes": { "Enabled": true, "EnterGameMode": { "TimeoutSeconds": 30 } } }
            """);

        Assert.Empty(config.GameModeLaunch.EnterActions);
        Assert.Equal(GameModeLaunchKind.Default, config.GameModeLaunch.Kind);
    }

    [Fact]
    public void DisabledRoutesAreDroppedWithoutContributingActions()
    {
        var config = Migrate("""
            {
              "DisplayRoutes": {
                "Enabled": false,
                "EnterGameMode": { "Plugin": { "PluginId": "p", "InstanceId": "i" }, "ActionId": "a" }
              }
            }
            """);

        Assert.Empty(config.GameModeLaunch.EnterActions);
    }

    [Fact]
    public void AnUndecodableRouteProfileIsDroppedRatherThanLosingTheFile()
    {
        // The blob is replay data for a topology that may no longer exist. Refusing the whole file
        // over it would cost the user every other setting they have.
        var config = Migrate("""
            {
              "DisplayRoutes": {
                "Enabled": true,
                "EnterGameMode": {
                  "Plugin": { "PluginId": "p", "InstanceId": "i" },
                  "ActionId": "a",
                  "Profile": { "FormatVersion": 1, "Targets": [], "PathData": ["AAAA"], "ModeData": [] }
                }
              }
            }
            """);

        Assert.Null(config.GameModeLaunch.GameLayout);
        Assert.Single(config.GameModeLaunch.EnterActions);
    }

    [Fact]
    public void ACurrentLaunchSectionWinsOverTheRetiredDisplayKeys()
    {
        var config = Migrate("""
            {
              "DisplayManagement": "FixedProfiles",
              "DisplayProfiles": [{ "DisplayName": "Old", "Game": { "Width": 800, "Height": 600 } }],
              "GameModeLaunch": { "Kind": "Default" }
            }
            """);

        Assert.Equal(GameModeLaunchKind.Default, config.GameModeLaunch.Kind);
        Assert.Null(config.GameModeLaunch.GameLayout);
    }

    [Fact]
    public void TheRetiredDisplayKeysAreGoneFromTheSavedDocument()
    {
        var config = Migrate("""
            { "DisplayManagement": "DpiOnly", "DisplayProfiles": [], "DisplayRoutes": { "Enabled": false } }
            """);

        var json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig);
        Assert.DoesNotContain("\"DisplayManagement\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"DisplayProfiles\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"DisplayRoutes\"", json, StringComparison.Ordinal);
        Assert.False(ConfigMigrations.MayNeedMigration(json));
    }

    [Fact]
    public void TheDisplayMigrationIsIdempotent()
    {
        var root = JsonNode.Parse("""
            {
              "DisplayManagement": "FixedProfiles",
              "DisplayProfiles": [{ "DisplayName": "A", "Desktop": { "Width": 1920, "Height": 1080 }, "Game": { "Width": 1920, "Height": 1080 } }]
            }
            """)!.AsObject();

        Assert.True(ConfigMigrations.Apply(root));
        var once = root.ToJsonString();
        Assert.False(ConfigMigrations.Apply(root));

        Assert.Equal(once, root.ToJsonString());
    }
}
