using System.Text;
using System.Text.Json.Nodes;
using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class GameToolCatalogTests
{
    [Fact]
    public void BundledOptiScalerOffersStableFirstAndNightlyFromItsOwnRepository()
    {
        var tool = GameToolCatalog.Current.Find("optiscaler")!;

        Assert.Equal(["stable", "nightly"], tool.Channels.Select(channel => channel.Id));
        Assert.Same(tool.Channels[0], tool.DefaultChannel);
        Assert.Equal("optiscaler/OptiScaler", tool.DefaultChannel.Source.Repository);
        Assert.Equal("stable", tool.DefaultChannel.Source.Channel);
        var nightly = tool.Channel("nightly")!;
        Assert.Equal("optiscaler/OptiScaler-nightly", nightly.Source.Repository);
        Assert.NotEqual("stable", nightly.Source.Channel);
        Assert.Equal("7z", nightly.Source.PackageType);
        Assert.Equal("Daily test builds of the next version; may be unstable", nightly.Description);
        Assert.Same(tool.DefaultChannel, tool.ChannelOrDefault(""));
        Assert.Same(tool.DefaultChannel, tool.ChannelOrDefault("retired"));
    }

    [Fact]
    public void TheV10LayoutKeepsItsSubfolderReplaceableAndRequired()
    {
        var tool = GameToolCatalog.Current.Find("optiscaler")!;

        // A manual v10 install keeps every bundled file below OptiScaler/, which Replace existing takes over.
        Assert.True(tool.IsReplaceable("OptiScaler/D3D12_OptiScaler/D3D12Core.dll"));
        Assert.True(tool.IsReplaceable("OptiScaler/Streamlined_fetcher_windows.bat"));
        Assert.Equal(["OptiScaler"], tool.Payload.FamilyDirectories["v10"]);
        Assert.Equal("v10", GameToolVersions.Family(tool, "10.0.0-pre1"));
        Assert.True(tool.IsExcluded("setup_windows.bat"));
        Assert.False(tool.IsExcluded("OptiScaler/Streamlined_fetcher_windows.bat"));
    }

    [Theory]
    [InlineData("no channels")]
    [InlineData("duplicate id")]
    [InlineData("unsupported provider")]
    [InlineData("plain http")]
    [InlineData("bad repository")]
    [InlineData("unknown package")]
    [InlineData("missing label")]
    [InlineData("pattern without a version group")]
    [InlineData("invalid pattern")]
    [InlineData("invalid asset pattern")]
    public void EveryChannelIsValidatedStrictly(string defect)
    {
        var catalog = Bundled();
        var tool = catalog["tools"]![0]!;
        var channels = tool["channels"]!.AsArray();
        var nightly = channels[1]!;
        switch (defect)
        {
            case "no channels":
                channels.Clear();
                break;
            case "duplicate id":
                nightly["id"] = "stable";
                break;
            case "unsupported provider":
                nightly["source"]!["provider"] = "Gitea";
                break;
            case "plain http":
                nightly["source"]!["baseUrl"] = "http://api.github.com/";
                break;
            case "bad repository":
                nightly["source"]!["repository"] = "optiscaler";
                break;
            case "unknown package":
                nightly["source"]!["packageType"] = "rar";
                break;
            case "missing label":
                nightly["label"] = "";
                break;
            case "pattern without a version group":
                nightly["versionPattern"] = "^optiscaler_(?<build>[0-9]{8})[.]7z$";
                break;
            case "invalid pattern":
                nightly["versionPattern"] = "(?<version>[0-9";
                break;
            case "invalid asset pattern":
                nightly["source"]!["assetPattern"] = "(";
                break;
        }

        Assert.ThrowsAny<Exception>(() => Parse(catalog));
    }

    [Fact]
    public void TheUntouchedBundleParses()
    {
        var parsed = Parse(Bundled());

        Assert.Equal(2, parsed.Tools[0].Channels.Length);
    }

    private static JsonNode Bundled()
    {
        using var stream = typeof(GameToolCatalog).Assembly
            .GetManifestResourceStream("WSGM.Core.GameTools.game-tools.json")!;
        return JsonNode.Parse(stream)!;
    }

    private static GameToolCatalog Parse(JsonNode catalog)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(catalog.ToJsonString()));
        return GameToolCatalog.Parse(stream);
    }
}
