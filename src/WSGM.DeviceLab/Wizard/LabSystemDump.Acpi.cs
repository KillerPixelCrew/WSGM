using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using WSGM.DeviceLab.Application;

namespace WSGM.DeviceLab.Wizard;

/// <summary>One ACPI table as listed in <c>acpi.json</c>.</summary>
internal sealed record LabAcpiTableEntry
{
    /// <summary>Four-character table signature, for example DSDT.</summary>
    public required string Signature { get; init; }

    /// <summary>Table length from its header, or the bytes returned when there is no header.</summary>
    public long? Length { get; init; }

    /// <summary>Header revision.</summary>
    public int? Revision { get; init; }

    /// <summary>OEM ID from the header.</summary>
    public string? OemId { get; init; }

    /// <summary>OEM table ID from the header.</summary>
    public string? OemTableId { get; init; }

    /// <summary>OEM revision from the header.</summary>
    public string? OemRevision { get; init; }

    /// <summary>Creator ID from the header.</summary>
    public string? CreatorId { get; init; }

    /// <summary>Where the table came from: the registry or GetSystemFirmwareTable.</summary>
    public string? Source { get; init; }

    /// <summary>Key under HKLM\HARDWARE\ACPI the table was read from.</summary>
    public string? RegistryKey { get; init; }

    /// <summary>File the raw table was written to.</summary>
    public string? FileName { get; init; }

    /// <summary>Earlier file with the same bytes, when Windows returned the same table twice.</summary>
    public string? SameAs { get; init; }

    /// <summary>Why the table was not written.</summary>
    public string? Skipped { get; init; }
}

/// <summary>An ACPI table header decoded from raw table bytes.</summary>
/// <param name="Signature">Signature.</param>
/// <param name="Length">Declared length.</param>
/// <param name="Revision">Revision.</param>
/// <param name="OemId">OEM ID.</param>
/// <param name="OemTableId">OEM table ID.</param>
/// <param name="OemRevision">OEM revision.</param>
/// <param name="CreatorId">Creator ID.</param>
internal sealed record LabAcpiHeader(
    string Signature,
    uint Length,
    int Revision,
    string OemId,
    string OemTableId,
    uint OemRevision,
    string CreatorId);

internal static partial class LabSystemDump
{
    private const uint AcpiProvider = 0x41435049; // 'ACPI'
    private const uint RsmbProvider = 0x52534D42; // 'RSMB'
    private const int MaximumAcpiTables = 256;
    private const int MaximumFirmwareTableBytes = 8 * 1024 * 1024;

    // MSDM and SLIC carry the Windows licence key and the OEM activation marker. They are never read.
    private static readonly string[] LicenceTables = ["MSDM", "SLIC"];

    /// <summary>Decodes the standard 36-byte ACPI table header.</summary>
    /// <param name="table">Raw table bytes.</param>
    /// <returns>The header, or <see langword="null" /> when the table is shorter than a header.</returns>
    /// <remarks>FACS has no standard header past its length, so only its signature and length are read.</remarks>
    public static LabAcpiHeader? ParseAcpiHeader(ReadOnlySpan<byte> table)
    {
        if (table.Length < 8)
        {
            return null;
        }

        var signature = AsciiField(table[..4]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(table[4..8]);
        if (table.Length < 36 || signature == "FACS")
        {
            return new LabAcpiHeader(signature, length, 0, string.Empty, string.Empty, 0, string.Empty);
        }

        return new LabAcpiHeader(
            signature,
            length,
            table[8],
            AsciiField(table.Slice(10, 6)),
            AsciiField(table.Slice(16, 8)),
            BinaryPrimitives.ReadUInt32LittleEndian(table.Slice(24, 4)),
            AsciiField(table.Slice(28, 4)));
    }

    /// <summary>Turns a table ID from the enumeration into its signature text.</summary>
    /// <param name="tableId">Table ID as returned by <c>EnumSystemFirmwareTables</c>.</param>
    /// <returns>Four characters, in the order they appear in the table.</returns>
    public static string AcpiSignature(uint tableId)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, tableId);
        return AsciiField(bytes);
    }

    /// <summary>Whether a table is a Windows licence table that must never be read.</summary>
    /// <param name="signature">Table signature.</param>
    /// <returns><see langword="true" /> for MSDM and SLIC.</returns>
    public static bool IsLicenceTable(string signature)
    {
        return LicenceTables.Contains(signature, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>File name for the <paramref name="occurrence" />th table with a signature.</summary>
    /// <param name="signature">Table signature.</param>
    /// <param name="occurrence">One for the first table with this signature.</param>
    /// <returns>A name such as <c>acpi-SSDT-2.aml</c>.</returns>
    public static string AcpiFileName(string signature, int occurrence)
    {
        var safe = new string([.. signature.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')]);
        return occurrence <= 1 ? $"acpi-{safe}.aml" : $"acpi-{safe}-{occurrence}.aml";
    }

    private static LabSystemDumpSectionResult CollectAcpi(LabSystemDumpContext context)
    {
        List<string> issues = [];
        List<LabAcpiTableEntry> tables = [];
        Dictionary<string, int> occurrences = new(StringComparer.Ordinal);
        Dictionary<string, string> filesByHash = new(StringComparer.Ordinal);
        var written = 0;

        // 1. HKLM\HARDWARE\ACPI holds every loaded table, each SSDT under its own OEM ID, table ID and
        //    revision. GetSystemFirmwareTable returns only the first table per signature.
        try
        {
            foreach (var (keyPath, table) in RegistryAcpiTables(context, issues))
            {
                Save(table, "registry", keyPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException)
        {
            AddIssue(issues, $@"HKLM\HARDWARE\ACPI: {ex.Message}");
        }

        // 2. GetSystemFirmwareTable, for anything the registry lacks.
        var ids = EnumerateFirmwareTables(AcpiProvider);
        foreach (var id in ids.Take(MaximumAcpiTables))
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var signature = AcpiSignature(id);
            if (IsLicenceTable(signature))
            {
                if (!tables.Any(entry => entry.Signature == signature))
                {
                    tables.Add(new LabAcpiTableEntry
                        { Signature = signature, Skipped = "Windows licence table, not read" });
                }

                continue;
            }

            byte[] table;
            try
            {
                table = ReadFirmwareTable(AcpiProvider, id);
            }
            catch (Win32Exception ex)
            {
                AddIssue(issues, $"{signature}: {ex.Message}");
                tables.Add(new LabAcpiTableEntry { Signature = signature, Source = "firmware", Skipped = ex.Message });
                continue;
            }

            // A table the registry already gave is not listed again.
            if (!filesByHash.ContainsKey(Convert.ToHexString(SHA256.HashData(table))))
            {
                Save(table, "firmware", null);
            }
        }

        if (ids.Count > MaximumAcpiTables)
        {
            AddIssue(issues, $"Only the first {MaximumAcpiTables} of {ids.Count} firmware tables were read.");
        }

        if (written == 0)
        {
            AddIssue(issues, context.Elevated
                ? "No ACPI tables could be read."
                : "No ACPI tables could be read; reading them may need administrator rights.");
        }

        context.Write("acpi", new { context.Elevated, Tables = tables, Issues = issues });
        if (written == 0)
        {
            return new LabSystemDumpSectionResult
            {
                Id = "acpi",
                Status = context.Elevated ? LabSystemDumpSectionStatus.Failed : LabSystemDumpSectionStatus.Skipped,
                Summary = context.Elevated ? "none found" : "skipped without administrator rights",
                Issues = issues
            };
        }

        return Result("acpi", written, Plural(written, "table saved", "tables saved"), issues);

        void Save(byte[] table, string source, string? keyPath)
        {
            var header = ParseAcpiHeader(table);
            var signature = header?.Signature ?? "UNKN";
            if (IsLicenceTable(signature))
            {
                tables.Add(new LabAcpiTableEntry
                    { Signature = signature, Source = source, Skipped = "Windows licence table, not kept" });
                return;
            }

            var hash = Convert.ToHexString(SHA256.HashData(table));
            if (filesByHash.TryGetValue(hash, out var earlier))
            {
                tables.Add(Entry(signature, header, table.Length) with
                    { Source = source, RegistryKey = keyPath, SameAs = earlier });
                return;
            }

            if (written >= MaximumAcpiTables)
            {
                AddIssue(issues, $"More than {MaximumAcpiTables} tables; the rest were not saved.");
                return;
            }

            occurrences[signature] = occurrences.GetValueOrDefault(signature) + 1;
            var fileName = AcpiFileName(signature, occurrences[signature]);
            DurableFile.WriteNew(Path.Combine(context.Attempt, fileName), stream => stream.Write(table));
            filesByHash[hash] = fileName;
            tables.Add(Entry(signature, header, table.Length) with
                { Source = source, RegistryKey = keyPath, FileName = fileName });
            written++;
        }

        static LabAcpiTableEntry Entry(string signature, LabAcpiHeader? header, int bytes)
        {
            return header is null || (header.OemId.Length == 0 && header.OemTableId.Length == 0)
                ? new LabAcpiTableEntry { Signature = signature, Length = header?.Length ?? (uint)bytes }
                : new LabAcpiTableEntry
                {
                    Signature = signature,
                    Length = header.Length,
                    Revision = header.Revision,
                    OemId = header.OemId,
                    OemTableId = header.OemTableId,
                    OemRevision = Hex(header.OemRevision),
                    CreatorId = header.CreatorId
                };
        }
    }

    // Walks HKLM\HARDWARE\ACPI\<signature>\<OEM ID>\<table ID>\<revision>, whose binary values are
    // the tables. MSDM and SLIC keys are never opened.
    private static List<(string KeyPath, byte[] Table)> RegistryAcpiTables(
        LabSystemDumpContext context,
        List<string> issues)
    {
        List<(string, byte[])> found = [];
        using var root = Registry.LocalMachine.OpenSubKey(@"HARDWARE\ACPI");
        if (root is null)
        {
            AddIssue(issues, @"HKLM\HARDWARE\ACPI is missing.");
            return found;
        }

        foreach (var signature in root.GetSubKeyNames())
        {
            if (IsLicenceTable(signature))
            {
                continue;
            }

            using var key = root.OpenSubKey(signature);
            if (key is not null)
            {
                Walk(key, signature, 0);
            }
        }

        return found;

        void Walk(RegistryKey key, string path, int depth)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            foreach (var name in key.GetValueNames())
            {
                if (found.Count >= MaximumAcpiTables)
                {
                    return;
                }

                if (key.GetValueKind(name) == RegistryValueKind.Binary
                    && key.GetValue(name) is byte[] { Length: >= 8 and <= MaximumFirmwareTableBytes } table)
                {
                    found.Add((name.Length == 0 ? path : $@"{path}\{name}", table));
                }
            }

            if (depth >= 4)
            {
                return;
            }

            foreach (var child in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(child);
                if (sub is not null)
                {
                    Walk(sub, $@"{path}\{child}", depth + 1);
                }
            }
        }
    }

    private static List<uint> EnumerateFirmwareTables(uint provider)
    {
        var size = EnumSystemFirmwareTables(provider, null, 0);
        if (size == 0 || size > MaximumFirmwareTableBytes)
        {
            return [];
        }

        var buffer = new byte[size];
        var read = EnumSystemFirmwareTables(provider, buffer, size);
        List<uint> ids = [];
        for (var offset = 0; offset + 4 <= Math.Min(read, size); offset += 4)
        {
            ids.Add(BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4)));
        }

        return ids;
    }

    private static byte[] ReadFirmwareTable(uint provider, uint id)
    {
        var size = GetSystemFirmwareTable(provider, id, null, 0);
        if (size == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        if (size > MaximumFirmwareTableBytes)
        {
            throw new Win32Exception($"The table is larger than {MaximumFirmwareTableBytes / (1024 * 1024)} MiB.");
        }

        var buffer = new byte[size];
        var read = GetSystemFirmwareTable(provider, id, buffer, size);
        if (read == 0 || read > size)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return read == size ? buffer : buffer[..(int)read];
    }

    private static string AsciiField(ReadOnlySpan<byte> bytes)
    {
        StringBuilder text = new(bytes.Length);
        foreach (var value in bytes)
        {
            if (value == 0)
            {
                break;
            }

            text.Append(value is >= 0x20 and < 0x7F ? (char)value : '?');
        }

        return text.ToString().TrimEnd();
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint EnumSystemFirmwareTables(uint provider, [Out] byte[]? buffer, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetSystemFirmwareTable(uint provider, uint tableId, [Out] byte[]? buffer, uint size);
}
