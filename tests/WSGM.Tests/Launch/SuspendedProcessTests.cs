extern alias launch;
using System.ComponentModel;
using System.Diagnostics;
using launch::WSGM.Launch;
using WSGM.Testing;

namespace WSGM.Tests.Launch;

public sealed class SuspendedProcessTests
{
    [Fact]
    public void AnInvalidPrimaryTokenIsRefusedWithoutStartingAtCallerIntegrity()
    {
        using var temporary = new TemporaryDirectory();
        ProcessStartInfo start = new(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            WorkingDirectory = temporary.Root
        };
        // If the token were ignored, this would create the marker at the caller's integrity.
        var marker = Path.Combine(temporary.Root, "must-not-run.txt");
        start.Arguments = $"/c echo launched>\"{marker}\"";

        Assert.Throws<launch::WSGM.Core.ProcessCreationException>(() =>
            SuspendedProcess.Start(start, primaryToken: -1));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void AContainmentFailureDoesNotAuthorizeAnotherCreationRoute()
    {
        ProcessStartInfo start = new(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            WorkingDirectory = Environment.CurrentDirectory
        };
        start.Arguments = "/c exit /b 17";
        var refused = new Win32Exception(5, "Containment refused after creation.");

        var actual = Assert.Throws<Win32Exception>(() =>
            launch::WSGM.Core.ContainedProcessStart.Start(start, _ => throw refused));

        Assert.Same(refused, actual);
    }

    [Fact]
    public async Task AnUnqualifiedTargetRetainsWindowsSearchAndImpliedExe()
    {
        using var temporary = new TemporaryDirectory();
        ProcessStartInfo start = new("cmd")
        {
            WorkingDirectory = temporary.Root
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("exit");
        start.ArgumentList.Add("/b");
        start.ArgumentList.Add("17");
        var target = SuspendedProcess.Start(start);
        using var process = target.Process;
        using var job = target.Job;
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(17, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                job.TerminateTree();
            }
        }
    }

    [Fact]
    public void EnvironmentBlockPreservesValuesSortsNamesAndTerminatesTwice()
    {
        ProcessStartInfo start = new();
        start.Environment.Clear();
        start.Environment["zebra"] = "spaces and quotes \"\"";
        start.Environment["Alpha"] = "";
        start.Environment["SteamAppId"] = "42";
        Assert.Equal("Alpha=\0SteamAppId=42\0zebra=spaces and quotes \"\"\0\0",
            SuspendedProcess.BuildEnvironment(start));
    }

    [Fact]
    public void EmptyEnvironmentStillHasItsDoubleTerminator()
    {
        ProcessStartInfo start = new();
        start.Environment.Clear();
        Assert.Equal("\0\0", SuspendedProcess.BuildEnvironment(start));
    }

    [Fact]
    public void EmbeddedNulIsRefusedBeforeNativeCreation()
    {
        ProcessStartInfo start = new();
        start.Environment.Clear();
        start.Environment["value"] = "a\0b";
        Assert.Throws<ArgumentException>(() => SuspendedProcess.BuildEnvironment(start));
    }
}
