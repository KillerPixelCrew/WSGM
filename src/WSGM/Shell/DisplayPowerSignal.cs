using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Interprets the display notifications shared by audio and standby policy.</summary>
internal static class DisplayPowerSignal
{
    /// <summary>Windows display-state value for an off display.</summary>
    internal const int DisplayOff = 0;
    /// <summary>Windows display-state value for a lit display.</summary>
    internal const int DisplayOn = 1;
    /// <summary>Windows display-state value for a dimmed, still-lit display.</summary>
    internal const int DisplayDimmed = 2;

    /// <summary>Only the documented off value means darkness; dimmed and unknown values mean lit.</summary>
    /// <param name="state">Raw power-broadcast display state.</param>
    /// <returns>True only for DisplayOff; dimmed and unknown states remain lit.</returns>
    internal static bool IsDisplayOff(int state)
    {
        return state == DisplayOff;
    }

    /// <summary>Only this session may report darkness; every source may report a wake.</summary>
    /// <param name="source">Origin of the Windows display-state notification.</param>
    /// <returns>True only for the current-session source; console-wide signals cannot darken session policy.</returns>
    internal static bool MayReportDark(DisplayStateSource source)
    {
        return source == DisplayStateSource.Session;
    }
}
