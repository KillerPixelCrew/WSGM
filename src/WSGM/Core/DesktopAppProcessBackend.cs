using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>Current-session adapter for the explicitly listed desktop integrations.</summary>
internal sealed class DesktopAppProcessBackend : IDesktopAppBackend
{
    public IReadOnlyList<DesktopAppInstance> Capture(DesktopAppRule rule)
    {
        List<DesktopAppInstance> instances = [];
        foreach (var name in rule.ProcessNames)
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                foreach (var process in processes)
                {
                    if (process.HasExited || process.SessionId != WindowFinder.CurrentSessionId)
                    {
                        continue;
                    }

                    var path = process.MainModule?.FileName
                               ?? throw new InvalidOperationException($"Cannot capture {rule.Name}'s executable.");
                    var integrity = NativeShellProcess.Inspect(checked((uint)process.Id)).Integrity;
                    if (integrity is not (NativeIntegrityLevel.Medium or NativeIntegrityLevel.High))
                    {
                        throw new InvalidOperationException($"Cannot preserve {rule.Name}'s process integrity.");
                    }

                    var elevated = integrity is NativeIntegrityLevel.High;
                    if (elevated && NativeShellProcess.Inspect(checked((uint)Environment.ProcessId)).Integrity
                            is not NativeIntegrityLevel.High)
                    {
                        throw new InvalidOperationException($"Cannot restore elevated {rule.Name} from this session.");
                    }

                    instances.Add(new DesktopAppInstance(rule, process.Id, process.StartTime.ToUniversalTime(), path,
                        elevated));
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return instances;
    }

    public async Task StopAsync(DesktopAppInstance instance, CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(instance.ProcessId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (process)
        {
            if (process.HasExited)
            {
                return;
            }

            // PID reuse is not authority to stop a new process, even with the same filename.
            if (process.SessionId != WindowFinder.CurrentSessionId
                || process.StartTime.ToUniversalTime() != instance.StartTime
                || !string.Equals(process.MainModule?.FileName, instance.ExecutablePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{instance.Rule.Name}'s process identity changed.");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            if (instance.Rule.CloseMainWindowFirst && process.MainWindowHandle != 0)
            {
                if (!process.CloseMainWindow())
                {
                    throw new InvalidOperationException($"{instance.Rule.Name}'s editor could not close safely.");
                }

                do
                {
                    await Task.Delay(100, timeout.Token).ConfigureAwait(false);
                    process.Refresh();
                    if (process.HasExited)
                    {
                        return;
                    }
                } while (process.MainWindowHandle != 0);
            }

            if (instance.Rule.ExitWindowClass is { } windowClass)
            {
                nint window = 0;
                var posted = false;
                while ((window = NativeMethods.FindWindowExW(0, window, windowClass, null)) != 0)
                {
                    NativeMethods.GetWindowThreadProcessId(window, out var owner);
                    if (owner != instance.ProcessId)
                    {
                        continue;
                    }

                    // The hidden event window handles a normal application exit. Never fall back
                    // to Kill: that marks Wallpaper Engine's next launch as crash recovery.
                    posted = NativeMethods.PostMessageW(window, 0x0010, 0, 0); // WM_CLOSE
                    break;
                }

                if (!posted)
                {
                    throw new InvalidOperationException($"Could not request {instance.Rule.Name} exit.");
                }
            }
            else if (instance.Rule.ExitCommand is { } command)
            {
                var commandPath = Path.Combine(Path.GetDirectoryName(instance.ExecutablePath)!, command);
                using var request = Process.Start(new ProcessStartInfo(commandPath, instance.Rule.ExitArguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(commandPath)!
                }) ?? throw new InvalidOperationException($"Could not request {instance.Rule.Name} exit.");
                await request.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                if (request.ExitCode != 0)
                {
                    throw new InvalidOperationException($"{instance.Rule.Name} exit command failed.");
                }
            }
            else
            {
                // These entries have no external full-exit command. Termination is restricted to
                // the captured primary process and its descendants, never a service/name sweep.
                process.Kill(true);
            }

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
    }

    public bool IsRunning(DesktopAppInstance instance)
    {
        return Capture(instance.Rule).Any(current =>
            string.Equals(current.ExecutablePath, instance.ExecutablePath, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ScheduledTaskLaunchDisposition> RestartAsync(DesktopAppInstance instance, DateTimeOffset deadline)
    {
        var appDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
        if (appDeadline < deadline)
        {
            deadline = appDeadline;
        }

        ScheduledTaskLaunchDisposition result;
        if (instance.Elevated)
        {
            using var launched = Process.Start(
                new ProcessStartInfo(instance.ExecutablePath, instance.Rule.RestartArguments)
                {
                    UseShellExecute = !instance.Rule.CreateNoWindow,
                    Verb = instance.Rule.CreateNoWindow ? "" : "runas",
                    CreateNoWindow = instance.Rule.CreateNoWindow,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(instance.ExecutablePath)!
                });
            // ShellExecute can succeed without returning a new process handle.
            result = ScheduledTaskLaunchDisposition.Dispatched;
        }
        else
        {
            result = await UnelevatedLauncher.TryStartViaScheduledTaskAsync(
                    instance.ExecutablePath, instance.Rule.RestartArguments, deadline,
                    Path.GetDirectoryName(instance.ExecutablePath), CancellationToken.None)
                .ConfigureAwait(false);
        }

        // Let a supervisor see its hook already running, and let a later same-path record observe
        // this launch. Never dispatch again if the scheduler's result is uncertain.
        try
        {
            while (result is ScheduledTaskLaunchDisposition.Dispatched && DateTimeOffset.UtcNow < deadline)
            {
                if (IsRunning(instance))
                {
                    break;
                }

                await Task.Delay(100).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            return ScheduledTaskLaunchDisposition.Unknown;
        }

        return result;
    }
}
