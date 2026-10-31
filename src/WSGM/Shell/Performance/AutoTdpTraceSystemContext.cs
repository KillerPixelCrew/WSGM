using System;
using System.ComponentModel;
using System.IO;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Samples the Windows power state, and the sensors when the control path did not.</summary>
/// <remarks>
///     Read-only. The control path reads the sensors before its decision and puts the sample on the
///     row, and that value wins; this source is the fallback for a tick that supplied none, and it
///     reads the session's shared sensor sample rather than starting a provider of its own.
/// </remarks>
/// <param name="metrics">The session's shared sensor reader, the one AutoTDP consults.</param>
internal sealed class AutoTdpTraceSystemContext(Func<RtssOsdMetrics> metrics)
{
    /// <summary>Fills the power and sensor columns of a tick row.</summary>
    /// <param name="row">The row.</param>
    internal void Sample(AutoTdpTraceRow row)
    {
        try
        {
            if (WindowsPower.TryGetStatus(out var power))
            {
                row.AcPower = PowerSource.OnAc(power);
                row.BatteryPercent = power.BatteryLifePercent == 255 ? null : power.BatteryLifePercent;
            }

            row.PowerMode = PowerModeName(WindowsPower.GetEffectiveMode());
            row.PowerScheme = WindowsPower.GetActiveScheme().ToString("D");
        }
        catch (Win32Exception)
        {
            // The trace records what it could read; a missing power field stays empty.
        }

        try
        {
            row.Metrics ??= metrics();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Change("autotdp-trace-sensors", $"AutoTDP trace sensors unavailable: {ex.Message}");
        }
    }

    private static string PowerModeName(Guid mode)
    {
        if (mode == Guid.Empty)
        {
            return "balanced";
        }

        if (mode == WindowsPowerModes.Id(DevicePowerMode.BetterBattery))
        {
            return "better-battery";
        }

        return mode == WindowsPowerModes.Id(DevicePowerMode.BestPerformance)
            ? "best-performance"
            : mode.ToString("D");
    }
}
