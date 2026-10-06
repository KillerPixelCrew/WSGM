using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Interprets the display notifications shared by audio and standby policy.</summary>
internal static class DisplayPowerSignal
{
    internal const int DisplayOff = 0;
    internal const int DisplayOn = 1;
    internal const int DisplayDimmed = 2;

    /// <summary>Only the documented off value means darkness; dimmed and unknown values mean lit.</summary>
    internal static bool IsDisplayOff(int state)
    {
        return state == DisplayOff;
    }

    /// <summary>Only this session may report darkness; every source may report a wake.</summary>
    internal static bool MayReportDark(DisplayStateSource source)
    {
        return source == DisplayStateSource.Session;
    }
}
