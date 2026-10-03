using WSGM.Core;
using WSGM.Setup.Engine;
using WSGM.Testing;

namespace WSGM.Tests.Setup;

// Setup and the running WSGM meet only through named kernel objects, and the running side can be an
// older build. These tests pin the names setup opens against the ones WSGM creates, and run the stop
// against a recording machine, since no unit test can run a real upgrade.
public sealed class SetupShutdownContractTests
{
    [Theory]
    [InlineData("{\"Deltas\":[{\"EntryKind\":\"Device\",\"Value\":\"controller\"}]}", true)]
    [InlineData("unreadable ledger", false)]
    public void MissingApplicationKeepsTheRecoveryLedger(string contents, bool hasDevice)
    {
        using TemporaryDirectory temporary = new();
        var data = temporary.GetPath("data");
        Directory.CreateDirectory(data);
        var ledger = Path.Combine(data, "hidhide-ownership.json");
        File.WriteAllText(ledger, contents);
        File.WriteAllText(Path.Combine(data, "config.json"), "{}");
        RecordingRuntime runtime = new();
        using SetupEngine engine = new(null, runtime);

        Assert.False(engine.RestoreController(Step(), temporary.GetPath("missing.exe"), data));
        Assert.Equal(hasDevice ? ["controller"] : Array.Empty<string>(), engine.StillHiddenDevices);
        Assert.True(engine.DeleteUserData(data, File.Delete));

        Assert.Equal(contents, File.ReadAllText(ledger));
        Assert.False(File.Exists(Path.Combine(data, "config.json")));
        Assert.Empty(runtime.Calls);
    }

    [Fact]
    public void SuccessfulControllerRestoreAllowsDeletingTheLedger()
    {
        using TemporaryDirectory temporary = new();
        var data = temporary.GetPath("data");
        Directory.CreateDirectory(data);
        var ledger = Path.Combine(data, "hidhide-ownership.json");
        File.WriteAllText(ledger, "{\"Deltas\":[{\"EntryKind\":1,\"Value\":\"controller\"}]}");
        var app = temporary.GetPath("WSGM.exe");
        File.WriteAllText(app, "fake application");
        RecordingRuntime runtime = new();
        using SetupEngine engine = new(null, runtime);

        Assert.True(engine.RestoreController(Step(), app, data));
        Assert.Empty(engine.StillHiddenDevices);
        Assert.True(engine.DeleteUserData(data, File.Delete));

        Assert.False(File.Exists(ledger));
        Assert.Equal([$"Run {app} --uninstall-restore"], runtime.Calls);
    }

    [Fact]
    public void ExitEvents_MatchTheNamesWsgmCreates()
    {
        Assert.Equal(UpdateExitWatcher.EventName, WindowsSetup.ExitForUpdate);
        Assert.Equal(UpdateExitWatcher.UninstallEventName, WindowsSetup.ExitForUninstall);
    }

    [Fact]
    public void ForceStop_IsScopedToTheSessionAndSparesChildProcesses()
    {
        Assert.Equal("/FI \"SESSION eq 3\" /IM \"WSGM.exe\" /F", WindowsSetup.ForceStopArguments(3, "WSGM.exe"));
    }

    [Theory]
    [InlineData(false, WindowsSetup.ExitForUpdate, WindowsSetup.UpdateGraceIterations)]
    [InlineData(true, WindowsSetup.ExitForUninstall, WindowsSetup.UninstallGraceIterations)]
    public void StopRuntime_StopsTheServiceThenWsgmThenSteamAndReservesTheOwnerLast(bool forUninstall,
        string exitEvent, int graceIterations)
    {
        RecordingRuntime runtime = new();
        using SetupEngine engine = new(null, runtime);
        var step = Step();

        // The recorded owner stays held, so the stop ends there instead of touching the installed files.
        Assert.False(engine.StopRuntime(step, forUninstall));

        string[] expected =
        [
            "InspectService",
            "StopService",
            "ShellRunning",
            "RunningWsgmPath",
            $"RequestExit {exitEvent} {graceIterations}",
            "ForceStop WSGM.exe",
            "ShellAnchorRecoverySettled",
            "ForceStop WSGM.ShellAnchor.exe",
            "CloseSteam 60",
            "Blockers",
            "ReserveDeviceOwner 30"
        ];
        Assert.Equal(expected, runtime.CallsWithoutPackagedLaunchRecovery);
        Assert.Contains("hardware owner is still active", step.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void StopRuntime_SparesTheShellAnchorUntilItsRecoverySettled()
    {
        RecordingRuntime runtime = new() { AnchorSettles = false };
        using SetupEngine engine = new(null, runtime);

        engine.StopRuntime(Step(), false);

        Assert.Contains("ShellAnchorRecoverySettled", runtime.Calls);
        Assert.DoesNotContain("ForceStop WSGM.ShellAnchor.exe", runtime.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopRuntime_RefusesWhileSteamStaysAndEndsOnlyWsgmImages(bool forUninstall)
    {
        RecordingRuntime runtime = new() { SteamCloses = false, StillRunning = ["steam"] };
        using SetupEngine engine = new(null, runtime);
        var step = Step();

        Assert.False(engine.StopRuntime(step, forUninstall));

        Assert.Equal(["ForceStop WSGM.exe", "ForceStop WSGM.ShellAnchor.exe"],
            runtime.Calls.Where(call => call.StartsWith("ForceStop ", StringComparison.Ordinal)));
        Assert.Contains("CloseSteam 60", runtime.Calls);
        Assert.DoesNotContain("ReserveDeviceOwner 30", runtime.Calls);
        Assert.Equal("steam is still running. Close it normally, then run setup again. No process was ended.",
            step.Note);
    }

    [Fact]
    public void StopRuntime_StopsNothingWhileTheServiceStateIsUnknown()
    {
        RecordingRuntime runtime = new() { Service = null };
        using SetupEngine engine = new(null, runtime);

        Assert.False(engine.StopRuntime(Step(), false));

        Assert.Equal(["InspectService"], runtime.Calls);
    }

    [Fact]
    public void StopRuntime_LeavesWsgmRunningWhenTheServiceDoesNotStop()
    {
        RecordingRuntime runtime = new() { ServiceStops = false };
        using SetupEngine engine = new(null, runtime);

        Assert.False(engine.StopRuntime(Step(), false));

        Assert.Equal(["InspectService", "StopService"], runtime.Calls);
    }

    [Fact]
    public void LegacyRemoval_StopsWsgmThroughTheUpdateEventBeforeTheOldUninstallerRuns()
    {
        // The old uninstaller sends the uninstall event, on which WSGM leaves Steam running. The update
        // event first lets WSGM close Steam gracefully, as the old installer's update did.
        RecordingRuntime runtime = new();
        using SetupEngine engine = new(null, runtime);

        Assert.True(engine.RemoveLegacy(Step(), "unins000.exe"));

        string[] expected =
        [
            "ShellRunning",
            "RunningWsgmPath",
            $"RequestExit {WindowsSetup.ExitForUpdate} {WindowsSetup.UpdateGraceIterations}",
            "ForceStop WSGM.exe",
            "ShellAnchorRecoverySettled",
            "ForceStop WSGM.ShellAnchor.exe",
            "RunInnoUninstaller unins000.exe"
        ];
        Assert.Equal(expected, runtime.Calls);
    }

    [Fact]
    public void Uninstall_UnhidesTheControllerBeforeAnyComponentOrFileIsRemoved()
    {
        RecordingRuntime runtime = new();
        using SetupEngine engine = new(null, runtime)
        {
            Components = new InstalledComponents { Usbip = true, HidHide = true }
        };

        var plan = engine.PlanUninstall(new UninstallChoices(false, true, true));

        string[] expected =
        [
            "Closing WSGM and Steam",
            "Removing the Steam Input shim",
            "Restoring Steam's guide chord template",
            "Removing the sign-in service",
            "Restoring the shell registration",
            "Showing your controller to games again and restoring Windows settings",
            "Removing the USB/IP driver",
            "Removing HidHide",
            "Deleting program files",
            "Deleting settings and data"
        ];
        Assert.Equal(expected, plan.Select(step => step.Label));
        Assert.Empty(runtime.Calls);
    }

    private static SetupStep Step()
    {
        return new SetupStep("Closing WSGM and Steam", "WSGM and Steam closed", true, _ => true);
    }

    /// <summary>Records every stop operation in order and answers as a machine with WSGM and Steam running.</summary>
    private sealed class RecordingRuntime : IRuntimeShutdown
    {
        public List<string> Calls { get; } = [];

        // The uninstall stop recovers imported packaged games only where WSGM is installed, which depends on
        // the machine the tests run on.
        public IEnumerable<string> CallsWithoutPackagedLaunchRecovery =>
            Calls.Where(call => !call.Contains("WSGM.PackagedLaunch.exe", StringComparison.Ordinal));

        public ServiceState? Service { get; init; } = new ServiceState(true, true);
        public bool ServiceStops { get; init; } = true;
        public bool AnchorSettles { get; init; } = true;
        public bool SteamCloses { get; init; } = true;
        public IReadOnlyList<string> StillRunning { get; init; } = [];

        public ServiceState? InspectService()
        {
            Calls.Add("InspectService");
            return Service;
        }

        public bool StopService()
        {
            Calls.Add("StopService");
            return ServiceStops;
        }

        public bool ShellRunning()
        {
            Calls.Add("ShellRunning");
            return true;
        }

        public string? RunningWsgmPath()
        {
            Calls.Add("RunningWsgmPath");
            return null;
        }

        public ShutdownHandoff RequestExit(string eventName, int graceIterations)
        {
            Calls.Add($"RequestExit {eventName} {graceIterations}");
            return ShutdownHandoff.Completed;
        }

        public void ForceStopCurrentSession(string image)
        {
            Calls.Add("ForceStop " + image);
        }

        public bool ShellAnchorRecoverySettled()
        {
            Calls.Add("ShellAnchorRecoverySettled");
            return AnchorSettles;
        }

        public bool CloseSteam(TimeSpan budget)
        {
            Calls.Add($"CloseSteam {budget.TotalSeconds:0}");
            return SteamCloses;
        }

        public IReadOnlyList<string> Blockers(bool includeSteam)
        {
            Calls.Add("Blockers");
            return StillRunning;
        }

        public int Run(string file, string arguments)
        {
            Calls.Add($"Run {file} {arguments}");
            return 0;
        }

        public Mutex? ReserveDeviceOwner(TimeSpan wait)
        {
            Calls.Add($"ReserveDeviceOwner {wait.TotalSeconds:0}");
            return null;
        }

        public bool RunInnoUninstaller(string command, Func<bool> stillInstalled)
        {
            Calls.Add("RunInnoUninstaller " + command);
            return true;
        }
    }
}
