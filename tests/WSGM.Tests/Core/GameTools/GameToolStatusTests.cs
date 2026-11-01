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
