using System;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>Queries the elevation state of the current process or a specific process.</summary>
public static class ElevationCheck
{
    /// <summary>Returns whether the current process runs elevated; null if
    /// undeterminable. Callers decide how to treat null: safety-critical paths
    /// (self-elevation) assume elevated, repair paths assume not.</summary>
    public static bool? IsCurrentProcessElevated() => IsProcessElevated((uint)Environment.ProcessId);

    /// <summary>Returns whether the given process runs elevated, or <see langword="null"/> if undeterminable.</summary>
    /// <param name="pid">The process identifier to query.</param>
    /// <returns>The elevation state when Windows exposes it; otherwise <see langword="null"/>.</returns>
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
