extern alias LogonService;
using WSGM.Testing;
using BootManifest = LogonService::WSGM.Core.BootManifest;
using ISessionHost = LogonService::WSGM.LogonService.ISessionHost;
using SessionLauncher = LogonService::WSGM.LogonService.SessionLauncher;

namespace WSGM.Tests.LogonService;

public sealed class SessionLauncherTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("missing-exe")]
    [InlineData("pending-setup")]
    public void IneligibleLogonsLeaveTheDesktopAloneAndCloseAnyQueriedToken(string reason)
    {
        using TemporaryDirectory directory = new();
        var host = CreateHost(directory);
        if (reason == "missing")
        {
            host.Manifest = null;
        }
        else if (reason == "disabled")
        {
            host.Manifest!.GameModeBoot = false;
        }
        else if (reason == "missing-exe")
        {
            File.Delete(host.Manifest!.ExePath);
        }
        else
        {
            host.HasPendingSetup = true;
        }

        SessionLauncher launcher = new(host);
        launcher.OnSessionLogon(1, null);
        Assert.Empty(host.Launches);
        Assert.Equal(reason == "pending-setup" ? 0 : 1, host.Closed.Count);
    }

    [Fact]
    public async Task StopWaitsForAnAlreadyDispatchedLaunchAndThenRejectsNewLogons()
    {
        using TemporaryDirectory directory = new();
        var host = CreateHost(directory);
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim resume = new(false);
        using ManualResetEventSlim stopping = new(false);
        host.BeforeLaunch = () =>
        {
            entered.Set();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(5)));
        };
        SessionLauncher launcher = new(host);
        var logon = Task.Run(() => launcher.OnSessionLogon(1, null));
        Task? stop = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            stop = Task.Run(() =>
            {
                stopping.Set();
                launcher.Stop();
            });
            Assert.True(stopping.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            resume.Set();
            await logon.WaitAsync(TimeSpan.FromSeconds(5));
            if (stop is not null)
            {
                await stop.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        host.BeforeLaunch = null;
        launcher.OnSessionLogon(2, null);
        Assert.Single(host.Launches);
        host.Watchdog!();
    }

    [Fact]
    public void StoppedLauncherDoesNotQueryOrLaunchForLiveOrCatchUpLogons()
    {
        using TemporaryDirectory directory = new();
        var host = CreateHost(directory);
        SessionLauncher launcher = new(host);
        launcher.Stop();
        launcher.Stop();
        launcher.OnSessionLogon(1, null);
        launcher.CatchUpExistingSessions();
        Assert.Equal(0, host.TokenQueries);
        Assert.Empty(host.Launches);
    }

    [Theory]
    [InlineData(1, 10, 0)]
    [InlineData(2, 10, 0)]
    [InlineData(3, 20, 1)]
    public void ElevatedManifestSelectsLinkedTokenOnlyForALimitedUser(int elevationType, int expectedToken,
        int linkedQueries)
    {
        using TemporaryDirectory directory = new();
        var host = CreateHost(directory);
        host.ElevationType = elevationType;
        host.Manifest!.Elevate = true;
        SessionLauncher launcher = new(host);
        launcher.OnSessionLogon(1, null);
        var launch = Assert.Single(host.Launches);
        Assert.Equal(expectedToken, launch.Token);
        Assert.Equal(host.Manifest.ExePath, launch.Executable);
        Assert.Equal("--boot", launch.Arguments);
        Assert.Equal(linkedQueries, host.LinkedQueries);
        Assert.Equal(expectedToken == 20, host.Closed.Contains(20));
        Assert.DoesNotContain(10, host.Closed);
        host.Watchdog!();
        Assert.Contains(10, host.Closed);
        Assert.Contains(30, host.Closed);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void TokenQueryFailureFallsBackToTheUnlinkedUser(bool elevationAvailable, bool linkedAvailable)
    {
        using TemporaryDirectory directory = new();
        var host = CreateHost(directory);
        host.Manifest!.Elevate = true;
        host.ElevationType = 3;
        host.ElevationAvailable = elevationAvailable;
        host.LinkedAvailable = linkedAvailable;
        SessionLauncher launcher = new(host);
        launcher.OnSessionLogon(1, null);
        Assert.Equal(10, Assert.Single(host.Launches).Token);
        Assert.DoesNotContain(20, host.Closed);
        host.Watchdog!();
    }

    [Fact]
    public async Task StopDuringTokenPreparationRejectsTheFinalLaunchAndClosesBothTokens()
    {
        using TemporaryDirectory directory = new();
        var host = CreateHost(directory);
        host.Manifest!.Elevate = true;
        host.ElevationType = 3;
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim resume = new(false);
        host.BeforeLinkedToken = () =>
        {
            entered.Set();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(5)));
        };
        SessionLauncher launcher = new(host);
        var logon = Task.Run(() => launcher.OnSessionLogon(1, null));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            launcher.Stop();
        }
        finally
        {
            resume.Set();
        }

        await logon.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(host.Launches);
        Assert.Equal(new nint[] { 20, 10 }, host.Closed);
    }

    [Fact]
    public async Task LiveLogonAndCatchUpRaceLaunchExactlyOnce()
    {
        using TemporaryDirectory directory = new();
        var host = CreateHost(directory);
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim resume = new(false);
        var reads = 0;
        host.BeforeManifest = () =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                entered.Set();
                Assert.True(resume.Wait(TimeSpan.FromSeconds(5)));
            }
        };
        SessionLauncher launcher = new(host);
        var live = Task.Run(() => launcher.OnSessionLogon(1, null));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            launcher.CatchUpExistingSessions();
            Assert.Empty(host.Launches);
        }
        finally
        {
            resume.Set();
        }

        await live.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(host.Launches);
        launcher.OnSessionLogon(1, null);
        Assert.Single(host.Launches);
        host.Watchdog!();
    }

    [Fact]
    public void FailedLaunchClosesOwnedTokensAndAllowsTheNextLogon()
    {
        using TemporaryDirectory directory = new();
        var host = CreateHost(directory);
        host.Manifest!.Elevate = true;
        host.ElevationType = 3;
        host.LaunchSucceeds = false;
        SessionLauncher launcher = new(host);
        launcher.OnSessionLogon(1, null);
        Assert.Equal(new nint[] { 20, 10 }, host.Closed);
        Assert.Null(host.Watchdog);
        host.LaunchSucceeds = true;
        launcher.OnSessionLogon(1, null);
        Assert.Equal(2, host.Launches.Count);
        host.Watchdog!();
    }

    [Fact]
    public void DirtyExitRestoresExplorerOnceWithTheUnlinkedToken()
    {
        using TemporaryDirectory directory = new();
        var host = CreateHost(directory);
        host.Manifest!.Elevate = true;
        host.ElevationType = 3;
        host.ExitCode = 1;
        host.DesktopShell = false;
        SessionLauncher launcher = new(host);
        launcher.OnSessionLogon(1, null);
        launcher.OnSessionLogoff(1);
        Assert.DoesNotContain(10, host.Closed);
        host.Watchdog!();
        Assert.Equal(2, host.Launches.Count);
        Assert.Equal(10, host.Launches[1].Token);
        Assert.Equal("explorer.exe", Path.GetFileName(host.Launches[1].Executable));
        Assert.Equal(1, host.AnchorWaits);
        Assert.Contains(10, host.Closed);
    }

    private static FakeSessionHost CreateHost(TemporaryDirectory directory)
    {
        var executable = Path.Combine(directory.Root, "WSGM.exe");
        File.WriteAllText(executable, "test fixture");
        return new FakeSessionHost { Manifest = new BootManifest { GameModeBoot = true, ExePath = executable } };
    }

    private sealed class FakeSessionHost : ISessionHost
    {
        public BootManifest? Manifest { get; set; }
        public int ElevationType { get; set; } = 1;
        public bool ElevationAvailable { get; set; } = true;
        public bool LinkedAvailable { get; set; } = true;
        public bool LaunchSucceeds { get; set; } = true;
        public uint ExitCode { get; set; }
        public bool DesktopShell { get; set; } = true;
        public int TokenQueries { get; private set; }
        public int LinkedQueries { get; private set; }
        public int AnchorWaits { get; private set; }
        public Action? BeforeManifest { get; set; }
        public Action? BeforeLinkedToken { get; set; }
        public Action? BeforeLaunch { get; set; }
        public Action? Watchdog { get; private set; }
        public List<(nint Token, string Executable, string Arguments)> Launches { get; } = [];
        public List<nint> Closed { get; } = [];
        public bool HasPendingSetup { get; set; }
        public int LastError => 0;

        public bool TryGetUserToken(uint sessionId, out nint token)
        {
            TokenQueries++;
            token = 10;
            return true;
        }

        public BootManifest? ReadManifest(nint userToken)
        {
            BeforeManifest?.Invoke();
            return Manifest;
        }

        public bool TryGetElevationType(nint token, out int elevationType)
        {
            elevationType = ElevationType;
            return ElevationAvailable;
        }

        public nint GetLinkedPrimaryToken(nint token, uint sessionId)
        {
            BeforeLinkedToken?.Invoke();
            LinkedQueries++;
            return LinkedAvailable ? 20 : 0;
        }

        public bool TryLaunch(nint token, string executable, string arguments, out nint process, out uint processId,
            out int error)
        {
            BeforeLaunch?.Invoke();
            Launches.Add((token, executable, arguments));
            process = 30;
            processId = 42;
            error = LaunchSucceeds ? 0 : 5;
            return LaunchSucceeds;
        }

        public IEnumerable<(uint SessionId, TimeSpan LogonAge)> ActiveSessions()
        {
            return [(1, TimeSpan.Zero)];
        }

        public string GetSessionUser(uint sessionId)
        {
            return "fixture user";
        }

        public uint WaitForSingleObject(nint process, uint milliseconds)
        {
            return 0;
        }

        public bool TryGetExitCode(nint process, out uint exitCode)
        {
            exitCode = ExitCode;
            return true;
        }

        public bool IsSessionActive(uint sessionId)
        {
            return true;
        }

        public bool IsDesktopShellInSession(nint userToken, string executable)
        {
            return DesktopShell;
        }

        public void CloseHandle(nint handle)
        {
            Closed.Add(handle);
        }

        public void StartWatchdog(Action watch, string name)
        {
            Watchdog = watch;
        }

        public void WaitForAnchor(TimeSpan grace)
        {
            AnchorWaits++;
        }

        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }

        public void Error(string message)
        {
        }
    }
}
