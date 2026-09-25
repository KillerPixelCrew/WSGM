using System;
using System.Threading;
using LibreHardwareMonitor.PawnIo;

namespace WSGM.DeviceLab.Transports;

/// <summary>The AMD SMU's identity: codename and firmware version.</summary>
/// <param name="CodeName">The CPU codename PawnIO's RyzenSMU module reports, or null.</param>
/// <param name="SmuVersion">The SMU firmware version, or null.</param>
/// <param name="Problem">Why the read failed, or null on success.</param>
internal sealed record LabSmuInfo(string? CodeName, string? SmuVersion, string? Problem);

/// <summary>A read-only snapshot of the Intel package power limits.</summary>
/// <param name="Msr610Hex">The raw MSR 0x610 value as hex, or null.</param>
/// <param name="MchbarBaseHex">The MCHBAR base address as hex, or null.</param>
/// <param name="Pl1Watts">PL1 in watts decoded from MSR 0x610, or null.</param>
/// <param name="Pl2Watts">PL2 in watts decoded from MSR 0x610, or null.</param>
/// <param name="Problem">Why the read failed, or null on success.</param>
internal sealed record LabIntelPowerInfo(
    string? Msr610Hex,
    string? MchbarBaseHex,
    double? Pl1Watts,
    double? Pl2Watts,
    string? Problem);

/// <summary>
///     Read-only low-level reads the system-dump stage records: the AMD SMU's identity and the Intel
///     package power limits. Every method returns a <c>Problem</c> string instead of throwing, never
///     writes hardware, and needs PawnIO.
/// </summary>
/// <remarks>
///     The embedded controller is deliberately absent. Raw 0x62/0x66 access races Windows' EC driver and
///     the firmware, and a full register scan was the leading suspect when an ROG Xbox Ally X hard-reset
///     twice after the dump (2026-09-25). The saved ACPI tables describe the EC instead.
/// </remarks>
internal static class LabLowLevel
{
    /// <summary>Reads the AMD SMU codename and firmware version.</summary>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The info, or info carrying only a problem.</returns>
    /// <remarks>
    ///     Goes through <see cref="LabAmdSmu" />, which holds the machine-wide PCI mutex for the mailbox
    ///     exchange. The PM table is not read here: refreshing it is itself an SMU command, and the Power
    ///     stage reads the limits through the hardware worker.
    /// </remarks>
    public static LabSmuInfo ReadAmdSmu(CancellationToken ct)
    {
        if (!PawnIoAvailable(out var problem))
        {
            return new LabSmuInfo(null, null, problem);
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            using var smu = LabAmdSmu.Open();
            var identity = smu.Identity();
            return new LabSmuInfo(identity.CodeNameText, $"0x{identity.SmuVersion:X8}", null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new LabSmuInfo(null, null, ex.Message);
        }
    }

    /// <summary>Reads the Intel package power limits from MSR 0x610.</summary>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The info, or info carrying only a problem.</returns>
    /// <remarks>
    ///     MSR 0x610 holds PL1 in bits 0-14 and PL2 in bits 32-46, each in units of 1/8 W. The MCHBAR
    ///     base is not read here; it needs the IntelMCHBAR module and is left null.
    /// </remarks>
    public static LabIntelPowerInfo ReadIntelPowerLimits(CancellationToken ct)
    {
        if (!PawnIoAvailable(out var problem))
        {
            return new LabIntelPowerInfo(null, null, null, null, problem);
        }

        IntelMsr? msr = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            msr = new IntelMsr();
            if (!msr.ReadMsr(0x610, out var value))
            {
                return new LabIntelPowerInfo(null, null, null, null, "MSR 0x610 could not be read.");
            }

            var pl1 = (value & 0x7FFF) / 8.0;
            var pl2 = ((value >> 32) & 0x7FFF) / 8.0;
            return new LabIntelPowerInfo($"0x{value:X16}", null, pl1, pl2, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new LabIntelPowerInfo(null, null, null, null, ex.Message);
        }
        finally
        {
            TryClose(msr);
        }
    }

    private static bool PawnIoAvailable(out string? problem)
    {
        try
        {
            if (!PawnIo.IsInstalled)
            {
                problem = "unavailable: PawnIO";
                return false;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            problem = "unavailable: PawnIO";
            return false;
        }

        problem = null;
        return true;
    }

    private static void TryClose(IntelMsr? msr)
    {
        try
        {
            msr?.Close();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }
}
