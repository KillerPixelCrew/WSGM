using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;

namespace WSGM.Tests;

public sealed class PluginSettingsResolverTests
{
    [Fact]
    public void Resolve_SettingTheUserNeverChanged_UsesTheDeclaredDefault()
    {
        var resolution = PluginSettingsResolver.Resolve(
            Manifest(Poll(minimum: 100, maximum: 5000, step: 100, @default: 1000)),
            stored: []);

        var value = Assert.Single(resolution.Values);
        Assert.Equal(PluginSettingOrigin.Default, value.Origin);
        Assert.Equal(1000, value.Value.IntegerValue);
    }

    [Fact]
    public void Resolve_StoredValueStillInsideTheDeclaration_IsRestoredUnchanged()
    {
        var resolution = PluginSettingsResolver.Resolve(
            Manifest(Poll(minimum: 100, maximum: 5000, step: 100, @default: 1000)),
            [Stored("ec.poll", integer: 2000)]);

        var value = Assert.Single(resolution.Values);
        Assert.Equal(PluginSettingOrigin.Stored, value.Origin);
        Assert.Equal(2000, value.Value.IntegerValue);
        Assert.Null(value.Reason);
    }

    [Fact]
    public void Resolve_PluginUpdateNarrowedTheRange_FallsBackAndNamesBothValueAndBound()
    {
        // The value was legal when it was written; the plugin has since narrowed the maximum.
        var resolution = PluginSettingsResolver.Resolve(
            Manifest(Poll(minimum: 100, maximum: 1000, step: 100, @default: 500)),
            [Stored("ec.poll", integer: 5000)]);

        var value = Assert.Single(resolution.Values);
        Assert.Equal(PluginSettingOrigin.Rejected, value.Origin);
        Assert.Equal(500, value.Value.IntegerValue);
        Assert.Contains("5000", value.Reason);
        Assert.Contains("1000", value.Reason);
    }

    [Fact]
    public void Resolve_StoredValueOffTheDeclaredStep_IsRejected()
    {
        var resolution = PluginSettingsResolver.Resolve(
            Manifest(Poll(minimum: 100, maximum: 5000, step: 100, @default: 1000)),
            [Stored("ec.poll", integer: 2050)]);

        Assert.Equal(PluginSettingOrigin.Rejected, Assert.Single(resolution.Values).Origin);
    }

    [Fact]
    public void Resolve_PluginDroppedTheStoredChoice_FallsBackAndNamesIt()
    {
        PluginSettingDescriptor mode = new()
        {
            SettingId = "ec.mode",
            ValueKind = CapabilityValueKind.Choice,
            Display = Label,
            Choices = [new CapabilityChoice("quiet", Label), new CapabilityChoice("loud", Label)],
            Default = new CapabilityValue
            {
                Kind = CapabilityValueKind.Choice,
                ChoiceValue = "quiet"
            }
        };

        var resolution = PluginSettingsResolver.Resolve(
            Manifest(mode),
            [new PluginSettingValue { SettingId = "ec.mode", Choice = "removed" }]);

        var value = Assert.Single(resolution.Values);
        Assert.Equal(PluginSettingOrigin.Rejected, value.Origin);
        Assert.Equal("quiet", value.Value.ChoiceValue);
        Assert.Contains("removed", value.Reason);
    }

    [Fact]
    public void Resolve_SettingWhoseKindChangedBetweenVersions_IsRejectedRatherThanReinterpreted()
    {
        // Stored as an integer, now declared as a colour. Reading the integer field into a colour
        // would silently produce a value the user never chose.
        PluginSettingDescriptor colour = new()
        {
            SettingId = "ec.tint",
            ValueKind = CapabilityValueKind.Color,
            Display = Label,
            Default = new CapabilityValue { Kind = CapabilityValueKind.Color, ColorValue = 0x00FF00 }
        };

        var resolution = PluginSettingsResolver.Resolve(
            Manifest(colour),
            [Stored("ec.tint", integer: 42)]);

        var value = Assert.Single(resolution.Values);
        Assert.Equal(PluginSettingOrigin.Rejected, value.Origin);
        Assert.Equal(0x00FF00, value.Value.ColorValue);
    }

    [Fact]
    public void Resolve_StoredSettingTheManifestNoLongerDeclares_IsReportedAsAnOrphan()
    {
        var resolution = PluginSettingsResolver.Resolve(
            Manifest(Poll(minimum: 100, maximum: 5000, step: 100, @default: 1000)),
            [Stored("ec.poll", integer: 1000), Stored("ec.gone", integer: 7)]);

        Assert.Equal("ec.gone", Assert.Single(resolution.Orphans));
    }

    [Fact]
    public void Resolve_ValuesFollowDeclarationOrder_SoTheSurfaceIsDeterministic()
    {
        PluginSettingsManifest manifest = new()
        {
            Settings =
            [
                Poll("a", 100, 5000, 100, 1000),
                Poll("b", 100, 5000, 100, 1000),
                Poll("c", 100, 5000, 100, 1000)
            ]
        };

        var resolution = PluginSettingsResolver.Resolve(manifest, stored: []);

        Assert.Equal(["a", "b", "c"], resolution.Values.Select(v => v.SettingId));
    }

    private static readonly CapabilityDisplay Label = new()
    {
        Key = DisplayKey.Custom,
        CustomLabel = "A setting"
    };

    private static PluginSettingsManifest Manifest(params PluginSettingDescriptor[] settings) =>
        new() { Settings = settings };

    private static PluginSettingDescriptor Poll(
        string id = "ec.poll",
        int minimum = 100,
        int maximum = 5000,
        int step = 100,
        int @default = 1000
    ) => new()
    {
        SettingId = id,
        ValueKind = CapabilityValueKind.Integer,
        Display = Label,
        Minimum = minimum,
        Maximum = maximum,
        Step = step,
        Default = new CapabilityValue
        {
            Kind = CapabilityValueKind.Integer,
            IntegerValue = @default
        }
    };

    private static PluginSettingValue Stored(string id, int integer) =>
        new() { SettingId = id, Integer = integer };
}
