extern alias launch;
using System.Diagnostics;
using launch::WSGM.Launch;

namespace WSGM.Tests.Launch;

public sealed class SuspendedProcessTests
{
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
