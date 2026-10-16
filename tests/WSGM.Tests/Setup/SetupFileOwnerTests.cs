using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using WSGM.Setup.Engine;

namespace WSGM.Tests.Setup;

public sealed class SetupFileOwnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeReleaseEndsTheOwnedHelperHoldingAnEmptyWorkingDirectory(bool nested)
    {
        using SetupTestInstallation installation = new();
        var directory = Path.Combine(installation.Root, "directory-owner");
        var working = nested ? Path.Combine(directory, "empty") : directory;
        Directory.CreateDirectory(working);
        var ready = Path.Combine(installation.Machine, "directory-owner-ready.txt");
        var script = $$"""
            Add-Type -TypeDefinition @'
            using System.Runtime.InteropServices;
            public static class DirectoryOwner {
                [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
                public static extern bool SetCurrentDirectory(string path);
            }
            '@
            if (-not [DirectoryOwner]::SetCurrentDirectory('{{working.Replace("'", "''", StringComparison.Ordinal)}}')) { exit 2 }
            [IO.File]::WriteAllText('{{ready.Replace("'", "''", StringComparison.Ordinal)}}', 'ready')
            while ($true) { Start-Sleep -Seconds 1 }
            """;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        using var helper = Process.Start(new ProcessStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            "-NoProfile -NonInteractive -EncodedCommand " + encoded)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        }) ?? throw new InvalidOperationException("The isolated directory-owner helper could not start.");
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(ready) && !helper.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(30))
            {
                Thread.Sleep(100);
            }

            Assert.True(File.Exists(ready), "The helper did not establish its working directory.");
            var moved = working + ".released";
            Assert.Throws<IOException>(() => Directory.Move(working, moved));

            Assert.Null(SetupFileOwners.Release([directory]));

            Assert.True(helper.WaitForExit(10000));
            Directory.Move(working, moved);
            Directory.Delete(moved);
        }
        finally
        {
            if (!helper.HasExited)
            {
                helper.Kill(entireProcessTree: false);
                Assert.True(helper.WaitForExit(10000));
            }
        }
    }

    [Fact]
    public void NativeReleaseEndsOnlyTheOwnedHelperAndReleasesItsLoadedLibrary()
    {
        using SetupTestInstallation installation = new();
        var directory = Path.Combine(installation.Root, "native-file-owner");
        Directory.CreateDirectory(directory);
        var library = Path.Combine(directory, $"wsgm-fixture-{Guid.NewGuid():N}.dll");
        var moved = library + ".released";
        var ready = Path.Combine(installation.Machine, "native-owner-ready.txt");
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        File.Copy(Path.Combine(system, "version.dll"), library);
        var quotedLibrary = library.Replace("'", "''", StringComparison.Ordinal);
        var quotedReady = ready.Replace("'", "''", StringComparison.Ordinal);
        var script = $$"""
            $ownerNativeSource = @'
            using System;
            using System.Runtime.InteropServices;
            public static class WsgmSetupFileOwnerFixture
            {
                [DllImport("kernel32.dll", EntryPoint = "LoadLibraryW", CharSet = CharSet.Unicode, SetLastError = true)]
                public static extern IntPtr LoadLibrary(string path);
            }
            '@
            Add-Type -TypeDefinition $ownerNativeSource -ErrorAction Stop
            $ownerLibraryHandle = [WsgmSetupFileOwnerFixture]::LoadLibrary('{{quotedLibrary}}')
            if ($ownerLibraryHandle -eq [IntPtr]::Zero)
            {
                throw "LoadLibrary failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
            }
            [IO.File]::WriteAllText('{{quotedReady}}', [Diagnostics.Process]::GetCurrentProcess().Id.ToString())
            while ($true) { Start-Sleep -Seconds 1 }
            """;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        using var helper = Process.Start(new ProcessStartInfo(
            Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"),
            "-NoProfile -NonInteractive -EncodedCommand " + encoded)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        }) ?? throw new InvalidOperationException("The isolated file-owner helper could not start.");

        try
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(ready) && !helper.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(30))
            {
                Thread.Sleep(100);
            }

            Assert.True(File.Exists(ready), "The isolated helper did not confirm loading its temporary DLL.");
            Assert.Equal(helper.Id.ToString(), File.ReadAllText(ready));
            Assert.False(helper.HasExited);
            // This establishes a real image lock before exercising Restart Manager. No library
            // export is invoked, and the only terminable process belongs to this fixture.
            var lockFailure = Record.Exception(() => File.Delete(library));
            Assert.True(lockFailure is IOException or UnauthorizedAccessException,
                "The helper's loaded library did not establish a Windows file lock.");

            var refusal = SetupFileOwners.Release([directory]);

            Assert.Null(refusal);
            Assert.True(helper.WaitForExit(10000),
                "Restart Manager did not identify and release the isolated helper's mapped DLL.");
            var stopped = Assert.Single(File.ReadLines(SetupLog.Path),
                line => line.Contains("Ending file owner ", StringComparison.Ordinal));
            Assert.Contains($"PID {helper.Id}", stopped, StringComparison.Ordinal);
            File.Move(library, moved);
            File.Delete(moved);
            Assert.False(File.Exists(moved));
        }
        finally
        {
            // Process retains the exact child handle. Cleanup never resolves a PID to a new
            // process and never stops a child tree or any application started by the user.
            if (!helper.HasExited)
            {
                helper.Kill(entireProcessTree: false);
                Assert.True(helper.WaitForExit(10000), "The owned helper did not finish cleanup.");
            }
        }
    }

    [Fact]
    public void ReleaseStopsOnlyIdentifiedOwnersAndRechecksAfterTheHeldFileIsReleased()
    {
        using SetupTestInstallation installation = new();
        var file = Path.Combine(installation.Root, "App", "WSGM.exe");
        FileStream? held = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        List<string> calls = [];
        FakeProcess process = new()
        {
            OnStop = () =>
            {
                held!.Dispose();
                held = null;
                return true;
            }
        };
        try
        {
            var refusal = SetupFileOwners.Release([file], 900,
                files =>
                {
                    Assert.Equal([file], files);
                    calls.Add("scan");
                    return held is null ? [] : [Owner()];
                }, owner =>
                {
                    Assert.Equal(Owner(), owner);
                    calls.Add("open");
                    return process;
                });

            Assert.Null(refusal);
            Assert.Equal(["scan", "open", "scan"], calls);
            Assert.Equal(1, process.Stops);
            Assert.True(process.Disposed);
            Assert.Equal("old-app", File.ReadAllText(file));
        }
        finally
        {
            held?.Dispose();
        }
    }

    [Fact]
    public void ReleaseNeverOpensOrTerminatesTheCurrentInstaller()
    {
        using SetupTestInstallation installation = new();
        var file = Path.Combine(installation.Root, "Setup", "WSGM.Setup.exe");
        Assert.Null(SetupFileOwners.Release([file], 25, _ => [Owner()],
            _ => throw new InvalidOperationException("The installer must not be opened for termination.")));
        Assert.Equal("old-setup", File.ReadAllText(file));
    }

    [Fact]
    public void ChangedProcessIdentityIsNeverTerminatedAndAStillHeldFileRefusesReplacement()
    {
        using SetupTestInstallation installation = new();
        var file = Path.Combine(installation.Root, "App", "WSGM.exe");
        FakeProcess replacement = new() { Started = 101 };
        var refusal = SetupFileOwners.Release([file], 900, _ => [Owner()], _ => replacement);

        Assert.Equal(0, replacement.Stops);
        Assert.True(replacement.Disposed);
        Assert.Contains("holder (PID 25)", refusal, StringComparison.Ordinal);
        Assert.Contains(file, refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4u, 0u)]
    [InlineData(25u, 1000u)]
    public void ProtectedOwnersRefuseBeforeOpeningAProcess(uint processId, uint applicationType)
    {
        using SetupTestInstallation installation = new();
        var file = Path.Combine(installation.Root, "App", "WSGM.exe");
        var refusal = SetupFileOwners.Release([file], 900,
            _ => [new SetupFileOwners.Owner(processId, 100, "protected", applicationType)],
            _ => throw new InvalidOperationException("A protected process must not be opened."));
        Assert.Contains("Windows protects", refusal, StringComparison.Ordinal);
        Assert.Contains(file, refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplorerClassificationDoesNotExemptAnActualInstallationFileHolder()
    {
        using SetupTestInstallation installation = new();
        var file = Path.Combine(installation.Root, "App", "WSGM.exe");
        FakeProcess process = new() { Image = "explorer.exe" };
        var scans = 0;

        Assert.Null(SetupFileOwners.Release([file], 900,
            _ => scans++ == 0 ? [new SetupFileOwners.Owner(25, 100, "Explorer", 4)] : [], _ => process));
        Assert.Equal(1, process.Stops);
    }

    [Theory]
    [InlineData(true, true, "critical")]
    [InlineData(false, false, "10 seconds")]
    public void CriticalOrUnreleasedOwnersProduceAnActionableRefusal(bool critical, bool exits, string reason)
    {
        using SetupTestInstallation installation = new();
        var file = Path.Combine(installation.Root, "App", "WSGM.exe");
        FakeProcess process = new() { Critical = critical, OnStop = () => exits };
        var refusal = SetupFileOwners.Release([file], 900, _ => [Owner()], _ => process);

        Assert.Equal(critical ? 0 : 1, process.Stops);
        Assert.Contains(reason, refusal, StringComparison.Ordinal);
        Assert.Contains(file, refusal, StringComparison.Ordinal);
        Assert.Contains("PID 25", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ScanFailuresNameTheExactReplacementPath()
    {
        using SetupTestInstallation installation = new();
        var file = Path.Combine(installation.Root, "App", "WSGM.exe");
        var refusal = SetupFileOwners.Release([file], 900,
            _ => throw new Win32Exception(5), _ => throw new InvalidOperationException());
        Assert.Contains(file, refusal, StringComparison.Ordinal);
        Assert.Contains("before changing", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShellAnchorMustSettleItsRecoveryBeforeAnActualFileOwnerStop(bool settled)
    {
        using SetupTestInstallation installation = new();
        var file = Path.Combine(installation.Root, "App", "WSGM.exe");
        FakeProcess anchor = new() { Image = "WSGM.ShellAnchor.exe" };
        var scans = 0;
        var refusal = SetupFileOwners.Release([file], 900,
            _ => scans++ == 0 ? [Owner()] : [], _ => anchor, () => settled);

        Assert.Equal(settled ? 1 : 0, anchor.Stops);
        if (settled)
        {
            Assert.Null(refusal);
        }
        else
        {
            Assert.Contains("Explorer recovery", refusal, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MissingFreshInstallPathsRequireNoScanOrStop()
    {
        using SetupTestInstallation installation = new();
        Assert.Null(SetupFileOwners.Release([Path.Combine(installation.Root, "absent")], 900,
            _ => throw new InvalidOperationException("No files need a scan."),
            _ => throw new InvalidOperationException("No process needs a stop.")));
    }

    [Fact]
    public void FileOwnerRefusalHappensAfterNormalShutdownAndBeforeAnyTransactionOrSwap()
    {
        using SetupTestInstallation installation = new();
        var original = Path.Combine(installation.Root, "App", "WSGM.exe");
        installation.Runtime.OnReleaseFiles = paths =>
        {
            Assert.Contains(Path.Combine(installation.Root, "App"), paths);
            Assert.Contains("ForceStop WSGM.exe", installation.Runtime.Calls);
            Assert.Equal("old-app", File.ReadAllText(original));
            return "Held by holder (PID 25): " + original;
        };
        var steps = installation.InstallPlan().Take(2).ToArray();

        Assert.False(installation.Engine.Run(steps, () => { }));
        Assert.Equal(StepState.Failed, steps[0].State);
        Assert.Equal(StepState.Waiting, steps[1].State);
        Assert.Contains(original, steps[0].Note, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(installation.Machine, "setup-transaction.json")));
        Assert.False(Directory.Exists(Path.Combine(installation.Root, "App.previous")));
        Assert.Equal("old-app", File.ReadAllText(original));
    }

    [Fact]
    public void StoredSetupIsScannedAgainBeforeOverwriteAndARefusalRollsBackTheApp()
    {
        using SetupTestInstallation installation = new();
        var setup = Path.Combine(installation.Root, "Setup", "WSGM.Setup.exe");
        var refused = false;
        installation.Runtime.OnReleaseFiles = paths =>
        {
            if (!refused && paths.Count == 3 && paths.Contains(setup))
            {
                Assert.Equal("old-setup", File.ReadAllText(setup));
                refused = true;
                return "Held stored setup: " + setup;
            }

            return null;
        };
        var steps = installation.InstallPlan().Take(3).ToArray();
        Assert.False(installation.Engine.Run(steps, () => { }));

        Assert.True(refused);
        Assert.Equal(StepState.Failed, steps[2].State);
        Assert.Equal("old-app", File.ReadAllText(Path.Combine(installation.Root, "App", "WSGM.exe")));
        Assert.Equal("old-setup", File.ReadAllText(setup));
        Assert.False(installation.Engine.RollbackIncomplete);
        Assert.False(File.Exists(Path.Combine(installation.Machine, "setup-transaction.json")));
    }

    private static SetupFileOwners.Owner Owner()
    {
        return new SetupFileOwners.Owner(25, 100, "holder", 0);
    }

    private sealed class FakeProcess : SetupFileOwners.IProcess
    {
        public ulong Started { get; init; } = 100;
        public bool Critical { get; init; }
        public string Image { get; init; } = "holder.exe";
        public Func<bool>? OnStop { get; init; }
        public int Stops { get; private set; }
        public bool Disposed { get; private set; }

        public bool Stop(TimeSpan budget)
        {
            Assert.Equal(TimeSpan.FromSeconds(10), budget);
            Stops++;
            return OnStop?.Invoke() ?? true;
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
