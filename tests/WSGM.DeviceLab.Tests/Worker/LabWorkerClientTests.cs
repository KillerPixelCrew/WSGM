using System.Runtime.InteropServices;
using WSGM.DeviceLab.Worker;

namespace WSGM.DeviceLab.Tests.Worker;

public sealed class LabWorkerClientTests
{
    [Fact]
    public async Task Start_CompletesTheHandshakeWithARealWorker()
    {
        // The worker reads its secret to end of stream before it says hello. A client that held the
        // authorization pipe open until the hello deadlocked both processes (2026-09-25, 0.1.2).
        var starting = Task.Run(() => LabWorkerClient.Start(ThroughDotnetHost()));

        var client = await starting.WaitAsync(TimeSpan.FromSeconds(15));

        client.Dispose();
    }

    /// <summary>
    ///     The self-contained apphost copied beside the tests cannot run (its runtime configuration
    ///     names no framework), so the worker assembly runs through the dotnet host with this test
    ///     assembly's runtime configuration and dependency file.
    /// </summary>
    private static LabWorkerLaunch ThroughDotnetHost()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var entry = typeof(LabWorkerClientTests).Assembly.GetName().Name!;
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrEmpty(host) || !File.Exists(host))
        {
            // shared\Microsoft.NETCore.App\<version>\ sits three levels below the dotnet root.
            var root = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
            host = Path.Combine(root, "dotnet.exe");
        }

        return new LabWorkerLaunch(host,
        [
            "exec",
            "--runtimeconfig", Path.Combine(baseDirectory, entry + ".runtimeconfig.json"),
            "--depsfile", Path.Combine(baseDirectory, entry + ".deps.json"),
            Path.Combine(baseDirectory, "wsgm-device.dll")
        ]);
    }
}
