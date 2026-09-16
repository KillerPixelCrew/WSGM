using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;

namespace WSGM.Device.Tests;

public sealed class SdkCapabilitySectionTests
{
    private static CapabilitySection Section(
        string id = "cooling",
        SettingSectionKey key = SettingSectionKey.Fans,
        string? customTitle = null,
        string? customDescription = null,
        SectionIcon icon = SectionIcon.Fan,
        IReadOnlyList<CapabilityCategory>? categories = null) => new()
        {
            SectionId = id,
            Key = key,
            CustomTitle = customTitle,
            CustomDescription = customDescription,
            Icon = icon,
            Categories = categories ?? []
        };

    private static CapabilityCategory Category(
        string id = "readings",
        SettingSectionKey key = SettingSectionKey.Custom,
        string? customTitle = "Readings") => new()
        {
            CategoryId = id,
            Key = key,
            CustomTitle = customTitle
        };

    [Fact]
    public void AKeyedSectionValidates()
    {
        Assert.True(Section().TryValidate(out var error));
        Assert.Null(error);
    }

    [Fact]
    public void ACustomSectionRequiresATitle()
    {
        Assert.False(
            Section(key: SettingSectionKey.Custom, customTitle: null).TryValidate(out _));
        Assert.True(
            Section(key: SettingSectionKey.Custom, customTitle: "Cooling").TryValidate(out _));
    }

    [Fact]
    public void AKeyedSectionMayNotCarryACustomTitle()
    {
        // A title alongside a real key is dead weight some surface eventually renders instead of
        // the localized string.
        Assert.False(Section(customTitle: "Cooling").TryValidate(out var error));
        Assert.Contains("customTitle", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has spaces")]
    [InlineData("has/slash")]
    public void AnIllegalSectionIdIsRefused(string id)
    {
        Assert.False(Section(id: id).TryValidate(out _));
    }

    [Fact]
    public void AnOverlongSectionIdIsRefused()
    {
        Assert.False(
            Section(id: new string('a', CapabilitySection.MaxSectionIdLength + 1))
                .TryValidate(out _));
    }

    [Fact]
    public void AnUndefinedKeyOrIconIsRefused()
    {
        Assert.False(Section(key: (SettingSectionKey)999).TryValidate(out _));
        Assert.False(Section(icon: (SectionIcon)999).TryValidate(out _));
    }

    [Fact]
    public void ADescriptionIsBoundedPlainText()
    {
        Assert.True(
            Section(customDescription: "Fan curves and thermal readings.").TryValidate(out _));
        Assert.False(
            Section(customDescription: new string(
                'd',
                CapabilitySection.MaxCustomDescriptionLength + 1)).TryValidate(out _));
    }

    [Fact]
    public void CategoriesValidateThroughTheirSection()
    {
        Assert.True(Section(categories: [Category()]).TryValidate(out _));

        // The failing child is named so a plugin author can find it in a long declaration.
        Assert.False(
            Section(categories: [Category(id: "has spaces")]).TryValidate(out var error));
        Assert.Contains("has spaces", error);
    }

    [Fact]
    public void ADuplicateCategoryIdIsRefusedByName()
    {
        var valid = Section(categories: [Category(), Category()])
            .TryValidate(out var error);

        Assert.False(valid);
        Assert.Contains("readings", error);
    }

    [Fact]
    public void MoreCategoriesThanTheBoundAreRefused()
    {
        CapabilityCategory[] categories =
        [
            .. Enumerable.Range(0, CapabilitySection.MaxCategories + 1)
                .Select(index => Category(id: $"category-{index}"))
        ];

        Assert.False(Section(categories: categories).TryValidate(out _));
    }

    [Fact]
    public void AKeyedCategoryMayNotCarryACustomTitle()
    {
        var category = Category(
            key: SettingSectionKey.Power,
            customTitle: "Power");

        Assert.False(category.TryValidate(out var error));
        Assert.Contains("customTitle", error);
    }

    [Fact]
    public void SharedSectionsAreAvailableWithoutPluginDeclarations()
    {
        Assert.Equal(new[] { "power", "rgb", "controller", "info" },
            DeviceSections.IncludePredefined([]).Select(section => section.SectionId));
        Assert.All(DeviceSections.All, section => Assert.True(section.TryValidate(out _)));
    }

    [Fact]
    public void PluginsCanAddCategoriesAndCustomSections()
    {
        var power = DeviceSections.Power with
        { Categories = [new CapabilityCategory { CategoryId = "fans", Key = SettingSectionKey.Fans }] };
        var custom = new CapabilitySection
        { SectionId = "extra", Key = SettingSectionKey.Custom, CustomTitle = "Extra" };
        Assert.True(power.TryValidate(out _));
        var sections = DeviceSections.IncludePredefined([power, custom]);
        Assert.Equal(5, sections.Count);
        Assert.Equal("fans", Assert.Single(sections[0].Categories).CategoryId);
        Assert.Equal(custom, sections[^1]);
        Assert.False((power with { CustomTitle = "Replacement" }).TryValidate(out _));
    }

    [Fact]
    public void ExistingDeclarationsKeepCategoriesAndUseSharedMetadata()
    {
        var legacy = DeviceSections.Power with
        { Key = SettingSectionKey.Custom, CustomTitle = "Legacy", SortOrder = 9 };
        Assert.True(legacy.TryValidate(out _));
        var shared = DeviceSections.IncludePredefined([legacy])[0];
        Assert.Equal(DeviceSections.Power.Key, shared.Key);
        Assert.Null(shared.CustomTitle);
        Assert.Equal(DeviceSections.Power.SortOrder, shared.SortOrder);
    }
}
