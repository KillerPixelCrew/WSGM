using System;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>Detects whether winsta0\Default is the interactive input desktop.
/// WTS_SESSION_LOGON fires while LogonUI still owns the screen — starting apps
/// then leaks Steam audio behind the Welcome screen (device-observed in the
/// service era). WTS_SESSION_DESKTOP_READY is never delivered on the Claw, so
/// polling this is the working barrier.</summary>
public static class InputDesktop
{
    private const uint DesktopReadObjects = 0x0001;
    private const int UoiName = 2;

    /// <summary>True when the current input desktop is winsta0\Default (LogonUI
    /// dismissed). A normal user cannot open Winlogon's protected desktops, so a
    /// failed open reads as "not ready yet".</summary>
    public static bool IsDefaultInputDesktop()
    {
        var desktop = NativeMethods.OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == 0)
        {
            return false;
        }
        try
        {
            var buffer = new char[64];
            if (!NativeMethods.GetUserObjectInformationW(desktop, UoiName, buffer,
                    (uint)(buffer.Length * sizeof(char)), out _))
            {
                return false;
            }
            var terminator = Array.IndexOf(buffer, '\0');
            var name = new string(buffer, 0, terminator < 0 ? buffer.Length : terminator);
            return string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            NativeMethods.CloseDesktop(desktop);
        }
    }
}
