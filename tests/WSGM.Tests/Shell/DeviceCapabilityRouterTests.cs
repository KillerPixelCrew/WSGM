using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Shell;
using WSGM.Tests.Builders;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Shell;

public sealed class DeviceCapabilityRouterTests
{
    [Fact]
    public async Task ALateOldResultCannotClearTheNewCommandsPendingValueOrReplaceItsResult()
    {
        await using DeviceCapabilityRouter router = new(action => action());
        FakeCapabilityPublisher publisher = new(CapabilityRole.GenericToggle);
        TaskCompletionSource<CapabilityCommandResult> oldCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<DeviceCommandDispatch> newDispatch =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        publisher.Dispatch = (command, _) => publisher.Commands.Count == 1
            ? Task.FromResult(new DeviceCommandDispatch(Result(command, CommandOutcome.TimedOut), oldCompletion.Task))
            : newDispatch.Task;
        router.Attach(publisher, 1);
        publisher.Publish(CapabilityBuilders.Set(1, CapabilityBuilders.Toggle(CapabilityProfileScope.Switched)));
        publisher.PublishState(1, CapabilityBuilders.State(1, CapabilityBuilders.Flag(false)));
        await router.ExecuteAsync("graphics.toggle", null, CapabilityBuilders.Flag(true), TimeSpan.FromSeconds(1));
        var observer = router.LateCommandCompletion;
        var pending = router.ExecuteAsync("graphics.toggle", null, CapabilityBuilders.Flag(false),
            TimeSpan.FromSeconds(1));
        var latest = publisher.Commands[1];
        try
        {
            oldCompletion.SetResult(Result(publisher.Commands[0], CommandOutcome.Indeterminate));
            await observer;
            var view = Assert.IsType<DeviceCapabilityView>(
                router.TryGetView(new DeviceCapabilityKey("graphics.toggle", null)));
            Assert.False(view.Projection.PendingValue!.BooleanValue);
            Assert.Equal(CommandOutcome.TimedOut, view.LastResult!.Outcome);

            newDispatch.SetResult(new DeviceCommandDispatch(Result(latest, CommandOutcome.AppliedVerified)));
            await pending;
            view = Assert.IsType<DeviceCapabilityView>(
                router.TryGetView(new DeviceCapabilityKey("graphics.toggle", null)));
            Assert.Null(view.Projection.PendingValue);
            Assert.Equal(latest.CommandId, view.LastResult!.CommandId);
            Assert.False(view.LastCommandValue!.BooleanValue);
        }
        finally
        {
            newDispatch.TrySetResult(new DeviceCommandDispatch(Result(latest, CommandOutcome.AppliedVerified)));
            await pending;
        }
    }

    [Fact]
    public async Task DescriptorReplacementDropsTheOldLateResultAndTryGetViewUsesTheNewDescriptor()
    {
        await using DeviceCapabilityRouter router = new(action => action());
        FakeCapabilityPublisher publisher = new(CapabilityRole.GenericToggle);
        TaskCompletionSource<CapabilityCommandResult> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
        publisher.Dispatch = (command, _) => Task.FromResult(
            new DeviceCommandDispatch(Result(command, CommandOutcome.TimedOut), late.Task));
        router.Attach(publisher, 1);
        publisher.Publish(CapabilityBuilders.Set(1, CapabilityBuilders.Toggle(CapabilityProfileScope.Switched)));
        publisher.PublishState(1, CapabilityBuilders.State(1, CapabilityBuilders.Flag(false)));
        await router.ExecuteAsync("graphics.toggle", null, CapabilityBuilders.Flag(true), TimeSpan.FromSeconds(1));
        var observer = router.LateCommandCompletion;
        var replacement = CapabilityBuilders.Toggle(CapabilityProfileScope.GlobalOnly);
        publisher.Publish(CapabilityBuilders.Set(2, replacement));
        late.SetResult(Result(publisher.Commands[0], CommandOutcome.AppliedVerified));
        await observer;

        var view = Assert.IsType<DeviceCapabilityView>(
            router.TryGetView(new DeviceCapabilityKey("graphics.toggle", null)));
        Assert.Same(replacement, view.Descriptor);
        Assert.Null(view.LastResult);
        Assert.Null(view.Projection.PendingValue);
        Assert.Null(router.TryGetView(new DeviceCapabilityKey("missing", null)));
    }

    private static CapabilityCommandResult Result(CapabilityCommand command, CommandOutcome outcome)
    {
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = outcome,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

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
    public void ASectionIdIsCheckedForShapeNotLength()
    {
        Assert.True(Validates(Generic(new string('a', 300)), out _));
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

        Assert.True(CapabilityValueValidation.ValueMatches(Curve(0, 100), fanCurve, out _));
        Assert.False(CapabilityValueValidation.ValueMatches(Curve(0, 101), fanCurve, out _));
        Assert.False(CapabilityValueValidation.ValueMatches(Curve(-1, 100), fanCurve, out _));

        // An undeclared bound means the device has no limit there; inventing one would refuse a
        // curve it would have accepted.
        var unbounded = fanCurve with { Minimum = null, Maximum = null };
        Assert.True(CapabilityValueValidation.ValueMatches(Curve(-500, 5000), unbounded, out _));
    }

    [Theory]
    // Raising the sustained limit past the boost limit carries the boost limit up with it.
    [InlineData(true, 30, 20, false, 30)]
    // A sustained change below the boost limit leaves the boost limit where it is.
    [InlineData(true, 15, 25, false, 25)]
    // A boost ceiling below the sustained limit carries that limit down.
    [InlineData(false, 18, 25, false, 18)]
    // A boost raise leaves the sustained limit where it is.
    [InlineData(false, 28, 20, false, 20)]
    // A unified target moves both limits to it.
    [InlineData(true, 15, 25, true, 15)]
    // A paired limit nothing has observed yet counts as the commanded wattage.
    [InlineData(true, 22, null, false, 22)]
    public void ThePairPolicyKeepsTheSustainedLimitAtOrBelowTheBoostLimit(
        bool sustainedCommanded, int watts, int? pairedWatts, bool unified, int expected)
    {
        var sustained = Descriptor() with { PairedPowerLimitId = "power.boost-limit" };
        var boost = Descriptor() with { CapabilityId = "power.boost-limit", Role = CapabilityRole.PowerSlowLimit };

        var paired = sustainedCommanded
            ? DeviceCapabilityRouter.PairedWatts(sustained, boost, watts, pairedWatts, unified)
            : DeviceCapabilityRouter.PairedWatts(boost, sustained, watts, pairedWatts, unified);

        Assert.Equal(expected, paired);
    }

    [Fact]
    public void ThePairedWattageStaysInsideThePairedDescriptorsRange()
    {
        var sustained = Descriptor() with { PairedPowerLimitId = "power.boost-limit" };
        var boost = Descriptor() with
        {
            CapabilityId = "power.boost-limit", Role = CapabilityRole.PowerSlowLimit, Minimum = 12, Maximum = 25
        };

        Assert.Equal(25, DeviceCapabilityRouter.PairedWatts(sustained, boost, 30, 20, false));
        Assert.Equal(12, DeviceCapabilityRouter.PairedWatts(sustained, boost, 10, null, true));
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

    [Fact]
    public async Task AGraphicsRouterResolvesEachProfileScopeAndStampsItsPublisher()
    {
        FakeCapabilityPublisher publisher = new(CapabilityRole.GenericToggle);
        await using DeviceCapabilityRouter router = new(action => action(), CapabilityBuilders.GpuPublisher);
        router.Attach(publisher, 1);
        publisher.Publish(CapabilityBuilders.Set(1,
            CapabilityBuilders.Toggle(CapabilityProfileScope.Switched, "switched"),
            CapabilityBuilders.Toggle(CapabilityProfileScope.GlobalOnly, "global") with
            {
                ApplyTiming = CapabilityApplyTiming.SystemRestart
            },
            CapabilityBuilders.Toggle(CapabilityProfileScope.NativePerApplication, "native") with
            {
                ApplyTiming = CapabilityApplyTiming.NextApplicationStart
            }));
        ProfileValues global = new();
        ProfileValues game = new();
        foreach (var id in new[] { "switched", "global", "native" })
        {
            global.SetDevice(CapabilityBuilders.GpuPublisher, id, null, CapabilityBuilders.Flag(false));
            game.SetDevice(CapabilityBuilders.GpuPublisher, id, null, CapabilityBuilders.Flag(true));
        }

        router.UpdateDesiredContext(CapabilityBuilders.GpuPublisher, new ProfileLayers(global, game), true);
        var views = router.Snapshot().ToDictionary(view => view.Descriptor.CapabilityId);

        Assert.All(views.Values, view => Assert.Equal(CapabilityBuilders.GpuPublisher, view.Publisher));
        Assert.Equal(ProfileSource.Game, views["switched"].Projection.DesiredSource);
        Assert.Equal(ProfileSource.Global, views["global"].Projection.DesiredSource);
        Assert.False(views["global"].Projection.DesiredValue!.BooleanValue);
        Assert.Equal(CapabilityApplyTiming.SystemRestart, views["global"].Projection.ApplyTiming);
        Assert.Equal(ProfileSource.Game, views["native"].Projection.DesiredSource);
        Assert.False(views["native"].Projection.GlobalDesiredValue!.BooleanValue);
        Assert.Equal(CapabilityProfileScope.NativePerApplication, views["native"].Projection.ProfileScope);
        Assert.Equal(CapabilityApplyTiming.NextApplicationStart, views["native"].Projection.ApplyTiming);
        Assert.Equal("gpu:wsgm.test-gpu/native#", views["native"].SettingKey.Id);
    }

    [Fact]
    public async Task AGraphicsRouterIgnoresAnotherPublishersValues()
    {
        FakeCapabilityPublisher publisher = new(CapabilityRole.GenericToggle);
        await using DeviceCapabilityRouter router = new(action => action(), CapabilityBuilders.GpuPublisher);
        router.Attach(publisher, 1);
        publisher.Publish(CapabilityBuilders.Set(1, CapabilityBuilders.Toggle(CapabilityProfileScope.Switched)));
        ProfileValues global = new();
        global.SetDevice("gpu:wsgm.other", "graphics.toggle", null, CapabilityBuilders.Flag(true));
        global.SetDevice("claw", "graphics.toggle", null, CapabilityBuilders.Flag(true));

        router.UpdateDesiredContext(CapabilityBuilders.GpuPublisher, new ProfileLayers(global, null), true);

        Assert.Equal(ProfileSource.None, Assert.Single(router.Snapshot()).Projection.DesiredSource);
    }

    [Fact]
    public async Task ARoleThePublisherDoesNotDeclareRefusesTheWholeSet()
    {
        FakeCapabilityPublisher publisher = new(CapabilityRole.GenericToggle);
        await using DeviceCapabilityRouter router = new(action => action(), CapabilityBuilders.GpuPublisher);
        var accepted = 0;
        router.DescriptorsAccepted += (_, _) => accepted++;
        router.Attach(publisher, 1);

        publisher.Publish(CapabilityBuilders.Set(1,
            CapabilityBuilders.Toggle(CapabilityProfileScope.Switched),
            CapabilityBuilders.Vrr("internal")));

        Assert.Empty(router.Snapshot());
        Assert.Equal(0, accepted);
    }

    [Fact]
    public async Task ACommandReachesAnyPublisherWithItsGenerations()
    {
        FakeCapabilityPublisher publisher = new(CapabilityRole.GenericToggle);
        await using DeviceCapabilityRouter router = new(action => action(), CapabilityBuilders.GpuPublisher);
        var accepted = 0;
        router.DescriptorsAccepted += (_, _) => accepted++;
        router.Attach(publisher, 1);
        publisher.Publish(CapabilityBuilders.Set(1, CapabilityBuilders.Toggle(CapabilityProfileScope.Switched)));
        publisher.PublishState(1, CapabilityBuilders.State(1, CapabilityBuilders.Flag(false)));

        var result = await router.ExecuteAsync("graphics.toggle", null, CapabilityBuilders.Flag(true),
            TimeSpan.FromSeconds(1));

        Assert.Equal(1, accepted);
        Assert.Equal(CommandOutcome.AppliedVerified, result.Outcome);
        var command = Assert.Single(publisher.Commands);
        Assert.Equal(1, command.ExpectedCycleGeneration);
        Assert.Equal(1, command.ExpectedDescriptorGeneration);
        Assert.True(command.RequestedValue!.BooleanValue);
    }
}
