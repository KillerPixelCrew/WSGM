using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class DeviceSectionLayoutTests
{
    [Fact]
    public void SharedSectionsAreAvailableWithoutPluginDeclarations()
    {
        Assert.Equal(["power", "rgb", "controller", "info"],
            DeviceSectionLayout.IncludePredefined([]).Select(section => section.SectionId));
        Assert.All(DeviceSections.All, section => Assert.True(section.TryValidate(out _)));
    }

    [Fact]
    public void PluginsCanAddCategoriesAndCustomSections()
    {
        var power = DeviceSections.Power with
        {
            Categories = [new CapabilityCategory { CategoryId = "fans", Key = SettingSectionKey.Fans }]
        };
        var custom = new CapabilitySection
            { SectionId = "extra", Key = SettingSectionKey.Custom, CustomTitle = "Extra" };
        Assert.True(power.TryValidate(out _));
        var sections = DeviceSectionLayout.IncludePredefined([power, custom]);
        Assert.Equal(5, sections.Count);
        Assert.Equal("fans", Assert.Single(sections[0].Categories).CategoryId);
        Assert.Equal(custom, sections[^1]);
        Assert.False((power with { CustomTitle = "Replacement" }).TryValidate(out _));
    }

    [Fact]
    public void ExistingDeclarationsKeepCategoriesAndUseSharedMetadata()
    {
        var legacy = DeviceSections.Power with
        {
            Key = SettingSectionKey.Custom, CustomTitle = "Legacy", SortOrder = 9
        };
        Assert.True(legacy.TryValidate(out _));
        var shared = DeviceSectionLayout.IncludePredefined([legacy])[0];
        Assert.Equal(DeviceSections.Power.Key, shared.Key);
        Assert.Null(shared.CustomTitle);
        Assert.Equal(DeviceSections.Power.SortOrder, shared.SortOrder);
    }
}
