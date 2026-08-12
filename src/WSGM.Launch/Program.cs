using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Launch;

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

            if (!CommandLine.TryParse(args, out var options, out var error))
            {
                LaunchLog.Error(error!);
                Console.Error.WriteLine(error);
                Console.Error.WriteLine();
                Console.Error.WriteLine(CommandLine.UsageText);
                return 64;
            }

            if (options.Help)
            {
                Console.WriteLine(CommandLine.UsageText);
                return 0;
            }
            if (options.Status)
            {
                return RunStatus(options);
            }
            if (options.Rescan)
            {
                return RunRescan(options);
            }

            return await RunWrapperAsync(options);
        }
        catch (Exception ex)
        {
            LaunchLog.Error($"Unhandled wrapper failure: {ex}");
            return 1;
        }
    }

    private static async Task<int> RunWrapperAsync(LaunchOptions options)
    {
        var elevated = Elevation.IsCurrentProcessElevated();
        LaunchLog.Info($"Steam wrapper invoked (elevated={elevated?.ToString() ?? "unknown"}, " +
                       $"deelevate={options.Deelevate}, inputLease={options.InputLease}, " +
                       $"target={Path.GetFileName(options.Command[0])}, " +
                       $"argumentCount={options.Command.Length - 1}).");

        // Without de-elevation the native wrapper is strictly better: it starts the
        // target suspended, assigns it to a job object and waits for the whole
        // process tree, so a launcher that spawns the real game and exits still
        // holds the lease. The de-elevation path cannot use it — it would create
        // the process from this elevated parent, which is the thing we are avoiding.
        if (options.InputLease && !options.Deelevate)
        {
            return RunLeaseWrapped(options);
        }

        using var lease = options.InputLease ? SteamInputLeaseHost.TryAcquire(options) : null;
        var payload = LaunchPayload.Capture(options.Command);
        return elevated == false
            ? await LaunchAndWaitAsync(payload)
            : await RunElevatedParentAsync(payload);
    }

    private static int RunLeaseWrapped(LaunchOptions options)
    {
        try
        {
            using var client = SteamInputLeaseHost.CreateClient(options);
            Console.WriteLine("Acquiring Steam Input block lease...");
            var exitCode = client.RunWrapped(options.Command);
            Console.WriteLine("Game process tree exited; Steam Input unblocked.");
            LaunchLog.Info($"Steam Input lease wrapper finished with exit code {exitCode}.");
            return unchecked((int)exitCode);
        }
        catch (Exception ex)
        {
            // Fail open: a controller Steam refuses to let go of is a degraded
            // experience, but a game that never starts is a broken one.
            LaunchLog.Error($"Steam Input lease wrapper failed: {ex.Message}. Launching without it.");
            Console.Error.WriteLine($"Steam Input block unavailable: {ex.Message}");
            var payload = LaunchPayload.Capture(options.Command);
            return LaunchAndWaitAsync(payload).GetAwaiter().GetResult();
        }
    }

    private static int RunStatus(LaunchOptions options)
    {
        try
        {
            using var client = SteamInputLeaseHost.CreateClient(options);
            var status = client.GetStatus();
            Console.WriteLine(
                $"Payload active; leases={status.LeaseCount}, tracked HID handles={status.HidHandleCount}, " +
                $"handles revoked by last transition={status.LastRevokedHandleCount}.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunRescan(LaunchOptions options)
    {
        try
        {
            using var client = SteamInputLeaseHost.CreateClient(options);
            var result = client.Rescan();
            Console.WriteLine(
                $"Requested Steam controller discovery (scan counter {result.ScanCountBefore} -> " +
                $"{result.ScanCountAfter}).");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunElevatedParentAsync(LaunchPayload payload)
    {
        var pipeName = $"WSGM.Launch.{Environment.ProcessId}.{Guid.NewGuid():N}";
        // NOT CurrentUserOnly: this parent is elevated, and CurrentUserOnly grants
        // the pipe to the token's OWNER — for an elevated admin that is
        // BUILTIN\Administrators, a deny-only SID in the medium child's filtered
        // token, so the child's connect fails "Access to the path is denied"
        // (device-observed). Grant the real USER SID explicitly; it is enabled in
        // both the elevated parent's and the medium child's token.
        var pipeSecurity = new PipeSecurity();
        using (var identity = WindowsIdentity.GetCurrent())
        {
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        using var pipe = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity);

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executablePath))
        {
            LaunchLog.Error("Cannot determine the de-elevation helper path.");
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
                LaunchLog.Error($"Medium-integrity launch failed: {error}");
                return 1;
            }
            if (started != 1)
            {
                LaunchLog.Error($"Medium-integrity helper returned invalid status {started}.");
                return 1;
            }

            var processId = await PipeProtocol.ReadInt32Async(pipe, handshake.Token);
            LaunchLog.Info($"Medium-integrity target started (pid {processId}); waiting for exit.");
            // No timeout after launch: Steam expects its launch-option wrapper to
            // remain alive for the entire game/emulator lifetime.
            var exitCode = await PipeProtocol.ReadInt32Async(pipe, CancellationToken.None);
            LaunchLog.Info($"Medium-integrity target pid {processId} exited with {exitCode}.");
            return exitCode;
        }
        catch (OperationCanceledException)
        {
            LaunchLog.Error("Timed out waiting for the medium-integrity helper.");
            return 1;
        }
        catch (Exception ex)
        {
            LaunchLog.Error($"Medium-integrity helper communication failed: {ex.Message}");
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
                LaunchLog.Error(error);
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
            LaunchLog.Info($"Launched {Path.GetFileName(payload.Arguments[0])} at medium integrity " +
                              $"(pid {process.Id}); preserving Steam wrapper lifetime.");

            using var disconnectCancellation = new CancellationTokenSource();
            var parentDisconnected = WaitForParentDisconnectAsync(pipe, disconnectCancellation.Token);
            var processExited = process.WaitForExitAsync();
            var completed = await Task.WhenAny(processExited, parentDisconnected);
            if (completed == parentDisconnected)
            {
                LaunchLog.Info($"Steam wrapper exited before target pid {process.Id}; stopping its process tree.");
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
            LaunchLog.Error($"Medium-integrity child failed: {ex.Message}");
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
        LaunchLog.Info($"Wrapper already has medium integrity; target started directly (pid {process.Id}).");
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

        // De-elevation is the whole point, so run the target at THIS (medium)
        // integrity even when it carries a RUNASADMIN AppCompat flag or a
        // highestAvailable/requireAdministrator manifest. Without this a medium
        // CreateProcess fails ERROR_ELEVATION_REQUIRED (740, device-observed) —
        // UseShellExecute=false cannot elevate. RunAsInvoker tells the AppCompat
        // engine to drop the elevation requirement and run as the caller. Set it
        // both on this process and in the child's environment so the shim sees it
        // whichever it reads; prepend so any existing layer is preserved.
        startInfo.Environment.TryGetValue("__COMPAT_LAYER", out var existingLayer);
        var compatLayer = string.IsNullOrEmpty(existingLayer)
            ? "RunAsInvoker"
            : $"RunAsInvoker {existingLayer}";
        startInfo.Environment["__COMPAT_LAYER"] = compatLayer;
        Environment.SetEnvironmentVariable("__COMPAT_LAYER", compatLayer);
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
