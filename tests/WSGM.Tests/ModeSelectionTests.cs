namespace WSGM.Tests;

public sealed class ModeSelectionTests
{
    [Fact]
    public void ExplicitShellModeHasHighestPrecedence()
    {
        var mode = Program.DecideMode(["--settings", "--overlay-test", "--SHELL"], false, true);

        Assert.Equal(RunMode.Shell, mode);
    }

    [Fact]
    public void ExplicitSettingsModeWinsOverOverlayTest()
    {
        var mode = Program.DecideMode(["--overlay-test", "--settings"], true, false);

        Assert.Equal(RunMode.Settings, mode);
    }

    [Fact]
    public void OverlayTestFlagSelectsTheSafeOverlaySmokeTestMode()
    {
        // The only local surface that exercises the overlay without a takeover; every
        // other test in this file passes --overlay-test as a LOSER of the precedence
        // rules, so deleting its branch would go unnoticed without this one.
        var mode = Program.DecideMode(["--OVERLAY-TEST"], false, true);

        Assert.Equal(RunMode.OverlayTest, mode);
    }

    [Theory]
    [InlineData(true, false, RunMode.Shell)]
    [InlineData(true, true, RunMode.Settings)]
    [InlineData(false, false, RunMode.Settings)]
    [InlineData(false, true, RunMode.Settings)]
    public void AutoModeRequiresTheRegisteredShellAndNoDesktop(
        bool registeredAsShell,
        bool desktopAlive,
        RunMode expected)
    {
        var mode = Program.DecideMode([], registeredAsShell, desktopAlive);

        Assert.Equal(expected, mode);
    }

    [Fact]
    public void ServiceBootSelectsShellModeEvenWithADesktopAlive()
    {
        var mode = Program.DecideMode(["--BOOT"], false, true);

        Assert.Equal(RunMode.Shell, mode);
    }

    [Fact]
    public void ServiceBootOutranksSettingsAndOverlayTest()
    {
        var mode = Program.DecideMode(["--settings", "--overlay-test", "--boot"], false, true);

        Assert.Equal(RunMode.Shell, mode);
    }

    [Theory]
    [InlineData(new[] { "--boot" }, true)]
    [InlineData(new[] { "--BOOT", "--elevated-relaunch" }, true)]
    [InlineData(new[] { "--shell" }, false)]
    [InlineData(new string[0], false)]
    public void IsServiceBootDetectsOnlyTheBootFlag(string[] args, bool expected)
    {
        Assert.Equal(expected, Program.IsServiceBoot(args));
    }

    [Theory]
    [InlineData(new[] { "--shell", "--installer-rollback-no-device" }, true)]
    [InlineData(new[] { "--shell", "--INSTALLER-ROLLBACK-NO-DEVICE" }, true)]
    [InlineData(new[] { "--shell" }, false)]
    public void InstallerRollbackDeviceSuppressionDetectsOnlyItsExactFlag(
        string[] args,
        bool expected)
    {
        Assert.Equal(expected, Program.IsInstallerRollbackWithoutDeviceIntegration(args));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public void DeviceCoordinatorAdmissionHonorsOverlayAndInstallerRollbackSuppression(
        bool overlayTestOnly,
        bool suppressDeviceIntegration,
        bool expected)
    {
        Assert.Equal(expected, WSGM.Shell.ShellSession.ShouldStartDeviceCoordinator(
            overlayTestOnly,
            suppressDeviceIntegration));
    }

    [Fact]
    public async Task ProductionSessionAdmissionPreservesTheAsynchronousCoordinatorFactory()
    {
        var pending = new TaskCompletionSource<WSGM.Shell.DeviceCoordinator?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool called = false;

        Task<WSGM.Shell.DeviceCoordinator?> admission =
            WSGM.Shell.ShellSession.AdmitDeviceCoordinatorAsync(
                new WSGM.Core.AppConfig(),
                overlayTestOnly: false,
                suppressDeviceIntegration: false,
                CancellationToken.None,
                (_, _) =>
                {
                    called = true;
                    return pending.Task;
                });

        Assert.True(called);
        Assert.False(admission.IsCompleted);
        pending.SetResult(null);
        Assert.Null(await admission);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SafeSessionModesBypassTheProductionCoordinatorFactory(
        bool overlayTestOnly,
        bool suppressDeviceIntegration)
    {
        bool called = false;

        WSGM.Shell.DeviceCoordinator? coordinator =
            await WSGM.Shell.ShellSession.AdmitDeviceCoordinatorAsync(
                new WSGM.Core.AppConfig(),
                overlayTestOnly,
                suppressDeviceIntegration,
                CancellationToken.None,
                (_, _) =>
                {
                    called = true;
                    return Task.FromResult<WSGM.Shell.DeviceCoordinator?>(null);
                });

        Assert.False(called);
        Assert.Null(coordinator);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void InstallerRollbackRestartsDoNotCountAsCrashes(
        bool installerRollbackWithoutDeviceIntegration,
        bool expected)
    {
        Assert.Equal(expected, Program.ShouldApplyCrashLoopProtection(
            installerRollbackWithoutDeviceIntegration));
    }
}
