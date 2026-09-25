using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.DeviceLab.Knowledge;

namespace WSGM.DeviceLab.Wizard;

/// <summary>The context recorded with every power write test: identity, managers and power source.</summary>
/// <param name="RecordId">The confirmed record.</param>
/// <param name="IdentityMatches">Whether the live machine still matches the record.</param>
/// <param name="Managers">Conflicting managers running, by label.</param>
/// <param name="AcLine">Charger state: 0 unplugged, 1 plugged in, null unknown.</param>
/// <param name="BatteryPercent">Battery percentage, or null.</param>
internal sealed record LabPowerContext(
    string RecordId,
    bool IdentityMatches,
    IReadOnlyList<string> Managers,
    int? AcLine,
    int? BatteryPercent);

/// <summary>Whether the machine is safe for a power write, and the pinned charger state if so.</summary>
/// <param name="Ok">Whether a write may proceed.</param>
/// <param name="Problem">Why not, in plain language, when it may not.</param>
/// <param name="AcLine">The charger state a write is pinned to.</param>
/// <param name="Context">The context to record in evidence.</param>
internal sealed record LabPowerGateResult(bool Ok, string? Problem, int AcLine, LabPowerContext Context);

/// <summary>
///     The checks made before any power, performance mode, fan or charge write: the live machine still
///     matches the confirmed device, no other manager that owns the same hardware is running, the charger
///     state is known, and the battery is at least 30 %. These mirror AllyXLab's guards.
/// </summary>
internal static class LabPowerGate
{
    /// <summary>The minimum battery before any power write, as AllyXLab required.</summary>
    public const int MinimumBatteryPercent = 30;

    // Managers that own the same power, fan or lighting hardware the tests write. A window close request
    // is offered in preflight; this is the last check before a write.
    private static readonly string[] BlockingManagers =
    [
        "WSGM", "HandheldCompanion", "ControllerService", "ArmouryCrate", "ArmouryCrateControlInterface",
        "ArmourySocketServer", "GHelper", "MSI Center", "MSI.CentralServer", "AsusAppService"
    ];

    /// <summary>Runs the pre-write checks.</summary>
    /// <param name="record">The confirmed record.</param>
    /// <returns>Whether a write may proceed, and the context.</returns>
    public static LabPowerGateResult Check(DeviceKnowledgeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Check(record.Id, LabPowerIdentity.Matches(record));
    }

    /// <summary>
    ///     Runs the pre-write checks for the processor power-limit test on a machine without a curated
    ///     record. There is no device identity to recheck; the other checks are the same.
    /// </summary>
    /// <returns>Whether a write may proceed, and the context.</returns>
    public static LabPowerGateResult CheckProcessor()
    {
        return Check(LabPowerChanges.ProcessorRecordId, true);
    }

    private static LabPowerGateResult Check(string recordId, bool matches)
    {
        var managers = ManagerConflicts.Running()
            .Where(manager => BlockingManagers.Contains(manager.Manager.Process))
            .Select(manager => manager.Manager.Label)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();
        var ac = LabPowerTelemetry.AcLine();
        var battery = LabPowerTelemetry.BatteryPercent();
        LabPowerContext context = new(recordId, matches, managers, ac, battery);

        if (!matches)
        {
            return new LabPowerGateResult(false,
                "This machine no longer looks like the confirmed device, so no power or fan test was run.", -1,
                context);
        }

        if (managers.Length > 0)
        {
            return new LabPowerGateResult(false,
                "Close these programs first, they control the same hardware: " + string.Join(", ", managers) + ".",
                -1, context);
        }

        if (ac is not (0 or 1))
        {
            return new LabPowerGateResult(false,
                "Windows does not report whether the charger is plugged in, so no power or fan test was run.", -1,
                context);
        }

        if (battery is < MinimumBatteryPercent)
        {
            return new LabPowerGateResult(false,
                $"The battery is below {MinimumBatteryPercent}%, so no power or fan test was run. Charge it and try again.",
                -1, context);
        }

        return new LabPowerGateResult(true, null, ac.Value, context);
    }

    /// <summary>Whether the charger state still matches the one a write was pinned to.</summary>
    /// <param name="pinnedAcLine">The charger state at the start of the write.</param>
    /// <returns>True when it is unchanged.</returns>
    public static bool PowerSourceUnchanged(int pinnedAcLine)
    {
        return LabPowerTelemetry.AcLine() == pinnedAcLine;
    }
}
