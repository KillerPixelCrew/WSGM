using WSGM.Core;
using WSGM.Device.Tests;
using WSGM.Setup.Engine;

namespace WSGM.Tests.Setup;

// Setup and the running WSGM meet only through named kernel objects, and the running side can be an
// older build. These tests pin the names setup opens against the ones WSGM creates, and the order of
// the stop, since no unit test can run a real upgrade.
public sealed class SetupShutdownContractTests
{
    private static string Source(string relativePath)
    {
        return File.ReadAllText(Path.Combine(RepositoryFiles.Root, relativePath));
    }

    [Fact]
    public void ExitEvents_MatchTheNamesWsgmCreates()
    {
        Assert.Equal(UpdateExitWatcher.EventName, WindowsSetup.ExitForUpdate);
        Assert.Equal(UpdateExitWatcher.UninstallEventName, WindowsSetup.ExitForUninstall);
    }

    [Fact]
    public void ForceStop_IsScopedToTheSessionAndSparesTheShellAnchorUntilRecoverySettled()
    {
        var source = Source(@"src\WSGM.Setup\Engine\WindowsSetup.cs");

        Assert.Contains("/FI \\\"SESSION eq {session}\\\" /IM \\\"{image}\\\" /F", source, StringComparison.Ordinal);
        Assert.DoesNotContain(" /T", source, StringComparison.Ordinal);
        AssertOrdered(source, "settled.WaitOne(", "ForceStopCurrentSession(\"WSGM.ShellAnchor.exe\")");
        foreach (var stop in new[] { "StopForUpdate()", "StopForUninstall()" })
        {
            var body = source[source.IndexOf("ShutdownHandoff " + stop, StringComparison.Ordinal)..];
            AssertOrdered(body, "RequestExit(", "ForceStopCurrentSession(\"WSGM.exe\")");
            AssertOrdered(body, "ForceStopCurrentSession(\"WSGM.exe\")", "WaitForShellAnchorRecovery()");
        }
    }

    [Fact]
    public void StopRuntime_StopsTheServiceFirstAndReservesTheOwnerLast()
    {
        var source = Source(@"src\WSGM.Setup\Engine\SetupEngine.cs");
        var stop = source[source.IndexOf("private bool StopRuntime", StringComparison.Ordinal)..];

        AssertOrdered(stop, "WindowsSetup.InspectService()", "WindowsSetup.StopService()");
        AssertOrdered(stop, "WindowsSetup.StopService()", "WindowsSetup.StopForUpdate()");
        AssertOrdered(stop, "WindowsSetup.StopForUpdate()", "WindowsSetup.Blockers(");
        AssertOrdered(stop, "WindowsSetup.Blockers(", "WindowsSetup.ReserveDeviceOwner()");
        Assert.Contains("No process was ended.", stop, StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstall_UnhidesTheControllerBeforeAnyComponentOrFileIsRemoved()
    {
        var source = Source(@"src\WSGM.Setup\Engine\SetupEngine.cs");
        var plan = source[source.IndexOf("PlanUninstall(UninstallChoices", StringComparison.Ordinal)..];

        AssertOrdered(plan, "--remove-steam-input-shim", "--uninstall\"");
        AssertOrdered(plan, "--uninstall\"", "--unregister-shell");
        AssertOrdered(plan, "--unregister-shell", "RestoreController");
        AssertOrdered(plan, "RestoreController", "RemoveComponent(step, \"USBip\")");
        AssertOrdered(plan, "RemoveComponent(step, \"HidHide\")", "DeleteProgramFiles()");
        AssertOrdered(plan, "DeleteProgramFiles()", "DeleteUserData()");
    }

    private static void AssertOrdered(string source, string first, string second)
    {
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        Assert.True(firstIndex >= 0, $"Setup operation was not found: {first}");
        var secondIndex = source.IndexOf(second, firstIndex + first.Length, StringComparison.Ordinal);
        Assert.True(secondIndex > firstIndex, $"Setup operation must follow {first}: {second}");
    }
}
