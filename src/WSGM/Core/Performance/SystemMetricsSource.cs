using System;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Devices.Power;
using WindowsDeviceControl;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>
///     Sources the system metrics the OSD and AutoTDP read: the in-process LibreHardwareMonitor host
///     first, with the kernel counters (CPU times, memory status, power status) filling anything it
///     does not supply. Samples are cached for one second — HandheldCompanion's sensor cadence — so
///     an explicit OSD redraw does not turn scheduler noise into a flickering number.
/// </summary>
internal sealed class SystemMetricsSource : IDisposable
{
    private static readonly TimeSpan SampleLifetime = TimeSpan.FromSeconds(1);

    private readonly LhmSensorHost _lhm = new();
    private readonly Lock _sampleLock = new();

    // "Never" is one interval in the past, not long.MinValue: TickCount64 minus MinValue
    // overflows negative, which read as "cache still fresh" forever and shipped an OSD with no
    // sensor values at all. One interval back keeps the first call eligible even right at boot.
    private RtssOsdMetrics _cached = RtssOsdMetrics.Empty;
    private long _cachedAtTicks = -(long)SampleLifetime.TotalMilliseconds;
    private bool _hasCpuSample;
    private long _lastBusyBase;
    private long _lastIdle;

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sampleLock)
        {
            _lhm.Dispose();
        }
    }

    /// <summary>
    ///     Takes one sample, at most once per second. CPU load needs two samples before the
    ///     kernel fallback reports.
    /// </summary>
    /// <returns>The sample.</returns>
    internal RtssOsdMetrics Sample()
    {
        lock (_sampleLock)
        {
            return SampleCore();
        }
    }

    private RtssOsdMetrics SampleCore()
    {
        var now = Environment.TickCount64;
        if (now - _cachedAtTicks < SampleLifetime.TotalMilliseconds)
        {
            return _cached;
        }

        _cachedAtTicks = now;
        var metrics = _lhm.TrySample() ?? RtssOsdMetrics.Empty;
        var (usedGb, totalGb) = SampleMemory();
        var (percent, minutes, onAc) = SampleBattery();
        _cached = metrics with
        {
            CpuLoadPercent = metrics.CpuLoadPercent ?? SampleCpu(),
            MemoryUsedGb = usedGb,
            MemoryTotalGb = totalGb,
            CapturedAt = DateTimeOffset.UtcNow,
            BatteryPercent = percent,
            BatteryWatts = percent is null ? null : SampleBatteryWatts(),
            BatteryMinutesRemaining = minutes,
            OnAcPower = onAc
        };
        return _cached;
    }

    private double? SampleCpu()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }

        // Kernel time includes idle, so busy = (kernel + user) - idle.
        var busyBase = kernel + user;
        double? load = null;
        if (_hasCpuSample)
        {
            var totalDelta = busyBase - _lastBusyBase;
            var idleDelta = idle - _lastIdle;
            if (totalDelta > 0)
            {
                load = Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
            }
        }

        _lastIdle = idle;
        _lastBusyBase = busyBase;
        _hasCpuSample = true;
        return load;
    }

    private static (double? UsedGb, double? TotalGb) SampleMemory()
    {
        NativeMethods.MemoryStatusEx status = default;
        status.Length = (uint)Marshal
            .SizeOf<NativeMethods.MemoryStatusEx>();
        if (!NativeMethods.GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0)
        {
            return (null, null);
        }

        const double Gib = LhmSensorSelection.Gib;
        return ((status.TotalPhys - status.AvailPhys) / Gib, status.TotalPhys / Gib);
    }

    private static (double? Percent, int? MinutesRemaining, bool OnAcPower) SampleBattery()
    {
        if (!WindowsPower.TryGetStatus(out var power)
            || (power.BatteryFlag & 0x80) != 0)
        {
            return (null, null, false);
        }

        double? percent = power.BatteryLifePercent == 255 ? null : power.BatteryLifePercent;
        int? minutes = power.BatteryLifeTime == uint.MaxValue
            ? null
            : (int)(power.BatteryLifeTime / 60);
        return (percent, minutes, power.ACLineStatus == 1);
    }

    private static double? SampleBatteryWatts()
    {
        // The WinRT battery report allocates; SampleCore caches the whole sample for a second.
        try
        {
            return Battery.AggregateBattery.GetReport().ChargeRateInMilliwatts / 1000.0;
        }
        catch (Exception)
        {
            // No aggregate battery, or WinRT unavailable in this session.
            return null;
        }
    }
}
