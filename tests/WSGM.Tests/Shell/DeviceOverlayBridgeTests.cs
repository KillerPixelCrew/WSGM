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
            ObservedValue = new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = observedWatts },
            DescriptorGeneration = 1,
            CycleGeneration = 1
        };
        CapabilityProjection projection = new()
        {
            State = state,
            DesiredValue = new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = 31 },
            PendingValue = pendingWatts is { } pending
                ? new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = pending }
                : null
        };

        var capability = DeviceOverlayBridge.ToOverlayCapability(
            new DeviceCapabilityView(descriptor, projection, null),
            new HashSet<string>(StringComparer.Ordinal));

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
            Quality = HardwareStateQuality.Unknown,
            DescriptorGeneration = 4,
            CycleGeneration = 3
        };

        var capability = DeviceOverlayBridge.ToOverlayCapability(
            new DeviceCapabilityView(
                descriptor,
                new CapabilityProjection { State = state },
                null),
            new HashSet<string>(StringComparer.Ordinal));

        Assert.Equal(DescriptorStatus.Available, capability.Status);
        Assert.True(capability.CanInvoke);
        Assert.Equal("RUN", capability.TrailingText);
        Assert.Equal("Ready · action has no readback", capability.Description);
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
        Assert.Null(DeviceOverlayBridge.ControllerView(
            false,
            Status(ControllerManagementState.Off, null)));
    }

    [Fact]
    public void AnActiveTargetIsNamedInTheRowRatherThanOnlyMarkedOn()
    {
        var row = DeviceOverlayBridge.ControllerView(
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
        var row = DeviceOverlayBridge.ControllerView(
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
        var row = DeviceOverlayBridge.ControllerView(
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

    [Theory]
    [InlineData(null, ManagedControllerTarget.SteamDeckComposite)]
    [InlineData(ManagedControllerTarget.SteamDeckComposite, ManagedControllerTarget.Xbox360)]
    [InlineData(ManagedControllerTarget.Xbox360, ManagedControllerTarget.DualShock4)]
    [InlineData(ManagedControllerTarget.DualShock4, ManagedControllerTarget.SteamDeckComposite)]
    public void CyclingVisitsEveryTargetAndReturns(
        ManagedControllerTarget? current,
        ManagedControllerTarget expected)
    {
        Assert.Equal(expected, DeviceOverlayBridge.NextTarget(current));
    }

    [Fact]
    public void CyclingSkipsTargetsTheBackendCannotBuild()
    {
        // With one supported target the row is a no-op rather than a way to persist a selection
        // the backend refuses, which would leave controller management unavailable.
        ManagedControllerTarget[] supported = [ManagedControllerTarget.SteamDeckComposite];

        Assert.Equal(
            ManagedControllerTarget.SteamDeckComposite,
            DeviceOverlayBridge.NextTarget(ManagedControllerTarget.SteamDeckComposite, supported));
        Assert.Equal(
            ManagedControllerTarget.SteamDeckComposite,
            DeviceOverlayBridge.NextTarget(ManagedControllerTarget.DualShock4, supported));
        Assert.Equal(
            ManagedControllerTarget.DualShock4,
            DeviceOverlayBridge.NextTarget(
                ManagedControllerTarget.SteamDeckComposite,
                [ManagedControllerTarget.SteamDeckComposite, ManagedControllerTarget.DualShock4]));
    }

    [Fact]
    public void AHealthyCycleOffersNoRecoveryRow()
    {
        // A recovery control that is always present but almost always inert trains a user to ignore
        // it, which is the opposite of what it is for.
        Assert.Null(DeviceOverlayBridge.RecoveryView(DeviceCycleState.Active));
    }

    [Fact]
    public void AnAvailableRetryIsOfferedAndCarriesTheCycleState()
    {
        var row = DeviceOverlayBridge.RecoveryView(DeviceCycleState.Faulted);

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
            DeviceOverlayBridge.ControllerView(
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
            DeviceOverlayBridge.AutoTdpView(false, null),
            DeviceOverlayBridge.ControllerView(
                true,
                Status(ControllerManagementState.Idle, ManagedControllerTarget.Xbox360)),
            DeviceOverlayBridge.RecoveryView(DeviceCycleState.Faulted));

        Assert.Equal(
            [
                DeviceOverlaySection.PowerAndThermals,
                DeviceOverlaySection.ControllerAndMotion,
                DeviceOverlaySection.Diagnostics
            ],
            DeviceOverlaySectionPages.Build(snapshot).Select(entry => entry.Section));
    }

    [Fact]
    public void WithNoProfilesTheRowSaysWhereToMakeOneRatherThanVanishing()
    {
        // Unlike recovery, this row is always present: profiles are a feature a user has to find
        // before they can use it, and an absent row would read as the feature being missing.
        var row = DeviceOverlayBridge.ProfileView([], null);

        Assert.False(row.CanInvoke);
        Assert.Equal("NONE", row.TrailingText);
        Assert.Contains("Settings", row.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ASelectedProfileIsNamedAndMarkedActive()
    {
        var row = DeviceOverlayBridge.ProfileView(["docked", "handheld"], "handheld");

        Assert.Equal("HANDHELD", row.TrailingText);
        Assert.Equal(DescriptorStatus.Available, row.Status);
        Assert.True(row.CanInvoke);
    }

    [Fact]
    public void ASelectionNamingAProfileThatNoLongerExistsReadsAsNone()
    {
        // Which is what it now behaves as: the resolver finds no value under that name and falls
        // through to the power and global layers. Showing the stale name would claim otherwise.
        var row = DeviceOverlayBridge.ProfileView(["docked"], "deleted");

        Assert.Equal("NONE", row.TrailingText);
        Assert.Equal(DescriptorStatus.None, row.Status);
    }

    [Theory]
    [InlineData(null, "docked")]
    [InlineData("docked", "handheld")]
    [InlineData("handheld", null)]
    public void CyclingProfilesPassesThroughNoneSoDefaultsAreAlwaysReachable(
        string? selected,
        string? expected)
    {
        // None is a position in the cycle rather than a separate control, so the same button that
        // applied a profile can always get back to unmodified defaults.
        Assert.Equal(expected, DeviceOverlayBridge.NextProfile(["docked", "handheld"], selected));
    }

    [Fact]
    public void CyclingFromAnUnknownSelectionLandsOnTheFirstProfileRatherThanStalling()
    {
        Assert.Equal("docked", DeviceOverlayBridge.NextProfile(["docked", "handheld"], "deleted"));
    }

    [Fact]
    public void CyclingWithNoProfilesStaysAtNone()
    {
        Assert.Null(DeviceOverlayBridge.NextProfile([], "anything"));
    }

    private static DeviceAuthoredProfile Profile(string id, string name)
    {
        return new DeviceAuthoredProfile
        {
            ProfileId = id,
            Name = name,
            CapabilityId = "thermal.fan-curve"
        };
    }

    [Fact]
    public void NoAuthoredProfilesShowsNoRowAtAll()
    {
        // Unlike the hardware-profile row, which is always present because the user cannot create
        // one. These are created in Settings, so a row offering a choice between nothing would
        // invite a press that cannot do anything.
        Assert.Null(DeviceOverlayBridge.AuthoredProfileView([], null, false));
    }

    [Fact]
    public void AProfileChosenForEverythingSaysSo()
    {
        var row = DeviceOverlayBridge.AuthoredProfileView(
            [Profile("quiet", "Quiet"), Profile("loud", "Loud")],
            "quiet",
            false);

        Assert.Equal("QUIET", row?.TrailingText);
        Assert.Contains("everything", row?.Description);
    }

    [Fact]
    public void AProfileChosenForOneGameSaysThatInstead()
    {
        // The same word with very different consequences: this is the difference the user opens the
        // row to check mid-game.
        var row = DeviceOverlayBridge.AuthoredProfileView(
            [Profile("quiet", "Quiet")],
            "quiet",
            true);

        Assert.Contains("this game only", row?.Description);
    }

    [Fact]
    public void NothingChosenReadsAsNoneRatherThanEmpty()
    {
        var row = DeviceOverlayBridge.AuthoredProfileView(
            [Profile("quiet", "Quiet")],
            null,
            false);

        Assert.Equal("NONE", row?.TrailingText);
        Assert.Equal(DescriptorStatus.None, row?.Status);
    }

    [Fact]
    public void ASelectionNamingADeletedProfileIsSaidPlainlyNotShownAsNone()
    {
        // None is a state the user chose; this is not, and showing them identically hides a
        // selection that has silently stopped working.
        var row = DeviceOverlayBridge.AuthoredProfileView(
            [Profile("quiet", "Quiet")],
            "deleted",
            true);

        Assert.Equal("MISSING", row?.TrailingText);
        Assert.Equal(DescriptorStatus.Warning, row?.Status);
    }

    [Fact]
    public void TheRowOffersACycleOnceThereIsSomethingToCycleTo()
    {
        var row = DeviceOverlayBridge.AuthoredProfileView(
            [Profile("quiet", "Quiet")],
            "quiet",
            false);

        Assert.True(row?.CanInvoke);
    }

    [Fact]
    public void ADeletedSelectionStaysCyclableSoTheUserCanGetOutOfIt()
    {
        // Pressing it moves to a profile that does exist, which is the fastest way out of the state
        // for someone mid-game.
        var row = DeviceOverlayBridge.AuthoredProfileView(
            [Profile("quiet", "Quiet")],
            "deleted",
            false);

        Assert.True(row?.CanInvoke);
    }

    [Fact]
    public void CyclingWrapsThroughNoneSoAProfileCanBeTurnedOffWithoutSettings()
    {
        // The same wrap the hardware-profile row already offers: past the last profile is "none",
        // so a user can turn one off mid-game without opening Settings.
        Assert.Equal("loud", DeviceOverlayBridge.NextProfile(["quiet", "loud"], "quiet"));
        Assert.Null(DeviceOverlayBridge.NextProfile(["quiet", "loud"], "loud"));
        Assert.Equal("quiet", DeviceOverlayBridge.NextProfile(["quiet", "loud"], null));
    }

    [Fact]
    public void SwitchedOffReadsAsOffAndStaysToggleable()
    {
        var row = DeviceOverlayBridge.AutoTdpView(false, null);

        Assert.Equal("OFF", row.TrailingText);
        Assert.Equal(DescriptorStatus.None, row.Status);
        Assert.True(row.CanInvoke);
    }

    [Fact]
    public void SwitchedOnBeforeTheServiceReportsAnythingSaysSoRatherThanLookingIdle()
    {
        var row = DeviceOverlayBridge.AutoTdpView(true, null);

        Assert.Equal("ON", row.TrailingText);
        Assert.Contains("Starting", row.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ControllingShowsTheLimitItSettledOnAndHowFramesAreLanding()
    {
        var row = DeviceOverlayBridge.AutoTdpView(
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
        var row = DeviceOverlayBridge.AutoTdpView(
            true,
            new AutoTdpStatus(AutoTdpState.Paused, 22, null, null, null, "Paused by a manual change."));

        Assert.Equal(DescriptorStatus.Warning, row.Status);
        Assert.Equal("22 W", row.TrailingText);
    }

    [Fact]
    public void AMissingPrerequisiteReadsAsUnsupportedNotBroken()
    {
        var row = DeviceOverlayBridge.AutoTdpView(
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
        var row = DeviceOverlayBridge.AutoTdpView(
            true,
            new AutoTdpStatus(AutoTdpState.Idle, 15, null, null, null, "No application is rendering."));

        Assert.Equal(DescriptorStatus.Stale, row.Status);
    }

    [Fact]
    public async Task TheSimulatedSourceTogglesWithoutTouchingAnyDevice()
    {
        using SimulatedDeviceOverlaySource source = new();

        Assert.Equal("OFF", source.Snapshot().AutoTdp!.TrailingText);
        await source.ToggleAutoTdpAsync();

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
        Assert.Equal("TDP", DeviceOverlayBridge.ProjectManualTdp(primary, true).Title);
        var readback = DeviceOverlayBridge.ProjectManualTdp(boost, true);
        Assert.False(readback.Writable);
        Assert.False(readback.CanInvoke);
        Assert.Equal("30 W", readback.TrailingText);
        Assert.Equal(boost, DeviceOverlayBridge.ProjectManualTdp(boost, false));
    }
}
