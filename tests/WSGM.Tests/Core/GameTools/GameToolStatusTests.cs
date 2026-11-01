using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class GameToolStatusTests
{
    private static readonly GameToolDefinition Tool = GameToolCatalog.Current.Find("optiscaler")!;
    private static readonly GameToolChannel Nightly = Tool.Channel("nightly")!;
    private static readonly GameToolInspection Clean = new([], [], [], []);

    [Fact]
    public void ANewNightlyBuildOfTheSameNumberIsAnUpdate()
    {
        var installed = Manifest("nightly", "10.0.0-pre1", "nightly-20261030:21:aaa",
            "OptiScaler_v10.0.0-pre1_20261030.7z", "v10");
        var latest = Release("10.0.0-pre1", "nightly-20261031:22:bbb", "OptiScaler_v10.0.0-pre1_20261031.7z");

        var evaluation = GameToolStatus.Evaluate(Tool, "ready", null, "x64", installed, Clean, latest, Nightly, []);

        Assert.Equal("updateAvailable", evaluation.Status);
        Assert.Equal("Version 10.0.0-pre1 · nightly 31 Oct 2026 is available.", evaluation.StatusText);
        Assert.Contains("update", evaluation.Actions);
        Assert.Contains("switchChannel", evaluation.Actions);
        Assert.False(evaluation.UpdateChangesConfig);
    }

    [Fact]
    public void TheInstalledNightlyBuildIsCurrent()
    {
        var installed = Manifest("nightly", "10.0.0-pre1", "nightly-20261031:22:bbb",
            "OptiScaler_v10.0.0-pre1_20261031.7z", "v10");
        var latest = Release("10.0.0-pre1", "nightly-20261031:22:bbb", "OptiScaler_v10.0.0-pre1_20261031.7z");

        var evaluation = GameToolStatus.Evaluate(Tool, "ready", null, "x64", installed, Clean, latest, Nightly, []);

        Assert.Equal("installed", evaluation.Status);
        Assert.DoesNotContain("update", evaluation.Actions);
        Assert.Contains("switchChannel", evaluation.Actions);
    }

    [Fact]
    public void ReturningToAnOlderStableChangesTheConfigurationFormat()
    {
        var installed = Manifest("nightly", "10.0.0-pre1", "nightly-20261031:22:bbb",
            "OptiScaler_v10.0.0-pre1_20261031.7z", "v10");
        var stable = Release("0.9.4", "v0.9.4:11:ccc", "Optiscaler_0.9.4-final.20260718._MM.7z");

        // An older release in another channel is never an update; switching to it is the user's own choice.
        Assert.Equal(GameToolVersionRelation.NewerThanLatest, GameToolStatus.Relation(Tool, installed, stable));
        Assert.True(GameToolStatus.ChangesConfig(Tool, installed, stable));
        Assert.False(GameToolStatus.ChangesConfig(Tool, installed with { Family = "v09" }, stable));
        Assert.False(GameToolStatus.ChangesConfig(Tool, null, stable));
    }

    [Fact]
    public void ARunningGameOffersNoSwitch()
    {
        var installed = Manifest("stable", "0.9.4", "v0.9.4:11:ccc", "Optiscaler_0.9.4-final.20260718._MM.7z",
            "v09");

        var evaluation = GameToolStatus.Evaluate(Tool, "running", "Close the game.", "x64", installed, Clean, null,
            Tool.DefaultChannel, []);

        Assert.Empty(evaluation.Actions);
    }

    [Fact]
    public void TheResolverRoleForTheGameReadsAsLikely()
    {
        Assert.Equal("Likely the game", GameToolStatus.RoleLabel(GameRuntimeRole.Game));
    }

    [Fact]
    public void OptiPatcherIsOfferedOnlyForAGameOnItsListAndSuggestedOnlyWhereItHelps()
    {
        var addon = Tool.Addon("optipatcher")!;
        var listed = new GameToolAddonSupport(new HashSet<string> { "mgsdelta-win64-shipping.exe" }, null,
            DateTime.UtcNow);

        var amd = GameToolStatus.Addon(addon, null, "MGSDelta-Win64-Shipping.exe", listed, null, ["amd"], true, true);
        Assert.True(amd is { Show: true, Support: "supported", Recommended: true, CanChange: true, Installed: false });
        Assert.Equal("OptiPatcher supports MGSDelta-Win64-Shipping.exe.", amd.Text);

        var nvidia = GameToolStatus.Addon(addon, null, "MGSDelta-Win64-Shipping.exe", listed, null, ["nvidia"],
            true, true);
        Assert.True(nvidia is { Show: true, Recommended: false, CanChange: true });
        Assert.Equal("Not needed on NVIDIA graphics.", nvidia.Note);

        var other = GameToolStatus.Addon(addon, null, "DS2.exe", listed, null, ["amd"], true, true);
        Assert.False(other.Show);

        var checking = GameToolStatus.Addon(addon, null, "MGSDelta-Win64-Shipping.exe", null, null, ["amd"], true,
            true);
        Assert.False(checking.Show);

        var failed = GameToolStatus.Addon(addon, null, "MGSDelta-Win64-Shipping.exe",
            new GameToolAddonSupport(null, "Couldn't reach GitHub.", DateTime.UtcNow), null, ["amd"], true, true);
        Assert.True(failed is { Show: true, CanChange: false, Recommended: false, Text: "Couldn't reach GitHub." });
    }

    [Fact]
    public void AnInstalledOptiPatcherStaysShownAndOffersANewerBuild()
    {
        var addon = Tool.Addon("optipatcher")!;
        var manifest = Manifest("stable", "0.9.4", "v0.9.4:11:ccc", "Optiscaler_0.9.4-final.20260718._MM.7z",
            "v09") with
        {
            Addons = [new GameToolAddonRecord { Id = "optipatcher", ReleaseId = "rolling:1:aaa", BuildDate = "2026-10-18" }]
        };
        var newer = new HostedRelease("rolling", "rolling:1:bbb", "", "GitHub",
            new ReleaseAsset("OptiPatcher.asi", "https://example.test/OptiPatcher.asi", "bbb", 1,
                "9:2026-10-30T01:59:01Z"));

        var current = GameToolStatus.Addon(addon, manifest, "Game.exe", null, null, ["amd"], true, true);
        var outdated = GameToolStatus.Addon(addon, manifest, "Game.exe", null, newer, ["amd"], true, true);

        Assert.True(current is { Show: true, Installed: true, CanChange: true, UpdateAvailable: false });
        Assert.Equal("Installed, the build of 18 Oct 2026.", current.Text);
        Assert.True(outdated.UpdateAvailable);
        Assert.Equal("The build of 30 Oct 2026 is available.", outdated.Text);
    }

    private static GameToolManifest Manifest(string channel, string version, string releaseId, string asset,
        string family)
    {
        return new GameToolManifest
        {
            ToolId = "optiscaler", Channel = channel, Version = version, ReleaseVersion = version,
            ReleaseId = releaseId, AssetName = asset, Family = family, ProxyName = "dxgi.dll",
            State = GameToolManifestState.Installed
        };
    }

    private static HostedRelease Release(string version, string id, string asset)
    {
        return new HostedRelease(version, id, "", "GitHub", new ReleaseAsset(asset, "https://example.test/" + asset));
    }
}
