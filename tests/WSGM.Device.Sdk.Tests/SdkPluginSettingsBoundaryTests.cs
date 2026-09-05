using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;

namespace WSGM.Device.Tests;

public sealed class SdkPluginSettingsBoundaryTests
{
    [Fact]
    public void Display_EveryDefinedKey_IsAcceptedWithItsRequiredShape()
    {
        foreach (DisplayKey key in Enum.GetValues<DisplayKey>())
        {
            CapabilityDisplay display = new()
            {
                Key = key,
                CustomLabel = key is DisplayKey.Custom ? "Device feature" : null,
            };

            Assert.True(display.TryValidate(out string? error), $"{key}: {error}");
        }
    }

    [Fact]
    public void Display_UndefinedKey_IsRejected()
    {
        CapabilityDisplay display = new() { Key = (DisplayKey)int.MaxValue };

        Assert.False(display.TryValidate(out string? error));
        Assert.Contains("not defined", error);
    }

    [Fact]
    public void Section_EveryDefinedKey_IsAcceptedWithItsRequiredShape()
    {
        foreach (SettingSectionKey key in Enum.GetValues<SettingSectionKey>())
        {
            PluginSettingSection section = new()
            {
                SectionId = $"section-{(int)key}",
                Key = key,
                CustomTitle = key is SettingSectionKey.Custom ? "Device settings" : null,
            };

            Assert.True(section.TryValidate(out string? error), $"{key}: {error}");
        }
    }

    [Fact]
    public void Section_UndefinedKey_IsRejected()
    {
        PluginSettingSection section = new()
        {
            SectionId = "advanced",
            Key = (SettingSectionKey)int.MaxValue,
        };

        Assert.False(section.TryValidate(out string? error));
        Assert.Contains("undefined key", error);
    }

    [Fact]
    public void Setting_UndefinedValueKind_IsRejectedBeforeItCanBehaveLikeAnUnconstrainedKind()
    {
        CapabilityValueKind undefined = (CapabilityValueKind)int.MaxValue;
        PluginSettingDescriptor setting = Toggle() with
        {
            ValueKind = undefined,
            Default = new CapabilityValue { Kind = undefined },
        };

        Assert.False(setting.TryValidate(out string? error));
        Assert.Contains("undefined valueKind", error);
        Assert.False(setting.TryValidateValue(
            new CapabilityValue { Kind = undefined },
            out error));
        Assert.Contains("undefined", error);
    }

    [Fact]
    public void Setting_UndefinedUnit_IsRejected()
    {
        PluginSettingDescriptor setting = Toggle() with
        {
            Unit = (CapabilityUnit)int.MaxValue,
        };

        Assert.False(setting.TryValidate(out string? error));
        Assert.Contains("undefined unit", error);
    }

    [Fact]
    public void Setting_EveryDefinedUnit_IsAccepted()
    {
        foreach (CapabilityUnit unit in Enum.GetValues<CapabilityUnit>())
        {
            PluginSettingDescriptor setting = IntegerSetting(int.MinValue, step: 1) with
            {
                Unit = unit,
            };

            Assert.True(setting.TryValidate(out string? error), $"{unit}: {error}");
        }
    }

    [Fact]
    public void ChoiceSetting_ValidatesEveryChoiceDisplay()
    {
        PluginSettingDescriptor setting = ChoiceSetting(
            Choice("quiet", "Quiet"),
            new CapabilityChoice(
                "performance",
                new CapabilityDisplay { Key = DisplayKey.Custom }));

        Assert.False(setting.TryValidate(out string? error));
        Assert.Contains("performance", error);
        Assert.Contains("display metadata", error);
    }

    [Fact]
    public void ChoiceSetting_AllValidChoiceDisplays_AreAccepted()
    {
        PluginSettingDescriptor setting = ChoiceSetting(
            Choice("quiet", "Quiet"),
            Choice("performance", "Performance"));

        Assert.True(setting.TryValidate(out string? error), error);
    }

    [Fact]
    public void ChoiceSetting_NullChoiceDisplay_IsRejectedWithoutThrowing()
    {
        PluginSettingDescriptor setting = ChoiceSetting(
            new CapabilityChoice("quiet", null!));

        Assert.False(setting.TryValidate(out string? error));
        Assert.Contains("no display metadata", error);
    }

    [Fact]
    public void ChoiceSetting_NullChoiceItem_IsRejectedWithoutThrowing()
    {
        PluginSettingDescriptor setting = ChoiceSetting(Choice("quiet", "Quiet")) with
        {
            Choices = [null!],
        };

        Assert.False(setting.TryValidate(out string? error));
        Assert.Contains("null choice", error);
    }

    [Fact]
    public void Setting_NullChoicesCollection_IsRejectedWithoutThrowing()
    {
        PluginSettingDescriptor setting = Toggle() with { Choices = null! };

        Assert.False(setting.TryValidate(out string? error));
        Assert.Contains("choices collection", error);
    }

    [Fact]
    public void Setting_NullDisplay_IsRejectedWithoutThrowing()
    {
        PluginSettingDescriptor setting = Toggle() with { Display = null! };

        Assert.False(setting.TryValidate(out string? error));
        Assert.Contains("no display metadata", error);
    }

    [Fact]
    public void Setting_NullDefault_IsRejectedWithoutThrowing()
    {
        PluginSettingDescriptor setting = Toggle() with { Default = null! };

        Assert.False(setting.TryValidate(out string? error));
        Assert.Contains("default", error);
    }

    [Fact]
    public void ChoiceValueValidation_NullChoicesCollection_IsRejectedWithoutThrowing()
    {
        PluginSettingDescriptor setting = ChoiceSetting(Choice("quiet", "Quiet")) with
        {
            Choices = null!,
        };

        Assert.False(setting.TryValidateValue(
            new CapabilityValue { Kind = CapabilityValueKind.Choice, ChoiceValue = "quiet" },
            out string? error));
        Assert.Contains("choices collection", error);
    }

    [Fact]
    public void ChoiceValueValidation_NullChoiceItem_IsRejectedWithoutThrowing()
    {
        PluginSettingDescriptor setting = ChoiceSetting(Choice("quiet", "Quiet")) with
        {
            Choices = [null!],
        };

        Assert.False(setting.TryValidateValue(
            new CapabilityValue { Kind = CapabilityValueKind.Choice, ChoiceValue = "quiet" },
            out string? error));
        Assert.Contains("null item", error);
    }

    [Fact]
    public void Manifest_NullSectionsCollection_IsRejectedWithoutThrowing()
    {
        PluginSettingsManifest manifest = new() { Sections = null! };

        Assert.False(manifest.TryValidate(out string? error));
        Assert.Contains("sections collection", error);
    }

    [Fact]
    public void Manifest_NullSettingsCollection_IsRejectedWithoutThrowing()
    {
        PluginSettingsManifest manifest = new() { Settings = null! };

        Assert.False(manifest.TryValidate(out string? error));
        Assert.Contains("settings collection", error);
    }

    [Fact]
    public void Manifest_NullSectionItem_IsRejectedWithoutThrowing()
    {
        PluginSettingsManifest manifest = new() { Sections = [null!] };

        Assert.False(manifest.TryValidate(out string? error));
        Assert.Contains("null section", error);
        Assert.Contains("0", error);
    }

    [Fact]
    public void Manifest_NullSettingItem_IsRejectedWithoutThrowing()
    {
        PluginSettingsManifest manifest = new() { Settings = [null!] };

        Assert.False(manifest.TryValidate(out string? error));
        Assert.Contains("null setting", error);
        Assert.Contains("0", error);
    }

    [Fact]
    public void IntegerStepValidation_FullWidthDifferenceThatIsOnStep_IsAccepted()
    {
        PluginSettingDescriptor setting = IntegerSetting(int.MaxValue, step: 3);

        Assert.True(setting.TryValidate(out string? error), error);
        Assert.True(setting.TryValidateValue(
            new CapabilityValue
            {
                Kind = CapabilityValueKind.Integer,
                IntegerValue = int.MaxValue,
            },
            out error), error);
    }

    [Fact]
    public void IntegerStepValidation_FullWidthDifferenceThatIsOffStep_IsRejected()
    {
        PluginSettingDescriptor setting = IntegerSetting(int.MinValue, step: 3);

        Assert.False(setting.TryValidateValue(
            new CapabilityValue
            {
                Kind = CapabilityValueKind.Integer,
                IntegerValue = int.MaxValue - 1,
            },
            out string? error));
        Assert.Contains("not on", error);
    }

    private static PluginSettingDescriptor Toggle() => new()
    {
        SettingId = "device.setting",
        ValueKind = CapabilityValueKind.Boolean,
        Display = new CapabilityDisplay
        {
            Key = DisplayKey.Custom,
            CustomLabel = "Device setting",
        },
        Default = new CapabilityValue
        {
            Kind = CapabilityValueKind.Boolean,
            BooleanValue = false,
        },
    };

    private static PluginSettingDescriptor ChoiceSetting(params CapabilityChoice[] choices) =>
        Toggle() with
        {
            ValueKind = CapabilityValueKind.Choice,
            Choices = choices,
            Default = new CapabilityValue
            {
                Kind = CapabilityValueKind.Choice,
                ChoiceValue = choices[0].Value,
            },
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
                IntegerValue = defaultValue,
            },
        };
}
