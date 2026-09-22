using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class DeviceCapabilityRouterTests
{
    [Fact]
    public void ProductionAdmissionRejectsInvalidLayoutHints()
    {
        Assert.False(Validates(Generic() with { Prominence = (CapabilityProminence)999 }, out _));
        Assert.False(Validates(Generic() with { LayoutPair = new CapabilityLayoutPair("missing") }, out _));
        Assert.True(Validates(Generic() with { Prominence = CapabilityProminence.Compact }, out _));
    }

    [Fact]
    public async Task DisconnectedRouterRejectsACommandWithAnActionableReason()
    {
        await using DeviceCapabilityRouter router = new(action => action());

        var result = await router.ExecuteAsync(
            "power.sustained",
            null,
            new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = 18 },
            TimeSpan.FromSeconds(1));

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Equal(CapabilityReasonCode.HostUnavailable, result.Reason?.Code);
        Assert.True(result.Reason!.Retryable);
    }

    [Fact]
    public async Task ABurstOfChangesPostsOneBuildThatRaisesChangedOnce()
    {
        List<Action> posted = [];
        await using DeviceCapabilityRouter router = new(posted.Add);
        var notifications = 0;
        router.Changed += _ => notifications++;

        // Changes that arrive before the posted build runs join it; the build reads the state
        // when it runs on the UI thread, so it carries all of them.
        router.UpdateDesiredContext(null, new ProfileLayers(new ProfileValues(), null), true);
        router.UpdateDesiredContext(null, new ProfileLayers(new ProfileValues(), null), false);

        Assert.Single(posted);
        posted[0]();
        Assert.Equal(1, notifications);

        // Once it has run, the next change needs a build of its own.
        router.UpdateDesiredContext(null, new ProfileLayers(new ProfileValues(), null), true);

        Assert.Equal(2, posted.Count);
        posted[1]();
        Assert.Equal(2, notifications);
    }

    [Fact]
    public async Task DisposalClosesCommandAdmissionWithoutDisposingAnOwnedGate()
    {
        DeviceCapabilityRouter router = new(action => action());
        await router.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => router.ExecuteAsync(
            "power.sustained",
            null,
            new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = 18 },
            TimeSpan.FromSeconds(1)));
    }

    /// <remarks>
    ///     Two shapes, because a descriptor's role and value kind have to agree for the router to reach
    ///     the section check at all. Sharing one shape across both made the refusal test pass for the
    ///     wrong reason: an invalid power limit is refused whether or not it names a section.
    /// </remarks>
    private static CapabilityDescriptor Generic(string? sectionId = null)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = "vendor.control",
            Role = CapabilityRole.GenericToggle,
            ValueKind = CapabilityValueKind.Boolean,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "Control" },
            SectionId = sectionId,
            SupportsRead = true,
            SupportsWrite = true,
            Persistence = CapabilityPersistence.Volatile
        };
    }

    private static CapabilityDescriptor Semantic(string? sectionId = null)
    {
        return new CapabilityDescriptor
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
            Persistence = CapabilityPersistence.Volatile
        };
    }

    private static bool Validates(
        CapabilityDescriptor descriptor,
        out string? error,
        params CapabilitySection[] sections)
    {
        return DeviceCapabilityValidation.TryValidateDescriptorSet(
            new CapabilityDescriptorSet
            {
                Generation = 1,
                CycleGeneration = 1,
                Sections = sections,
                Descriptors = [descriptor]
            },
            1,
            0,
            out error);
    }

    private static CapabilitySection Declared(string id = "vendor.tuning")
    {
        return new CapabilitySection
        {
            SectionId = id,
            Key = SettingSectionKey.Power,
            Categories =
            [
                new CapabilityCategory
                {
                    CategoryId = "general",
                    Key = SettingSectionKey.General
                }
            ]
        };
    }

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

        var valid = Validates(Semantic("vendor.tuning"), out var error);

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

    [Fact]
    public void ASemanticCapabilityMayBePlacedInASectionTheSetDeclares()
    {
        // The declared layout is the plugin authoring its own overlay surface; every title and
        // icon in it comes from a WSGM-owned vocabulary, so the consistency rule is not weakened.
        Assert.True(Validates(Semantic("vendor.tuning"), out var error, Declared()), error);
    }

    [Fact]
    public void ACategoryMustBelongToTheDeclaredSection()
    {
        Assert.True(Validates(
            Generic("vendor.tuning") with { CategoryId = "general" },
            out _,
            Declared()));

        var valid = Validates(
            Generic("vendor.tuning") with { CategoryId = "missing" },
            out var error,
            Declared());

        Assert.False(valid);
        Assert.Contains("missing", error);
    }

    [Fact]
    public void ACategoryWithoutADeclaredSectionIsRefused()
    {
        Assert.False(Validates(Generic() with { CategoryId = "general" }, out _));
    }

    [Fact]
    public void ADuplicateDeclaredSectionIsRefusedByName()
    {
        var valid = Validates(
            Generic("vendor.tuning"),
            out var error,
            Declared(),
            Declared());

        Assert.False(valid);
        Assert.Contains("more than once", error);
    }

    [Fact]
    public void DescriptorValidationRejectsDuplicateAndStaleShapes()
    {
        var descriptor = Descriptor();
        CapabilityDescriptorSet duplicated = new()
        {
            Generation = 2,
            CycleGeneration = 3,
            Descriptors = [descriptor, descriptor]
        };

        Assert.False(DeviceCapabilityValidation.TryValidateDescriptorSet(
            duplicated, 3, 1, out _));
        Assert.False(DeviceCapabilityValidation.TryValidateDescriptorSet(
            duplicated with { Descriptors = [descriptor], Generation = 1 }, 3, 1, out _));
    }

    // A curve can be written straight through ExecuteCapabilityAsync without passing an authored
    // profile, so the declared output bounds have to be enforced on this path too — every other
    // numeric kind here is, and the refusal message promises "shape or bounds" for all of them.
    [Fact]
    public void CurveWritesAreHeldToTheDeclaredOutputBoundsLikeEveryOtherNumericKind()
    {
        var fanCurve = Descriptor() with
        {
            CapabilityId = "fan.curve",
            Role = CapabilityRole.FanCurve,
            ValueKind = CapabilityValueKind.Curve,
            Display = new CapabilityDisplay { Key = DisplayKey.FanCurve },
            Minimum = 0,
            Maximum = 100
        };

        Assert.True(DeviceCapabilityValidation.ValueMatches(Curve(0, 100), fanCurve, out _));
        Assert.False(DeviceCapabilityValidation.ValueMatches(Curve(0, 101), fanCurve, out _));
        Assert.False(DeviceCapabilityValidation.ValueMatches(Curve(-1, 100), fanCurve, out _));

        // An undeclared bound means the device has no limit there; inventing one would refuse a
        // curve it would have accepted.
        var unbounded = fanCurve with { Minimum = null, Maximum = null };
        Assert.True(DeviceCapabilityValidation.ValueMatches(Curve(-500, 5000), unbounded, out _));
    }

    private static CapabilityValue Curve(int firstOutput, int secondOutput)
    {
        return CapabilityValue.Curve([new CurvePoint(0, firstOutput), new CurvePoint(100, secondOutput)]);
    }

    private static CapabilityDescriptor Descriptor()
    {
        return new CapabilityDescriptor
        {
            CapabilityId = "power.primary-limit",
            Role = CapabilityRole.PowerSustainedLimit,
            ValueKind = CapabilityValueKind.Integer,
            Display = new CapabilityDisplay { Key = DisplayKey.Tdp },
            SupportsRead = true,
            SupportsWrite = true,
            Minimum = 8,
            Maximum = 30,
            Step = 1,
            Persistence = CapabilityPersistence.Volatile
        };
    }
}
