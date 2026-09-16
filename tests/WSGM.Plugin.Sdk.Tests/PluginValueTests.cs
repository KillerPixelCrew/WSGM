using Xunit;

namespace WSGM.Plugin.Sdk.Tests;

public sealed class PluginValueTests
{
    [Fact]
    public void SchemasRejectDuplicateKeysInvalidDefaultsAndInvertedBounds()
    {
        PluginSetting setting = new("level", "Level", PluginSettingKind.Number, new PluginValue(Number: 20), 0, 100);
        Assert.True(PluginConfigurationRules.IsValid([setting]));
        Assert.False(PluginConfigurationRules.IsValid([setting, setting]));
        Assert.False(PluginConfigurationRules.IsValid([setting with { Minimum = 101 }]));
        Assert.False(PluginConfigurationRules.IsValid([setting with { Default = new PluginValue(Number: 101) }]));
        Assert.False(PluginConfigurationRules.IsValid([setting with { Choices = ["one"] }]));
        Assert.False(PluginConfigurationRules.IsValid([setting with { Kind = PluginSettingKind.Boolean }]));
    }

    [Fact]
    public void TextChoicesAndPrimitiveTypesAreCheckedBeforeDelivery()
    {
        PluginSetting setting = new("route", "Route", PluginSettingKind.Text, new PluginValue(Text: "desk"),
            Choices: ["desk", "tv"]);
        Assert.True(PluginConfigurationRules.IsValid([setting]));
        Assert.True(PluginConfigurationRules.Accepts(setting, new PluginValue(Text: "tv")));
        Assert.False(PluginConfigurationRules.Accepts(setting, new PluginValue(Text: "other")));
        Assert.False(PluginConfigurationRules.Accepts(setting, new PluginValue(false)));
        Assert.False(PluginConfigurationRules.IsValid([setting with { Choices = ["desk", "desk"] }]));
    }

    [Fact]
    public void ValuesRequireExactlyOneFiniteBoundedPrimitive()
    {
        Assert.True(new PluginValue(false).IsValid);
        Assert.True(new PluginValue(Number: 0).IsValid);
        Assert.True(new PluginValue(Text: "").IsValid);
        Assert.False(default(PluginValue).IsValid);
        Assert.False(new PluginValue(true, 1).IsValid);
        Assert.False(new PluginValue(Number: double.NaN).IsValid);
        Assert.False(new PluginValue(Number: double.PositiveInfinity).IsValid);
        Assert.False(new PluginValue(Text: new string('x', 4097)).IsValid);
    }
}
