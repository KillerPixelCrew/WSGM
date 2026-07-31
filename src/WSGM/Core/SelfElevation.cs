using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;

namespace WSGM.Core;

/// <summary>Relaunches WSGM elevated when the config starts elevated apps.
///
/// UIPI shields input aimed at high-integrity windows from medium-integrity
/// processes: with an elevated home app focused, a non-elevated WSGM never
/// receives the touch digitizer's raw input (and cannot take foreground), so
/// edge swipes only work if WSGM matches that integrity level.</summary>
public static class SelfElevation
{
    private const string RelaunchMarker = "--elevated-relaunch";
    private const int ErrorCancelled = 1223;

    /// <summary>Returns the exit code to propagate when this process handed over to an
    /// elevated copy of itself, or null to continue running normally.</summary>
    public static int? EnsureElevatedIfConfigured(string[] args)
    {
        if (args.Contains(RelaunchMarker, StringComparer.OrdinalIgnoreCase))
        {
            // Already the relaunched copy — never loop, even if elevation was denied
            // in some unexpected way.
            return null;
        }

        AppConfig config;
        try
        {
            config = ConfigStore.Load();
        }
        catch
        {
            return null;
        }

        var wantsElevation = config.StartupApps.Any(a => a.Enabled && a.Elevated);
        if (!wantsElevation ||
            ElevationCheck.IsProcessElevated((uint)Environment.ProcessId) != false)
        {
            return null;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo(exe,
                string.Join(' ', args.Append(RelaunchMarker).Select(Quote)))
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            using var child = Process.Start(psi);
            if (child is null)
            {
                return null;
            }
            Log.Info($"Config starts elevated apps — handed over to elevated instance (pid {child.Id}).");
            // Stay alive while the elevated instance runs: in shell mode Winlogon's
            // AutoRestartShell watches THIS process and would respawn it endlessly
            // if it exited while the real shell keeps running.
            child.WaitForExit();
            return child.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Log.Warn("Elevation DECLINED — continuing non-elevated. Edge swipes will NOT " +
                     "work while an elevated app has the focus (UIPI).");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Self-elevation failed — continuing non-elevated", ex);
            return null;
        }
    }

    private static string Quote(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;
}
