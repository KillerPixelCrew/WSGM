using System.Diagnostics;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Core;

public static class PowerActions
{
    public static void Sleep()
    {
        Log.Info("Power: sleep");
        if (!NativeMethods.SetSuspendState(false, false, false))
        {
            Log.Error("SetSuspendState failed");
        }
    }

    public static void Restart()
    {
        Log.Info("Power: restart");
        Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { UseShellExecute = false, CreateNoWindow = true });
    }

    public static void Shutdown()
    {
        Log.Info("Power: shutdown");
        Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { UseShellExecute = false, CreateNoWindow = true });
    }
}
