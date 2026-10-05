using System;
using System.Collections.Generic;
using System.Threading;
using WSGM.DeviceLab.Preflight;
using WSGM.DeviceLab.Transports;
using WSGM.DeviceLab.Worker;

namespace WSGM.DeviceLab.Wizard;

/// <summary>Undoes, at the next wizard start, what a killed or crashed session left on this machine.</summary>
/// <remarks>
///     The order is PawnIO reconcile (elevated), power and fan settings, the Aura note, rumble routes
///     (elevated), then the controller: opt-in mode commands and a switched controller mode. Every item is
///     attempted even when an earlier one failed; a failed item keeps its record for the next start and
///     adds a line to the notice. Hardware items run only while the one owner reservation taken here is
///     held, and the hardware worker is started only when an item needs it. Restoring a recorded original
///     is recovery, not a retry of an uncertain write.
/// </remarks>
internal static class LabRecovery
{
    /// <summary>Runs every pending recovery item. Blocking; call off the UI thread.</summary>
    /// <param name="machine">The machine record.</param>
    /// <param name="elevated">Whether the wizard runs elevated; power, rumble and PawnIO need it.</param>
    /// <param name="pawnIo">Reconciles the PawnIO install record.</param>
    /// <param name="worker">Starts the hardware worker, or returns the running one.</param>
    /// <param name="reserve">Reserves the device owner while hardware is written.</param>
    /// <param name="cancellationToken">Ends the wait for a controller to come back.</param>
    /// <returns>One line per item that had something to report, for the page.</returns>
    public static IReadOnlyList<string> Run(
        LabMachineState machine,
        bool elevated,
        LabPawnIo pawnIo,
        Func<LabWorkerClient> worker,
        Func<DeviceLabOwnerReservationResult> reserve,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(pawnIo);
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(reserve);
        List<string> notices = [];
        if (elevated)
        {
            Attempt(notices, "The PawnIO install record could not be checked", () =>
            {
                pawnIo.Reconcile();
                return null;
            });
        }

        // An unreadable record is reported and kept; it is never read as "nothing recorded".
        LabMachineChanges? changes;
        try
        {
            changes = machine.Read();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            changes = null;
            notices.Add(
                $"An earlier test's record could not be read, so nothing it recorded was put back: {ex.Message}");
        }

        var power = elevated && LabPowerChanges.PowerPending(changes?.Power);
        var rumble = elevated && changes is { Rumble.Count: > 0 };
        var controller = LabControllerInit.HasPending(machine) || LabModeCommands.HasPending(machine);
        var reservation = power || rumble || controller ? reserve().Reservation : null;
        using (reservation)
        {
            if (power)
            {
                if (reservation is null)
                {
                    notices.Add(
                        "An earlier test left power or fan settings changed. Close WSGM, then start Device Lab again to put them back.");
                }
                else
                {
                    Attempt(notices, "The power and fan settings an earlier test left changed could not be put back",
                        () =>
                        {
                            var outcome = LabPowerRecovery.RestorePower(machine, worker(), new LabPowerLog());
                            return outcome.Restored
                                ? "Put back the power and fan settings an earlier test left changed."
                                : outcome.Message;
                        });
                }
            }

            if (elevated && changes?.Power is not null)
            {
                // Write-only Aura lighting cannot be put back, so the tester is told once; an empty power
                // record is dropped here too.
                Attempt(notices, "The power record could not be updated", () =>
                {
                    var aura = machine.Read().Power?.AuraWrittenAt is not null;
                    LabPowerRecovery.ClearAura(machine);
                    return aura
                        ? "An earlier test changed the light colour. Set your colour again in Armoury Crate."
                        : null;
                });
            }

            if (rumble)
            {
                if (reservation is null)
                {
                    notices.Add("An earlier rumble test may still be active. Close WSGM, then start Device Lab again.");
                }
                else
                {
                    Attempt(notices, "Could not confirm that every motor stopped",
                        () => LabRumbleRecovery.RestoreRecorded(machine, worker()));
                }
            }

            if (controller && reservation is null)
            {
                notices.Add(
                    "An earlier test left a controller setting changed. Close WSGM, then start Device Lab again to put it back.");
            }
            else if (controller || changes?.CuratedInitRecordId is not null)
            {
                Attempt(notices, "The controller could not be switched back after an earlier test", () =>
                    RestoreController(machine, worker, cancellationToken) is { } problem
                        ? $"The controller could not be switched back after an earlier test: {problem}"
                        : "The controller was switched back to the mode it had before an earlier test.");
            }
        }

        return notices;
    }

    /// <summary>
    ///     Undoes what a controller init left recorded: opt-in mode commands, then a switched controller
    ///     mode through the worker, and forgets a curated init whose result was never known. The caller holds
    ///     the device owner reservation. Blocking; call off the UI thread.
    /// </summary>
    /// <param name="machine">The machine record.</param>
    /// <param name="worker">Starts the hardware worker, or returns the running one.</param>
    /// <param name="cancellationToken">Ends the wait for the controller to come back.</param>
    /// <returns>Null when nothing was left or everything was put back; otherwise the problems.</returns>
    public static string? RestoreController(LabMachineState machine, Func<LabWorkerClient> worker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(worker);
        string? modes;
        try
        {
            modes = LabModeCommands.HasPending(machine) ? LabModeCommands.RecoverPending(machine) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            modes = ex.Message;
        }

        string? restored;
        try
        {
            restored = LabControllerInit.ReadPending(machine) is { } pending
                ? RestoreControllerMode(machine, worker(), pending, cancellationToken)
                : ForgetUnknownInit(machine);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            restored = ex.Message;
        }

        return modes is null ? restored : restored is null ? modes : $"{restored} {modes}";
    }

    private static string? RestoreControllerMode(LabMachineState machine, LabWorkerClient worker,
        LabPendingControllerMode pending, CancellationToken cancellationToken)
    {
        using var init = worker.Open<ILabCuratedInitWorker>(LabCuratedInitWorker.Service.Name, null, (string?)null);
        var (_, token) = worker.Checkpoint<int?>(init, AlreadyRecorded);
        var problem = init.RecoverControllerMode(pending, cancellationToken);
        if (problem is null)
        {
            worker.Release(init, token);
            LabControllerInit.ClearPending(machine);
            machine.Update(changes => changes with { CuratedInitRecordId = null });
        }

        return problem;
    }

    // Nothing records what an unconfirmed curated init changed, so it cannot be undone or checked: it is
    // reported once and forgotten rather than warned about on every start. It is never resent.
    private static string? ForgetUnknownInit(LabMachineState machine)
    {
        if (machine.Read().CuratedInitRecordId is null)
        {
            return null;
        }

        machine.Update(changes => changes with { CuratedInitRecordId = null });
        return "A controller setup stopped before its result was known. Check the OEM button layout.";
    }

    // The controller-mode original is already on disk, so the checkpoint only arms the restore.
    private static void AlreadyRecorded(int? current)
    {
        _ = current;
    }

    private static void Attempt(List<string> notices, string failure, Func<string?> item)
    {
        try
        {
            if (item() is { Length: > 0 } notice)
            {
                notices.Add(notice);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            notices.Add($"{failure}: {ex.Message}");
        }
    }
}
