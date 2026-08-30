using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Settings;

namespace WSGM.Tests;

public sealed class PluginSettingsPageLayoutTests
{
    private static PluginSettingSection Section(
        string id,
        int sortOrder = 0,
        SettingSectionKey key = SettingSectionKey.General) =>
        new() { SectionId = id, Key = key, SortOrder = sortOrder };

    private static PluginSettingDescriptor Setting(
        string id,
        string? sectionId = null,
        int sortOrder = 0) => new()
        {
            SettingId = id,
            ValueKind = CapabilityValueKind.Boolean,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = id },
            Default = Value(false),
            SectionId = sectionId,
            SortOrder = sortOrder,
        };

    private static CapabilityValue Value(bool value) =>
        new() { Kind = CapabilityValueKind.Boolean, BooleanValue = value };

    private static EffectivePluginSetting Effective(string id) =>
        new(id, Value(true), PluginSettingOrigin.Stored, null);

    private static PluginSettingsPage Build(
        PluginSettingsManifest manifest,
        params string[] resolvedIds) =>
        PluginSettingsPageLayout.Build(
            manifest,
            [.. resolvedIds.Select(Effective)]);

    [Fact]
    public void SectionsRenderInDeclaredOrderAndSettingsWithinThem()
    {
        PluginSettingsManifest manifest = new()
        {
            Sections = [Section("second", 2), Section("first", 1)],
            Settings =
            [
                Setting("b", "first", 2),
                Setting("a", "first", 1),
                Setting("c", "second"),
            ],
        };

        PluginSettingsPage page = Build(manifest, "a", "b", "c");

        Assert.Equal(["first", "second"], page.Sections.Select(section => section.SectionId));
        Assert.Equal(["a", "b"], page.Sections[0].Rows.Select(row => row.SettingId));
    }

    [Fact]
    public void TiesBreakOnDeclarationOrderSoAnUnorderedManifestStillRendersDeterministically()
    {
        PluginSettingsManifest manifest = new()
        {
            Sections = [Section("alpha"), Section("beta")],
            Settings = [Setting("second", "alpha"), Setting("first", "alpha")],
        };

        PluginSettingsPage page = Build(manifest, "first", "second");

        Assert.Equal(["alpha"], page.Sections.Select(section => section.SectionId));
        Assert.Equal(["second", "first"], page.Sections[0].Rows.Select(row => row.SettingId));
    }

    [Fact]
    public void ASettingNamingAnUndeclaredSectionIsPlacedRatherThanDropped()
    {
        PluginSettingsManifest manifest = new()
        {
            Sections = [Section("known")],
            Settings = [Setting("stray", "missing")],
        };

        PluginSettingsPage page = Build(manifest, "stray");

        PluginSettingsSection fallback = Assert.Single(page.Sections);
        Assert.Equal(PluginSettingsPageLayout.FallbackSectionId, fallback.SectionId);
        Assert.Equal("stray", Assert.Single(fallback.Rows).SettingId);
        // The plugin author needs to see that the control exists and simply landed elsewhere.
        Assert.Contains(page.Diagnostics, entry => entry.Contains("missing"));
    }

    [Fact]
    public void ASettingWithNoSectionAtAllGoesToTheFallback()
    {
        PluginSettingsManifest manifest = new()
        {
            Sections = [Section("known")],
            Settings = [Setting("loose")],
        };

        PluginSettingsPage page = Build(manifest, "loose");

        Assert.Equal(
            PluginSettingsPageLayout.FallbackSectionId,
            Assert.Single(page.Sections).SectionId);
    }

    [Fact]
    public void TheFallbackIsDrawnLastSoLeftoversDoNotLeadThePage()
    {
        PluginSettingsManifest manifest = new()
        {
            Sections = [Section("declared")],
            Settings = [Setting("loose"), Setting("placed", "declared")],
        };

        PluginSettingsPage page = Build(manifest, "loose", "placed");

        Assert.Equal("declared", page.Sections[0].SectionId);
        Assert.Equal(PluginSettingsPageLayout.FallbackSectionId, page.Sections[1].SectionId);
    }

    [Fact]
    public void AnEmptySectionIsNotDrawnAndTheReasonIsReported()
    {
        PluginSettingsManifest manifest = new()
        {
            Sections = [Section("populated"), Section("empty")],
            Settings = [Setting("only", "populated")],
        };

        PluginSettingsPage page = Build(manifest, "only");

        Assert.Equal("populated", Assert.Single(page.Sections).SectionId);
        Assert.Contains(page.Diagnostics, entry => entry.Contains("empty"));
    }

    [Fact]
    public void ASettingWithNoReconciledValueIsNotDrawn()
    {
        // The resolver emits one entry per declared setting, so a missing one means the two
        // disagree; drawing it would show a state the plugin never reported.
        PluginSettingsManifest manifest = new()
        {
            Sections = [Section("declared")],
            Settings = [Setting("present", "declared"), Setting("absent", "declared")],
        };

        PluginSettingsPage page = Build(manifest, "present");

        Assert.Equal("present", Assert.Single(Assert.Single(page.Sections).Rows).SettingId);
        Assert.Contains(page.Diagnostics, entry => entry.Contains("absent"));
    }

    [Fact]
    public void APluginCannotDeclareTheFallbackSectionAndTakeItOver()
    {
        // Guarded structurally: the fallback id carries a character the manifest's own identifier
        // rule forbids, so this can never become a naming race.
        Assert.False(PlainText.IsIdentifier(
            PluginSettingsPageLayout.FallbackSectionId,
            PluginSettingSection.MaxSectionIdLength));
    }

    [Fact]
    public void AManifestWithNoSettingsRendersNothingRatherThanAnEmptyShell()
    {
        PluginSettingsPage page = Build(new PluginSettingsManifest());

        Assert.Empty(page.Sections);
    }

    [Fact]
    public void TheSectionKeyAndCustomTitleSurviveIntoTheRenderedSection()
    {
        PluginSettingsManifest manifest = new()
        {
            Sections =
            [
                new PluginSettingSection
                {
                    SectionId = "vendor",
                    Key = SettingSectionKey.Custom,
                    CustomTitle = "Vendor tuning",
                },
            ],
            Settings = [Setting("one", "vendor")],
        };

        PluginSettingsSection section = Assert.Single(Build(manifest, "one").Sections);

        Assert.Equal(SettingSectionKey.Custom, section.Key);
        Assert.Equal("Vendor tuning", section.CustomTitle);
    }
}
