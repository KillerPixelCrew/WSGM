using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace WSGM.DeviceLab.Wizard;

/// <summary>One SMBIOS structure with its identifying fields removed.</summary>
internal sealed record LabSmbiosStructure
{
    /// <summary>Structure type.</summary>
    public required int Type { get; init; }

    /// <summary>Readable type name, when the type is a standard one this tool knows.</summary>
    public string? TypeName { get; init; }

    /// <summary>Structure handle, as hex.</summary>
    public required string Handle { get; init; }

    /// <summary>Length of the formatted area, including the four header bytes.</summary>
    public required int Length { get; init; }

    /// <summary>
    ///     The formatted area after the header, as hex, with binary serial numbers and UUIDs zeroed.
    ///     Left out for OEM-defined types, whose layout is unknown.
    /// </summary>
    public string? Data { get; init; }

    /// <summary>The string set in order; string N is at index N - 1.</summary>
    public IReadOnlyList<string> Strings { get; init; } = [];
}

/// <summary>The parsed SMBIOS table.</summary>
internal sealed record LabSmbiosTable
{
    /// <summary>SMBIOS version, for example 3.4.</summary>
    public required string Version { get; init; }

    /// <summary>Structures in table order.</summary>
    public required IReadOnlyList<LabSmbiosStructure> Structures { get; init; }

    /// <summary>How many fields were replaced by "(removed)" or zeroed.</summary>
    public int RemovedFields { get; init; }

    /// <summary>Problems found while parsing.</summary>
    public IReadOnlyList<string> Issues { get; init; } = [];
}

internal static partial class LabSystemDump
{
    /// <summary>What replaces a removed SMBIOS string.</summary>
    public const string RemovedText = "(removed)";

    private const int MaximumSmbiosStructures = 2048;

    // Offsets of string-number fields that hold serial numbers, asset tags or part numbers.
    private static readonly Dictionary<int, int[]> SensitiveStringFields = new()
    {
        [1] = [0x07], // System: serial number
        [2] = [0x07, 0x08], // Baseboard: serial number, asset tag
        [3] = [0x07, 0x08], // Chassis: serial number, asset tag
        [4] = [0x20, 0x21, 0x22], // Processor: serial number, asset tag, part number
        [17] = [0x18, 0x19, 0x1A], // Memory device: serial number, asset tag, part number
        [22] = [0x07], // Portable battery: serial number
        [39] = [0x08, 0x09, 0x0A] // Power supply: serial number, asset tag, model part number
    };

    // Binary identifying fields: offset and length inside the formatted area.
    private static readonly Dictionary<int, (int Offset, int Length)> SensitiveBinaryFields = new()
    {
        [1] = (0x08, 16), // System UUID
        [22] = (0x10, 2) // Portable battery: SBDS serial number
    };

    private static readonly Dictionary<int, string> SmbiosTypeNames = new()
    {
        [0] = "BIOS",
        [1] = "System",
        [2] = "Baseboard",
        [3] = "Chassis",
        [4] = "Processor",
        [7] = "Cache",
        [8] = "Port connector",
        [9] = "System slot",
        [10] = "Onboard devices",
        [11] = "OEM strings",
        [12] = "System configuration options",
        [13] = "BIOS language",
        [14] = "Group associations",
        [16] = "Physical memory array",
        [17] = "Memory device",
        [19] = "Memory array mapped address",
        [20] = "Memory device mapped address",
        [21] = "Built-in pointing device",
        [22] = "Portable battery",
        [24] = "Hardware security",
        [26] = "Voltage probe",
        [27] = "Cooling device",
        [28] = "Temperature probe",
        [29] = "Electrical current probe",
        [32] = "System boot",
        [39] = "System power supply",
        [41] = "Onboard devices extended",
        [43] = "TPM device",
        [44] = "Processor additional information",
        [45] = "Firmware inventory",
        [127] = "End of table"
    };

    /// <summary>Parses the raw <c>RSMB</c> firmware table and removes identifying fields.</summary>
    /// <param name="raw">Bytes returned by <c>GetSystemFirmwareTable('RSMB', 0)</c>.</param>
    /// <returns>The parsed table.</returns>
    /// <remarks>
    ///     Serial numbers, asset tags and part numbers of the system, baseboard, chassis, processor,
    ///     memory, battery and power supply become "(removed)", as does any other string with the same
    ///     text. The system UUID and the battery's binary serial are zeroed in <c>Data</c>.
    /// </remarks>
    public static LabSmbiosTable ParseSmbios(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 8)
        {
            return new LabSmbiosTable { Version = "unknown", Structures = [], Issues = ["The table is empty."] };
        }

        List<string> issues = [];
        var version = $"{raw[1].ToString(CultureInfo.InvariantCulture)}.{raw[2].ToString(CultureInfo.InvariantCulture)}";
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(raw[4..8]);
        var data = raw[8..];
        if (declared < data.Length)
        {
            data = data[..(int)declared];
        }
        else if (declared > data.Length)
        {
            issues.Add($"The table says it is {declared} bytes but only {data.Length} were returned.");
        }

        List<(int Type, int Handle, int Length, byte[] Formatted, List<string> Strings)> parsed = [];
        HashSet<string> removedValues = new(StringComparer.Ordinal);
        var removed = 0;
        var offset = 0;
        while (offset + 4 <= data.Length)
        {
            if (parsed.Count >= MaximumSmbiosStructures)
            {
                issues.Add($"Only the first {MaximumSmbiosStructures} structures were read.");
                break;
            }

            int type = data[offset];
            int length = data[offset + 1];
            int handle = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 2, 2));
            if (length < 4 || offset + length > data.Length)
            {
                issues.Add($"Structure at byte {offset} has an invalid length {length}.");
                break;
            }

            var formatted = data.Slice(offset, length).ToArray();
            var cursor = offset + length;
            List<string> strings = [];
            if (cursor + 1 < data.Length && data[cursor] == 0 && data[cursor + 1] == 0)
            {
                cursor += 2;
            }
            else
            {
                var ended = false;
                while (cursor < data.Length)
                {
                    var end = data[cursor..].IndexOf((byte)0);
                    if (end < 0)
                    {
                        break;
                    }

                    strings.Add(Encoding.UTF8.GetString(data.Slice(cursor, end)).Trim());
                    cursor += end + 1;
                    if (cursor < data.Length && data[cursor] == 0)
                    {
                        cursor++;
                        ended = true;
                        break;
                    }
                }

                if (!ended)
                {
                    issues.Add($"Structure {Hex((uint)handle, 4)} has an unterminated string set.");
                    cursor = data.Length;
                }
            }

            if (SensitiveStringFields.TryGetValue(type, out var fields))
            {
                foreach (var field in fields.Where(field => field < length))
                {
                    var number = formatted[field];
                    if (number >= 1 && number <= strings.Count && strings[number - 1] != RemovedText)
                    {
                        if (strings[number - 1].Length >= 4)
                        {
                            removedValues.Add(strings[number - 1]);
                        }

                        strings[number - 1] = RemovedText;
                        removed++;
                    }
                }
            }

            if (SensitiveBinaryFields.TryGetValue(type, out var binary) && binary.Offset < length)
            {
                formatted.AsSpan(binary.Offset, Math.Min(binary.Length, length - binary.Offset)).Clear();
                removed++;
            }

            parsed.Add((type, handle, length, formatted, strings));
            offset = cursor;
            if (type == 127)
            {
                break;
            }
        }

        // The same serial often appears again in another structure, for example the system serial
        // copied into an OEM string, so every string with a removed value's text is removed as well.
        foreach (var strings in parsed.Select(item => item.Strings))
        {
            for (var index = 0; index < strings.Count; index++)
            {
                if (removedValues.Contains(strings[index]))
                {
                    strings[index] = RemovedText;
                    removed++;
                }
            }
        }

        return new LabSmbiosTable
        {
            Version = version,
            RemovedFields = removed,
            Issues = issues,
            Structures =
            [
                .. parsed.Select(item => new LabSmbiosStructure
                {
                    Type = item.Type,
                    TypeName = SmbiosTypeNames.GetValueOrDefault(item.Type),
                    Handle = Hex((uint)item.Handle, 4),
                    Length = item.Length,
                    Data = item.Type < 128 && item.Length > 4 ? Convert.ToHexString(item.Formatted, 4, item.Length - 4) : null,
                    Strings = item.Strings
                })
            ]
        };
    }

    private static LabSystemDumpSectionResult CollectSmbios(LabSystemDumpContext context)
    {
        var raw = ReadFirmwareTable(RsmbProvider, 0);
        var table = ParseSmbios(raw);
        context.Write("smbios", table);
        return Result(
            "smbios",
            table.Structures.Count,
            Plural(table.Structures.Count, "entry", "entries"),
            [.. table.Issues]);
    }
}
