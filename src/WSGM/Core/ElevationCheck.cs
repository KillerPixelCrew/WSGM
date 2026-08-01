using System;
using WSGM.Interop;

namespace WSGM.Core;

public static class ElevationCheck
{
    /// <summary>Returns whether the current process runs elevated; null if
    /// undeterminable. Callers decide how to treat null: safety-critical paths
    /// (self-elevation) assume elevated, repair paths assume not.</summary>
    public static bool? IsCurrentProcessElevated() => IsProcessElevated((uint)Environment.ProcessId);

    /// <summary>Returns whether the given pid runs elevated; null if undeterminable.</summary>
    public static bool? IsProcessElevated(uint pid)
    {
        var hProcess = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, pid);
        if (hProcess == 0)
        {
            return null;
        }
        try
        {
            if (!NativeMethods.OpenProcessToken(hProcess, NativeMethods.TokenQuery, out var hToken))
            {
                return null;
            }
            try
            {
                if (NativeMethods.GetTokenInformation(hToken, NativeMethods.TokenElevationClass,
                        out var elevation, sizeof(uint), out _))
                {
                    return elevation != 0;
                }
                return null;
            }
            finally
            {
                NativeMethods.CloseHandle(hToken);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(hProcess);
        }
    }
}
