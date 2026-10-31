using System.Text.Json;
using System.Text.RegularExpressions;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;
using static WSGM.Tests.Builders.ControllerBuilders;

namespace WSGM.Tests.Shell;

public sealed partial class SteamNativeSettingsServiceTests
{
    [Fact]
    public void TdpProjectionUsesTheAuthoritativeDesiredObservedAndProgressState()
    {
        var view = PrimaryLimitView("pl1");

        var projection = IntegerRangeProjection.PowerLimit([view]);

        Assert.True(projection.Available);
        Assert.Equal("pl1", projection.InstanceId);
        Assert.Equal(8, projection.Minimum);
        Assert.Equal(30, projection.Maximum);
        Assert.Equal(1, projection.Step);
        Assert.Equal(18, projection.Desired);
        Assert.Equal(17, projection.Observed);
        Assert.Equal("applying", projection.Progress);
    }

    [Fact]
    public void TdpProjectionFailsClosedWhenPrimaryLimitIsAmbiguous()
    {
        var projection = IntegerRangeProjection.PowerLimit([PrimaryLimitView("first"), PrimaryLimitView("second")]);

        Assert.False(projection.Available);
        Assert.Null(projection.Minimum);
        Assert.Contains("ambiguous", projection.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PowerSlidersPublishIndependentObservedValuesAndTrackProfiles()
    {
        var pl1 = PrimaryLimitView("pl1");
        var pl2 = pl1 with
        {
            Descriptor = pl1.Descriptor with
            {
                CapabilityId = "vendor.boost",
                InstanceId = "pl2",
                Role = CapabilityRole.PowerSlowLimit,
                Maximum = 37
            },
            Projection = pl1.Projection with
            {
                State = pl1.Projection.State with
                {
                    CapabilityId = "vendor.boost",
                    InstanceId = "pl2",
                    ObservedValue = CapabilityValue.Integer(30)
                }
            }
        };
        Assert.Equal(17, IntegerRangeProjection.PowerLimit([pl1, pl2]).Observed);
        Assert.Equal(30, IntegerRangeProjection.PowerLimit([pl1, pl2], CapabilityRole.PowerSlowLimit).Observed);
        Assert.Equal("vendor.boost", IntegerRangeProjection.PowerLimit(
            [pl1, pl2], CapabilityRole.PowerSlowLimit).CapabilityId);

        pl1 = pl1 with
        {
            Descriptor = pl1.Descriptor with { Maximum = 37 },
            Projection = pl1.Projection with
            {
                State = pl1.Projection.State with { ObservedValue = CapabilityValue.Integer(37) }
            }
        };
        pl2 = pl2 with
        {
            Projection = pl2.Projection with
            {
                State = pl2.Projection.State with { ObservedValue = CapabilityValue.Integer(37) }
            }
        };
        Assert.Equal(37, IntegerRangeProjection.PowerLimit([pl1, pl2]).Observed);
        Assert.Equal(37, IntegerRangeProjection.PowerLimit([pl1, pl2], CapabilityRole.PowerSlowLimit).Observed);
        Assert.False(IntegerRangeProjection.PowerLimit([pl1], CapabilityRole.PowerSlowLimit).Available);
        Assert.False(IntegerRangeProjection.PowerLimit([pl1, pl2, pl2], CapabilityRole.PowerSlowLimit).Available);
    }

    [Fact]
    public async Task PowerSlidersRefuseBothWritesWhenDeviceIntegrationIsOff()
    {
        await using var rig = await ProducerRig.CreateAsync();
        var state = await rig.Producer.ReadQuickAccessAsync();
        Assert.DoesNotContain(state!.Sections, section => section.Id is "powerLimit" or "autoTdp");
        Assert.False((await rig.Producer.SetPowerLimitAsync(CapabilityRole.PowerSustainedLimit, 20,
            CancellationToken.None)).Succeeded);
        Assert.False((await rig.Producer.SetPowerLimitAsync(CapabilityRole.PowerSlowLimit, 25,
            CancellationToken.None)).Succeeded);
        Assert.False((await rig.Producer.SetAsync("power.auto-tdp", Json("true"), CancellationToken.None))
            .Succeeded);
    }

    [Fact]
    public void DeviceControlsProjectionUsesSemanticRolesAndIndependentLightingZones()
    {
        IReadOnlyList<DeviceCapabilityView> views =
        [
            IntegerDeviceView(
                "vendor.charge",
                CapabilityRole.ChargeLimit,
                DisplayKey.ChargeLimit,
                60,
                100,
                80,
                79),
            IntegerDeviceView(
                "vendor.brightness",
                CapabilityRole.LightingBrightness,
                DisplayKey.Brightness,
                0,
                100,
                50,
                45),
            ColorDeviceView("vendor.color", "right-ring", "Right ring", 0xFF8000),
            ColorDeviceView("vendor.color", "buttons", "Buttons", 0x0080FF)
        ];
        var rows = SteamNativeSettingsService.DeviceQuickAccessRows(views, FanCurvePresetTable.HandheldCompanion);

        var charge = Assert.Single(rows, row => row.Section == "charging").Row;
        Assert.Equal(SteamSettingsRowKind.Range, charge.Kind);
        Assert.Equal("Battery charge limit", charge.Label);
        Assert.Equal("percent", charge.Icon);
        Assert.Equal(60, charge.Minimum);
        Assert.Equal(79, charge.Number);
        Assert.Equal("%", charge.Suffix);
        Assert.Equal("Limit 79%", charge.Summary);
        var brightness = Assert.Single(rows, row => row.Section == "lighting").Row;
        Assert.Equal("Lighting brightness", brightness.Label);
        Assert.Equal("45%", brightness.Summary);
        // The zones are the inline editor's, so they are not descriptor rows as well.
        Assert.DoesNotContain(rows, row => row.Section == "deviceDescriptors");
        var zones = DeviceCoordinatorLightingZonesService.Project(views, FanCurvePresetTable.HandheldCompanion);
        Assert.Equal(2, zones.Count);
        Assert.Contains(zones, zone => zone is { Id: "right-ring", Label: "Right ring", ObservedColor: 0xFF8000 });
        Assert.Contains(zones, zone => zone is { Id: "buttons", Label: "Buttons", ObservedColor: 0x0080FF });
    }

    [Fact]
    public void UnknownProcessorRangeRemainsWritableWithoutAnInventedMinimumValue()
    {
        var view = IntegerDeviceView("processor.cpu-curve-all", CapabilityRole.GenericRange,
            DisplayKey.CpuTemperature, -24, 12, 0, 0);
        view = view with { Projection = view.Projection with
        {
            State = view.Projection.State with { ObservedValue = null, Quality = HardwareStateQuality.Unknown },
            DesiredValue = null
        } };
        var (section, row, _) = Assert.Single(SteamNativeSettingsService.DeviceQuickAccessRows([view],
            FanCurvePresetTable.HandheldCompanion));
        Assert.Equal("deviceDescriptors", section);
        Assert.Null(row.Number);
        Assert.False(row.Disabled);
    }

    [Fact]
    public void DeviceControlsProjectionFailsClosedForAmbiguousChargeRole()
    {
        var first = IntegerDeviceView(
            "first",
            CapabilityRole.ChargeLimit,
            DisplayKey.ChargeLimit,
            60,
            100,
            80,
            80);
        var second = IntegerDeviceView(
            "second",
            CapabilityRole.ChargeLimit,
            DisplayKey.ChargeLimit,
            60,
            100,
            80,
            80);

        var rows = SteamNativeSettingsService.DeviceQuickAccessRows([first, second],
            FanCurvePresetTable.HandheldCompanion);

        // No dedicated slider for an ambiguous role; each control stays reachable as its own descriptor row.
        Assert.DoesNotContain(rows, row => row.Section == "charging");
        Assert.Contains("ambiguous", IntegerRangeProjection.Percent([first, second], CapabilityRole.ChargeLimit)
            .StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, rows.Count(row => row.Section == "deviceDescriptors"));
    }

    [Fact]
    public void DescriptorControlsKeepFanInstancesChoicesCurvesAndProfileOverrides()
    {
        var fan = IntegerDeviceView("vendor.fan", CapabilityRole.FanDuty, DisplayKey.FanSpeed,
            15, 100, 40, 35);
        fan = fan with
        {
            Descriptor = fan.Descriptor with { InstanceId = "right" },
            Projection = fan.Projection with { DesiredSource = ProfileSource.Game, Progress = CommandProgress.Pending }
        };
        CapabilityDescriptor protection = new()
        {
            CapabilityId = "vendor.protection", Role = CapabilityRole.ChargeProtectionMode,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "Charge protection" },
            ValueKind = CapabilityValueKind.Choice, SupportsWrite = true, Persistence = CapabilityPersistence.Volatile,
            Choices = [new CapabilityChoice("full", new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "Full" }),
                new CapabilityChoice("balanced", new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "Balanced" })]
        };
        var curveDescriptor = fan.Descriptor with
        {
            CapabilityId = "vendor.curve", Role = CapabilityRole.FanCurve, ValueKind = CapabilityValueKind.Curve
        };
        var curve = CapabilityValue.Curve([new CurvePoint(40, 20), new CurvePoint(80, 90)]);
        var rows = SteamNativeSettingsService.DeviceQuickAccessRows([
            fan, DeviceView(protection, CapabilityValue.Choice("balanced"), CapabilityValue.Choice("full")),
            DeviceView(curveDescriptor, curve, curve)
        ], FanCurvePresetTable.HandheldCompanion).Select(entry => entry.Row).ToArray();
        var fanRow = Assert.Single(rows, row => row.Kind == SteamSettingsRowKind.Range);
        Assert.Contains("right", fanRow.Label, StringComparison.Ordinal);
        Assert.Equal(15, fanRow.Minimum);
        Assert.Equal(35, fanRow.Number);
        Assert.True(fanRow.Disabled);
        Assert.True(fanRow.Accent);
        var choiceRow = Assert.Single(rows, row => row.Kind == SteamSettingsRowKind.Choice);
        Assert.Equal("full", choiceRow.Text);
        Assert.Equal(["full", "balanced"], choiceRow.Choices!.Select(choice => choice.Value));
        var curveRow = Assert.Single(rows, row => row.Kind == SteamSettingsRowKind.Curve);
        Assert.Equal([40, 80], curveRow.Curve!.Select(point => point.Input));
        Assert.Equal(3, curveRow.CurvePresets!.Count);
        Assert.All(curveRow.CurvePresets, preset => Assert.Equal([40, 80], preset.Points.Select(point => point.Input)));
    }

    [Theory]
    [InlineData("41", true)]
    [InlineData("101", false)]
    [InlineData("\"41\"", false)]
    [InlineData("null", false)]
    public void DescriptorValueParsingUsesCurrentBoundsAndRejectsDifferentWireKinds(string payload, bool expected)
    {
        var row = CapabilityRowProjection.ToOverlayCapability(IntegerDeviceView("fan", CapabilityRole.FanDuty,
            DisplayKey.FanSpeed, 15, 100, 40, 35), new HashSet<string>(), FanCurvePresetTable.HandheldCompanion);
        using var document = JsonDocument.Parse(payload);
        Assert.Equal(expected, SteamCapabilityRows.TryValue(row, document.RootElement, out _));
    }

    [Fact]
    public void AmbiguousLightingZoneIsDroppedWithoutHidingIndependentControls()
    {
        IReadOnlyList<DeviceCapabilityView> views =
        [
            IntegerDeviceView(
                "vendor.charge",
                CapabilityRole.ChargeLimit,
                DisplayKey.ChargeLimit,
                60,
                100,
                80,
                80),
            ColorDeviceView("first", "ring", "First ring", 0xFF0000),
            ColorDeviceView("second", "ring", "Second ring", 0x0000FF),
            ColorDeviceView("buttons", "button zone", "Buttons", 0x00FF00)
        ];

        Assert.Contains(SteamNativeSettingsService.DeviceQuickAccessRows(views, FanCurvePresetTable.HandheldCompanion),
            row => row.Section == "charging");
        var zone = Assert.Single(DeviceCoordinatorLightingZonesService.Project(views,
            FanCurvePresetTable.HandheldCompanion));
        Assert.Equal("button zone", zone.Id);
    }

    [Fact]
    public async Task UnavailableControllerServicePublishesNoSelectableTargets()
    {
        await using var rig = await ProducerRig.CreateAsync();

        var state = await rig.Producer.ReadQuickAccessAsync();

        Assert.DoesNotContain(state!.Sections, section => section.Id == "controllerTarget");
        var result = await rig.Producer.SetAsync("controller.target", Json("\"Xbox360\""), CancellationToken.None);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void PerformanceProjectionPublishesExactAdapterCapabilitiesAndReadback()
    {
        var state = PerformanceStateFixture(
            new HashSet<int> { 0, 1 },
            PerformanceCommandState.Idle);

        var frame =
            PerformanceServiceNativeQamAdapter.ProjectFrameLimit(state, true);
        Assert.True(frame.Available);
        Assert.Equal(0, frame.MinimumFps);
        // The overlay's ceiling, not RTSS's 1000: both sliders offer the same caps.
        Assert.Equal(PerformanceProjection.MaximumFrameLimit, frame.MaximumFps);
        Assert.Equal(45, frame.DesiredFps);
        Assert.Equal(44, frame.ObservedFps);
    }

    [Fact]
    public void PerformanceFaultIsPublishedOnlyForItsCommandedControl()
    {
        PerformanceCommandState command = new(
            7,
            "native-qam",
            "steam-ui:4:5:6:7",
            PerformanceControl.FrameLimit,
            60,
            PerformanceCommandPhase.Failed,
            "RTSS did not finish within the bounded command timeout.");
        var state = PerformanceStateFixture(new HashSet<int> { 0, 1 }, command);

        var frame =
            PerformanceServiceNativeQamAdapter.ProjectFrameLimit(state, true);
        Assert.Equal("failed", frame.Progress);
        Assert.Equal("RTSS did not finish within the bounded command timeout.", frame.Fault);
    }

    /// <remarks>
    ///     The injected row treats a progress term it does not know as a malformed state and renders
    ///     nothing, so a phase missing from its vocabulary deletes the whole control. `Deferred` was
    ///     missing, and adjusting the frame-limit slider while Steam had named a game whose executable
    ///     Windows had not exposed took the row away mid-drag (Claw, 2026-09-04). The vocabulary is
    ///     read out of the built asset rather than restated here: a copy would agree with itself while
    ///     disagreeing with the script that actually runs.
    /// </remarks>
    [Fact]
    public void EveryCommandPhaseProjectsToAProgressTermTheInjectedRowAccepts()
    {
        var accepted = InjectedProgressVocabulary();

        foreach (var phase in Enum.GetValues<PerformanceCommandPhase>())
        {
            var state = PerformanceStateFixture(
                new HashSet<int> { 0, 1 },
                new PerformanceCommandState(
                    1,
                    "native-qam",
                    "correlation",
                    PerformanceControl.FrameLimit,
                    60,
                    phase,
                    null));

            var frame =
                PerformanceServiceNativeQamAdapter.ProjectFrameLimit(state, true);

            Assert.Contains(frame.Progress, accepted);
        }
    }

    /// <summary>The progress terms the injected frame-limit row will accept, from the built asset.</summary>
    private static string[] InjectedProgressVocabulary()
    {
        var source = SteamUiAssetCatalog.LoadNativeQamBootstrap().Source;
        const string Marker = "validEnum(value.progress, [";
        var start = source.IndexOf(Marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "The injected asset no longer validates a progress vocabulary.");
        start += Marker.Length;
        var end = source.IndexOf("])", start, StringComparison.Ordinal);
        Assert.True(end > start, "The progress vocabulary in the injected asset is unterminated.");

        string[] terms =
        [
            .. QuotedTerm()
                .Matches(source[start..end])
                .Select(match => match.Groups[1].Value)
        ];
        Assert.NotEmpty(terms);
        return terms;
    }

    [GeneratedRegex("\"([a-z-]+)\"")]
    private static partial Regex QuotedTerm();

    private static PerformanceState PerformanceStateFixture(
        IReadOnlySet<int> overlayLevels,
        PerformanceCommandState command)
    {
        return new PerformanceState(
            new RtssProbe(
                RtssAvailability.Ready,
                "7.3.6",
                "RTSS.exe",
                3,
                new RtssCapabilities(0, 1000, overlayLevels),
                null),
            new PerformanceApplicationTarget("steam:123", 123, "game.exe", 123),
            true,
            ProfileSource.Game,
            ProfileSource.Global,
            new PerformanceValues(45, 1),
            new PerformanceValues(44, 0),
            DateTimeOffset.UtcNow,
            command);
    }

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
            Persistence = CapabilityPersistence.Volatile
        };
        CapabilityState state = new()
        {
            CapabilityId = descriptor.CapabilityId,
            InstanceId = descriptor.InstanceId,
            Available = true,
            ObservedValue = CapabilityValue.Integer(17),
            Quality = HardwareStateQuality.Verified,
            ObservedAt = DateTimeOffset.UtcNow
        };
        return new DeviceCapabilityView(
            descriptor,
            new CapabilityProjection
            {
                State = state,
                DesiredValue = CapabilityValue.Integer(18),
                DesiredSource = ProfileSource.Game,
                PendingValue = CapabilityValue.Integer(19),
                Progress = CommandProgress.Pending
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
            Persistence = CapabilityPersistence.DevicePersistent
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
            Persistence = CapabilityPersistence.DevicePersistent
        };
        CapabilityValue value = new()
        {
            Kind = CapabilityValueKind.Color,
            ColorValue = color
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
            ObservedAt = DateTimeOffset.UtcNow
        };
        return new DeviceCapabilityView(
            descriptor,
            new CapabilityProjection
            {
                State = state,
                DesiredValue = desired,
                DesiredSource = ProfileSource.Global,
                Progress = CommandProgress.Idle
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
        var state = Project(true, null, false);

        Assert.False(state.Available);
        Assert.Contains("No primary power limit", state.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchedOnBeforeTheServiceReportsAnythingSaysItIsStarting()
    {
        var state = Project(true, null);

        Assert.True(state.Available);
        Assert.True(state.Enabled);
        Assert.False(state.Controlling);
        Assert.Equal("applying", state.Progress);
        Assert.Equal("Starting.", state.StatusText);
    }

    [Fact]
    public void SwitchedOffIsQuietRatherThanReportingAnythingToExplain()
    {
        var state = Project(false, null);

        Assert.True(state.Available);
        Assert.False(state.Enabled);
        Assert.Empty(state.Progress);
        Assert.Empty(state.StatusText);
    }

    [Fact]
    public void ControllingCarriesTheLimitItSettledOn()
    {
        var state = Project(
            true,
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
        var state = Project(
            true,
            new AutoTdpStatus(AutoTdpState.Paused, 22, null, null, null, "Paused by a manual change."));

        Assert.True(state.Available);
        Assert.False(state.Controlling);
        Assert.Equal("Paused by a manual change.", state.StatusText);
    }

    [Fact]
    public void UnavailableStillLeavesTheSwitchOperable()
    {
        // A locked switch left AutoTDP turned on with no way to turn it off; the failure shows in the
        // status instead.
        var state = Project(
            true,
            new AutoTdpStatus(
                AutoTdpState.Unavailable,
                null,
                null,
                null,
                null,
                "No primary power limit is available."));

        Assert.True(state.Available);
        Assert.Equal("failed", state.Progress);
    }

    [Fact]
    public void WaitingForAGameIsNotReportedAsControlling()
    {
        var state = Project(
            true,
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
        var state = Project(true, null, false);

        Assert.True(state.Enabled);
    }

    [Fact]
    public void TheTargetSliderCarriesTheSharedBoundsLineAndMarker()
    {
        var state = SteamNativeSettingsService.ProjectAutoTdp(true, null, true, null,
            new AutoTdpTargetView(45, 40, true));

        Assert.Equal(new AutoTdpRowTarget(45, AutoTdpTarget.MinimumFps, AutoTdpTarget.MaximumFps,
            "Held to the 40 FPS frame limit.", true), state.Target);
    }

    [Fact]
    public async Task TheUnavailableServiceRefusesRatherThanSilentlyAccepting()
    {
        // No device platform in this session: the producer is built without a coordinator, which is how a
        // session with device integration off projects this row.
        await using var rig = await ProducerRig.CreateAsync();

        await rig.Producer.ReadQuickAccessAsync();
        Assert.False((await rig.Producer.SetAsync("power.auto-tdp", Json("true"), CancellationToken.None))
            .Succeeded);
        Assert.False((await rig.Producer.SetAsync("power.auto-tdp-target", Json("45"), CancellationToken.None))
            .Succeeded);
    }

    private static AutoTdpRowState Project(
        bool enabled,
        AutoTdpStatus? status,
        bool powerLimitAvailable = true)
    {
        return SteamNativeSettingsService.ProjectAutoTdp(enabled, status, powerLimitAvailable);
    }

    // The controller target Steam's native quick-access menu shows is WSGM's own setting.
    // These pin the projection, which is the whole of the menu's truthfulness: which targets it offers,
    // which one it says is selected, which one it says is actually up, and whether a running game has
    // to be restarted before a change reaches it.
    [Fact]
    public void ManagementSwitchedOffOffersNothingAndSaysWhy()
    {
        var state = ProjectTarget(
            false,
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
        var state = ProjectTarget(
            true,
            Status(ControllerManagementState.Idle, ManagedControllerTarget.Xbox360),
            supportedTargets:
            [
                ManagedControllerTarget.SteamDeckComposite,
                ManagedControllerTarget.Xbox360,
                ManagedControllerTarget.DualShock4
            ]);

        Assert.True(state.Available);
        Assert.Collection(
            state.Targets,
            target => Assert.Equal(nameof(ManagedControllerTarget.SteamDeckComposite), target.Id),
            target => Assert.Equal(nameof(ManagedControllerTarget.Xbox360), target.Id),
            target => Assert.Equal(nameof(ManagedControllerTarget.DualShock4), target.Id));
        Assert.All(state.Targets, target => Assert.True(target.Selectable));
    }

    [Fact]
    public void ATargetTheBackendCannotBuildIsNotOffered()
    {
        // Offering one is worse than offering fewer: the selection persists, target creation is
        // refused, and controller management reports itself unavailable until the user finds the
        // setting again. The production backend supports only the Deck composite today.
        var state = ProjectTarget(
            true,
            Status(ControllerManagementState.Idle, ManagedControllerTarget.SteamDeckComposite),
            supportedTargets: [ManagedControllerTarget.SteamDeckComposite]);

        Assert.True(state.Available);
        var target = Assert.Single(state.Targets);
        Assert.Equal(nameof(ManagedControllerTarget.SteamDeckComposite), target.Id);
    }

    [Fact]
    public void ATargetChosenButNotYetUpIsNotReportedAsObserved()
    {
        // Idle means the selection is stored and nothing is present for it. Echoing the selection
        // back as observed would make a target that never came up look like it had.
        var state = ProjectTarget(
            true,
            Status(ControllerManagementState.Idle, ManagedControllerTarget.DualShock4));

        Assert.Equal(nameof(ManagedControllerTarget.DualShock4), state.SelectedTarget);
        Assert.Empty(state.ObservedTarget);
    }

    [Fact]
    public void AnActiveTargetIsReportedAsBothSelectedAndObserved()
    {
        var state = ProjectTarget(
            true,
            Status(ControllerManagementState.Active, ManagedControllerTarget.SteamDeckComposite));

        Assert.Equal(nameof(ManagedControllerTarget.SteamDeckComposite), state.SelectedTarget);
        Assert.Equal(nameof(ManagedControllerTarget.SteamDeckComposite), state.ObservedTarget);
        Assert.Equal("completed", state.Progress);
    }

    [Fact]
    public void AFaultedManagerIsUnavailableRatherThanQuietlySelectable()
    {
        var state = ProjectTarget(
            true,
            Status(
                ControllerManagementState.Faulted,
                ManagedControllerTarget.Xbox360,
                "The virtual controller could not be attached."));

        Assert.False(state.Available);
        Assert.Equal("failed", state.Progress);
        Assert.Equal("The virtual controller could not be attached.", state.StatusText);
    }

    [Fact]
    public void WithNoLiveTargetTheStoredChoiceStaysSelected()
    {
        // After a wake faulted the cycle the selector showed nothing, and a choice made there snapped
        // back to blank (Xbox Ally X, 2026-09-28). The choice is shown; only Observed stays empty.
        var state = SteamNativeSettingsService.ProjectControllerTarget(
            true,
            Status(ControllerManagementState.Faulted, null, "The virtual controller could not be attached."),
            true,
            Enum.GetValues<ManagedControllerTarget>(),
            ManagedControllerTarget.Xbox360);

        Assert.Equal(nameof(ManagedControllerTarget.Xbox360), state.SelectedTarget);
        Assert.Empty(state.ObservedTarget);
    }

    [Fact]
    public void ARunningGameIsToldItNeedsARestart()
    {
        // A game holds the target it launched with, so a change reaches it only next launch. Saying
        // so is the difference between a control that looks broken and one the user understands.
        var state = ProjectTarget(
            true,
            Status(
                ControllerManagementState.Active,
                ManagedControllerTarget.Xbox360,
                applicationId: "steam:70"));

        Assert.EndsWith(SteamNativeSettingsService.RestartToRebind, state.StatusText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NoRunningGameNeedsNoRestart()
    {
        var state = ProjectTarget(
            true,
            Status(ControllerManagementState.Active, ManagedControllerTarget.Xbox360));

        Assert.DoesNotContain(SteamNativeSettingsService.RestartToRebind, state.StatusText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnsupportedHandheldIsExplainedRatherThanLeftBlank()
    {
        // Controller management runs without a supported handheld, but with nothing capturing the physical
        // controller the result is a target that never moves. That is worth saying.
        var state = ProjectTarget(
            true,
            Status(ControllerManagementState.Idle, ManagedControllerTarget.Xbox360, string.Empty),
            false);

        Assert.True(state.Available);
        Assert.Contains("no supported handheld implementation", state.StatusText, StringComparison.Ordinal);
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
            SteamNativeSettingsService.TryParseTarget(candidate, out _));
    }

    [Fact]
    public void EveryProjectedTargetSurvivesTheRowBoundary()
    {
        var state = ProjectTarget(true, Status(ControllerManagementState.Idle, ManagedControllerTarget.Xbox360));
        foreach (var target in state.Targets)
        {
            Assert.True(SteamNativeSettingsService.TryParseTarget(target.Id, out var parsed));
            Assert.Equal(target.Id, parsed.ToString());
        }

        Assert.Equal(Enum.GetValues<ManagedControllerTarget>().Length, state.Targets.Count);
    }

    [Fact]
    public void TheNativePadIsTheLiveTargetAndTheOthersStayOffered()
    {
        var state = ProjectTarget(
            true,
            Status(ControllerManagementState.Native, ManagedControllerTarget.SteamDeckComposite,
                ControllerManager.NativeDetail));

        Assert.True(state.Available);
        Assert.Equal(nameof(ManagedControllerTarget.SteamDeckComposite), state.SelectedTarget);
        Assert.Equal(nameof(ManagedControllerTarget.SteamDeckComposite), state.ObservedTarget);
        Assert.Equal("completed", state.Progress);
        Assert.Equal(3, state.Targets.Count);
        Assert.Contains(ControllerManager.NativeDetail, state.StatusText, StringComparison.Ordinal);
    }

    private static ControllerTargetRowState ProjectTarget(
        bool enabled,
        ControllerManagerStatus status,
        bool packageInstalled = true,
        IReadOnlyList<ManagedControllerTarget>? supportedTargets = null)
    {
        return SteamNativeSettingsService.ProjectControllerTarget(
            enabled,
            status,
            packageInstalled,
            supportedTargets ?? Enum.GetValues<ManagedControllerTarget>());
    }
}
