namespace WSGM.Interop;

/// <summary>Turns the display off through the system command every window answers.</summary>
internal static class MonitorPower
{
    private const uint WmSysCommand = 0x0112;
    private const nint ScMonitorPower = 0xF170;
    private const nint MonitorOff = 2;

    /// <summary>Posts the display-off command to every top-level window.</summary>
    /// <returns>Whether Windows accepted the message.</returns>
    /// <remarks>
    ///     On a Modern Standby (S0 low power idle) machine this enters standby: Kernel-Power logs event 506,
    ///     "entering modern standby", reason SC_MONITORPOWER (Atlas 8, 2026-11-01). It is the only request a
    ///     desktop process can make that does; SetSuspendState and NtInitiatePowerAction hibernate there.
    ///     Posted, not sent, so a hung window cannot stall the caller.
    /// </remarks>
    internal static bool TurnOff()
    {
        return NativeMethods.PostMessageW(NativeMethods.HwndBroadcast, WmSysCommand, ScMonitorPower, MonitorOff);
    }
}
