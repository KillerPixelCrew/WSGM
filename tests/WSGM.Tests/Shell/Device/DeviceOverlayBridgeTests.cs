using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Overlay;
using WSGM.Shell;
using static WSGM.Tests.Builders.ControllerBuilders;

namespace WSGM.Tests.Shell;

/// <summary>How the Device overlay bridge projects plugin capabilities and WSGM's own Device rows.</summary>
public sealed class DeviceOverlayBridgeTests
{
    [Theory]
    [InlineData("firmware", false)]
    [InlineData("cpu", true)]
    [InlineData("xg", false)]
    public void UnknownNativeCurvesUseDeclaredEditorInputsWithoutInventingAnObservation(string instance,
        bool supportsRead)
    {
        CapabilityDescriptor descriptor = new()
        {
            CapabilityId = CapabilityIds.FanCurve, InstanceId = instance, Role = CapabilityRole.FanCurve,
            ValueKind = CapabilityValueKind.Curve, Display = new CapabilityDisplay { Key = DisplayKey.FanCurve },
            SupportsRead = supportsRead, SupportsWrite = true, Minimum = 0, Maximum = 100,
            CurveInputs = [58, 61, 64, 68, 72, 77, 81, 98], Persistence = CapabilityPersistence.Volatile
        };
        CapabilityState state = new()
        {
            CapabilityId = descriptor.CapabilityId, InstanceId = instance, Available = true,
            Quality = HardwareStateQuality.Unknown
        };
        var capability = new DeviceCapabilityView(descriptor, new CapabilityProjection { State = state }, null);
        var row = CapabilityRowProjection.ToOverlayCapability(capability, new HashSet<string>(), FanCurvePresetTable.HandheldCompanion);
        Assert.True(row.CanInvoke);
        Assert.Equal(instance, row.InstanceId);
        Assert.Equal(descriptor.CurveInputs, row.CurrentValue!.CurveValue.Select(point => point.Input));
        Assert.True(CapabilityValueValidation.ValueMatches(row.CurrentValue, descriptor, out _));
        Assert.Null(capability.Projection.State.ObservedValue);
        Assert.Null(capability.Projection.DesiredValue);
        var steamRow = Assert.Single(SteamNativeSettingsService.DeviceQuickAccessRows([capability],
            FanCurvePresetTable.HandheldCompanion)).Row;
        Assert.False(steamRow.Disabled);
        Assert.Equal(descriptor.CurveInputs, steamRow.Curve!.Select(point => point.Input));

        var observed = CapabilityValue.Curve([new CurvePoint(40, 30), new CurvePoint(80, 90)]);
        var known = CapabilityRowProjection.ToOverlayCapability(capability with
        {
            Projection = capability.Projection with { State = state with { ObservedValue = observed } }
        }, new HashSet<string>(), FanCurvePresetTable.HandheldCompanion);
        Assert.Same(observed, known.CurrentValue);
    }

    [Fact]
    public void NativeCurveEditorInputsAreCopiedIntoTheHostDescriptor()
    {
        int[] inputs = [0, 20, 40, 60, 80, 100];
        LibHandheld.Contracts.CapabilityDescriptor native = new()
        {
            CapabilityId = CapabilityIds.FanCurve, Role = LibHandheld.Contracts.CapabilityRole.FanCurve,
            ValueKind = LibHandheld.Contracts.CapabilityValueKind.Curve,
            Display = new LibHandheld.Contracts.CapabilityDisplay { Key = LibHandheld.Contracts.DisplayKey.FanCurve },
            SupportsWrite = true, CurveInputs = inputs,
            Persistence = LibHandheld.Contracts.CapabilityPersistence.Volatile
        };
        inputs[0] = 50;
        var descriptor = Assert.Single(HandheldUiProjection.Descriptors(
            new LibHandheld.Contracts.CapabilityDescriptorSet { Descriptors = [native] }).Descriptors);
        Assert.Equal([0, 20, 40, 60, 80, 100], descriptor.CurveInputs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ObservedFanCurveCanBeEditedWithoutACyclingNextValue(bool available)
    {
        var descriptor = new CapabilityDescriptor
        {
            CapabilityId = "fan.curve", Role = CapabilityRole.FanCurve,
            ValueKind = CapabilityValueKind.Curve,
            Display = new CapabilityDisplay { Key = DisplayKey.FanCurve },
            SupportsRead = true, SupportsWrite = true, Persistence = CapabilityPersistence.Volatile
        };
        var state = new CapabilityState
        {
            CapabilityId = descriptor.CapabilityId, Available = available,
            Quality = HardwareStateQuality.Observed,
            ObservedValue = CapabilityValue.Curve([new CurvePoint(40, 20), new CurvePoint(90, 100)])
        };
        var view = CapabilityRowProjection.ToOverlayCapability(
            new DeviceCapabilityView(descriptor, new CapabilityProjection { State = state }, null),
            new HashSet<string>(), FanCurvePresetTable.HandheldCompanion);
        Assert.Equal(available, view.CanInvoke);
        Assert.Null(view.NextValue);
    }

    // Pure final-overlay projection coverage; no device package or host is started.
    [Theory]
    [InlineData(37, null, 37)]
    [InlineData(26, null, 26)]
    [InlineData(37, 35, 35)]
    public void PowerControlShowsReadbackOverSavedIntentUnlessACommandIsPending(
        int observedWatts, int? pendingWatts, int displayedWatts)
    {
        CapabilityDescriptor descriptor = new()
        {
            CapabilityId = "power.boost-limit",
            Role = CapabilityRole.PowerSlowLimit,
            ValueKind = CapabilityValueKind.Integer,
            Display = new CapabilityDisplay { Key = DisplayKey.BoostPowerLimit },
            SupportsRead = true,
            SupportsWrite = true,
            Minimum = 8,
            Maximum = 37,
            Step = 1,
            Unit = CapabilityUnit.Watt,
            Persistence = CapabilityPersistence.Volatile
        };
        CapabilityState state = new()
        {
            CapabilityId = descriptor.CapabilityId,
            Available = true,
            Quality = HardwareStateQuality.Observed,
            ObservedValue = new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = observedWatts }
        };
        CapabilityProjection projection = new()
        {
            State = state,
            DesiredValue = new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = 31 },
            PendingValue = pendingWatts is { } pending
                ? new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = pending }
                : null
        };

        var capability = CapabilityRowProjection.ToOverlayCapability(
            new DeviceCapabilityView(descriptor, projection, null),
            new HashSet<string>(StringComparer.Ordinal), FanCurvePresetTable.HandheldCompanion);

        Assert.Equal(displayedWatts, capability.CurrentValue?.IntegerValue);
        Assert.Equal($"{displayedWatts} W", capability.TrailingText);
    }

    [Fact]
    public void SimulatedDeviceGroupsEveryCapabilityIntoAStableSemanticSection()
    {
        using SimulatedDeviceOverlaySource source = new();

        var snapshot = source.Snapshot();

        Assert.True(snapshot.Visible);
        Assert.Equal(DeviceOverlaySection.PowerAndThermals,
            DeviceOverlaySectionPages.SectionAbsorbedInto(snapshot, DeviceSections.PowerId));
        Assert.Contains(snapshot.Capabilities,
            capability => capability.Section == DeviceOverlaySection.PowerAndThermals);
        Assert.Contains(snapshot.Capabilities,
            capability => capability.Section == DeviceOverlaySection.ControllerAndMotion);
        Assert.Contains(snapshot.Capabilities,
            capability => capability.Section == DeviceOverlaySection.LightingAndFeatures);
        Assert.All(snapshot.Capabilities,
            capability => Assert.NotEqual(DescriptorStatus.None, capability.Status));
        Assert.NotNull(snapshot.GlyphSelection);
        Assert.DoesNotContain(snapshot.Capabilities,
            capability => capability.CapabilityId == "wsgm.glyph.selection");
    }

    [Fact]
    public void AvailableActionOnlyCapabilityIsRunnableWithoutInventingReadback()
    {
        CapabilityDescriptor descriptor = new()
        {
            CapabilityId = "haptic.rumble",
            Role = CapabilityRole.HapticSink,
            ValueKind = CapabilityValueKind.None,
            Display = new CapabilityDisplay { Key = DisplayKey.Rumble },
            SupportsAction = true,
            Persistence = CapabilityPersistence.Volatile
        };
        CapabilityState state = new()
        {
            CapabilityId = descriptor.CapabilityId,
            Available = true,
            Quality = HardwareStateQuality.Unknown
        };

        var capability = CapabilityRowProjection.ToOverlayCapability(
            new DeviceCapabilityView(
                descriptor,
                new CapabilityProjection { State = state },
                null),
            new HashSet<string>(StringComparer.Ordinal), FanCurvePresetTable.HandheldCompanion);

        Assert.Equal(DescriptorStatus.Available, capability.Status);
        Assert.True(capability.CanInvoke);
        Assert.Equal("RUN", capability.TrailingText);
        Assert.Equal("Ready", capability.Description);
    }

    /// An action's published description is its caption while nothing more urgent needs saying.
    [Fact]
    public void AnActionsPublishedDescriptionIsItsCaptionUntilAReasonReplacesIt()
    {
        var descriptor = new CapabilityDescriptor
        {
            CapabilityId = "vendor.reset", Role = CapabilityRole.GenericAction,
            ValueKind = CapabilityValueKind.None, SupportsAction = true,
            Display = new CapabilityDisplay
            {
                Key = DisplayKey.Custom, CustomLabel = "Reset tuning", Description = "Clears every tuning value."
            },
            Persistence = CapabilityPersistence.Volatile
        };
        CapabilityState state = new()
        {
            CapabilityId = descriptor.CapabilityId, Available = true, Quality = HardwareStateQuality.Unknown
        };
        var row = CapabilityRowProjection.ToOverlayCapability(
            new DeviceCapabilityView(descriptor, new CapabilityProjection { State = state }, null),
            new HashSet<string>(), FanCurvePresetTable.HandheldCompanion);
        Assert.Equal("Clears every tuning value.", row.Description);

        var blocked = CapabilityRowProjection.ToOverlayCapability(
            new DeviceCapabilityView(descriptor, new CapabilityProjection
            {
                State = state with
                {
                    Reason = new CapabilityReason(CapabilityReasonCode.Unsupported, "Restart Windows first.")
                }
            }, null),
            new HashSet<string>(), FanCurvePresetTable.HandheldCompanion);
        Assert.Equal("Restart Windows first.", blocked.Description);
    }

    /// WSGM clears the saved processor layers itself before both actions run, so it says so ahead of the
    /// library's own explanation, and Steam's rows read the same caption as the overlay.
    [Theory]
    [InlineData(CapabilityIds.ProcessorReset, "Restores supported values.",
        "Clears processor tuning for this device in Global and every game. Restores supported values.")]
    [InlineData(CapabilityIds.ProcessorGpuClockAutomatic, null,
        "Clears GPU clock tuning for this device in Global and every game.")]
    public void ProcessorActionsExplainThatWsgmClearsGlobalAndEveryGame(string capabilityId, string? published,
        string expected)
    {
        var descriptor = new CapabilityDescriptor
        {
            CapabilityId = capabilityId, Role = CapabilityRole.GenericAction,
            ValueKind = CapabilityValueKind.None, SupportsAction = true,
            Display = new CapabilityDisplay
            {
                Key = DisplayKey.Custom, CustomLabel = "Processor action", Description = published
            },
            Persistence = CapabilityPersistence.Volatile
        };
        CapabilityState state = new()
        {
            CapabilityId = descriptor.CapabilityId, Available = true, Quality = HardwareStateQuality.Unknown
        };
        var view = new DeviceCapabilityView(descriptor, new CapabilityProjection { State = state }, null);
        var row = CapabilityRowProjection.ToOverlayCapability(view, new HashSet<string>(),
            FanCurvePresetTable.HandheldCompanion);
        Assert.Equal(expected, row.Description);
        var steamRow = Assert.Single(SteamNativeSettingsService.DeviceQuickAccessRows([view],
            FanCurvePresetTable.HandheldCompanion)).Row;
        Assert.Equal(expected, steamRow.Description);
    }

    [Fact]
    public async Task SimulatedDeviceMutationRaisesOneSharedChangeAndUpdatesReadback()
    {
        using SimulatedDeviceOverlaySource source = new();
        var changes = 0;
        source.Changed += () => changes++;
        var tdp = source.Snapshot().Capabilities.Single(capability => capability.CapabilityId == "preview.power.tdp");

        await source.InvokeAsync(tdp);

        Assert.Equal(1, changes);
        Assert.Equal("16 W",
            source.Snapshot().Capabilities.Single(capability => capability.CapabilityId == "preview.power.tdp")
                .TrailingText);
    }

    [Fact]
    public async Task SimulatedGlyphSelectionUsesItsDedicatedCommandPath()
    {
        using SimulatedDeviceOverlaySource source = new();
        var changes = 0;
        source.Changed += () => changes++;

        var before = Assert.IsType<DescriptorRow>(
            source.Snapshot().GlyphSelection);
        await source.SetPhysicalGlyphSelectionAsync(DeviceGlyphSelection.NativeSteam);
        var after = Assert.IsType<DescriptorRow>(
            source.Snapshot().GlyphSelection);

        Assert.Equal("AUTO", before.TrailingText);
        Assert.Equal("STEAM", after.TrailingText);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void SimulatedDeviceDeclaresThePluginLayout()
    {
        using SimulatedDeviceOverlaySource source = new();

        var snapshot = source.Snapshot();

        Assert.Equal(
            ["power", "cooling", "rgb"],
            snapshot.PluginSections.Select(section => section.SectionId));
        Assert.Contains(snapshot.Capabilities, capability =>
            capability is { PluginSectionId: DeviceSections.RgbId, Role: CapabilityRole.LightingZoneColor });
        Assert.Contains(snapshot.Capabilities, capability =>
            capability.Role == CapabilityRole.LightingBrightness);
        // One row stays unplaced so the WSGM fallback grouping keeps working beside the layout.
        Assert.Contains(snapshot.Capabilities, capability => capability.PluginSectionId is null);
    }

    [Fact]
    public async Task SimulatedColorAndBrightnessAcceptStagedValues()
    {
        using SimulatedDeviceOverlaySource source = new();
        var snapshot = source.Snapshot();
        var rings = snapshot.Capabilities.Single(capability => capability.CapabilityId == "preview.lighting.rings");
        var brightness =
            snapshot.Capabilities.Single(capability => capability.CapabilityId == "preview.lighting.brightness");

        await source.InvokeAsync(rings with
        {
            NextValue = new CapabilityValue
            {
                Kind = CapabilityValueKind.Color,
                ColorValue = 0x123456
            }
        });
        await source.InvokeAsync(brightness with
        {
            NextValue = new CapabilityValue
            {
                Kind = CapabilityValueKind.Integer,
                IntegerValue = 55
            }
        });

        var after = source.Snapshot();
        Assert.Equal("#123456",
            after.Capabilities.Single(capability => capability.CapabilityId == "preview.lighting.rings").TrailingText);
        Assert.Equal("55%",
            after.Capabilities.Single(capability => capability.CapabilityId == "preview.lighting.brightness")
                .TrailingText);
    }

    // The Device rows that are WSGM's own rather than a plugin's.
    // AutoTDP, the controller target, glyph selection and cycle recovery are WSGM settings and WSGM
    // actions. They never arrive through the plugin capability list, which is what keeps the capability
    // invoke path single-purpose — and is also why each has to be counted into its section by hand, or
    // a section holding only one of them is dropped from the menu and the row becomes unreachable.
    [Fact]
    public void ControllerManagementSwitchedOffShowsNoRowRatherThanAnInertOne()
    {
        // Off is a setting, not a fault, and the page has other rows. A permanently greyed control
        // the user cannot act on from this page is worse than nothing.
        Assert.Null(DeviceHostRows.ControllerView(
            false,
            Status(ControllerManagementState.Off, null)));
    }

    [Fact]
    public void AnActiveTargetIsNamedInTheRowRatherThanOnlyMarkedOn()
    {
        var row = DeviceHostRows.ControllerView(
            true,
            Status(ControllerManagementState.Active, ManagedControllerTarget.SteamDeckComposite));

        Assert.NotNull(row);
        Assert.Equal("DECK", row.TrailingText);
        Assert.Equal(DescriptorStatus.Available, row.Status);
        Assert.True(row.CanInvoke);
    }

    [Fact]
    public void ARunningGameIsToldTheChangeWillNotReachIt()
    {
        // A game holds the target it launched with. Without saying so, the control looks broken.
        var row = DeviceHostRows.ControllerView(
            true,
            Status(
                ControllerManagementState.Active,
                ManagedControllerTarget.Xbox360,
                applicationId: "steam:70"));

        Assert.NotNull(row);
        Assert.Contains("restart the running game", row.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnavailableBackendCannotBeCycledIntoAnotherBrokenTarget()
    {
        var row = DeviceHostRows.ControllerView(
            true,
            Status(
                ControllerManagementState.Unavailable,
                null,
                "The virtual controller component is not installed."));

        Assert.NotNull(row);
        Assert.False(row.CanInvoke);
        Assert.Equal("NONE", row.TrailingText);
        Assert.Equal(DescriptorStatus.Unsupported, row.Status);
    }

    [Fact]
    public void TheNativePadIsNamedAndTheTargetCanStillBeChanged()
    {
        var row = DeviceHostRows.ControllerView(
            true,
            Status(ControllerManagementState.Native, ManagedControllerTarget.SteamDeckComposite,
                ControllerManager.NativeDetail));

        Assert.NotNull(row);
        Assert.Equal("DECK", row.TrailingText);
        Assert.Equal(DescriptorStatus.Available, row.Status);
        Assert.Equal(ControllerManager.NativeDetail, row.Description);
        Assert.True(row.CanInvoke);
    }

    [Fact]
    public void AHealthyCycleOffersNoRecoveryRow()
    {
        // A recovery control that is always present but almost always inert trains a user to ignore
        // it, which is the opposite of what it is for.
        Assert.Null(DeviceHostRows.RecoveryView(DeviceCycleState.Active));
    }

    [Fact]
    public void AnAvailableRetryIsOfferedAndCarriesTheCycleState()
    {
        var row = DeviceHostRows.RecoveryView(DeviceCycleState.Faulted);

        Assert.NotNull(row);
        Assert.Equal("READY", row.TrailingText);
        // The row says what is wrong, not only that a button exists.
        Assert.Contains("·", row.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ASectionHoldingOnlyAWsgmRowStillAppearsInTheMenu()
    {
        // The regression this guards: no plugin publishes a controller capability, so the Controller
        // and motion section is empty of capabilities and would be dropped, taking the only way to
        // reach the target row with it.
        DeviceOverlaySnapshot snapshot = new(
            true,
            "Active",
            string.Empty,
            null,
            [],
            null,
            DeviceHostRows.ControllerView(
                true,
                Status(ControllerManagementState.Active, ManagedControllerTarget.Xbox360)));

        var entry = Assert.Single(DeviceOverlaySectionPages.Build(snapshot),
            candidate => candidate.PluginSectionId == "controller");

        Assert.Equal(DeviceOverlaySection.ControllerAndMotion, entry.Section);
        Assert.Equal(1, entry.Count);
    }

    [Fact]
    public void EachWsgmRowIsCountedIntoItsOwnSection()
    {
        DeviceOverlaySnapshot snapshot = new(
            true,
            "Active",
            string.Empty,
            new DescriptorRow(
                "device.glyph-selection",
                "Physical glyphs",
                string.Empty,
                "AUTO",
                true,
                DescriptorStatus.Available),
            [],
            DeviceHostRows.AutoTdpView(false, null),
            DeviceHostRows.ControllerView(
                true,
                Status(ControllerManagementState.Idle, ManagedControllerTarget.Xbox360)),
            DeviceHostRows.RecoveryView(DeviceCycleState.Faulted));

        Assert.Equal(
            [
                DeviceOverlaySection.PowerAndThermals,
                DeviceOverlaySection.ControllerAndMotion,
                DeviceOverlaySection.Diagnostics
            ],
            DeviceOverlaySectionPages.Build(snapshot).Select(entry => entry.Section));
    }

    private static DeviceAuthoredProfile Profile(
        string id,
        string name,
        string capabilityId = CapabilityIds.FanCurve)
    {
        return new DeviceAuthoredProfile
        {
            ProfileId = id,
            Name = name,
            CapabilityId = capabilityId
        };
    }

    [Fact]
    public void NoAuthoredProfilesShowsNoRowAtAll()
    {
        // These are created in Settings, so a row offering a choice between nothing would invite a
        // press that cannot do anything.
        Assert.Null(DeviceHostRows.AuthoredProfileView([], null, ProfileSource.None));
    }

    [Fact]
    public void AProfileChosenForEverythingSaysSo()
    {
        var row = DeviceHostRows.AuthoredProfileView(
            [Profile("quiet", "Quiet"), Profile("loud", "Loud")],
            "quiet",
            ProfileSource.Global);

        Assert.Equal("QUIET", row?.TrailingText);
        Assert.Contains("from Global", row?.Description);
    }

    [Theory]
    [InlineData(CapabilityIds.FanCurve, "Fan curve")]
    [InlineData(CapabilityIds.LightingColor, "Lighting")]
    [InlineData("future.profile", "Device profile")]
    public void AnAuthoredProfileUsesItsCapabilityKindAsTheLabel(string capabilityId, string expected)
    {
        var row = DeviceHostRows.AuthoredProfileView(
            [Profile("profile", "Profile", capabilityId)],
            "profile",
            ProfileSource.Global);

        Assert.Equal(expected, row?.Title);
    }

    [Fact]
    public void AProfileChosenForOneGameSaysThatInstead()
    {
        // The same word with very different consequences: this is the difference the user opens the
        // row to check mid-game.
        var row = DeviceHostRows.AuthoredProfileView(
            [Profile("quiet", "Quiet")],
            "quiet",
            ProfileSource.Game);

        Assert.Contains("this game's override", row?.Description);
    }

    [Fact]
    public void NothingChosenReadsAsNoneRatherThanEmpty()
    {
        var row = DeviceHostRows.AuthoredProfileView(
            [Profile("quiet", "Quiet")],
            null,
            ProfileSource.Global);

        Assert.Equal("NONE", row?.TrailingText);
        Assert.Equal(DescriptorStatus.None, row?.Status);
    }

    [Fact]
    public void ASelectionNamingADeletedProfileIsSaidPlainlyNotShownAsNone()
    {
        // None is a state the user chose; this is not, and showing them identically hides a
        // selection that has silently stopped working.
        var row = DeviceHostRows.AuthoredProfileView(
            [Profile("quiet", "Quiet")],
            "deleted",
            ProfileSource.Game);

        Assert.Equal("MISSING", row?.TrailingText);
        Assert.Equal(DescriptorStatus.Warning, row?.Status);
    }

    [Fact]
    public void ADeletedSelectionStillUsesTheAuthoredCapabilityKindAsTheLabel()
    {
        var row = DeviceHostRows.AuthoredProfileView(
            [Profile("colour", "Colour", CapabilityIds.LightingColor)],
            "deleted",
            ProfileSource.Game);

        Assert.Equal("Lighting", row?.Title);
    }

    [Fact]
    public void TheRowOffersACycleOnceThereIsSomethingToCycleTo()
    {
        var row = DeviceHostRows.AuthoredProfileView(
            [Profile("quiet", "Quiet")],
            "quiet",
            ProfileSource.Global);

        Assert.True(row?.CanInvoke);
    }

    [Fact]
    public void ADeletedSelectionStaysCyclableSoTheUserCanGetOutOfIt()
    {
        // Pressing it moves to a profile that does exist, which is the fastest way out of the state
        // for someone mid-game.
        var row = DeviceHostRows.AuthoredProfileView(
            [Profile("quiet", "Quiet")],
            "deleted",
            ProfileSource.Global);

        Assert.True(row?.CanInvoke);
    }

    [Fact]
    public void SwitchedOffReadsAsOffAndStaysToggleable()
    {
        var row = DeviceHostRows.AutoTdpView(false, null);

        Assert.Equal("OFF", row.TrailingText);
        Assert.Equal(DescriptorStatus.None, row.Status);
        Assert.True(row.CanInvoke);
    }

    [Fact]
    public void SwitchedOnBeforeTheServiceReportsAnythingSaysSoRatherThanLookingIdle()
    {
        var row = DeviceHostRows.AutoTdpView(true, null);

        Assert.Equal("ON", row.TrailingText);
        Assert.Contains("Starting", row.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ControllingShowsTheLimitItSettledOnAndHowFramesAreLanding()
    {
        var row = DeviceHostRows.AutoTdpView(
            true,
            new AutoTdpStatus(
                AutoTdpState.Controlling,
                17,
                14.2,
                16.6,
                "steam:70",
                "sustained-miss"));

        Assert.Equal("17 W", row.TrailingText);
        Assert.Equal(DescriptorStatus.Available, row.Status);
        Assert.Contains("14.2 ms", row.Description, StringComparison.Ordinal);
        Assert.Contains("16.6 ms", row.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void APausedSwitchWarnsRatherThanLookingHealthy()
    {
        // A user who turned AutoTDP on and then moved the slider needs to see that it stopped.
        var row = DeviceHostRows.AutoTdpView(
            true,
            new AutoTdpStatus(AutoTdpState.Paused, 22, null, null, null, "Paused by a manual change."));

        Assert.Equal(DescriptorStatus.Warning, row.Status);
        Assert.Equal("22 W", row.TrailingText);
    }

    [Fact]
    public void AMissingPrerequisiteReadsAsUnsupportedNotBroken()
    {
        var row = DeviceHostRows.AutoTdpView(
            true,
            new AutoTdpStatus(
                AutoTdpState.Unavailable,
                null,
                null,
                null,
                null,
                "No primary power limit is available."));

        Assert.Equal(DescriptorStatus.Unsupported, row.Status);
        Assert.Equal("ON", row.TrailingText);
    }

    [Fact]
    public void WaitingForAGameIsDistinctFromControllingOne()
    {
        var row = DeviceHostRows.AutoTdpView(
            true,
            new AutoTdpStatus(AutoTdpState.Idle, 15, null, null, null, "No application is rendering."));

        Assert.Equal(DescriptorStatus.Stale, row.Status);
    }

    [Fact]
    public void TheTargetSliderFollowsTheSwitchAndMarksAGameOverride()
    {
        Assert.Null(DeviceHostRows.AutoTdpTargetRow(false, new AutoTdpTargetView(45, 45, false)));

        var row = DeviceHostRows.AutoTdpTargetRow(true, new AutoTdpTargetView(45, 40, true))!;

        Assert.Equal(DeviceHostRowIds.AutoTdpTarget, row.Id);
        Assert.Equal(new DescriptorRange(AutoTdpTarget.MinimumFps, AutoTdpTarget.MaximumFps, 1), row.Range);
        Assert.Equal(45, row.Value);
        Assert.Equal(nameof(ProfileField.AutoTdpTarget), row.OverrideId);
        Assert.Equal("Held to the 40 FPS frame limit.", row.Description);
        Assert.True(row.CanInvoke);
    }

    [Fact]
    public async Task TheSimulatedSourceTogglesWithoutTouchingAnyDevice()
    {
        using SimulatedDeviceOverlaySource source = new();

        Assert.Equal("OFF", source.Snapshot().AutoTdp!.TrailingText);
        await source.SetHostSelectionAsync(DeviceHostRowIds.AutoTdp, "on");

        var row = source.Snapshot().AutoTdp!;
        Assert.Equal("15 W", row.TrailingText);
        Assert.Contains("Preview only", row.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void UnifiedModeKeepsBoostReadbackButOnlyPrimaryIsWritable()
    {
        DeviceOverlayCapability primary = new("primary", null, DeviceOverlaySection.PowerAndThermals,
            default, "Sustained", "", "20 W", true)
        {
            Role = CapabilityRole.PowerSustainedLimit,
            Writable = true
        };
        var boost = primary with
        {
            CapabilityId = "boost", Role = CapabilityRole.PowerSlowLimit, TrailingText = "30 W"
        };
        Assert.Equal("TDP", CapabilityRowProjection.ProjectManualTdp(primary, true).Title);
        var readback = CapabilityRowProjection.ProjectManualTdp(boost, true);
        Assert.False(readback.Writable);
        Assert.False(readback.CanInvoke);
        Assert.Equal("30 W", readback.TrailingText);
        Assert.Equal(boost, CapabilityRowProjection.ProjectManualTdp(boost, false));
    }
}
