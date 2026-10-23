using System.Text.Json;
using LibGPUDriverInteract;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class ScaledResolutionServiceTests
{
    [Fact]
    public void OriginalListSurvivesConfigurationAndRestoreKeepsTheNativeMode()
    {
        var backup = new ScaledResolutionBackup
        {
            DisplayPath = "monitor", AdapterIdentity = "gpu", OutputSpecific = true,
            Modes = [new ScaledResolutionSize(1920, 1080)]
        };
        var config = ConfigJson.Clone(new AppConfig { ScaledResolutionBackups = [backup] }, ConfigJsonContext.Default.AppConfig);
        var saved = Assert.Single(config.ScaledResolutionBackups);
        Assert.True(saved.Valid);
        Assert.Equal(backup.Modes, saved.Modes);
        var native = new GpuScaledResolution(2560, 1600);
        var edits = ScaledResolutionService.RestoreEdits(saved, [native, new GpuScaledResolution(1600, 900)], native);
        Assert.Equal([(new GpuScaledResolution(1920, 1080), false), (new GpuScaledResolution(1600, 900), true)], edits);
    }

    [Theory]
    [InlineData("{\"operation\":\"remove\",\"width\":1760,\"height\":1100}", true)]
    [InlineData("{\"operation\":\"remove\",\"width\":1920,\"height\":1080}", false)]
    [InlineData("{\"operation\":\"steps\",\"width\":2560,\"height\":1600}", true)]
    [InlineData("{\"operation\":\"steps\",\"width\":1920,\"height\":1080}", false)]
    [InlineData("{\"operation\":\"add\",\"width\":\"1760\",\"height\":1100}", false)]
    [InlineData("{\"operation\":\"restore\"}", false)]
    public void ResolutionListRevalidatesOwnershipAndThePercentageBasis(string edit, bool accepted)
    {
        var row = new SteamSettingsRow("resolutions", SteamSettingsRowKind.ResolutionList, "Resolutions",
            ResolutionEditor: new SteamSettingsResolutionEditor(["1760x1100"], 2560, 1600));
        using var payload = JsonDocument.Parse(edit);
        Assert.Equal(accepted, SteamNativeSettingsService.ValidValue(row, payload.RootElement));
    }

    [Theory]
    [InlineData("1760 × 1100", true)]
    [InlineData("1760x1100", true)]
    [InlineData(" 1760 X 1100 ", true)]
    [InlineData("99x1100", false)]
    [InlineData("1760x16000", false)]
    [InlineData("1760x1100x60", false)]
    [InlineData("1760 ×", false)]
    public void CustomSizeAcceptsPixelDimensionsOnly(string text, bool valid)
    {
        Assert.Equal(valid, ScaledResolutionService.TryParseResolution(text, out var resolution));
        if (valid)
        {
            Assert.Equal(new GpuScaledResolution(1760, 1100), resolution);
        }
    }

    [Fact]
    public void FourKPreviewUsesWidthAndHeightPercentagesRatherThanPixelArea()
    {
        var preview = ScaledResolutionService.PercentagePreview(new GpuScaledResolution(3840, 2160));
        Assert.Equal(11, preview.Count);
        Assert.Equal(new GpuScaledResolution(3840, 2160), preview[0]);
        Assert.Equal(new GpuScaledResolution(3648, 2052), preview[1]);
        Assert.Equal(new GpuScaledResolution(2880, 1620), preview[5]);
        Assert.Equal(new GpuScaledResolution(1920, 1080), preview[10]);
    }
}
