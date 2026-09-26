using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace WSGM.DeviceLab.Capture.Live;

/// <summary>One WMI event the firmware declares in an ACPI <c>_WDG</c> table.</summary>
/// <param name="Guid">The event GUID.</param>
/// <param name="NotifyId">The ACPI notification value that raises it.</param>
/// <param name="Expensive">Whether enabling it runs the firmware's <c>WExx</c> method.</param>
internal readonly record struct LabWdgEvent(Guid Guid, int NotifyId, bool Expensive);

/// <summary>
///     Finds the WMI event classes that belong to the machine's own firmware, for any vendor.
/// </summary>
/// <remarks>
///     <para>
///         Every ACPI-WMI firmware declares its data blocks, methods and events in <c>_WDG</c> buffers
///         (20 bytes each: GUID, object ID or notification value, instance count, flags; flag 0x8 marks an
///         event, 0x1 an expensive one). Microsoft's <c>wmiacpi.sys</c> publishes each event GUID as a
///         <c>root\wmi</c> class with that GUID as its <c>guid</c> qualifier. Reading the tables from
///         <c>HKLM\HARDWARE\ACPI</c> and the class qualifiers are both plain reads.
///     </para>
///     <para>
///         Only those classes are subscribed to. Subscribing to every <c>root\wmi</c> event class also
///         enabled 40-odd kernel trace providers (stack walks, handles, loader, processor states, storage
///         and audio drivers), and one of them completed the enable request twice on an ROG Xbox Ally X:
///         bugcheck 0x44 in <c>nt!WmipSendEnableDisableRequest</c>, three times on 2026-09-25. None of those
///         classes carries a button press.
///     </para>
/// </remarks>
internal static class LabWmiFirmwareEvents
{
    private const int EntryBytes = 20;
    private const int MaximumTables = 256;
    private const int MaximumTableBytes = 8 * 1024 * 1024;

    /// <summary>Decodes every <c>_WDG</c> buffer in one AML table (DSDT or SSDT).</summary>
    /// <param name="table">The whole table, header included.</param>
    /// <returns>The events it declares; data blocks and methods are left out.</returns>
    public static List<LabWdgEvent> ParseWdgEvents(ReadOnlySpan<byte> table)
    {
        List<LabWdgEvent> events = [];
        var name = "_WDG"u8;
        var at = 0;
        while ((at = IndexOf(table, name, at)) >= 0)
        {
            var position = at + name.Length;
            at = position;
            // Name(_WDG, Buffer(size) { ... }): BufferOp 0x11, PkgLength, size term, then the bytes.
            if (position >= table.Length || table[position] != 0x11)
            {
                continue;
            }

            position++;
            if (!TrySkipPackageLength(table, ref position) || !TryReadInteger(table, ref position, out var size))
            {
                continue;
            }

            var bytes = (int)Math.Min(size, (ulong)(table.Length - position));
            for (var entry = 0; entry + EntryBytes <= bytes; entry += EntryBytes)
            {
                var item = table.Slice(position + entry, EntryBytes);
                var flags = item[19];
                if ((flags & 0x8) == 0)
                {
                    continue;
                }

                events.Add(new LabWdgEvent(new Guid(item[..16]), item[16], (flags & 0x1) != 0));
            }
        }

        return events;
    }

    /// <summary>
    ///     The <c>root\wmi</c> event classes the firmware declares, by name, read from the loaded ACPI tables
    ///     and the WMI class qualifiers.
    /// </summary>
    /// <param name="problem">Why nothing could be read, or null.</param>
    /// <returns>Class names with their <c>_WDG</c> entry.</returns>
    public static List<(string Class, LabWdgEvent Event)> Discover(out string? problem)
    {
        problem = null;
        List<LabWdgEvent> declared = [];
        try
        {
            foreach (var table in AmlTables())
            {
                declared.AddRange(ParseWdgEvents(table));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or SecurityException)
        {
            problem = $@"HKLM\HARDWARE\ACPI: {ex.Message}";
            return [];
        }

        if (declared.Count == 0)
        {
            return [];
        }

        List<(string, LabWdgEvent)> found = [];
        try
        {
            using ManagementObjectSearcher search = new(@"root\wmi",
                "SELECT * FROM meta_class WHERE __this ISA 'WMIEvent'");
            using var classes = search.Get();
            foreach (var definition in classes)
            {
                using (definition)
                {
                    var className = Convert.ToString(definition["__CLASS"]);
                    if (string.IsNullOrEmpty(className) || Qualifier(definition, "guid") is not string text
                                                        || !Guid.TryParse(text, out var guid))
                    {
                        continue;
                    }

                    foreach (var match in declared.Where(item => item.Guid == guid))
                    {
                        found.Add((className, match));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            problem = $@"root\wmi event classes: {ex.Message}";
        }

        return found;
    }

    private static object? Qualifier(ManagementBaseObject definition, string name)
    {
        try
        {
            return definition.Qualifiers[name].Value;
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    // HKLM\HARDWARE\ACPI\<signature>\<OEM ID>\<table ID>\<revision> holds every loaded table; only the AML
    // tables can carry _WDG. The licence tables are never opened.
    private static List<byte[]> AmlTables()
    {
        List<byte[]> found = [];
        using var root = Registry.LocalMachine.OpenSubKey(@"HARDWARE\ACPI");
        if (root is null)
        {
            return found;
        }

        foreach (var signature in new[] { "DSDT", "SSDT" })
        {
            using var key = root.OpenSubKey(signature);
            if (key is not null)
            {
                Walk(key, 0);
            }
        }

        return found;

        void Walk(RegistryKey key, int depth)
        {
            foreach (var value in key.GetValueNames())
            {
                if (found.Count < MaximumTables && key.GetValueKind(value) == RegistryValueKind.Binary
                                                && key.GetValue(value) is byte[]
                                                {
                                                    Length: >= 36 and <= MaximumTableBytes
                                                } table)
                {
                    found.Add(table);
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
                    Walk(sub, depth + 1);
                }
            }
        }
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle, int from)
    {
        if (from >= haystack.Length)
        {
            return -1;
        }

        var found = haystack[from..].IndexOf(needle);
        return found < 0 ? -1 : from + found;
    }

    // ACPI PkgLength: bits 7-6 of the lead byte count the bytes that follow.
    private static bool TrySkipPackageLength(ReadOnlySpan<byte> table, ref int position)
    {
        if (position >= table.Length)
        {
            return false;
        }

        position += 1 + (table[position] >> 6);
        return position < table.Length;
    }

    // The buffer size term: ByteConst, WordConst, DWordConst, QWordConst, Zero or One.
    private static bool TryReadInteger(ReadOnlySpan<byte> table, ref int position, out ulong value)
    {
        value = 0;
        if (position >= table.Length)
        {
            return false;
        }

        var (width, constant) = table[position] switch
        {
            0x0A => (1, (ulong?)null),
            0x0B => (2, null),
            0x0C => (4, null),
            0x0E => (8, null),
            0x00 => (0, 0UL),
            0x01 => (0, 1UL),
            _ => (-1, null)
        };
        if (width < 0 || position + 1 + width > table.Length)
        {
            return false;
        }

        var bytes = table.Slice(position + 1, width);
        value = constant ?? width switch
        {
            1 => bytes[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            _ => BinaryPrimitives.ReadUInt64LittleEndian(bytes)
        };
        position += 1 + width;
        return true;
    }
}

/// <summary>
///     Remembers a WMI event class across a crash. Its name is on disk, written through, while its enable
///     request is in flight; a name still there at the next start means enabling it took the machine down,
///     and it is never enabled on this machine again.
/// </summary>
internal static class LabWmiQuarantine
{
    private static readonly string Folder = Path.Combine(
        // wsgm-allow-live-data-path: Device Lab's own root beside WSGM's data, never inside it.
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WSGM Device Lab", "wizard");

    private static string Pending => Path.Combine(Folder, "wmi-enabling.txt");

    private static string Blocked => Path.Combine(Folder, "wmi-blocked.txt");

    /// <summary>Moves a class left pending by a crash to the blocked list; call once per start.</summary>
    /// <returns>The class that crashed the machine last time, or null.</returns>
    public static string? RecoverFromCrash()
    {
        try
        {
            if (!File.Exists(Pending))
            {
                return null;
            }

            var name = File.ReadAllText(Pending).Trim();
            if (name.Length > 0 && !IsBlocked(name))
            {
                Append(Blocked, name);
            }

            File.Delete(Pending);
            return name.Length > 0 ? name : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether a class crashed this machine before.</summary>
    /// <param name="name">Class name.</param>
    /// <returns>True when it must not be enabled.</returns>
    public static bool IsBlocked(string name)
    {
        try
        {
            return File.Exists(Blocked)
                   && File.ReadAllLines(Blocked)
                       .Any(line => string.Equals(line.Trim(), name, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Records the class about to be enabled, on disk before the request is sent.</summary>
    /// <param name="name">Class name.</param>
    public static void Begin(string name)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            using FileStream stream = new(Pending, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
                FileOptions.WriteThrough);
            stream.Write(Encoding.UTF8.GetBytes(name));
            stream.Flush(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Clears the pending record once the enable request returned.</summary>
    public static void End()
    {
        try
        {
            File.Delete(Pending);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Append(string path, string line)
    {
        Directory.CreateDirectory(Folder);
        using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096,
            FileOptions.WriteThrough);
        stream.Write(Encoding.UTF8.GetBytes(line + Environment.NewLine));
        stream.Flush(true);
    }
}
