using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WSGM.DeviceLab.Preflight;
using WSGM.DeviceLab.Transports;
using WSGM.DeviceLab.Worker;

namespace WSGM.DeviceLab.Wizard;

/// <summary>Zeroes any route recorded before a worker stopped unexpectedly.</summary>
internal static class LabRumbleRecovery
{
    /// <summary>Attempts one zero per pending route, retaining every route whose result is uncertain.</summary>
    /// <remarks>
    ///     A route that is no longer present cannot be driving a motor, so it is dropped from the record
    ///     and reported once instead of on every start.
    /// </remarks>
    /// <param name="machine">The machine record.</param>
    /// <param name="worker">The hardware worker.</param>
    /// <returns>What was done, or null when nothing was pending.</returns>
    public static string? RestoreRecorded(LabMachineState machine, LabWorkerClient worker)
    {
        var pending = machine.Read().Rumble;
        if (pending.Count == 0)
        {
            return null;
        }

        var reserved = DeviceLabOwnerInspector.Reserve();
        if (reserved.Reservation is null)
        {
            return "An earlier rumble test may still be active. Close WSGM, then start Device Lab again.";
        }

        List<string> problems = [];
        List<string> gone = [];
        var zeroed = 0;
        using (reserved.Reservation)
        {
            foreach (var route in pending)
            {
                try
                {
                    using var output = worker.Open<ILabRumbleWorker>(LabRumbleWorker.Service.Name, null,
                        route.RecordId, route.RouteId, route.Target);
                    output.Zero();
                    Forget(machine, route);
                    zeroed++;
                }
                catch (LabRumbleRouteGoneException)
                {
                    Forget(machine, route);
                    gone.Add(route.RouteId);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException)
                {
                    problems.Add($"{route.RouteId}: {ex.Message}");
                    if (ex is LabWorkerLostException)
                    {
                        break;
                    }
                }
            }
        }

        List<string> notices = [];
        if (zeroed > 0)
        {
            notices.Add("Sent zero to the motor routes left by an earlier test.");
        }

        if (gone.Count > 0)
        {
            notices.Add(
                $"No longer present, so no longer driving a motor, and forgotten: {string.Join(", ", gone)}.");
        }

        if (problems.Count > 0)
        {
            notices.Add($"Could not confirm that every motor stopped: {string.Join(" ", problems)}");
        }

        return string.Join(" ", notices);
    }

    private static void Forget(LabMachineState machine, LabPendingRumbleRoute route)
    {
        machine.Update(changes => changes with
        {
            Rumble = [.. changes.Rumble.Where(item => item != route)]
        });
    }
}
