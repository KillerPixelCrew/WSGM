using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
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
    public void UnavailableControllerServicePublishesNoSelectableTargets()
    {
        using var service = new UnavailableNativeQamControllerTargetService();

        NativeQamControllerTargetState state = service.Current;

        Assert.False(state.Available);
        Assert.Empty(state.Targets);
        Assert.Empty(state.SelectedTarget);
        Assert.Empty(state.ObservedTarget);
        // The reason is surfaced verbatim rather than replaced with a generic message, so a user
        // reading native QAM learns why controller management is off.
        Assert.Equal(DeviceFeatureAvailability.ControllerManagementDetail, state.StatusText);
    }

    [Fact]
    public void PerformanceProjectionPublishesExactAdapterCapabilitiesAndReadback()
    {
        PerformanceState state = PerformanceStateFixture(
            new HashSet<int> { 0, 1 },
            PerformanceCommandState.Idle);

        NativeQamFrameLimitState frame =
            PerformanceServiceNativeQamAdapter.ProjectFrameLimit(state, enabled: true);
        Assert.True(frame.Available);
        Assert.Equal(0, frame.MinimumFps);
        Assert.Equal(1000, frame.MaximumFps);
        Assert.Equal(45, frame.DesiredFps);
        Assert.Equal(44, frame.ObservedFps);
        Assert.True(frame.SupportsReadback);
        Assert.Equal("verified", frame.ReadbackQuality);
        Assert.Equal("application", frame.PolicyLayer);
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

        NativeQamFrameLimitState frame =
            PerformanceServiceNativeQamAdapter.ProjectFrameLimit(state, enabled: true);
        Assert.Equal("timed-out", frame.Progress);
        Assert.Equal("RTSS readback timed out.", frame.Fault);
    }

    private static PerformanceState PerformanceStateFixture(
        IReadOnlySet<int> overlayLevels,
        PerformanceCommandState command) => new(
            new RtssProbe(
                RtssAvailability.Ready,
                "7.3.6",
                "RTSS.exe",
                42,
                DateTimeOffset.UtcNow,
                3,
                new RtssCapabilities(0, 1000, overlayLevels, true, true),
                null),
            new RtssApplicationTarget("steam:123", "game.exe", 123),
            PerformancePolicyLayer.Application,
            PerformancePolicyLayer.Global,
            new PerformanceValues(45, 1),
            new PerformanceValues(44, 0),
            PerformanceReadbackQuality.Verified,
            PerformanceReadbackQuality.Verified,
            RtssTelemetryHealth.Healthy,
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
            ObservedValue = Integer(17),
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
                DesiredValue = Integer(18),
                DesiredSource = DesiredValueSource.TemporaryRequest,
                PendingValue = Integer(19),
                Progress = CommandProgress.Pending,
            },
            null);
    }

    private static CapabilityValue Integer(int value) => new()
    {
        Kind = CapabilityValueKind.Integer,
        IntegerValue = value,
    };
}
