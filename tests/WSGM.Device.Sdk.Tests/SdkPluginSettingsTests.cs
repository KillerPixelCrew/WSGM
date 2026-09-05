using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;

namespace WSGM.Device.Tests;

public sealed class SdkPluginSettingsTests
{
    [Theory]
    [InlineData("power.advanced")]
    [InlineData("EC_Poll-Interval")]
    [InlineData("a")]
    public void IsIdentifier_ShapesWSGMItselfSends_AreAccepted(string value)
    {
        Assert.True(PlainText.IsIdentifier(value, 64));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("slash/es")]
    [InlineData("emoji\U0001F600")]
    public void IsIdentifier_ShapesThatWouldNotSurviveLoggingOrKeying_AreRejected(string value)
    {
        Assert.False(PlainText.IsIdentifier(value, 64));
    }

    [Fact]
    public void IsIdentifier_LongerThanTheDeclaredBound_IsRejected()
    {
        Assert.False(PlainText.IsIdentifier(new string('a', 65), 64));
    }

    [Fact]
    public void TryValidate_TextCarryingABidirectionalOverride_IsRejected()
    {
        // The character that lets a label render in an order other than the one it is written in.
        Assert.False(PlainText.TryValidate("safe‮txet", 48, "label", out string? error));
        Assert.Contains("bidirectional", error);
    }

    [Fact]
    public void TryValidate_TextCarryingAControlCharacter_IsRejected()
    {
        Assert.False(PlainText.TryValidate("one\nline", 48, "label", out string? error));
        Assert.Contains("control", error);
    }

    [Fact]
    public void TryValidate_TextLongerThanTheBound_NamesTheField()
    {
        Assert.False(PlainText.TryValidate(new string('a', 49), 48, "customLabel", out string? error));
        Assert.Contains("customLabel", error);
        Assert.Contains("48", error);
    }

    [Fact]
    public void Section_CustomKeyWithoutATitle_IsRejected()
    {
        PluginSettingSection section = new()
        {
            SectionId = "advanced",
            Key = SettingSectionKey.Custom,
        };

        Assert.False(section.TryValidate(out string? error));
        Assert.Contains("customTitle", error);
    }

    [Fact]
    public void Section_TitleAlongsideARealKey_IsRejectedAsDeadWeight()
    {
        PluginSettingSection section = new()
        {
            SectionId = "power",
            Key = SettingSectionKey.Power,
            CustomTitle = "Power",
        };

        Assert.False(section.TryValidate(out string? error));
        Assert.Contains("customTitle", error);
    }

    [Fact]
    public void Manifest_DuplicateSectionId_NamesTheOffender()
    {
        PluginSettingsManifest manifest = new()
        {
            Sections =
            [
                Section("power"),
                Section("power"),
            ],
        };

        Assert.False(manifest.TryValidate(out string? error));
        Assert.Contains("power", error);
        Assert.Contains("more than once", error);
    }

    [Fact]
    public void Manifest_MoreSectionsThanAGamepadCanNavigate_IsRejected()
    {
        PluginSettingsManifest manifest = new()
        {
            Sections = [.. Enumerable.Range(0, PluginSettingsManifest.MaxSections + 1)
                .Select(i => Section($"s{i}"))],
        };

        Assert.False(manifest.TryValidate(out string? error));
        Assert.Contains($"{PluginSettingsManifest.MaxSections}", error);
    }

    [Fact]
    public void Manifest_SettingNamingAnUnknownSection_IsAcceptedSoItCanFallBackRatherThanVanish()
    {
        PluginSettingsManifest manifest = new()
        {
            Sections = [Section("power")],
            Settings = [Toggle("ec.trace", section: "nonexistent")],
        };

        Assert.True(manifest.TryValidate(out string? error), error);
    }

    [Fact]
    public void Setting_TextWithoutItsOwnBound_IsRejected()
    {
        PluginSettingDescriptor setting = Toggle("label") with
        {
            ValueKind = CapabilityValueKind.Text,
            Default = new CapabilityValue { Kind = CapabilityValueKind.Text, TextValue = "x" },
        };

        Assert.False(setting.TryValidate(out string? error));
        Assert.Contains("maximumLength", error);
    }

    [Fact]
    public void Setting_MaximumLengthOnANonTextKind_IsRejected()
    {
        PluginSettingDescriptor setting = Toggle("ec.trace") with { MaximumLength = 16 };

        Assert.False(setting.TryValidate(out string? error));
        Assert.Contains("maximumLength", error);
    }

    [Fact]
    public void Setting_DefaultOfTheWrongKind_IsRejected()
    {
        PluginSettingDescriptor setting = Toggle("ec.trace") with
        {
            Default = new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = 1 },
        };

        Assert.False(setting.TryValidate(out string? error));
        Assert.Contains("value kind", error);
    }

    [Fact]
    public void Setting_ActionShapedValue_IsRejectedBecauseThatIsACapability()
    {
        PluginSettingDescriptor setting = Toggle("ec.reset") with
        {
            ValueKind = CapabilityValueKind.None,
            Default = new CapabilityValue { Kind = CapabilityValueKind.None },
        };

        Assert.False(setting.TryValidate(out string? error));
        Assert.Contains("capability", error);
    }

    [Fact]
    public void Setting_IntegerWithoutARange_IsRejected()
    {
        PluginSettingDescriptor setting = Toggle("ec.poll") with
        {
            ValueKind = CapabilityValueKind.Integer,
            Default = new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = 10 },
        };

        Assert.False(setting.TryValidate(out string? error));
        Assert.Contains("minimum", error);
    }

    [Fact]
    public void Setting_DefaultOutsideItsDeclaredRange_IsRejected()
    {
        PluginSettingDescriptor setting = Toggle("ec.poll") with
        {
            ValueKind = CapabilityValueKind.Integer,
            Minimum = 100,
            Maximum = 5000,
            Step = 100,
            Default = new CapabilityValue
            {
                Kind = CapabilityValueKind.Integer,
                IntegerValue = 50,
            },
        };

        Assert.False(setting.TryValidate(out string? error));
        Assert.Contains("invalid default", error);
        Assert.Contains("outside", error);
    }

    [Fact]
    public void Manifest_WellFormedDeclaration_IsAccepted()
    {
        PluginSettingsManifest manifest = new()
        {
            Sections = [Section("power"), Section("advanced")],
            Settings =
            [
                Toggle("ec.trace", section: "advanced"),
                Toggle("ec.poll", section: "power") with
                {
                    ValueKind = CapabilityValueKind.Integer,
                    Minimum = 100,
                    Maximum = 5000,
                    Step = 100,
                    Unit = CapabilityUnit.Millisecond,
                    Default = new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Integer,
                        IntegerValue = 1000,
                    },
                },
            ],
        };

        Assert.True(manifest.TryValidate(out string? error), error);
    }

    private static PluginSettingSection Section(string id) => new()
    {
        SectionId = id,
        Key = SettingSectionKey.General,
    };

    private static PluginSettingDescriptor Toggle(string id, string? section = null) => new()
    {
        SettingId = id,
        ValueKind = CapabilityValueKind.Boolean,
        Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "A setting" },
        Default = new CapabilityValue { Kind = CapabilityValueKind.Boolean, BooleanValue = false },
        SectionId = section,
    };
}
