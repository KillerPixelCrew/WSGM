using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>Decodes the Windows power source once for every reader.</summary>
internal static class PowerSource
{
    /// <summary>The power source, or null when Windows cannot say.</summary>
    /// <returns>True on AC, false on battery, or null when the Windows query fails or reports unknown status.</returns>
    internal static bool? ReadOnAc()
    {
        return WindowsPower.TryGetStatus(out var power) ? OnAc(power) : null;
    }

    /// <summary>The power source a status sample reports.</summary>
    /// <param name="power">A successful Windows power status sample.</param>
    /// <returns>True on AC, false on battery, or null when the status is unknown.</returns>
    internal static bool? OnAc(WindowsPowerStatus power)
    {
        return power.ACLineStatus switch
        {
            0 => false,
            1 => true,
            _ => null
        };
    }
}
