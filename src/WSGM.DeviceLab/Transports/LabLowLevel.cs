using System;
using System.Collections.Generic;
using System.Threading;
using LibreHardwareMonitor.PawnIo;

namespace WSGM.DeviceLab.Transports;

/// <summary>A read-only snapshot of the 256 ACPI embedded-controller registers.</summary>
/// <param name="Registers">
///     256 byte values read from the ACPI EC through the 0x62/0x66 command ports, or null when they could
///     not be read.
/// </param>
/// <param name="Source">Where the read came from, for the report.</param>
/// <param name="Problem">Why the read failed, or null on success.</param>
internal sealed record LabEcDump(IReadOnlyList<int>? Registers, string Source, string? Problem);

/// <summary>A read-only snapshot of the AMD SMU: codename, version and PM table.</summary>
/// <param name="CodeName">The CPU codename PawnIO's RyzenSMU module reports, or null.</param>
/// <param name="SmuVersion">The SMU firmware version, or null.</param>
/// <param name="MailboxSet">Which mailbox answered, or null.</param>
/// <param name="PmTable">Named PM table values, or null when the table could not be read.</param>
/// <param name="Problem">Why the read failed, or null on success.</param>
internal sealed record LabSmuInfo(
    string? CodeName,
    string? SmuVersion,
    string? MailboxSet,
    IReadOnlyDictionary<string, double>? PmTable,
    string? Problem);

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
///     Read-only low-level reads the system-dump stage records: the ACPI EC registers, the AMD SMU, and
///     the Intel package power limits. Every method returns a <c>Problem</c> string instead of throwing,
///     never writes hardware, and needs PawnIO. All access goes through LibreHardwareMonitor's signed
///     PawnIO modules, the same driver and modules Handheld Companion uses.
/// </summary>
internal static class LabLowLevel
{
    /// <summary>Reads all 256 ACPI EC registers through the 0x62/0x66 ports.</summary>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The dump, or a dump carrying only a problem.</returns>
    public static LabEcDump ReadEcRegisters(CancellationToken ct)
    {
        const string source = "PawnIO LpcACPIEC (ACPI EC 0x62/0x66)";
        if (!PawnIoAvailable(out var problem))
        {
            return new LabEcDump(null, source, problem);
        }

        LpcAcpiEc? ec = null;
        try
        {
            ec = new LpcAcpiEc();
            var registers = new int[256];
            for (var register = 0; register < 256; register++)
            {
                ct.ThrowIfCancellationRequested();

                // The EC data port is read through the reviewed 0x62/0x66 handshake inside the module;
                // this only reads, and the plan forbids writing the EC on any device.
                registers[register] = ec.ReadPort((byte)register);
            }

            return new LabEcDump(registers, source, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new LabEcDump(null, source, ex.Message);
        }
        finally
        {
            TryClose(ec);
        }
    }

    /// <summary>Reads the AMD SMU codename, version and PM table.</summary>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The info, or info carrying only a problem.</returns>
    public static LabSmuInfo ReadAmdSmu(CancellationToken ct)
    {
        if (!PawnIoAvailable(out var problem))
        {
            return new LabSmuInfo(null, null, null, null, problem);
        }

        RyzenSmu? smu = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            smu = new RyzenSmu();
            var codeName = smu.GetCodeName();
            var version = smu.GetSmuVersion();
            string? mailbox = null;
            IReadOnlyDictionary<string, double>? table = null;
            try
            {
                smu.ResolvePmTable(out _, out var tableBase);
                mailbox = $"0x{tableBase:X8}";
                smu.UpdatePmTable();
                ct.ThrowIfCancellationRequested();

                // The PM table layout is codename-specific and undocumented, so the raw doubles are
                // recorded by index for the maintainer rather than mapped to named limits here.
                var values = smu.ReadPmTable(0);
                if (values is { Length: > 0 })
                {
                    var named = new Dictionary<string, double>(values.Length);
                    for (var i = 0; i < values.Length; i++)
                    {
                        named[$"pm[{i}]"] = values[i];
                    }

                    table = named;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The codename and version are still worth recording without the table.
                return new LabSmuInfo(CodeNameText(codeName), $"0x{version:X8}", mailbox, null, ex.Message);
            }

            return new LabSmuInfo(CodeNameText(codeName), $"0x{version:X8}", mailbox, table, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new LabSmuInfo(null, null, null, null, ex.Message);
        }
        finally
        {
            TryClose(smu);
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

    private static string CodeNameText(long codeName)
    {
        return Enum.IsDefined(typeof(AmdCodeName), (uint)codeName)
            ? ((AmdCodeName)(uint)codeName).ToString()
            : $"0x{codeName:X}";
    }

    private static void TryClose(LpcAcpiEc? ec)
    {
        try
        {
            ec?.Close();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Closing a read-only handle can only fail transiently; nothing depends on it here.
        }
    }

    private static void TryClose(RyzenSmu? smu)
    {
        try
        {
            smu?.Close();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
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

    // AMD CPU codenames as PawnIO's RyzenSMU ioctl_get_code_name reports them (from Handheld
    // Companion 1.3.1.6 HandheldCompanion.Processors.AMD.CpuCodeName).
    private enum AmdCodeName : uint
    {
        Colfax = 0,
        Renoir = 1,
        Picasso = 2,
        Matisse = 3,
        Threadripper = 4,
        CastlePeak = 5,
        RavenRidge = 6,
        RavenRidge2 = 7,
        SummitRidge = 8,
        PinnacleRidge = 9,
        Rembrandt = 10,
        Vermeer = 11,
        Vangogh = 12,
        Cezanne = 13,
        Milan = 14,
        Dali = 15,
        Raphael = 16,
        GraniteRidge = 17,
        Naples = 18,
        FireFlight = 19,
        Rome = 20,
        Chagall = 21,
        Lucienne = 22,
        Phoenix = 23,
        Phoenix2 = 24,
        Mendocino = 25,
        Genoa = 26,
        StormPeak = 27,
        DragonRange = 28,
        Mero = 29,
        HawkPoint = 30,
        StrixPoint = 31,
        StrixHalo = 32,
        KrackanPoint = 33,
        KrackanPoint2 = 34,
        Turin = 35,
        TurinD = 36,
        Bergamo = 37,
        ShimadaPeak = 38
    }
}
