using System.Text.Json;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class SteamNativeSettingsServiceTests
{
    [Theory]
    [InlineData("#FF0000", 0xFF0000)]
    [InlineData("hsla(120, 100%, 50%, 1)", 0x00FF00)]
    [InlineData("hsla(240, 100%, 50%, 1)", 0x0000FF)]
    [InlineData("hsla(0, 0%, 100%, 1)", 0xFFFFFF)]
    public void SavedOpaqueColorPreservesRgb(string css, int expected)
    {
        Assert.True(SteamNativeSettingsService.TryColor(JsonSerializer.SerializeToElement(css), out var color));
        Assert.Equal(expected, color);
    }

    [Theory]
    [InlineData("hsla(0, 100%, 50%, 0.5)")]
    [InlineData("hsla(NaN, 100%, 50%, 1)")]
    [InlineData("hsla(0, 101%, 50%, 1)")]
    [InlineData("hsla(0, 100%, -1%, 1)")]
    [InlineData("#FFFFFFFF")]
    [InlineData("#GGGGGG")]
    public void MalformedOrTransparentColorCannotReachDevice(string css)
    {
        Assert.False(SteamNativeSettingsService.TryColor(JsonSerializer.SerializeToElement(css), out _));
    }

    [Fact]
    public void RangeReplacementRequiresRebuildingTheMountedEditor()
    {
        var row = new DeviceOverlayCapability("limit", null, DeviceOverlaySection.PowerAndThermals,
            DescriptorStatus.Available, "Limit", "", "", true, CapabilityValue.Integer(10))
        {
            Role = CapabilityRole.GenericRange, Writable = true, ValueKind = CapabilityValueKind.Integer,
            Minimum = 0, Maximum = 40, Step = 1
        };
        Assert.True(row.SameLayoutAs(row with { CurrentValue = CapabilityValue.Integer(15) }));
        Assert.False(row.SameLayoutAs(row with { Maximum = 20 }));
        Assert.False(row.SameLayoutAs(row with { Step = 5 }));
        Assert.False(row.SameLayoutAs(row with { Role = CapabilityRole.PowerSustainedLimit }));
    }

    [Fact]
    public void LightingEditorStagesOpaqueColorWithStableLogicalIdentity()
    {
        var capability = new DeviceOverlayCapability("lighting.color", "left", DeviceOverlaySection.LightingAndFeatures,
            DescriptorStatus.Available, "Left ring", string.Empty, string.Empty, true,
            new CapabilityValue { Kind = CapabilityValueKind.Color, ColorValue = 0x123456 })
        {
            Writable = true,
            ValueKind = CapabilityValueKind.Color
        };
        var row = SteamNativeSettingsService.DeviceRow(capability);
        Assert.Equal(SteamSettingsRowKind.Color, row.Kind);
        Assert.Equal("#123456", row.Text);
        Assert.False(row.ColorAlpha);
        Assert.Contains("Save", row.Description);
        Assert.Equal(row.Key, SteamNativeSettingsService.DeviceRow(capability).Key);
        Assert.NotEqual(row.Key, SteamNativeSettingsService.DeviceRow(capability with { InstanceId = "right" }).Key);
    }

    [Fact]
    public void StepAndSupportedChoicesAreValidatedBeforeTheOwnerIsCalled()
    {
        var range = new SteamSettingsRow("limit", SteamSettingsRowKind.Range, "Limit",
            Minimum: 5, Maximum: 35, Step: 5);
        Assert.True(SteamNativeSettingsService.ValidValue(range, JsonSerializer.SerializeToElement(20)));
        Assert.False(SteamNativeSettingsService.ValidValue(range, JsonSerializer.SerializeToElement(21)));
        Assert.False(SteamNativeSettingsService.ValidValue(range, JsonSerializer.SerializeToElement(40)));
        Assert.False(SteamNativeSettingsService.ValidValue(range with { Disabled = true },
            JsonSerializer.SerializeToElement(20)));

        var choice = new SteamSettingsRow("format", SteamSettingsRowKind.Choice, "Format",
            Choices: [new SteamSettingsChoice("stereo", "Stereo")]);
        Assert.True(SteamNativeSettingsService.ValidValue(choice, JsonSerializer.SerializeToElement("stereo")));
        Assert.False(SteamNativeSettingsService.ValidValue(choice, JsonSerializer.SerializeToElement("surround")));
    }

    [Fact]
    public void RepublishedProfileRowsKeepOldScopeCommandsOutOfTheOfferedSet()
    {
        var active = new ActiveProfile("steam:1", "game.exe", 1, "game-one", true);
        var original = SteamNativeSettingsService.ProfileKey("power.sustained", active, true);
        Assert.Equal(original, SteamNativeSettingsService.ProfileKey("power.sustained", active, true));
        Assert.NotEqual(original, SteamNativeSettingsService.ProfileKey("power.sustained", active, false));
        Assert.NotEqual(original, SteamNativeSettingsService.ProfileKey("power.sustained",
            active with { ApplicationId = "steam:2" }, true));
        Assert.NotEqual(original, SteamNativeSettingsService.ProfileKey("power.sustained",
            active with { GameProfileId = "game-two" }, true));

        var offered = new Dictionary<string, bool>
        {
            [SteamNativeSettingsService.ProfileKey("power.sustained", active, false)] = true
        };
        Assert.False(offered.ContainsKey(original));
    }
}
