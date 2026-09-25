using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using WSGM.DeviceLab.Knowledge;
using WSGM.DeviceLab.Preflight;
using WSGM.DeviceLab.Transports;

namespace WSGM.DeviceLab.Wizard;

/// <summary>
///     Power, fan, charge and lighting settings the power stage changed and has not yet seen put back.
///     Each field is recorded before the first write and cleared only after a readback shows the
///     original again.
/// </summary>
internal sealed record LabPowerChanges
{
    /// <summary>The record ID used for the processor power-limit test, which has no knowledge record.</summary>
    public const string ProcessorRecordId = "processor";

    /// <summary>The knowledge record whose mechanisms were used, so recovery uses the same IDs.</summary>
    public required string RecordId { get; init; }

    /// <summary>The AMD SMU limits before the processor test.</summary>
    public LabAmdLimits? AmdLimits { get; init; }

    /// <summary>The Intel MCHBAR and MSR power limits before the processor test.</summary>
    public LabIntelLimits? IntelLimits { get; init; }

    /// <summary>When the first change was recorded.</summary>
    public required DateTimeOffset RecordedAt { get; init; }

    /// <summary>The charger state (0 unplugged, 1 plugged in) when the ASUS state was captured.</summary>
    public int? AcLine { get; init; }

    /// <summary>The ASUS mode, limits and fan curves before the test.</summary>
    public LabAsusState? AsusPower { get; init; }

    /// <summary>The ASUS charge limit before the test.</summary>
    public int? AsusChargeLimit { get; init; }

    /// <summary>The MSI limits and scenario before the test.</summary>
    public LabMsiState? MsiPower { get; init; }

    /// <summary>The raw MSI charge limit byte before the test.</summary>
    public int? MsiChargeRaw { get; init; }

    /// <summary>
    ///     When the lab first wrote the write-only Aura lighting. It cannot be read back, so recovery only
    ///     tells the tester to set their colour again.
    /// </summary>
    public DateTimeOffset? AuraWrittenAt { get; init; }

    /// <summary>Whether anything is still recorded.</summary>
    /// <param name="changes">The record, or null.</param>
    /// <returns>True when something is pending.</returns>
    public static bool AnyPending(LabPowerChanges? changes)
    {
        return changes is not null
               && (changes.AsusPower is not null || changes.AsusChargeLimit is not null
                                                  || changes.MsiPower is not null || changes.MsiChargeRaw is not null
                                                  || changes.AmdLimits is not null || changes.IntelLimits is not null
                                                  || changes.AuraWrittenAt is not null);
    }

    /// <summary>Whether a readable setting is still recorded.</summary>
    /// <param name="changes">The record, or null.</param>
    /// <returns>True when a power, fan or charge change is pending.</returns>
    public static bool PowerPending(LabPowerChanges? changes)
    {
        return changes is not null
               && (changes.AsusPower is not null || changes.AsusChargeLimit is not null
                                                  || changes.MsiPower is not null || changes.MsiChargeRaw is not null
                                                  || changes.AmdLimits is not null || changes.IntelLimits is not null);
    }
}

/// <summary>How putting recorded power settings back went.</summary>
/// <param name="Restored">Whether nothing is left recorded.</param>
/// <param name="Message">One plain sentence for the page.</param>
internal sealed record LabPowerRecoveryOutcome(bool Restored, string Message);

/// <summary>Puts back power, fan and charge settings the power stage recorded.</summary>
internal static class LabPowerRecovery
{
    /// <summary>
    ///     The wizard start hook: undoes what a killed or crashed power stage left behind. Run it off the UI
    ///     thread, elevated, before preflight reserves the device owner.
    /// </summary>
    /// <param name="machine">The machine-change record.</param>
    /// <returns>Null when nothing was recorded; otherwise what happened, for the page.</returns>
    /// <remarks>
    ///     It reserves <c>Global\WSGM.DeviceOwner</c> for the duration of the writes and refuses while WSGM's
    ///     device integration holds it. Each recorded setting is written once, only when it differs, and
    ///     read back; a failure leaves the record for the next start or the power stage's own button.
    /// </remarks>
    public static LabPowerRecoveryOutcome? RestoreRecorded(LabMachineState machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        var recorded = machine.Read().Power;
        if (!LabPowerChanges.AnyPending(recorded))
        {
            if (recorded is not null)
            {
                machine.Update(changes => changes with { Power = null });
            }

            return null;
        }

        List<string> messages = [];
        var restored = true;
        if (LabPowerChanges.PowerPending(recorded))
        {
            var reserved = DeviceLabOwnerInspector.Reserve();
            if (reserved.Reservation is null)
            {
                restored = false;
                messages.Add(
                    "An earlier test left power or fan settings changed. Close WSGM, then start Device Lab again to put them back.");
            }
            else
            {
                using (reserved.Reservation)
                {
                    var outcome = RestorePower(machine, new LabPowerLog());
                    restored = outcome.Restored;
                    messages.Add(outcome.Restored
                        ? "Put back the power and fan settings an earlier test left changed."
                        : outcome.Message);
                }
            }
        }

        if (machine.Read().Power?.AuraWrittenAt is not null)
        {
            messages.Add("An earlier test changed the light colour. Set your colour again in Armoury Crate.");
            machine.Update(changes => changes with
            {
                Power = changes.Power is null ? null : changes.Power with { AuraWrittenAt = null }
            });
        }

        ClearWhenEmpty(machine);
        return new LabPowerRecoveryOutcome(restored, string.Join(" ", messages));
    }

    /// <summary>
    ///     Puts back every recorded power, fan and charge setting once, reads each back and clears what
    ///     matches. The caller holds the device owner reservation.
    /// </summary>
    /// <param name="machine">The machine-change record.</param>
    /// <param name="log">Where every call is logged.</param>
    /// <returns>What happened.</returns>
    public static LabPowerRecoveryOutcome RestorePower(LabMachineState machine, LabPowerLog log)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(log);
        var recorded = machine.Read().Power;
        if (!LabPowerChanges.PowerPending(recorded))
        {
            return new LabPowerRecoveryOutcome(true, "Nothing needed putting back.");
        }

        var plan = LabPowerPlan.For(DeviceKnowledgeBase.Default.Records.FirstOrDefault(record =>
            record.Id == recorded!.RecordId));
        List<string> problems = [];
        if (recorded!.AsusPower is not null || recorded.AsusChargeLimit is not null)
        {
            RestoreAsus(machine, recorded, plan, log, problems);
        }

        if (recorded.MsiPower is not null || recorded.MsiChargeRaw is not null)
        {
            RestoreMsi(machine, recorded, plan, log, problems);
        }

        if (recorded.AmdLimits is { } amd)
        {
            RestoreAmd(machine, amd, log, problems);
        }

        if (recorded.IntelLimits is { } intel)
        {
            RestoreIntel(machine, intel, log, problems);
        }

        ClearWhenEmpty(machine);
        var left = machine.Read().Power;
        log.Add("restore-outcome", new { Pending = left, Problems = problems });
        return LabPowerChanges.PowerPending(left)
            ? new LabPowerRecoveryOutcome(false,
                problems.Count > 0 ? string.Join(" ", problems) : "Some settings could not be confirmed as put back.")
            : new LabPowerRecoveryOutcome(true, "All changed settings were put back and read back correctly.");
    }

    private static void RestoreAsus(LabMachineState machine, LabPowerChanges recorded, LabPowerPlan plan,
        LabPowerLog log, List<string> problems)
    {
        if (plan.Asus is not { } layout)
        {
            problems.Add("The device record the test used is missing from this build, so the ASUS settings were not put back.");
            return;
        }

        // A limit set is kept per charger state; replaying one captured on the charger after unplugging
        // (or the other way round) would be a new, unreviewed write (AllyXLab refused it too).
        if (recorded.AcLine is { } ac && LabPowerTelemetry.AcLine() != ac)
        {
            problems.Add(ac == 1
                ? "Plug the charger back in, then try again: the settings were saved while it was plugged in."
                : "Unplug the charger, then try again: the settings were saved while it was unplugged.");
            return;
        }

        try
        {
            using var acpi = LabAtkAcpi.Open(layout, log);
            if (recorded.AsusPower is { } power)
            {
                if (acpi.Restore(power))
                {
                    machine.Update(changes => changes with { Power = changes.Power! with { AsusPower = null } });
                }
                else
                {
                    problems.Add("The power limits, performance mode or fan curves did not read back as they were.");
                }
            }

            if (recorded.AsusChargeLimit is { } charge && layout.Charge is { } chargeId)
            {
                if (acpi.TryGet(chargeId) != charge)
                {
                    acpi.Set(chargeId, charge);
                    System.Threading.Thread.Sleep(150);
                }

                var readback = acpi.TryGet(chargeId);
                log.Add("restore-readback", new { Feature = "charge-limit", Expected = charge, Observed = readback });
                if (readback == charge)
                {
                    machine.Update(changes => changes with { Power = changes.Power! with { AsusChargeLimit = null } });
                }
                else
                {
                    problems.Add($"The charge limit did not read back as {charge} %.");
                }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            log.Add("restore-error", ex.Message);
            problems.Add($"The ASUS settings could not be put back: {ex.Message}");
        }
    }

    private static void RestoreMsi(LabMachineState machine, LabPowerChanges recorded, LabPowerPlan plan,
        LabPowerLog log, List<string> problems)
    {
        if (plan.Msi is not { } layout)
        {
            problems.Add("The device record the test used is missing from this build, so the MSI settings were not put back.");
            return;
        }

        try
        {
            using var wmi = LabMsiWmi.Open(layout, log);
            if (recorded.MsiPower is { } power && layout.HasTdp)
            {
                if (wmi.RestorePower(power))
                {
                    machine.Update(changes => changes with { Power = changes.Power! with { MsiPower = null } });
                }
                else
                {
                    problems.Add("The power limits did not read back as they were.");
                }
            }

            if (recorded.MsiChargeRaw is { } raw && layout.Charge is not null)
            {
                if (wmi.ReadChargeRaw() != raw)
                {
                    wmi.WriteChargeRaw(raw);
                }

                var readback = wmi.ReadChargeRaw();
                log.Add("restore-readback", new { Feature = "charge-limit", Expected = raw, Observed = readback });
                if (readback == raw)
                {
                    machine.Update(changes => changes with { Power = changes.Power! with { MsiChargeRaw = null } });
                }
                else
                {
                    problems.Add($"The charge limit did not read back as {raw & LabMsiWmi.ChargePercentMask} %.");
                }
            }
        }
        catch (Exception ex) when (LabMsiWmi.IsTransportFailure(ex))
        {
            log.Add("restore-error", ex.Message);
            problems.Add($"The MSI settings could not be put back: {ex.Message}");
        }
    }

    private static void RestoreAmd(LabMachineState machine, LabAmdLimits original, LabPowerLog log,
        List<string> problems)
    {
        try
        {
            using var smu = LabAmdSmu.Open();
            var now = smu.ReadLimits();
            if (!Same(now, original))
            {
                log.Add("restore-write", new { Feature = "processor-power", Transport = "ryzen-smu", Original = original,
                    Responses = smu.WriteLimits(original) });
                System.Threading.Thread.Sleep(1000);
                now = smu.ReadLimits();
            }

            log.Add("restore-readback", new { Feature = "processor-power", Expected = original, Observed = now });
            if (Same(now, original))
            {
                machine.Update(changes => changes with { Power = changes.Power! with { AmdLimits = null } });
            }
            else
            {
                problems.Add($"The processor power limits read back as {now.Stapm}/{now.Fast}/{now.Slow} W, not {original.Stapm}/{original.Fast}/{original.Slow} W.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException
                                       or ArgumentOutOfRangeException)
        {
            log.Add("restore-error", ex.Message);
            problems.Add($"The processor power limits could not be put back: {ex.Message}");
        }
    }

    private static void RestoreIntel(LabMachineState machine, LabIntelLimits original, LabPowerLog log,
        List<string> problems)
    {
        try
        {
            using var kx = LabIntelKx.Open();
            var problem = kx.Restore(original);
            var now = kx.Read(original.PowerUnitWatts);
            log.Add("restore-readback", new { Feature = "processor-power", Transport = "kx", Expected = original,
                Observed = now, Problem = problem, kx.Log });
            if (now.MchbarPl1 == original.MchbarPl1 && (original.MsrLocked || now.Msr610 == original.Msr610))
            {
                machine.Update(changes => changes with { Power = changes.Power! with { IntelLimits = null } });
            }
            else
            {
                problems.Add("The processor power limits did not read back as they were.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Win32Exception
                                       or UnauthorizedAccessException)
        {
            log.Add("restore-error", ex.Message);
            problems.Add($"The processor power limits could not be put back: {ex.Message}");
        }
    }

    /// <summary>Whether two AMD limit readings agree within half a watt.</summary>
    /// <param name="a">One reading.</param>
    /// <param name="b">The other.</param>
    /// <returns>True when all three limits agree.</returns>
    public static bool Same(LabAmdLimits a, LabAmdLimits b)
    {
        return Math.Abs(a.Stapm - b.Stapm) <= 0.5 && Math.Abs(a.Fast - b.Fast) <= 0.5 && Math.Abs(a.Slow - b.Slow) <= 0.5;
    }

    /// <summary>Records a change before it is made.</summary>
    /// <param name="machine">The machine-change record.</param>
    /// <param name="recordId">Knowledge record ID.</param>
    /// <param name="change">Adds the original value.</param>
    public static void Record(LabMachineState machine, string recordId, Func<LabPowerChanges, LabPowerChanges> change)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(change);
        machine.Update(changes =>
        {
            var current = changes.Power ?? new LabPowerChanges { RecordId = recordId, RecordedAt = DateTimeOffset.UtcNow };
            if (current.RecordId != recordId && LabPowerChanges.AnyPending(current))
            {
                throw new InvalidOperationException(
                    "An earlier test's changes for another device are still recorded; put those back first.");
            }

            return changes with { Power = change(current with { RecordId = recordId }) };
        });
    }

    /// <summary>Clears the Aura note after the tester was told to set their colour again.</summary>
    /// <param name="machine">The machine-change record.</param>
    public static void ClearAura(LabMachineState machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        machine.Update(changes => changes with
        {
            Power = changes.Power is null ? null : changes.Power with { AuraWrittenAt = null }
        });
        ClearWhenEmpty(machine);
    }

    private static void ClearWhenEmpty(LabMachineState machine)
    {
        if (machine.Read().Power is { } power && !LabPowerChanges.AnyPending(power))
        {
            machine.Update(changes => changes with { Power = null });
        }
    }
}
