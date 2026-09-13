using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Wsgm.UwpSpike;

internal enum LaunchMode
{
    /// Direct IApplicationActivationManager activation, no script interpreter in the
    /// chain. This is the case the issue's process-tree hypothesis is really about.
    Aam,

    /// Explorer-mediated activation through the shell:AppsFolder path for the AUMID.
    Shell,

    /// The shape most existing Xbox-to-Steam wrappers use, kept as the control case.
    PowerShell,

    /// A conventional Win32 child process, the reference case for what Steam's overlay
    /// does when lineage is intact.
    Exe,
}

internal sealed record ActivationResult(bool Succeeded, int SeedPid, string Detail);

internal static class Activation
{
    internal static ActivationResult Launch(Options options, SpikeLog log)
    {
        switch (options.Mode)
        {
            case LaunchMode.Aam:
                return ActivateWithManager(options, log);
            case LaunchMode.Shell:
                return ActivateWithShell(options, log);
            case LaunchMode.PowerShell:
                return ActivateWithPowerShell(options, log);
            case LaunchMode.Exe:
                return StartExecutable(options, log);
            default:
                return new ActivationResult(false, 0, $"Unhandled mode {options.Mode}.");
        }
    }

    private static ActivationResult ActivateWithManager(Options options, SpikeLog log)
    {
        object? manager = null;
        try
        {
            manager = new Native.ApplicationActivationManager();
            var activator = (Native.IApplicationActivationManager)manager;

            // Without this the activated app is denied the foreground, which for a game
            // launched from Big Picture looks exactly like a failed launch.
            var allow = Native.CoAllowSetForegroundWindow(manager, IntPtr.Zero);
            log.Info($"CoAllowSetForegroundWindow: 0x{allow:X8}");

            var hresult = activator.ActivateApplication(
                options.Aumid!,
                string.IsNullOrEmpty(options.GameArguments) ? null : options.GameArguments,
                Native.AoNoErrorUi | Native.AoNoSplashScreen,
                out var pid);

            if (hresult < 0)
            {
                return new ActivationResult(false, 0, $"ActivateApplication failed with 0x{hresult:X8} ({new COMException(string.Empty, hresult).Message.Trim()}).");
            }

            return new ActivationResult(true, (int)pid, $"ActivateApplication returned pid {pid}.");
        }
        catch (Exception ex)
        {
            return new ActivationResult(false, 0, $"ActivateApplication threw: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (manager is not null && Marshal.IsComObject(manager))
            {
                Marshal.FinalReleaseComObject(manager);
            }
        }
    }

    private static ActivationResult ActivateWithShell(Options options, SpikeLog log)
    {
        var target = @"shell:AppsFolder\" + options.Aumid;
        try
        {
            using var started = Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                Arguments = options.GameArguments ?? string.Empty,
            });

            // ShellExecute hands the request to Explorer, so there is normally no process
            // to return here at all. That absence is itself part of the result.
            var pid = started?.Id ?? 0;
            log.Info($"ShellExecute on {target} returned {(pid == 0 ? "no process handle" : "pid " + pid)}.");
            return new ActivationResult(true, pid, $"ShellExecute issued for {target}.");
        }
        catch (Exception ex)
        {
            return new ActivationResult(false, 0, $"ShellExecute failed: {ex.Message}");
        }
    }

    private static ActivationResult ActivateWithPowerShell(Options options, SpikeLog log)
    {
        var command = $"Start-Process -FilePath 'shell:AppsFolder\\{options.Aumid}'";
        try
        {
            using var started = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                ArgumentList =
                {
                    "-NoProfile",
                    "-NonInteractive",
                    "-ExecutionPolicy", "Bypass",
                    "-Command", command,
                },
            });

            var pid = started?.Id ?? 0;
            log.Info($"powershell.exe started as pid {pid} running: {command}");
            return new ActivationResult(true, pid, "PowerShell-mediated activation issued.");
        }
        catch (Exception ex)
        {
            return new ActivationResult(false, 0, $"PowerShell activation failed: {ex.Message}");
        }
    }

    private static ActivationResult StartExecutable(Options options, SpikeLog log)
    {
        try
        {
            var info = new ProcessStartInfo(options.Target!)
            {
                UseShellExecute = false,
                WorkingDirectory = System.IO.Path.GetDirectoryName(options.Target!) ?? Environment.CurrentDirectory,
            };
            // Steam's ignore list includes its own virtual controllers. Controlled
            // children must be able to enumerate those devices.
            info.Environment.Remove("SDL_GAMECONTROLLER_IGNORE_DEVICES");
            if (!string.IsNullOrEmpty(options.GameArguments))
            {
                info.Arguments = options.GameArguments;
            }

            using var started = Process.Start(info);
            var pid = started?.Id ?? 0;
            log.Info($"Started {options.Target} as pid {pid}.");
            return new ActivationResult(pid != 0, pid, $"CreateProcess returned pid {pid}.");
        }
        catch (Exception ex)
        {
            return new ActivationResult(false, 0, $"CreateProcess failed: {ex.Message}");
        }
    }
}
