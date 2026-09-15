using System.Text.Json;
using System.Text.RegularExpressions;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Input;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class NativeQamSemanticServicesTests
{
    [Fact]
    public void TdpProjectionUsesTheAuthoritativeDesiredObservedAndProgressState()
    {
        DeviceCapabilityView view = PrimaryLimitView("pl1");

        DeviceCoordinatorNativeQamTdpService.TdpProjection projection =
            DeviceCoordinatorNativeQamTdpService.Project([view]);

        Assert.True(projection.State.Available);
        Assert.Equal("pl1", projection.InstanceId);
        Assert.Equal(8, projection.State.MinimumWatts);
        Assert.Equal(30, projection.State.MaximumWatts);
        Assert.Equal(1, projection.State.StepWatts);
        Assert.Equal(18, projection.State.DesiredWatts);
        Assert.Equal(17, projection.State.ObservedWatts);
        Assert.Equal("applying", projection.State.Progress);
    }

    [Fact]
    public void TdpProjectionFailsClosedWhenPrimaryLimitIsAmbiguous()
    {
        DeviceCoordinatorNativeQamTdpService.TdpProjection projection =
            DeviceCoordinatorNativeQamTdpService.Project(
                [PrimaryLimitView("first"), PrimaryLimitView("second")]);

        Assert.False(projection.State.Available);
        Assert.Null(projection.State.MinimumWatts);
        Assert.Contains("ambiguous", projection.State.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PowerSlidersPublishIndependentObservedValuesAndTrackProfiles()
    {
        DeviceCapabilityView pl1 = PrimaryLimitView("pl1");
        DeviceCapabilityView pl2 = pl1 with
        {
            Descriptor = pl1.Descriptor with
            {
                CapabilityId = "vendor.boost",
                InstanceId = "pl2",
                Role = CapabilityRole.PowerSlowLimit,
                Maximum = 37,
            },
            Projection = pl1.Projection with
            {
                State = pl1.Projection.State with
                {
                    CapabilityId = "vendor.boost",
                    InstanceId = "pl2",
                    ObservedValue = CapabilityValue.Integer(30),
                },
            },
        };
        SteamPowerLimitState state = DeviceCoordinatorNativeQamTdpService.ProjectPowerLimits([pl1, pl2]);
        Assert.Equal(17, state.Sustained.ObservedWatts);
        Assert.Equal(30, state.Boost.ObservedWatts);
        Assert.Equal("vendor.boost", DeviceCoordinatorNativeQamTdpService.Project(
            [pl1, pl2], CapabilityRole.PowerSlowLimit).CapabilityId);

        pl1 = pl1 with
        {
            Descriptor = pl1.Descriptor with { Maximum = 37 },
            Projection = pl1.Projection with
            {
                State = pl1.Projection.State with { ObservedValue = CapabilityValue.Integer(37) },
            },
        };
        pl2 = pl2 with
        {
            Projection = pl2.Projection with
            {
                State = pl2.Projection.State with { ObservedValue = CapabilityValue.Integer(37) },
            },
        };
        state = DeviceCoordinatorNativeQamTdpService.ProjectPowerLimits([pl1, pl2]);
        Assert.Equal(37, state.Sustained.ObservedWatts);
        Assert.Equal(37, state.Boost.ObservedWatts);
        Assert.False(DeviceCoordinatorNativeQamTdpService.ProjectPowerLimits([pl1]).Boost.Available);
        Assert.False(DeviceCoordinatorNativeQamTdpService.ProjectPowerLimits([pl1, pl2, pl2]).Boost.Available);
    }

    [Fact]
    public async Task PowerSlidersRefuseBothWritesWhenDeviceIntegrationIsOff()
    {
        using DeviceCoordinatorNativeQamTdpService service = new(null);
        Assert.False(service.PowerLimit.Sustained.Available);
        Assert.False(service.PowerLimit.Boost.Available);
        Assert.False((await service.SetPrimaryLimitAsync(20, CancellationToken.None)).Succeeded);
        Assert.False((await service.SetBoostLimitAsync(25, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public void DeviceControlsProjectionUsesSemanticRolesAndIndependentLightingZones()
    {
        SteamDeviceControlsState state =
            DeviceCoordinatorNativeQamDeviceControlsService.Project(
            [
                IntegerDeviceView(
                    "vendor.charge",
                    CapabilityRole.ChargeLimit,
                    DisplayKey.ChargeLimit,
                    60,
                    100,
                    desired: 80,
                    observed: 79),
                IntegerDeviceView(
                    "vendor.brightness",
                    CapabilityRole.LightingBrightness,
                    DisplayKey.Brightness,
                    0,
                    100,
                    desired: 50,
                    observed: 45),
                ColorDeviceView("vendor.color", "right-ring", "Right ring", 0xFF8000),
                ColorDeviceView("vendor.color", "buttons", "Buttons", 0x0080FF),
            ]);

        Assert.True(state.ChargeLimit?.Available);
        Assert.Equal(60, state.ChargeLimit?.Minimum);
        Assert.Equal(80, state.ChargeLimit?.Desired);
        Assert.Equal(79, state.ChargeLimit?.Observed);
        Assert.True(state.LightingBrightness?.Available);
        Assert.Equal(2, state.LightingZones.Count);
        Assert.Contains(state.LightingZones, zone =>
            zone.Id == "right-ring"
            && zone.Label == "Right ring"
            && zone.ObservedColor == 0xFF8000);
        Assert.Contains(state.LightingZones, zone =>
            zone.Id == "buttons"
            && zone.Label == "Buttons"
            && zone.ObservedColor == 0x0080FF);
    }

    [Fact]
    public void DeviceControlsProjectionFailsClosedForAmbiguousChargeRole()
    {
        DeviceCapabilityView first = IntegerDeviceView(
            "first",
            CapabilityRole.ChargeLimit,
            DisplayKey.ChargeLimit,
            60,
            100,
            desired: 80,
            observed: 80);
        DeviceCapabilityView second = IntegerDeviceView(
            "second",
            CapabilityRole.ChargeLimit,
            DisplayKey.ChargeLimit,
            60,
            100,
            desired: 80,
            observed: 80);

        SteamDeviceControlsState state =
            DeviceCoordinatorNativeQamDeviceControlsService.Project([first, second]);

        Assert.False(state.ChargeLimit?.Available);
        Assert.Contains(
            "ambiguous",
            state.ChargeLimit?.StatusText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AmbiguousLightingZoneIsDroppedWithoutHidingIndependentControls()
    {
        SteamDeviceControlsState state =
            DeviceCoordinatorNativeQamDeviceControlsService.Project(
            [
                IntegerDeviceView(
                    "vendor.charge",
                    CapabilityRole.ChargeLimit,
                    DisplayKey.ChargeLimit,
                    60,
                    100,
                    desired: 80,
                    observed: 80),
                ColorDeviceView("first", "ring", "First ring", 0xFF0000),
                ColorDeviceView("second", "ring", "Second ring", 0x0000FF),
                ColorDeviceView("buttons", "button zone", "Buttons", 0x00FF00),
            ]);

        Assert.True(state.ChargeLimit?.Available);
        SteamLightingZoneState zone = Assert.Single(state.LightingZones);
        Assert.Equal("button zone", zone.Id);
    }

    [Fact]
    public void UnavailableControllerServicePublishesNoSelectableTargets()
    {
        using var service = new DeviceCoordinatorNativeQamControllerTargetService(null);

        SteamControllerTargetState state = service.Current;

        Assert.False(state.Available);
        Assert.Empty(state.Targets);
        Assert.Empty(state.SelectedTarget);
        Assert.Empty(state.ObservedTarget);
        // The reason is surfaced verbatim rather than replaced with a generic message, so a user
        // reading native QAM learns why controller management is off.
        Assert.Equal(
            DeviceCoordinatorNativeQamControllerTargetService.UnavailableDetail,
            state.StatusText);
    }

    [Fact]
    public void PerformanceProjectionPublishesExactAdapterCapabilitiesAndReadback()
    {
        PerformanceState state = PerformanceStateFixture(
            new HashSet<int> { 0, 1 },
            PerformanceCommandState.Idle);

        SteamFrameLimitState frame =
            PerformanceServiceNativeQamAdapter.ProjectFrameLimit(state, enabled: true);
        Assert.True(frame.Available);
        Assert.Equal(0, frame.MinimumFps);
        Assert.Equal(1000, frame.MaximumFps);
        Assert.Equal(45, frame.DesiredFps);
        Assert.Equal(44, frame.ObservedFps);
    }

    [Fact]
    public void PerformanceFaultIsPublishedOnlyForItsCommandedControl()
    {
        PerformanceCommandState command = new(
            7,
            "native-qam",
            "native-qam:4:5:6:7",
            PerformanceControl.FrameLimit,
            60,
            PerformanceCommandPhase.TimedOut,
            "RTSS readback timed out.");
        PerformanceState state = PerformanceStateFixture(new HashSet<int> { 0, 1 }, command);

        SteamFrameLimitState frame =
            PerformanceServiceNativeQamAdapter.ProjectFrameLimit(state, enabled: true);
        Assert.Equal("timed-out", frame.Progress);
        Assert.Equal("RTSS readback timed out.", frame.Fault);
    }

    /// <remarks>
    /// The injected row treats a progress term it does not know as a malformed state and renders
    /// nothing, so a phase missing from its vocabulary deletes the whole control. `Deferred` was
    /// missing, and adjusting the frame-limit slider while Steam had named a game whose executable
    /// Windows had not exposed took the row away mid-drag (Claw, 2026-09-04). The vocabulary is
    /// read out of the built asset rather than restated here: a copy would agree with itself while
    /// disagreeing with the script that actually runs.
    /// </remarks>
    [Fact]
    public void EveryCommandPhaseProjectsToAProgressTermTheInjectedRowAccepts()
    {
        IReadOnlyList<string> accepted = InjectedProgressVocabulary();

        foreach (PerformanceCommandPhase phase in Enum.GetValues<PerformanceCommandPhase>())
        {
            PerformanceState state = PerformanceStateFixture(
                new HashSet<int> { 0, 1 },
                new PerformanceCommandState(
                    1,
                    "native-qam",
                    "correlation",
                    PerformanceControl.FrameLimit,
                    60,
                    phase,
                    null));

            SteamFrameLimitState frame =
                PerformanceServiceNativeQamAdapter.ProjectFrameLimit(state, enabled: true);

            Assert.Contains(frame.Progress, accepted);
        }
    }

    /// <summary>The progress terms the injected frame-limit row will accept, from the built asset.</summary>
    private static IReadOnlyList<string> InjectedProgressVocabulary()
    {
        string source = SteamUiAssetCatalog.LoadNativeQamBootstrap();
        const string Marker = "validEnum(value.progress, [";
        int start = source.IndexOf(Marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "The injected asset no longer validates a progress vocabulary.");
        start += Marker.Length;
        int end = source.IndexOf("])", start, StringComparison.Ordinal);
        Assert.True(end > start, "The progress vocabulary in the injected asset is unterminated.");

        string[] terms = [.. Regex
            .Matches(source[start..end], "\"([a-z-]+)\"")
            .Select(match => match.Groups[1].Value)];
        Assert.NotEmpty(terms);
        return terms;
    }

    private static PerformanceState PerformanceStateFixture(
        IReadOnlySet<int> overlayLevels,
        PerformanceCommandState command) => new(
            new RtssProbe(
                RtssAvailability.Ready,
                "7.3.6",
                "RTSS.exe",
                3,
                new RtssCapabilities(0, 1000, overlayLevels, true, true),
                null),
            new PerformanceApplicationTarget("steam:123", 123, "game.exe", 123),
            true,
            PerformancePolicyLayer.Application,
            PerformancePolicyLayer.Global,
            new PerformanceValues(45, 1),
            new PerformanceValues(44, 0),
            PerformanceReadbackQuality.Verified,
            PerformanceReadbackQuality.Verified,
            DateTimeOffset.UtcNow,
            command);

    private static DeviceCapabilityView PrimaryLimitView(string instanceId)
    {
        CapabilityDescriptor descriptor = new()
        {
            CapabilityId = "power.primary-limit",
            InstanceId = instanceId,
            Role = CapabilityRole.PowerSustainedLimit,
            ValueKind = CapabilityValueKind.Integer,
            Display = new CapabilityDisplay { Key = DisplayKey.SustainedPowerLimit },
            SupportsRead = true,
            SupportsWrite = true,
            Minimum = 8,
            Maximum = 30,
            Step = 1,
            Unit = CapabilityUnit.Watt,
            Persistence = CapabilityPersistence.Volatile,
        };
        CapabilityState state = new()
        {
            CapabilityId = descriptor.CapabilityId,
            InstanceId = descriptor.InstanceId,
            Available = true,
            ObservedValue = CapabilityValue.Integer(17),
            Quality = HardwareStateQuality.Verified,
            ObservedAt = DateTimeOffset.UtcNow,
            DescriptorGeneration = 4,
            CycleGeneration = 3,
        };
        return new DeviceCapabilityView(
            descriptor,
            new CapabilityProjection
            {
                State = state,
                DesiredValue = CapabilityValue.Integer(18),
                DesiredSource = DeviceDesiredValueSource.ApplicationOverride,
                PendingValue = CapabilityValue.Integer(19),
                Progress = CommandProgress.Pending,
            },
            null);
    }

    private static DeviceCapabilityView IntegerDeviceView(
        string capabilityId,
        CapabilityRole role,
        DisplayKey display,
        int minimum,
        int maximum,
        int desired,
        int observed)
    {
        CapabilityDescriptor descriptor = new()
        {
            CapabilityId = capabilityId,
            Role = role,
            ValueKind = CapabilityValueKind.Integer,
            Display = new CapabilityDisplay { Key = display },
            SupportsRead = true,
            SupportsWrite = true,
            Minimum = minimum,
            Maximum = maximum,
            Step = 1,
            Unit = CapabilityUnit.Percent,
            Persistence = CapabilityPersistence.DevicePersistent,
        };
        return DeviceView(descriptor, CapabilityValue.Integer(desired), CapabilityValue.Integer(observed));
    }

    private static DeviceCapabilityView ColorDeviceView(
        string capabilityId,
        string instanceId,
        string label,
        int color)
    {
        CapabilityDescriptor descriptor = new()
        {
            CapabilityId = capabilityId,
            InstanceId = instanceId,
            Role = CapabilityRole.LightingZoneColor,
            ValueKind = CapabilityValueKind.Color,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = label },
            SupportsRead = true,
            SupportsWrite = true,
            Persistence = CapabilityPersistence.DevicePersistent,
        };
        CapabilityValue value = new()
        {
            Kind = CapabilityValueKind.Color,
            ColorValue = color,
        };
        return DeviceView(descriptor, value, value);
    }

    private static DeviceCapabilityView DeviceView(
        CapabilityDescriptor descriptor,
        CapabilityValue desired,
        CapabilityValue observed)
    {
        CapabilityState state = new()
        {
            CapabilityId = descriptor.CapabilityId,
            InstanceId = descriptor.InstanceId,
            Available = true,
            ObservedValue = observed,
            Quality = HardwareStateQuality.Verified,
            ObservedAt = DateTimeOffset.UtcNow,
            DescriptorGeneration = 4,
            CycleGeneration = 3,
        };
        return new DeviceCapabilityView(
            descriptor,
            new CapabilityProjection
            {
                State = state,
                DesiredValue = desired,
                DesiredSource = DeviceDesiredValueSource.GlobalDefault,
                Progress = CommandProgress.Idle,
            },
            null);
    }

    // AutoTDP as Steam's own quick-access menu shows it.
    // The switch is deliberately more than a boolean. A user watching the power limit move on its own
    // has to be able to tell control from a fault, so these pin what the menu says in each state rather
    // than only whether the setting is on.
    [Fact]
    public void WithNoPowerLimitTheSwitchIsNotOfferedAtAll()
    {
        // Better absent than present and silently ineffective: there is nothing for AutoTDP to
        // drive, so offering the switch would be a promise the device cannot keep.
        SteamAutoTdpState state = Project(enabled: true, status: null, powerLimitAvailable: false);

        Assert.False(state.Available);
        Assert.Contains("No primary power limit", state.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchedOnBeforeTheServiceReportsAnythingSaysItIsStarting()
    {
        SteamAutoTdpState state = Project(enabled: true, status: null);

        Assert.True(state.Available);
        Assert.True(state.Enabled);
        Assert.False(state.Controlling);
        Assert.Equal("applying", state.Progress);
        Assert.Equal("Starting.", state.StatusText);
    }

    [Fact]
    public void SwitchedOffIsQuietRatherThanReportingAnythingToExplain()
    {
        SteamAutoTdpState state = Project(enabled: false, status: null);

        Assert.True(state.Available);
        Assert.False(state.Enabled);
        Assert.Empty(state.Progress);
        Assert.Empty(state.StatusText);
    }

    [Fact]
    public void ControllingCarriesTheLimitItSettledOn()
    {
        SteamAutoTdpState state = Project(
            enabled: true,
            new AutoTdpStatus(AutoTdpState.Controlling, 17, 14.2, 16.6, "steam:70", "sustained-miss"));

        Assert.True(state.Controlling);
        Assert.Equal(17, state.Watts);
        Assert.Equal("completed", state.Progress);
    }

    [Fact]
    public void APausedSwitchStaysOperableSoTheUserCanTurnItOff()
    {
        // Paused is a state the user caused by moving the slider. Locking the switch would leave
        // them unable to act on what they are being told.
        SteamAutoTdpState state = Project(
            enabled: true,
            new AutoTdpStatus(AutoTdpState.Paused, 22, null, null, null, "Paused by a manual change."));

        Assert.True(state.Available);
        Assert.False(state.Controlling);
        Assert.Equal("Paused by a manual change.", state.StatusText);
    }

    [Fact]
    public void UnavailableIsTheOneStateThatLocksTheSwitch()
    {
        // It means AutoTDP cannot run on this device however the setting is left, so operating the
        // switch could not change anything.
        SteamAutoTdpState state = Project(
            enabled: true,
            new AutoTdpStatus(
                AutoTdpState.Unavailable,
                null,
                null,
                null,
                null,
                "No primary power limit is available."));

        Assert.False(state.Available);
        Assert.Equal("failed", state.Progress);
    }

    [Fact]
    public void WaitingForAGameIsNotReportedAsControlling()
    {
        SteamAutoTdpState state = Project(
            enabled: true,
            new AutoTdpStatus(AutoTdpState.Idle, 15, null, null, null, "No application is rendering."));

        Assert.True(state.Available);
        Assert.False(state.Controlling);
        Assert.Empty(state.Progress);
    }

    [Fact]
    public void TheStoredSettingIsReportedEvenWhileTheSwitchIsLocked()
    {
        // The switch shows the setting, not the outcome. A user who turned it on and hit an
        // unsupported device should still see their own choice reflected back.
        SteamAutoTdpState state = Project(enabled: true, status: null, powerLimitAvailable: false);

        Assert.True(state.Enabled);
    }

    [Fact]
    public async Task TheUnavailableServiceRefusesRatherThanSilentlyAccepting()
    {
        // No device platform in this session: the service is constructed without a coordinator,
        // which is how a session with device integration off projects this row.
        using DeviceCoordinatorNativeQamAutoTdpService service = new(null, null);

        Assert.False(service.Current.Available);
        SteamUiCommandResult result = await service.SetEnabledAsync(true, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(service.Current.StatusText, result.Error);
    }

    private static SteamAutoTdpState Project(
        bool enabled,
        AutoTdpStatus? status,
        bool powerLimitAvailable = true) =>
        DeviceCoordinatorNativeQamAutoTdpService.Project(enabled, status, powerLimitAvailable);

    // The controller target Steam's native quick-access menu shows is WSGM's own setting.
    // These pin the projection, which is the whole of the menu's truthfulness: which targets it offers,
    // which one it says is selected, which one it says is actually up, and whether a running game has
    // to be restarted before a change reaches it.
    [Fact]
    public void ManagementSwitchedOffOffersNothingAndSaysWhy()
    {
        SteamControllerTargetState state = ProjectTarget(
            enabled: false,
            Status(ControllerManagementState.Off, null, "Controller management is off."));

        Assert.False(state.Available);
        Assert.Empty(state.Targets);
        Assert.Empty(state.SelectedTarget);
        // Surfaced verbatim rather than replaced with a generic message, so a user reading native
        // QAM learns why the control is not there.
        Assert.Equal("Controller management is off.", state.StatusText);
    }

    [Fact]
    public void EveryTargetTheBackendCanBuildIsOfferedOnceManagementRuns()
    {
        SteamControllerTargetState state = ProjectTarget(
            enabled: true,
            Status(ControllerManagementState.Idle, ManagedControllerTarget.Xbox360),
            supportedTargets:
            [
                ManagedControllerTarget.SteamDeckComposite,
                ManagedControllerTarget.Xbox360,
                ManagedControllerTarget.DualShock4,
            ]);

        Assert.True(state.Available);
        Assert.Collection(
            state.Targets,
            target => Assert.Equal(nameof(ManagedControllerTarget.SteamDeckComposite), target.Id),
            target => Assert.Equal(nameof(ManagedControllerTarget.Xbox360), target.Id),
            target => Assert.Equal(nameof(ManagedControllerTarget.DualShock4), target.Id));
        Assert.All(state.Targets, target => Assert.True(target.Available));
    }

    [Fact]
    public void ATargetTheBackendCannotBuildIsNotOffered()
    {
        // Offering one is worse than offering fewer: the selection persists, target creation is
        // refused, and controller management reports itself unavailable until the user finds the
        // setting again. The production backend supports only the Deck composite today.
        SteamControllerTargetState state = ProjectTarget(
            enabled: true,
            Status(ControllerManagementState.Idle, ManagedControllerTarget.SteamDeckComposite),
            supportedTargets: [ManagedControllerTarget.SteamDeckComposite]);

        Assert.True(state.Available);
        Assert.Collection(
            state.Targets,
            target => Assert.Equal(nameof(ManagedControllerTarget.SteamDeckComposite), target.Id));
    }

    [Fact]
    public void ATargetChosenButNotYetUpIsNotReportedAsObserved()
    {
        // Idle means the selection is stored and nothing is present for it. Echoing the selection
        // back as observed would make a target that never came up look like it had.
        SteamControllerTargetState state = ProjectTarget(
            enabled: true,
            Status(ControllerManagementState.Idle, ManagedControllerTarget.DualShock4));

        Assert.Equal(nameof(ManagedControllerTarget.DualShock4), state.SelectedTarget);
        Assert.Empty(state.ObservedTarget);
    }

    [Fact]
    public void AnActiveTargetIsReportedAsBothSelectedAndObserved()
    {
        SteamControllerTargetState state = ProjectTarget(
            enabled: true,
            Status(ControllerManagementState.Active, ManagedControllerTarget.SteamDeckComposite));

        Assert.Equal(nameof(ManagedControllerTarget.SteamDeckComposite), state.SelectedTarget);
        Assert.Equal(nameof(ManagedControllerTarget.SteamDeckComposite), state.ObservedTarget);
        Assert.Equal("completed", state.Progress);
    }

    [Fact]
    public void AFaultedManagerIsUnavailableRatherThanQuietlySelectable()
    {
        SteamControllerTargetState state = ProjectTarget(
            enabled: true,
            Status(
                ControllerManagementState.Faulted,
                ManagedControllerTarget.Xbox360,
                "The virtual controller could not be attached."));

        Assert.False(state.Available);
        Assert.Equal("failed", state.Progress);
        Assert.Equal("The virtual controller could not be attached.", state.StatusText);
    }

    [Fact]
    public void ARunningGameIsToldItNeedsARestart()
    {
        // A game holds the target it launched with, so a change reaches it only next launch. Saying
        // so is the difference between a control that looks broken and one the user understands.
        SteamControllerTargetState state = ProjectTarget(
            enabled: true,
            Status(
                ControllerManagementState.Active,
                ManagedControllerTarget.Xbox360,
                applicationId: "steam:70"));

        Assert.True(state.ApplicationRestartRequired);
    }

    [Fact]
    public void NoRunningGameNeedsNoRestart()
    {
        SteamControllerTargetState state = ProjectTarget(
            enabled: true,
            Status(ControllerManagementState.Active, ManagedControllerTarget.Xbox360));

        Assert.False(state.ApplicationRestartRequired);
    }

    [Fact]
    public void AMissingDevicePackageIsExplainedRatherThanLeftBlank()
    {
        // Controller management runs without a plugin, but with nothing capturing the physical
        // controller the result is a target that never moves. That is worth saying.
        SteamControllerTargetState state = ProjectTarget(
            enabled: true,
            Status(ControllerManagementState.Idle, ManagedControllerTarget.Xbox360, detail: string.Empty),
            packageInstalled: false);

        Assert.True(state.Available);
        Assert.Contains("No device package", state.StatusText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Xbox360", true)]
    [InlineData("SteamDeckComposite", true)]
    [InlineData("DualShock4", true)]
    [InlineData("xbox360", false)]
    [InlineData("", false)]
    [InlineData("3", false)]
    [InlineData("NotATarget", false)]
    public void OnlyTheExactNamesTheMenuWasGivenParse(string candidate, bool expected)
    {
        // Case-sensitive on purpose: these names come from the projection, so anything else is a
        // caller defect rather than user input to be forgiving about. "3" is rejected even though
        // Enum.TryParse accepts numeric text, which would otherwise let an out-of-range value in.
        Assert.Equal(
            expected,
            DeviceCoordinatorNativeQamControllerTargetService.TryParseTarget(candidate, out _));
    }

    [Fact]
    public void EveryProjectedTargetSurvivesTheHostPayloadBoundary()
    {
        foreach (ManagedControllerTarget target in Enum.GetValues<ManagedControllerTarget>())
        {
            using JsonDocument payload = JsonDocument.Parse($$"""{"target":"{{target}}"}""");

            Assert.True(SteamUiPayload.TryReadTarget(payload.RootElement, out string parsed));
            Assert.Equal(target.ToString(), parsed);
        }
    }

    [Fact]
    public void TheUnavailableServiceStaysTheProjectionForASessionWithNoDevicePlatform()
    {
        using DeviceCoordinatorNativeQamControllerTargetService service = new(null);

        Assert.False(service.Current.Available);
        Assert.Empty(service.Current.Targets);
    }

    private static SteamControllerTargetState ProjectTarget(
        bool enabled,
        ControllerManagerStatus status,
        bool packageInstalled = true,
        IReadOnlyList<ManagedControllerTarget>? supportedTargets = null) =>
        DeviceCoordinatorNativeQamControllerTargetService.Project(
            enabled,
            status,
            packageInstalled,
            supportedTargets ?? Enum.GetValues<ManagedControllerTarget>());

    private static ControllerManagerStatus Status(
        ControllerManagementState state,
        ManagedControllerTarget? target,
        string detail = "",
        string? applicationId = null) =>
        new(
            state,
            target,
            ControllerTargetSource.GlobalDefault,
            applicationId,
            UiInputSource.SdlWithSteamLease,
            detail);
}
