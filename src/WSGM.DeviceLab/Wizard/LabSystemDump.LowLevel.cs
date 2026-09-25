using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Intrinsics.X86;
using System.Text;
using WSGM.DeviceLab.Transports;

namespace WSGM.DeviceLab.Wizard;

internal static partial class LabSystemDump
{
    private const string NoOwner =
        "\"Get ready\" has not reserved the device, so WSGM could be using the same registers.";

    private const string NoOwnerSummary = "skipped: run \"Get ready\" first";

    // A read-only dump of the 256 ACPI EC registers, to line up with the DSDT's EC OperationRegion.
    private static LabSystemDumpSectionResult CollectEc(LabSystemDumpContext context)
    {
        if (!context.Elevated)
        {
            return Skipped("ec", "Reading the embedded controller needs administrator rights.");
        }

        if (!context.OwnerReserved)
        {
            return Skipped("ec", NoOwner, NoOwnerSummary);
        }

        var dump = LabLowLevel.ReadEcRegisters(context.Cancellation);
        List<string> issues = dump.Problem is { } problem ? [problem] : [];
        context.Write("ec", new
        {
            dump.Source,
            dump.Problem,
            Rows = dump.Registers is { } registers ? EcRows(registers) : null,
            dump.Registers
        });
        return dump.Registers is { Count: > 0 } read
            ? Result("ec", read.Count, Plural(read.Count, "register read", "registers read"), issues)
            : new LabSystemDumpSectionResult
            {
                Id = "ec",
                Status = LabSystemDumpSectionStatus.Failed,
                Summary = "could not be read",
                Issues = issues.Count > 0 ? issues : ["No registers were returned."]
            };
    }

    /// <summary>Formats EC registers as sixteen per row, <c>"00: 12 34 ..."</c>, for reading beside the DSDT.</summary>
    /// <param name="registers">Register values; a negative value means that register could not be read.</param>
    /// <returns>One line per sixteen registers.</returns>
    public static List<string> EcRows(IReadOnlyList<int> registers)
    {
        List<string> rows = [];
        for (var start = 0; start < registers.Count; start += 16)
        {
            StringBuilder row = new(start.ToString("X2", CultureInfo.InvariantCulture) + ":");
            for (var index = start; index < Math.Min(start + 16, registers.Count); index++)
            {
                row.Append(' ');
                row.Append(registers[index] is >= 0 and <= 0xFF
                    ? registers[index].ToString("X2", CultureInfo.InvariantCulture)
                    : "??");
            }

            rows.Add(row.ToString());
        }

        return rows;
    }

    // AMD: the SMU codename and version. Intel: the MCHBAR base and MSR 0x610 (package power limits).
    private static LabSystemDumpSectionResult CollectCpuPower(LabSystemDumpContext context)
    {
        if (!context.Elevated)
        {
            return Skipped("cpu-power", "Reading the processor's power registers needs administrator rights.");
        }

        if (!context.OwnerReserved)
        {
            return Skipped("cpu-power", NoOwner, NoOwnerSummary);
        }

        var vendor = CpuVendor();
        if (vendor == "AuthenticAMD")
        {
            var smu = LabLowLevel.ReadAmdSmu(context.Cancellation);
            context.Write("cpu-power", new
            {
                Vendor = vendor,
                smu.CodeName,
                smu.SmuVersion,
                smu.MailboxSet,
                smu.PmTable,
                smu.Problem
            });
            return smu.CodeName is null && smu.SmuVersion is null
                ? Failed("cpu-power", smu.Problem ?? "The SMU did not answer.")
                : Result("cpu-power", 1, $"AMD {smu.CodeName ?? "unknown"}, SMU {smu.SmuVersion ?? "unknown"}",
                    smu.Problem is { } problem ? [problem] : []);
        }

        if (vendor == "GenuineIntel")
        {
            var intel = LabLowLevel.ReadIntelPowerLimits(context.Cancellation);
            context.Write("cpu-power", new
            {
                Vendor = vendor,
                intel.Msr610Hex,
                intel.MchbarBaseHex,
                intel.Pl1Watts,
                intel.Pl2Watts,
                intel.Problem
            });
            return intel.Msr610Hex is null && intel.MchbarBaseHex is null
                ? Failed("cpu-power", intel.Problem ?? "Nothing could be read.")
                : Result("cpu-power", 1,
                    $"Intel PL1 {Watts(intel.Pl1Watts)}, PL2 {Watts(intel.Pl2Watts)}",
                    intel.Problem is { } problem ? [problem] : []);
        }

        context.Write("cpu-power", new { Vendor = vendor, Problem = "Not an AMD or Intel processor." });
        return Skipped("cpu-power", $"Processor vendor {vendor} is not supported here.",
            "not an AMD or Intel processor");

        static string Watts(double? value)
        {
            return value is { } watts ? $"{watts.ToString("0.#", CultureInfo.InvariantCulture)} W" : "unknown";
        }
    }

    private static string CpuVendor()
    {
        if (!X86Base.IsSupported)
        {
            return "unknown";
        }

        var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
        Span<byte> bytes = stackalloc byte[12];
        BitConverter.TryWriteBytes(bytes, ebx);
        BitConverter.TryWriteBytes(bytes[4..], edx);
        BitConverter.TryWriteBytes(bytes[8..], ecx);
        return Encoding.ASCII.GetString(bytes);
    }

    private static LabSystemDumpSectionResult Skipped(
        string id,
        string reason,
        string summary = "skipped without administrator rights")
    {
        return new LabSystemDumpSectionResult
        {
            Id = id,
            Status = LabSystemDumpSectionStatus.Skipped,
            Summary = summary,
            Issues = [reason]
        };
    }

    private static LabSystemDumpSectionResult Failed(string id, string reason)
    {
        return new LabSystemDumpSectionResult
        {
            Id = id,
            Status = LabSystemDumpSectionStatus.Failed,
            Summary = "could not be read",
            Issues = [reason]
        };
    }
}
