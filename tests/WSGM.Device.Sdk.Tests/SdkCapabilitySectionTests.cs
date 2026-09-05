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
            Categories = categories ?? [],
        };

    private static CapabilityCategory Category(
        string id = "readings",
        SettingSectionKey key = SettingSectionKey.Custom,
        string? customTitle = "Readings") => new()
        {
            CategoryId = id,
            Key = key,
            CustomTitle = customTitle,
        };

    [Fact]
    public void AKeyedSectionValidates()
    {
        Assert.True(Section().TryValidate(out string? error));
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
        Assert.False(Section(customTitle: "Cooling").TryValidate(out string? error));
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
            Section(categories: [Category(id: "has spaces")]).TryValidate(out string? error));
        Assert.Contains("has spaces", error);
    }

    [Fact]
    public void ADuplicateCategoryIdIsRefusedByName()
    {
        bool valid = Section(categories: [Category(), Category()])
            .TryValidate(out string? error);

        Assert.False(valid);
        Assert.Contains("readings", error);
    }

    [Fact]
    public void MoreCategoriesThanTheBoundAreRefused()
    {
        CapabilityCategory[] categories =
        [
            .. Enumerable.Range(0, CapabilitySection.MaxCategories + 1)
                .Select(index => Category(id: $"category-{index}")),
        ];

        Assert.False(Section(categories: categories).TryValidate(out _));
    }

    [Fact]
    public void AKeyedCategoryMayNotCarryACustomTitle()
    {
        CapabilityCategory category = Category(
            key: SettingSectionKey.Power,
            customTitle: "Power");

        Assert.False(category.TryValidate(out string? error));
        Assert.Contains("customTitle", error);
    }

    [Fact]
    public void TheApiVersionIsPinnedSoRaisingItIsADeliberateAct()
    {
        // PluginManifestValidator requires exact equality, so every raise invalidates every
        // published package. Version 2 added sections and categories; version 3 added the
        // suppressed trace level and TraceChange.
        Assert.Equal(3, WSGM.Device.Sdk.DeviceApi.Version);
    }
}
