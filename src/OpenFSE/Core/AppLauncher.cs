using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace OpenFSE.Core;

/// <summary>Starts apps normally, elevated (runas), or via protocol. Ported from the
/// battle-tested AnyFSE launch logic (MIT).</summary>
public static class AppLauncher
{
    private const int ErrorCancelled = 1223;
    private const int ErrorElevationRequired = 740;

    public sealed record LaunchResult(Process? Process, bool Started, bool ElevationDeclined);

    public static LaunchResult Start(string path, string args, bool elevated)
    {
        if (path.Contains("://"))
        {
            return StartProtocol(path);
        }
        return elevated ? StartElevated(path, args) : StartNormal(path, args);
    }

    public static LaunchResult StartProtocol(string protocol)
    {
        try
        {
            Process.Start(new ProcessStartInfo(protocol) { UseShellExecute = true });
            Log.Info($"Started protocol: {protocol}");
            return new LaunchResult(null, true, false);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to start protocol {protocol}", ex);
            return new LaunchResult(null, false, false);
        }
    }

    private static LaunchResult StartNormal(string path, string args, bool retryWithElevation = true)
    {
        try
        {
            var psi = new ProcessStartInfo(path, args)
            {
                UseShellExecute = false,
                WorkingDirectory = SafeDirectory(path),
            };
            var process = Process.Start(psi);
            Log.Info($"Started: {path} {args} (pid {process?.Id.ToString() ?? "?"})");
            return new LaunchResult(process, process is not null, false);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorElevationRequired && retryWithElevation)
        {
            // Exe has the "Run as administrator" compat flag — honor it.
            Log.Warn($"{path} requires elevation (740), retrying via runas");
            return StartElevated(path, args);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorElevationRequired)
        {
            Log.Error($"{path} still requires elevation after the UAC prompt was declined", ex);
            return new LaunchResult(null, false, true);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to start {path}", ex);
            return new LaunchResult(null, false, false);
        }
    }

    private static LaunchResult StartElevated(string path, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(path, args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = SafeDirectory(path),
            };
            var process = Process.Start(psi); // may be null (no new process resource)
            Log.Info($"Started elevated: {path} {args} (pid {(process is null ? "unknown" : process.Id)})");
            return new LaunchResult(process, true, false);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Log.Warn($"Elevation DECLINED for {path} — falling back to a normal start. " +
                     "Controller input over elevated windows will NOT work this session.");
            // Do not retry StartElevated from the fallback: a compatibility flag
            // can return 740 again, otherwise creating a 740 -> cancel loop.
            var fallback = StartNormal(path, args, retryWithElevation: false);
            return fallback with { ElevationDeclined = true };
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to start elevated {path}", ex);
            return new LaunchResult(null, false, false);
        }
    }

    private static string SafeDirectory(string path)
    {
        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        }
        catch
        {
            return "";
        }
    }
}
