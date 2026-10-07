extern alias launch;
using System.Diagnostics;
using launch::WSGM.Launch;
using WSGM.Testing;

namespace WSGM.Tests.Launch;

public sealed class SuspendedProcessTests
{
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
