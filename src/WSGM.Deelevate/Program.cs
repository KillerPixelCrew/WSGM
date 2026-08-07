using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;

namespace WSGM.Deelevate;

internal static class Program
{
    private const string ChildArgument = "--medium-child";
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 2 &&
                string.Equals(args[0], ChildArgument, StringComparison.OrdinalIgnoreCase))
            {
                return await RunMediumChildAsync(args[1]);
            }

            if (args.Length == 0)
            {
                DeelevateLog.Error("No target command was supplied. Expected: WSGM.Deelevate.exe <program> [arguments].");
                return 64;
            }

            var elevated = Elevation.IsCurrentProcessElevated();
            DeelevateLog.Info($"Steam wrapper invoked (elevated={elevated?.ToString() ?? "unknown"}, " +
                              $"target={Path.GetFileName(args[0])}, argumentCount={args.Length - 1}).");
            var payload = LaunchPayload.Capture(args);
            return elevated == false
                ? await LaunchAndWaitAsync(payload)
                : await RunElevatedParentAsync(payload);
        }
        catch (Exception ex)
        {
            DeelevateLog.Error($"Unhandled wrapper failure: {ex}");
            return 1;
        }
    }

    private static async Task<int> RunElevatedParentAsync(LaunchPayload payload)
    {
        var pipeName = $"WSGM.Deelevate.{Environment.ProcessId}.{Guid.NewGuid():N}";
        using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executablePath))
        {
            DeelevateLog.Error("Cannot determine the de-elevation helper path.");
            return 1;
        }

        var taskName = ScheduledTaskLauncher.Start(executablePath, pipeName);
        if (taskName is null)
        {
            return 1;
        }

        try
        {
            using var handshake = new CancellationTokenSource(HandshakeTimeout);
            await pipe.WaitForConnectionAsync(handshake.Token);
            // /Run has already created the helper process; deleting the task does
            // not terminate its running action and prevents stale task buildup.
            ScheduledTaskLauncher.Delete(taskName);
            taskName = null;

            await payload.WriteAsync(pipe, handshake.Token);
            await pipe.FlushAsync(handshake.Token);

            var started = await PipeProtocol.ReadInt32Async(pipe, handshake.Token);
            if (started == 0)
            {
                var error = await PipeProtocol.ReadStringAsync(pipe, 64 * 1024, handshake.Token);
                DeelevateLog.Error($"Medium-integrity launch failed: {error}");
                return 1;
            }
            if (started != 1)
            {
                DeelevateLog.Error($"Medium-integrity helper returned invalid status {started}.");
                return 1;
            }

            var processId = await PipeProtocol.ReadInt32Async(pipe, handshake.Token);
            DeelevateLog.Info($"Medium-integrity target started (pid {processId}); waiting for exit.");
            // No timeout after launch: Steam expects its launch-option wrapper to
            // remain alive for the entire game/emulator lifetime.
            var exitCode = await PipeProtocol.ReadInt32Async(pipe, CancellationToken.None);
            DeelevateLog.Info($"Medium-integrity target pid {processId} exited with {exitCode}.");
            return exitCode;
        }
        catch (OperationCanceledException)
        {
            DeelevateLog.Error("Timed out waiting for the medium-integrity helper.");
            return 1;
        }
        catch (Exception ex)
        {
            DeelevateLog.Error($"Medium-integrity helper communication failed: {ex.Message}");
            return 1;
        }
        finally
        {
            ScheduledTaskLauncher.Delete(taskName);
        }
    }

    private static async Task<int> RunMediumChildAsync(string pipeName)
    {
        using var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.None);
        var launchResponseSent = false;
        try
        {
            using var handshake = new CancellationTokenSource(HandshakeTimeout);
            await pipe.ConnectAsync(handshake.Token);
            if (Elevation.IsCurrentProcessElevated() != false)
            {
                const string error = "Task Scheduler did not provide a medium-integrity token; UAC may be disabled.";
                DeelevateLog.Error(error);
                await WriteLaunchFailureAsync(pipe, error, handshake.Token);
                return 1;
            }

            var payload = await LaunchPayload.ReadAsync(pipe, handshake.Token);

            using var process = Start(payload);
            if (process is null)
            {
                await WriteLaunchFailureAsync(pipe, "Process.Start returned no process.", handshake.Token);
                return 1;
            }

            await PipeProtocol.WriteInt32Async(pipe, 1, handshake.Token);
            await PipeProtocol.WriteInt32Async(pipe, process.Id, handshake.Token);
            await pipe.FlushAsync(handshake.Token);
            launchResponseSent = true;
            DeelevateLog.Info($"Launched {Path.GetFileName(payload.Arguments[0])} at medium integrity " +
                              $"(pid {process.Id}); preserving Steam wrapper lifetime.");

            using var disconnectCancellation = new CancellationTokenSource();
            var parentDisconnected = WaitForParentDisconnectAsync(pipe, disconnectCancellation.Token);
            var processExited = process.WaitForExitAsync();
            var completed = await Task.WhenAny(processExited, parentDisconnected);
            if (completed == parentDisconnected)
            {
                DeelevateLog.Info($"Steam wrapper exited before target pid {process.Id}; stopping its process tree.");
                try { process.Kill(entireProcessTree: true); } catch { }
                await process.WaitForExitAsync();
                return 1;
            }

            disconnectCancellation.Cancel();
            try { await parentDisconnected; } catch (OperationCanceledException) { }
            await PipeProtocol.WriteInt32Async(pipe, process.ExitCode, CancellationToken.None);
            await pipe.FlushAsync(CancellationToken.None);
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            DeelevateLog.Error($"Medium-integrity child failed: {ex.Message}");
            if (!launchResponseSent && pipe.IsConnected)
            {
                try { await WriteLaunchFailureAsync(pipe, ex.Message, CancellationToken.None); } catch { }
            }
            return 1;
        }
    }

    private static async Task<int> LaunchAndWaitAsync(LaunchPayload payload)
    {
        using var process = Start(payload);
        if (process is null)
        {
            return 1;
        }
        DeelevateLog.Info($"Wrapper already has medium integrity; target started directly (pid {process.Id}).");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    internal static Process? Start(LaunchPayload payload)
    {
        if (payload.Arguments.Length == 0)
        {
            throw new InvalidDataException("The launch payload contains no target command.");
        }

        var workingDirectory = Directory.Exists(payload.WorkingDirectory)
            ? payload.WorkingDirectory
            : SafeTargetDirectory(payload.Arguments[0]);
        var target = payload.Arguments[0];
        if (!Path.IsPathFullyQualified(target))
        {
            var candidate = Path.Combine(workingDirectory, target);
            if (File.Exists(candidate))
            {
                target = candidate;
            }
        }

        var startInfo = new ProcessStartInfo(target)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        for (var i = 1; i < payload.Arguments.Length; i++)
        {
            startInfo.ArgumentList.Add(payload.Arguments[i]);
        }

        // Task Scheduler supplies a clean user environment, not Steam's dynamic
        // SteamAppId/GameId variables. Recreate the elevated wrapper's environment
        // so the target observes the same launch contract as a direct Steam child.
        startInfo.Environment.Clear();
        foreach (var pair in payload.EnvironmentVariables)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }
        return Process.Start(startInfo);
    }

    private static string SafeTargetDirectory(string target)
    {
        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(target)) ?? Environment.CurrentDirectory;
        }
        catch
        {
            return Environment.CurrentDirectory;
        }
    }

    private static async Task WriteLaunchFailureAsync(
        Stream pipe,
        string error,
        CancellationToken cancellationToken)
    {
        await PipeProtocol.WriteInt32Async(pipe, 0, cancellationToken);
        await PipeProtocol.WriteStringAsync(pipe, error, cancellationToken);
        await pipe.FlushAsync(cancellationToken);
    }

    private static async Task WaitForParentDisconnectAsync(Stream pipe, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        try
        {
            while (await pipe.ReadAsync(buffer, cancellationToken) != 0)
            {
                // The parent deliberately sends no bytes after the payload. Ignore
                // anything unexpected and continue monitoring its pipe lifetime.
            }
        }
        catch (IOException)
        {
            // A broken pipe is the expected signal when Steam kills the wrapper.
        }
    }
}
