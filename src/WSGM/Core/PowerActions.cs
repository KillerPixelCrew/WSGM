using System;
using System.Diagnostics;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>Invokes explicit user-requested Windows power operations.</summary>
public static class PowerActions
{
    /// <summary>Puts the device to sleep.</summary>
    public static void Sleep()
    {
        Log.Info("Power: sleep");
        if (!NativeMethods.SetSuspendState(false, false, false))
        {
            Log.Error("SetSuspendState failed");
        }
    }

    /// <summary>Restarts Windows.</summary>
    public static void Restart()
    {
        Log.Info("Power: restart");
        RunShutdown("/r /t 0");
    }

    /// <summary>Shuts Windows down.</summary>
    public static void Shutdown()
    {
        Log.Info("Power: shutdown");
        RunShutdown("/s /t 0");
    }

    private static void RunShutdown(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("shutdown.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error($"shutdown.exe {arguments} failed", ex);
        }
    }
}
