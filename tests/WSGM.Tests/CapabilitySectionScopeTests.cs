using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class CapabilitySectionScopeTests
{
    /// <remarks>
    /// Two shapes, because a descriptor's role and value kind have to agree for the router to reach
    /// the section check at all. Sharing one shape across both made the refusal test pass for the
    /// wrong reason: an invalid power limit is refused whether or not it names a section.
    /// </remarks>
    private static CapabilityDescriptor Generic(string? sectionId = null) => new()
    {
        CapabilityId = "vendor.control",
        Role = CapabilityRole.GenericToggle,
        ValueKind = CapabilityValueKind.Boolean,
        Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "Control" },
        SectionId = sectionId,
        SupportsRead = true,
        SupportsWrite = true,
        Persistence = CapabilityPersistence.Volatile,
    };

    private static CapabilityDescriptor Semantic(string? sectionId = null) => new()
    {
        CapabilityId = "power.primary-limit",
        Role = CapabilityRole.PowerSustainedLimit,
        ValueKind = CapabilityValueKind.Integer,
        Display = new CapabilityDisplay { Key = DisplayKey.Tdp },
        SectionId = sectionId,
        SupportsRead = true,
        SupportsWrite = true,
        Minimum = 8,
        Maximum = 30,
        Step = 1,
        Persistence = CapabilityPersistence.Volatile,
    };

    private static bool Validates(CapabilityDescriptor descriptor, out string? error) =>
        DeviceCapabilityValidation.TryValidateDescriptorSet(
            new CapabilityDescriptorSet
            {
                Generation = 1,
                CycleGeneration = 1,
                Descriptors = [descriptor],
            },
            1,
            0,
            out error);

    [Theory]
    [InlineData(CapabilityRole.GenericToggle)]
    [InlineData(CapabilityRole.GenericRange)]
    [InlineData(CapabilityRole.GenericChoice)]
    [InlineData(CapabilityRole.GenericAction)]
    [InlineData(CapabilityRole.GenericText)]
    [InlineData(CapabilityRole.GenericReadOnly)]
    public void AGenericRoleMayBePlacedBecauseWsgmHasNoHomeToGiveIt(CapabilityRole role)
    {
        Assert.True(role.IsGeneric());
    }

    [Theory]
    [InlineData(CapabilityRole.PowerSustainedLimit)]
    [InlineData(CapabilityRole.FanCurve)]
    [InlineData(CapabilityRole.VariableRefreshRate)]
    [InlineData(CapabilityRole.OemControl)]
    public void ASemanticRoleIsNotGeneric(CapabilityRole role)
    {
        Assert.False(role.IsGeneric());
    }

    [Fact]
    public void AGenericCapabilityMayDeclareASection()
    {
        Assert.True(Validates(Generic("vendor.tuning"), out _));
    }

    [Fact]
    public void ASemanticCapabilityDeclaringASectionIsRefusedByName()
    {
        // A plugin that could place semantic controls would scatter power and fan rows into
        // invented groupings, which is the cross-device consistency DisplayKey exists to protect.
        // The same descriptor without the section validates, so this refusal is the section rule
        // and not some other defect in the shape.
        Assert.True(Validates(Semantic(), out _));

        bool valid = Validates(Semantic("vendor.tuning"), out string? error);

        Assert.False(valid);
        // Named, because from the plugin author's side an ignored section looks like nothing
        // happened.
        Assert.Contains("PowerSustainedLimit", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has spaces")]
    [InlineData("has/slash")]
    public void AnIllegalSectionIdIsRefused(string sectionId)
    {
        Assert.False(Validates(Generic(sectionId), out _));
    }

    [Fact]
    public void AnOverlongSectionIdIsRefused()
    {
        Assert.False(Validates(Generic(new string('a', 65)), out _));
    }
}
