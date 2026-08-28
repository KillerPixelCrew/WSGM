using WSGM.Device.Contracts.Diagnostics;
using WSGM.Device.Contracts.Lifecycle;

namespace WSGM.Device.Contracts.Tests;

/// <summary>
/// The executable specification of lease arbitration and paste-safe diagnostics.
/// </summary>
public class DiagnosticsAndLeaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ADiagnosticSession_CoexistsWithTheProductionPlugin()
    {
        // It only reads what the production plugin already observes, so it cannot disturb what it is
        // diagnosing.
        Assert.True(LeaseArbitration.CanCoexist(LeaseKind.Production, LeaseKind.Diagnostic));
    }

    [Fact]
    public void AnExperiment_NeverCoexistsWithAnything()
    {
        // An experiment mutates hardware, and its own observation is the evidence. A second reader
        // mid-trial makes that evidence unreliable.
        Assert.False(LeaseArbitration.CanCoexist(LeaseKind.Production, LeaseKind.Experiment));
        Assert.False(LeaseArbitration.CanCoexist(LeaseKind.Diagnostic, LeaseKind.Experiment));
        Assert.False(LeaseArbitration.CanCoexist(LeaseKind.Experiment, LeaseKind.Diagnostic));
        Assert.False(LeaseArbitration.CanCoexist(LeaseKind.Experiment, LeaseKind.Production));
    }

    [Fact]
    public void AnExperimentBlockedByProduction_SaysSoSpecifically()
    {
        // The fix is specific - the production plugin must release that one resource in an orderly
        // way - so a generic "conflict" would send the operator looking in the wrong place.
        LeaseGrant grant = LeaseArbitration.Evaluate(
            Request(LeaseKind.Experiment),
            LeaseKind.Production,
            "wsgm.device.msi.claw-8-a2vm",
            ResourceState.Owned,
            Now);

        Assert.False(grant.Granted);
        Assert.Equal(LeaseRefusal.ProductionHolderActive, grant.Refusal);
        Assert.Equal("wsgm.device.msi.claw-8-a2vm", grant.ConflictingHolder);
    }

    [Fact]
    public void AFreeResource_GrantsAnyLease()
    {
        Assert.True(LeaseArbitration
            .Evaluate(Request(LeaseKind.Experiment), null, null, ResourceState.Idle, Now).Granted);
    }

    [Fact]
    public void AQuarantinedResource_GrantsNothing()
    {
        LeaseGrant grant = LeaseArbitration.Evaluate(
            Request(LeaseKind.Diagnostic), null, null, ResourceState.Faulted, Now);

        Assert.False(grant.Granted);
        Assert.Equal(LeaseRefusal.Quarantined, grant.Refusal);
    }

    [Fact]
    public void AResourceBeingReleased_TakesNoNewLeases()
    {
        LeaseGrant grant = LeaseArbitration.Evaluate(
            Request(LeaseKind.Production), null, null, ResourceState.Releasing, Now);

        Assert.Equal(LeaseRefusal.Quiescing, grant.Refusal);
    }

    [Fact]
    public void AnExpiredRequest_IsCancelled()
    {
        LeaseRequest expired = Request(LeaseKind.Diagnostic) with { Deadline = Now.AddSeconds(-1) };

        Assert.Equal(LeaseRefusal.Cancelled,
            LeaseArbitration.Evaluate(expired, null, null, ResourceState.Idle, Now).Refusal);
    }

    [Fact]
    public void Format_ProducesOneLineOfFieldValuePairs()
    {
        string line = DeviceLogFields.Format(
            "power.apply",
            (DeviceLogFields.Capability, "power.primary-limit"),
            (DeviceLogFields.DurationMs, 42),
            (DeviceLogFields.Result, "AppliedVerified"));

        Assert.Equal(
            "op=power.apply capability=power.primary-limit ms=42 result=AppliedVerified",
            line);
    }

    [Theory]
    [InlineData("line\nbreak")]
    [InlineData("line\rbreak")]
    [InlineData("null\0byte")]
    public void Sanitize_NeutralizesCharactersThatCouldForgeALogLine(string value)
    {
        // A value carrying a newline could otherwise fabricate an entire additional log line, and a
        // forged line is worse than a truncated value when the log is the only evidence available.
        string sanitized = DeviceLogFields.Sanitize(value);

        Assert.DoesNotContain('\n', sanitized);
        Assert.DoesNotContain('\r', sanitized);
        Assert.DoesNotContain('\0', sanitized);
    }

    [Fact]
    public void Sanitize_ReplacesSpacesSoFieldsStayParseable()
    {
        Assert.Equal("MSI_Center_M", DeviceLogFields.Sanitize("MSI Center M"));
    }

    [Fact]
    public void Sanitize_RendersNullAndEmptyIdentically()
    {
        Assert.Equal("-", DeviceLogFields.Sanitize(null));
        Assert.Equal("-", DeviceLogFields.Sanitize(""));
    }

    [Fact]
    public void TokenizeDevicePath_KeepsTheModelAndDropsTheInstance()
    {
        // VID and PID are identical across every unit of a model, so they identify the hardware.
        // The instance path and serial identify the owner.
        Dictionary<string, string> tokens = [];

        string token = DeviceLogFields.TokenizeDevicePath(
            @"\\?\HID#VID_0DB0&PID_1901&MI_00#7&2f9c1a3&0&0000#{4d1e55b2-f16f}", tokens);

        Assert.Contains("VID_0DB0&PID_190", token, StringComparison.Ordinal);
        Assert.DoesNotContain("2f9c1a3", token, StringComparison.Ordinal);
    }

    [Fact]
    public void TokenizeDevicePath_IsStableWithinASession()
    {
        // Two lines about the same device have to stay correlatable, or a remote diagnosis cannot
        // follow one device through a sequence of events.
        Dictionary<string, string> tokens = [];
        const string Path = @"\\?\HID#VID_0DB0&PID_1901&MI_00#7&2f9c1a3&0&0000#{4d1e55b2-f16f}";

        Assert.Equal(
            DeviceLogFields.TokenizeDevicePath(Path, tokens),
            DeviceLogFields.TokenizeDevicePath(Path, tokens));
    }

    [Fact]
    public void TokenizeDevicePath_GivesDifferentDevicesDifferentTokens()
    {
        Dictionary<string, string> tokens = [];

        string first = DeviceLogFields.TokenizeDevicePath(
            @"\\?\HID#VID_0DB0&PID_1901&MI_00#7&aaa&0&0000", tokens);
        string second = DeviceLogFields.TokenizeDevicePath(
            @"\\?\HID#VID_0DB0&PID_1901&MI_00#7&bbb&0&0000", tokens);

        Assert.NotEqual(first, second);
    }

    private static LeaseRequest Request(LeaseKind kind) =>
        new("physical-controller", kind, "device-lab", Now.AddSeconds(30));
}
