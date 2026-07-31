using WSGM.Interop;

namespace WSGM.Core;

public static class ElevationCheck
{
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
