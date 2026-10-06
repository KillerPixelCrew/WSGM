using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Devices.Sensors;
using WSGM.DeviceLab.Capture.Live;

namespace WSGM.DeviceLab.Wizard;

/// <summary>What the controller and sensors looked like at one moment.</summary>
/// <param name="Ms">Capture milliseconds.</param>
/// <param name="Devices">Raw Input devices present.</param>
/// <param name="XInputSlots">Connected XInput slots.</param>
/// <param name="Accelerometer">Whether the default accelerometer gave a reading.</param>
/// <param name="Gyrometer">Whether the default gyrometer gave a reading.</param>
internal sealed record LabSleepSnapshot(
    double Ms,
    IReadOnlyList<string> Devices,
    IReadOnlyList<int> XInputSlots,
    bool Accelerometer,
    bool Gyrometer);

/// <summary>Sleep and wake helpers. The wizard never puts the device to sleep itself; the tester presses the power button.</summary>
internal static class LabSleep
{
    /// <summary>Records the sleep result only after the controller's original setup has been restored.</summary>
    /// <param name="project">Project receiving the stage result.</param>
    /// <param name="attempt">Current sleep attempt, whose existing evidence is kept.</param>
    /// <param name="cycle">Collects the sleep evidence, including any controller init.</param>
    /// <param name="restore">Restores the original controller setup independently of stage cancellation.</param>
    /// <returns>The restoration problem, or null when restoration succeeded or was unnecessary.</returns>
    public static async Task<string?> RunAsync(
        LabProject project,
        string attempt,
        Func<Task<(LabSegmentStatus Status, string Summary)>> cycle,
        Func<Task<string?>>? restore)
    {
        (LabSegmentStatus Status, string Summary)? result = null;
        string? problem = null;
        try
        {
            result = await cycle();
        }
        finally
        {
            if (restore is not null)
            {
                try
                {
                    problem = await restore();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    problem = ex.Message;
                }
            }

            await Task.Run(() =>
            {
                if (restore is not null)
                {
                    project.WriteEvidence(attempt, "controller-restore",
                        new { Restored = problem is null, Problem = problem });
                }

                if (problem is not null)
                {
                    var summary = result?.Summary ?? "The sleep test did not finish.";
                    project.Finish(LabStages.Sleep, LabSegmentStatus.Failed,
                        $"{summary} The controller could not be put back: {problem}", DateTimeOffset.UtcNow);
                }
                else if (result is { } finished)
                {
                    project.Finish(LabStages.Sleep, finished.Status, finished.Summary, DateTimeOffset.UtcNow);
                }
            });
        }

        return problem;
    }

    /// <summary>Takes a snapshot. Blocking; call off the UI thread.</summary>
    /// <param name="ms">Capture milliseconds to stamp it with.</param>
    /// <returns>The snapshot.</returns>
    public static LabSleepSnapshot Snapshot(double ms)
    {
        return new LabSleepSnapshot(
            Math.Round(ms, 1),
            LabInputCapture.PresentDeviceKeys(),
            LabInputCapture.ConnectedXInputSlots(),
            Reads(() => Accelerometer.GetDefault()?.GetCurrentReading()),
            Reads(() => Gyrometer.GetDefault()?.GetCurrentReading()));
    }

    /// <summary>Lists what is missing from <paramref name="after" /> compared with <paramref name="before" />.</summary>
    /// <param name="before">Snapshot before sleep.</param>
    /// <param name="after">Snapshot after wake.</param>
    /// <returns>Readable lines; empty when everything is back.</returns>
    public static IReadOnlyList<string> Missing(LabSleepSnapshot before, LabSleepSnapshot after)
    {
        List<string> missing = [];
        var remaining = after.Devices.ToList();
        foreach (var device in before.Devices)
        {
            if (!remaining.Remove(device))
            {
                missing.Add(device);
            }
        }

        missing.AddRange(before.XInputSlots.Except(after.XInputSlots).Select(slot => $"XInput slot {slot}"));
        if (before.Accelerometer && !after.Accelerometer)
        {
            missing.Add("accelerometer");
        }

        if (before.Gyrometer && !after.Gyrometer)
        {
            missing.Add("gyrometer");
        }

        return missing;
    }

    private static bool Reads(Func<object?> read)
    {
        try
        {
            return read() is not null;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }
}
