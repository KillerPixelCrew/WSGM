using WSGM.Device.Tests;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Tests.Wizard;

public sealed class PreflightTests
{
    private const string Self = @"\Device\HarddiskVolume3\Tools\wsgm-device.exe";

    private static string? Volume(string drive)
    {
        return drive.Equals("C:", StringComparison.OrdinalIgnoreCase) ? @"\Device\HarddiskVolume3" : null;
    }

    [Fact]
    public void HidHidePaths_MatchAcrossBothNotations()
    {
        Assert.True(HidHideAllowance.Contains([@"\Device\HarddiskVolume3\Tools\wsgm-device.exe"], Self, Volume));
        Assert.True(HidHideAllowance.Contains([Self.ToUpperInvariant()], @"C:\Tools\wsgm-device.exe", Volume));
        Assert.False(HidHideAllowance.Contains([@"C:\Tools\wsgm-device.exe"], Self, Volume));
    }

    [Fact]
    public void HidHidePaths_OnAnotherVolumeDoNotMatch()
    {
        Assert.False(HidHideAllowance.Contains([@"\Device\HarddiskVolume4\Tools\wsgm-device.exe"], Self, Volume));
    }

    [Fact]
    public void TryAllow_RecordsTheEntryBeforeWritingIt()
    {
        using TemporaryDirectory temporary = new();
        var state = new LabMachineState(Path.Combine(temporary.Root, "machine.json"));
        var device = new FakeHidHide(["C:\\Other\\app.exe"]);
        device.BeforeWrite = () => Assert.Equal(Self, state.Read().HidHideEntry);
        var allowance = new HidHideAllowance(device, state, Self);

        var result = allowance.TryAllow();

        Assert.Equal(Self, result.Added);
        Assert.Equal(["C:\\Other\\app.exe", Self], device.Applications);
        Assert.Equal(1, device.Writes);
    }

    [Fact]
    public void TryAllow_RepairsAnIneffectiveDosEntryAndRestoresOnlyItsOwnEntry()
    {
        using TemporaryDirectory temporary = new();
        var state = new LabMachineState(Path.Combine(temporary.Root, "machine.json"));
        const string stale = @"C:\Tools\wsgm-device.exe";
        var device = new FakeHidHide([stale]);
        var allowance = new HidHideAllowance(device, state, Self);

        Assert.False(allowance.AlreadyAllowed(device.Read()));
        Assert.Equal(Self, allowance.TryAllow().Added);
        Assert.True(allowance.AlreadyAllowed(device.Read()));
        Assert.Equal([stale, Self], device.Applications);
        Assert.Null(allowance.RestoreRecorded());
        Assert.Equal([stale], device.Applications);
    }

    [Fact]
    public void TryAllow_LeavesAnInverseListAlone()
    {
        using TemporaryDirectory temporary = new();
        var state = new LabMachineState(Path.Combine(temporary.Root, "machine.json"));
        var device = new FakeHidHide([]) { Inverse = true };

        var result = new HidHideAllowance(device, state, Self).TryAllow();

        Assert.Null(result.Added);
        Assert.Equal(0, device.Writes);
        Assert.Null(state.Read().HidHideEntry);
    }

    [Fact]
    public void TryAllow_ForgetsTheRecordWhenTheWriteFails()
    {
        using TemporaryDirectory temporary = new();
        var state = new LabMachineState(Path.Combine(temporary.Root, "machine.json"));
        var device = new FakeHidHide([]) { WriteError = "refused" };

        var result = new HidHideAllowance(device, state, Self).TryAllow();

        Assert.Null(result.Added);
        Assert.Equal("refused", result.Reason);
        Assert.Null(state.Read().HidHideEntry);
    }

    [Fact]
    public void RestoreRecorded_RemovesOnlyTheExactEntryTheLabWrote()
    {
        using TemporaryDirectory temporary = new();
        var state = new LabMachineState(Path.Combine(temporary.Root, "machine.json"));
        var device = new FakeHidHide([]);
        var allowance = new HidHideAllowance(device, state, Self);
        allowance.TryAllow();

        // Someone else allows the same executable in the other notation while the test runs.
        device.Applications.Add(@"C:\Tools\wsgm-device.exe");

        Assert.Null(allowance.RestoreRecorded());
        Assert.Equal([@"C:\Tools\wsgm-device.exe"], device.Applications);
        Assert.Null(state.Read().HidHideEntry);
    }

    [Fact]
    public void RestoreRecorded_KeepsTheRecordWhenHidHideIsUnavailable()
    {
        using TemporaryDirectory temporary = new();
        var state = new LabMachineState(Path.Combine(temporary.Root, "machine.json"));
        state.Update(changes => changes with { HidHideEntry = Self });
        var device = new FakeHidHide([]) { Available = false };

        Assert.NotNull(new HidHideAllowance(device, state, Self).RestoreRecorded());
        Assert.Equal(Self, state.Read().HidHideEntry);
    }

    [Theory]
    [InlineData("ArmouryCrate", "Armoury Crate")]
    [InlineData("armourycratecontrolinterface", "Armoury Crate control interface")]
    [InlineData("MSI Center", "MSI Center")]
    public void Managers_MatchTheirExactProcessName(string process, string label)
    {
        Assert.Equal(label, ManagerConflicts.Match(process)?.Label);
    }

    [Theory]
    [InlineData("ArmouryCrateHelper")]
    [InlineData("wsgm-device")]
    [InlineData("WSGM.AllyXLab")]
    public void Managers_DoNotMatchOnSubstrings(string process)
    {
        Assert.Null(ManagerConflicts.Match(process));
    }

    [Fact]
    public void Managers_ServicesAreNeverClosed()
    {
        var service = ManagerConflicts.Known.First(manager => !manager.Closable);

        var result = ManagerConflicts.Close(new RunningManager(service, Environment.ProcessId));

        Assert.False(result.Exited);
        Assert.Equal("Services are never stopped by this tool.", result.Detail);
    }

    [Theory]
    [InlineData(null, true, true, nameof(PawnIoAction.Install))]
    [InlineData(null, false, false, nameof(PawnIoAction.Unavailable))]
    [InlineData("1.9.0", true, true, nameof(PawnIoAction.AskToReplace))]
    [InlineData("2.2.0", true, true, nameof(PawnIoAction.None))]
    [InlineData("2.2.0", false, true, nameof(PawnIoAction.ReportNotRunning))]
    [InlineData("garbage", true, true, nameof(PawnIoAction.AskToReplace))]
    public void PawnIo_DecidesFromWhatIsInstalled(string? installed, bool opened, bool bundled, string expected)
    {
        var status = new PawnIoStatus(installed, @"C:\Program Files\PawnIO", opened, opened ? 0 : 2);

        Assert.Equal(expected, PawnIoSetup.Decide(status, PawnIoSetup.Pin, bundled).ToString());
    }

    [Fact]
    public void PawnIo_PinMatchesTheLockFileAndNeverAllowsUnrestrictedModules()
    {
        var pin = PawnIoSetup.Pin;

        Assert.Equal("2.2.0", pin.Version);
        Assert.Equal(64, pin.AssetSha256.Length);
        Assert.Equal(["-install", "-silent"], pin.InstallArguments);
        Assert.DoesNotContain(pin.InstallArguments.Concat(pin.UninstallArguments),
            argument => argument.Contains("unrestricted", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeHidHide(List<string> applications) : IHidHideDevice
    {
        public List<string> Applications { get; } = applications;

        public bool Available { get; init; } = true;

        public bool Inverse { get; init; }

        public string? WriteError { get; init; }

        public Action? BeforeWrite { get; set; }

        public int Writes { get; private set; }

        public HidHideState Read()
        {
            return Available
                ? new HidHideState(true, true, Inverse, [.. Applications], null)
                : new HidHideState(false, false, false, [], "not installed");
        }

        public string? WriteApplications(IReadOnlyList<string> values)
        {
            BeforeWrite?.Invoke();
            if (WriteError is not null)
            {
                return WriteError;
            }

            Writes++;
            Applications.Clear();
            Applications.AddRange(values);
            return null;
        }
    }
}
