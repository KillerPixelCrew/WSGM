using WindowsDeviceControl;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class DisplayLayoutDiagnosticsTests
{
    private static DisplayLayout Layout() => new([new DisplayLayoutOutput(
        new DisplayTargetIdentity("test-tv", null, null, "TV", 1, 0, 2), 0, 0, 3840, 2160, DisplayRefresh.FromHertz(60))]);

    [Fact]
    public void RejectedLayoutKeepsTheNativeCodeRequestAndReadbackInTheTrace()
    {
        List<string> info = [], warnings = [];
        DisplayLayoutResult rejected = new(DisplayLayoutOutcome.Rejected, [], 87, false, false, [], "Invalid parameter");
        var result = DisplayLayoutDiagnostics.Apply(Layout(), _ => rejected,
            () => new DisplayArrangement([], "desktop-unchanged", DateTimeOffset.UnixEpoch), info.Add, warnings.Add);

        Assert.Same(rejected, result);
        Assert.Contains(info, line => line.Contains("test-tv") && line.Contains("3840"));
        Assert.Contains(info, line => line.Contains("desktop-unchanged"));
        Assert.Contains(warnings, line => line.Contains("nativeStatus=87")
            && line.Contains("outcome=Rejected") && line.Contains("rollbackAttempted=False"));
    }

    [Fact]
    public void AFailedDiagnosticReadDoesNotChangeTheApplyOutcomeOrRepeatTheWrite()
    {
        var writes = 0;
        List<string> warnings = [];
        DisplayLayoutResult rolledBack = new(DisplayLayoutOutcome.Unconfirmed, [], 31, true, true, [], "Rolled back");
        var result = DisplayLayoutDiagnostics.Apply(Layout(), _ => { writes++; return rolledBack; },
            () => throw new InvalidOperationException("driver busy"), _ => { }, warnings.Add);

        Assert.Same(rolledBack, result);
        Assert.Equal(1, writes);
        Assert.Contains(warnings, line => line.Contains("rollbackAttempted=True") && line.Contains("rollbackSucceeded=True"));
        Assert.Contains(warnings, line => line.Contains("readback unavailable: driver busy"));
    }
}
