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
        using (reserved.Reservation)
        {
            foreach (var route in pending)
            {
                try
                {
                    using var output = worker.Open<ILabRumbleWorker>(LabRumbleWorker.Service.Name, null,
                        route.RecordId, route.RouteId, route.Target);
                    output.Zero();
                    machine.Update(changes => changes with
                    {
                        Rumble = [.. changes.Rumble.Where(item => item != route)]
                    });
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

        return problems.Count == 0
            ? "Sent zero to the motor routes left by an earlier test."
            : $"Could not confirm that every motor stopped: {string.Join(" ", problems)}";
    }
}
