using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;

namespace WSGM.Core;

/// <summary>Hosts LibreHardwareMonitor in process for the CPU and GPU sensors the OSD shows.</summary>
/// <remarks>
///     RTSS's bundled provider ships LibreHardwareMonitor 0.9.5, which reads MSRs through the blocked
///     WinRing0 driver and does not know Panther Lake: on the Atlas it exported loads only. The pinned
///     build reads through PawnIO and recognizes current Intel and AMD parts. Only the CPU and GPU
///     groups are opened; motherboard Super I/O and EC probing stay off so no handheld EC is touched.
///     Opening enumerates hardware for a while, so it runs once in the background and the OSD shows
///     kernel-fed values until it completes. A failed open is logged once and not retried.
/// </remarks>
internal sealed class LhmSensorHost : IDisposable
{
    private readonly Lock _gate = new();
    private Computer? _computer;
    private bool _disposed;
    private Task? _opening;

    /// <inheritdoc />
    public void Dispose()
    {
        Computer? computer;
        lock (_gate)
        {
            _disposed = true;
            computer = _computer;
            _computer = null;
        }

        computer?.Close();
    }

    /// <summary>Updates the CPU and GPU hardware and selects the OSD's values.</summary>
    /// <returns>The selected sensors, or null while the library is still opening or unavailable.</returns>
    /// <remarks>Called under the metrics source's lock, at most once per second.</remarks>
    internal RtssOsdMetrics? TrySample()
    {
        Computer? computer;
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            _opening ??= Task.Run(Open);
            computer = _computer;
        }

        if (computer is null)
        {
            return null;
        }

        LhmSensorSelection selection = new();
        try
        {
            foreach (var hardware in computer.Hardware)
            {
                hardware.Update();
                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.Value is { } value)
                    {
                        selection.Add(hardware.HardwareType, sensor.SensorType, sensor.Name, value);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // One failed update leaves this sample kernel-fed; the next second tries again.
            Log.Change("lhm-update", $"LibreHardwareMonitor update failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        Log.Change("lhm-update", "LibreHardwareMonitor sensors updating.");
        return selection.Build();
    }

    private void Open()
    {
        Computer computer = new() { IsCpuEnabled = true, IsGpuEnabled = true };
        try
        {
            computer.Open();
        }
        catch (Exception ex)
        {
            Log.Warn($"LibreHardwareMonitor sensors unavailable: {ex.Message}");
            return;
        }

        lock (_gate)
        {
            if (!_disposed)
            {
                _computer = computer;
                Log.Info("LibreHardwareMonitor sensors open: "
                         + string.Join(", ", computer.Hardware.Select(item => $"{item.HardwareType} {item.Name}")));
                return;
            }
        }

        computer.Close();
    }
}
