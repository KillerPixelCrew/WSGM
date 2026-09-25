using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.DeviceLab.Wizard;

/// <summary>Runs every processor core at full load for a while and samples telemetry meanwhile.</summary>
internal static class LabPowerLoad
{
    /// <summary>Spins one thread per logical processor until the time is up or the token is cancelled.</summary>
    /// <param name="duration">How long.</param>
    /// <param name="interval">Time between samples.</param>
    /// <param name="sample">Takes one sample; called on a worker thread.</param>
    /// <param name="progress">Receives the seconds left, on a worker thread.</param>
    /// <param name="cancellationToken">Stops the load early.</param>
    /// <returns>The samples taken while loaded.</returns>
    /// <remarks>
    ///     The load threads run below normal priority so the window stays responsive; they still use every
    ///     idle cycle. They are always stopped and joined before this returns, cancelled or not.
    /// </remarks>
    public static async Task<IReadOnlyList<LabPowerSample>> RunAsync(
        TimeSpan duration,
        TimeSpan interval,
        Func<string, LabPowerSample> sample,
        Action<int>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var stop = 0;
        List<Thread> threads = [];
        for (var i = 0; i < Environment.ProcessorCount; i++)
        {
            Thread thread = new(() => Spin(ref stop))
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = $"Device Lab load {i}"
            };
            threads.Add(thread);
        }

        List<LabPowerSample> samples = [];
        var started = DateTimeOffset.UtcNow;
        try
        {
            foreach (var thread in threads)
            {
                thread.Start();
            }

            while (DateTimeOffset.UtcNow - started < duration)
            {
                var left = duration - (DateTimeOffset.UtcNow - started);
                progress?.Invoke((int)Math.Ceiling(left.TotalSeconds));
                await Task.Delay(left < interval ? left : interval, cancellationToken);
                var elapsed = (int)(DateTimeOffset.UtcNow - started).TotalSeconds;
                samples.Add(await Task.Run(() => sample($"load-{elapsed}s"), CancellationToken.None));
            }
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            await Task.Run(() =>
            {
                foreach (var thread in threads)
                {
                    if (thread.IsAlive)
                    {
                        thread.Join();
                    }
                }
            }, CancellationToken.None);
        }

        return samples;
    }

    private static void Spin(ref int stop)
    {
        var value = 1.0;
        while (Volatile.Read(ref stop) == 0)
        {
            for (var i = 0; i < 10_000; i++)
            {
                value = Math.Sqrt(value + i) * 1.000001;
            }
        }

        GC.KeepAlive(value);
    }
}
