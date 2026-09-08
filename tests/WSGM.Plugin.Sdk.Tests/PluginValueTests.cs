using Xunit;

namespace WSGM.Plugin.Sdk.Tests;

public sealed class PluginValueTests
{
    [Fact]
    public void SchemasRejectDuplicateKeysInvalidDefaultsAndInvertedBounds()
    {
        PluginSetting setting = new("level", "Level", PluginSettingKind.Number, new(Number: 20), 0, 100);
        Assert.True(PluginConfigurationRules.IsValid([setting]));
        Assert.False(PluginConfigurationRules.IsValid([setting, setting]));
        Assert.False(PluginConfigurationRules.IsValid([setting with { Minimum = 101 }]));
        Assert.False(PluginConfigurationRules.IsValid([setting with { Default = new(Number: 101) }]));
        Assert.False(PluginConfigurationRules.IsValid([setting with { Choices = ["one"] }]));
        Assert.False(PluginConfigurationRules.IsValid([setting with { Kind = PluginSettingKind.Boolean }]));
    }

    [Fact]
    public void TextChoicesAndPrimitiveTypesAreCheckedBeforeDelivery()
    {
        PluginSetting setting = new("route", "Route", PluginSettingKind.Text, new(Text: "desk"), Choices: ["desk", "tv"]);
        Assert.True(PluginConfigurationRules.IsValid([setting]));
        Assert.True(PluginConfigurationRules.Accepts(setting, new(Text: "tv")));
        Assert.False(PluginConfigurationRules.Accepts(setting, new(Text: "other")));
        Assert.False(PluginConfigurationRules.Accepts(setting, new(Boolean: false)));
        Assert.False(PluginConfigurationRules.IsValid([setting with { Choices = ["desk", "desk"] }]));
    }

    [Fact]
    public void ValuesRequireExactlyOneFiniteBoundedPrimitive()
    {
        Assert.True(new PluginValue(Boolean: false).IsValid);
        Assert.True(new PluginValue(Number: 0).IsValid);
        Assert.True(new PluginValue(Text: "").IsValid);
        Assert.False(default(PluginValue).IsValid);
        Assert.False(new PluginValue(Boolean: true, Number: 1).IsValid);
        Assert.False(new PluginValue(Number: double.NaN).IsValid);
        Assert.False(new PluginValue(Number: double.PositiveInfinity).IsValid);
        Assert.False(new PluginValue(Text: new string('x', 4097)).IsValid);
    }
}
