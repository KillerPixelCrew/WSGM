using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;

namespace WSGM.Device.Sdk.Tests.Settings;

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
        Assert.False(PlainText.TryValidate("safe‮txet", 48, "label", out var error));
        Assert.Contains("bidirectional", error);
    }

    [Fact]
    public void TryValidate_TextCarryingAControlCharacter_IsRejected()
    {
        Assert.False(PlainText.TryValidate("one\nline", 48, "label", out var error));
        Assert.Contains("control", error);
    }

    [Fact]
    public void TryValidate_TextLongerThanTheBound_NamesTheField()
    {
        Assert.False(PlainText.TryValidate(new string('a', 49), 48, "customLabel", out var error));
        Assert.Contains("customLabel", error);
        Assert.Contains("48", error);
    }

    [Fact]
    public void Section_CustomKeyWithoutATitle_IsRejected()
    {
        PluginSettingSection section = new()
        {
            SectionId = "advanced",
            Key = SettingSectionKey.Custom
        };

        Assert.False(section.TryValidate(out var error));
        Assert.Contains("customTitle", error);
    }

    [Fact]
    public void Section_TitleAlongsideARealKey_IsRejectedAsDeadWeight()
    {
        PluginSettingSection section = new()
        {
            SectionId = "power",
            Key = SettingSectionKey.Power,
            CustomTitle = "Power"
        };

        Assert.False(section.TryValidate(out var error));
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
                Section("power")
            ]
        };

        Assert.False(manifest.TryValidate(out var error));
        Assert.Contains("power", error);
        Assert.Contains("more than once", error);
    }

    [Fact]
    public void Manifest_MoreSectionsThanAGamepadCanNavigate_IsRejected()
    {
        PluginSettingsManifest manifest = new()
        {
            Sections = [.. Enumerable.Range(0, PluginSettingsManifest.MaxSections + 1)
                .Select(i => Section($"s{i}"))]
        };

        Assert.False(manifest.TryValidate(out var error));
        Assert.Contains($"{PluginSettingsManifest.MaxSections}", error);
    }

    [Fact]
    public void Manifest_SettingNamingAnUnknownSection_IsAcceptedSoItCanFallBackRatherThanVanish()
    {
        PluginSettingsManifest manifest = new()
        {
            Sections = [Section("power")],
            Settings = [Toggle("ec.trace", section: "nonexistent")]
        };

        Assert.True(manifest.TryValidate(out var error), error);
    }

    [Fact]
    public void Setting_TextWithoutItsOwnBound_IsRejected()
    {
        var setting = Toggle("label") with
        {
            ValueKind = CapabilityValueKind.Text,
            Default = new CapabilityValue { Kind = CapabilityValueKind.Text, TextValue = "x" }
        };

        Assert.False(setting.TryValidate(out var error));
        Assert.Contains("maximumLength", error);
    }

    [Fact]
    public void Setting_MaximumLengthOnANonTextKind_IsRejected()
    {
        var setting = Toggle("ec.trace") with { MaximumLength = 16 };

        Assert.False(setting.TryValidate(out var error));
        Assert.Contains("maximumLength", error);
    }

    [Fact]
    public void Setting_DefaultOfTheWrongKind_IsRejected()
    {
        var setting = Toggle("ec.trace") with
        {
            Default = new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = 1 }
        };

        Assert.False(setting.TryValidate(out var error));
        Assert.Contains("value kind", error);
    }

    [Fact]
    public void Setting_ActionShapedValue_IsRejectedBecauseThatIsACapability()
    {
        var setting = Toggle("ec.reset") with
        {
            ValueKind = CapabilityValueKind.None,
            Default = new CapabilityValue { Kind = CapabilityValueKind.None }
        };

        Assert.False(setting.TryValidate(out var error));
        Assert.Contains("capability", error);
    }

    [Fact]
    public void Setting_IntegerWithoutARange_IsRejected()
    {
        var setting = Toggle("ec.poll") with
        {
            ValueKind = CapabilityValueKind.Integer,
            Default = new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = 10 }
        };

        Assert.False(setting.TryValidate(out var error));
        Assert.Contains("minimum", error);
    }

    [Fact]
    public void Setting_DefaultOutsideItsDeclaredRange_IsRejected()
    {
        var setting = Toggle("ec.poll") with
        {
            ValueKind = CapabilityValueKind.Integer,
            Minimum = 100,
            Maximum = 5000,
            Step = 100,
            Default = new CapabilityValue
            {
                Kind = CapabilityValueKind.Integer,
                IntegerValue = 50
            }
        };

        Assert.False(setting.TryValidate(out var error));
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
                        IntegerValue = 1000
                    }
                }
            ]
        };

        Assert.True(manifest.TryValidate(out var error), error);
    }

    [Fact]
    public void Display_EveryDefinedKey_IsAcceptedWithItsRequiredShape()
    {
        foreach (var key in Enum.GetValues<DisplayKey>())
        {
            CapabilityDisplay display = new()
            {
                Key = key,
                CustomLabel = key is DisplayKey.Custom ? "Device feature" : null
            };

            Assert.True(display.TryValidate(out var error), $"{key}: {error}");
        }
    }

    [Fact]
    public void Display_UndefinedKey_IsRejected()
    {
        CapabilityDisplay display = new() { Key = (DisplayKey)int.MaxValue };

        Assert.False(display.TryValidate(out var error));
        Assert.Contains("not defined", error);
    }

    [Fact]
    public void Section_EveryDefinedKey_IsAcceptedWithItsRequiredShape()
    {
        foreach (var key in Enum.GetValues<SettingSectionKey>())
        {
            PluginSettingSection section = new()
            {
                SectionId = $"section-{(int)key}",
                Key = key,
                CustomTitle = key is SettingSectionKey.Custom ? "Device settings" : null
            };

            Assert.True(section.TryValidate(out var error), $"{key}: {error}");
        }
    }

    [Fact]
    public void Section_UndefinedKey_IsRejected()
    {
        PluginSettingSection section = new()
        {
            SectionId = "advanced",
            Key = (SettingSectionKey)int.MaxValue
        };

        Assert.False(section.TryValidate(out var error));
        Assert.Contains("undefined key", error);
    }

    [Fact]
    public void Setting_UndefinedValueKind_IsRejectedBeforeItCanBehaveLikeAnUnconstrainedKind()
    {
        const CapabilityValueKind undefined = (CapabilityValueKind)int.MaxValue;
        var setting = Toggle() with
        {
            ValueKind = undefined,
            Default = new CapabilityValue { Kind = undefined }
        };

        Assert.False(setting.TryValidate(out var error));
        Assert.Contains("undefined valueKind", error);
        Assert.False(setting.TryValidateValue(
            new CapabilityValue { Kind = undefined },
            out error));
        Assert.Contains("undefined", error);
    }

    [Fact]
    public void Setting_UndefinedUnit_IsRejected()
    {
        var setting = Toggle() with
        {
            Unit = (CapabilityUnit)int.MaxValue
        };

        Assert.False(setting.TryValidate(out var error));
        Assert.Contains("undefined unit", error);
    }

    [Fact]
    public void Setting_EveryDefinedUnit_IsAccepted()
    {
        foreach (var unit in Enum.GetValues<CapabilityUnit>())
        {
            var setting = IntegerSetting(int.MinValue, step: 1) with
            {
                Unit = unit
            };

            Assert.True(setting.TryValidate(out var error), $"{unit}: {error}");
        }
    }

    [Fact]
    public void ChoiceSetting_ValidatesEveryChoiceDisplay()
    {
        var setting = ChoiceSetting(
            Choice("quiet", "Quiet"),
            new CapabilityChoice(
                "performance",
                new CapabilityDisplay { Key = DisplayKey.Custom }));

        Assert.False(setting.TryValidate(out var error));
        Assert.Contains("performance", error);
        Assert.Contains("display metadata", error);
    }

    [Fact]
    public void ChoiceSetting_AllValidChoiceDisplays_AreAccepted()
    {
        var setting = ChoiceSetting(
            Choice("quiet", "Quiet"),
            Choice("performance", "Performance"));

        Assert.True(setting.TryValidate(out var error), error);
    }

    [Fact]
    public void ChoiceSetting_NullChoiceDisplay_IsRejectedWithoutThrowing()
    {
        var setting = ChoiceSetting(
            new CapabilityChoice("quiet", null!));

        Assert.False(setting.TryValidate(out var error));
        Assert.Contains("no display metadata", error);
    }

    [Fact]
    public void ChoiceSetting_NullChoiceItem_IsRejectedWithoutThrowing()
    {
        var setting = ChoiceSetting(Choice("quiet", "Quiet")) with
        {
            Choices = [null!]
        };

        Assert.False(setting.TryValidate(out var error));
        Assert.Contains("null choice", error);
    }

    [Fact]
    public void Setting_NullChoicesCollection_IsRejectedWithoutThrowing()
    {
        var setting = Toggle() with { Choices = null! };

        Assert.False(setting.TryValidate(out var error));
        Assert.Contains("choices collection", error);
    }

    [Fact]
    public void Setting_NullDisplay_IsRejectedWithoutThrowing()
    {
        var setting = Toggle() with { Display = null! };

        Assert.False(setting.TryValidate(out var error));
        Assert.Contains("no display metadata", error);
    }

    [Fact]
    public void Setting_NullDefault_IsRejectedWithoutThrowing()
    {
        var setting = Toggle() with { Default = null! };

        Assert.False(setting.TryValidate(out var error));
        Assert.Contains("default", error);
    }

    [Fact]
    public void ChoiceValueValidation_NullChoicesCollection_IsRejectedWithoutThrowing()
    {
        var setting = ChoiceSetting(Choice("quiet", "Quiet")) with
        {
            Choices = null!
        };

        Assert.False(setting.TryValidateValue(
            new CapabilityValue { Kind = CapabilityValueKind.Choice, ChoiceValue = "quiet" },
            out var error));
        Assert.Contains("choices collection", error);
    }

    [Fact]
    public void ChoiceValueValidation_NullChoiceItem_IsRejectedWithoutThrowing()
    {
        var setting = ChoiceSetting(Choice("quiet", "Quiet")) with
        {
            Choices = [null!]
        };

        Assert.False(setting.TryValidateValue(
            new CapabilityValue { Kind = CapabilityValueKind.Choice, ChoiceValue = "quiet" },
            out var error));
        Assert.Contains("null item", error);
    }

    [Fact]
    public void Manifest_NullSectionsCollection_IsRejectedWithoutThrowing()
    {
        PluginSettingsManifest manifest = new() { Sections = null! };

        Assert.False(manifest.TryValidate(out var error));
        Assert.Contains("sections collection", error);
    }

    [Fact]
    public void Manifest_NullSettingsCollection_IsRejectedWithoutThrowing()
    {
        PluginSettingsManifest manifest = new() { Settings = null! };

        Assert.False(manifest.TryValidate(out var error));
        Assert.Contains("settings collection", error);
    }

    [Fact]
    public void Manifest_NullSectionItem_IsRejectedWithoutThrowing()
    {
        PluginSettingsManifest manifest = new() { Sections = [null!] };

        Assert.False(manifest.TryValidate(out var error));
        Assert.Contains("null section", error);
        Assert.Contains("0", error);
    }

    [Fact]
    public void Manifest_NullSettingItem_IsRejectedWithoutThrowing()
    {
        PluginSettingsManifest manifest = new() { Settings = [null!] };

        Assert.False(manifest.TryValidate(out var error));
        Assert.Contains("null setting", error);
        Assert.Contains("0", error);
    }

    [Fact]
    public void IntegerStepValidation_FullWidthDifferenceThatIsOnStep_IsAccepted()
    {
        var setting = IntegerSetting(int.MaxValue, step: 3);

        Assert.True(setting.TryValidate(out var error), error);
        Assert.True(setting.TryValidateValue(
            new CapabilityValue
            {
                Kind = CapabilityValueKind.Integer,
                IntegerValue = int.MaxValue
            },
            out error), error);
    }

    [Fact]
    public void IntegerStepValidation_FullWidthDifferenceThatIsOffStep_IsRejected()
    {
        var setting = IntegerSetting(int.MinValue, step: 3);

        Assert.False(setting.TryValidateValue(
            new CapabilityValue
            {
                Kind = CapabilityValueKind.Integer,
                IntegerValue = int.MaxValue - 1
            },
            out var error));
        Assert.Contains("not on", error);
    }

    private static PluginSettingSection Section(string id) => new()
    {
        SectionId = id,
        Key = SettingSectionKey.General
    };

    private static PluginSettingDescriptor Toggle(string id = "device.setting", string? section = null) => new()
    {
        SettingId = id,
        ValueKind = CapabilityValueKind.Boolean,
        Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "Device setting" },
        Default = new CapabilityValue { Kind = CapabilityValueKind.Boolean, BooleanValue = false },
        SectionId = section
    };

    private static PluginSettingDescriptor ChoiceSetting(params CapabilityChoice[] choices) =>
        Toggle() with
        {
            ValueKind = CapabilityValueKind.Choice,
            Choices = choices,
            Default = new CapabilityValue
            {
                Kind = CapabilityValueKind.Choice,
                ChoiceValue = choices[0].Value
            }
        };

    private static CapabilityChoice Choice(string value, string label) => new(
        value,
        new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = label });

    private static PluginSettingDescriptor IntegerSetting(int defaultValue, int step) =>
        Toggle() with
        {
            ValueKind = CapabilityValueKind.Integer,
            Minimum = int.MinValue,
            Maximum = int.MaxValue,
            Step = step,
            Default = new CapabilityValue
            {
                Kind = CapabilityValueKind.Integer,
                IntegerValue = defaultValue
            }
        };
}
