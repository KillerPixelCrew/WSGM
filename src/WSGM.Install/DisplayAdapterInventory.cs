using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace WSGM.Install;

/// <summary>PCI identity of one present display adapter.</summary>
/// <param name="PciVendorId">Four uppercase hexadecimal digits, for example <c>8086</c>.</param>
/// <param name="PciDeviceId">Four uppercase hexadecimal digits.</param>
/// <param name="InstanceId">Windows device instance id, for diagnostics.</param>
public sealed record DisplayAdapterIdentity(string PciVendorId, string PciDeviceId, string InstanceId);

/// <summary>Read-only list of the display adapters Windows reports as present.</summary>
/// <remarks>
///     Used by setup and by WSGM to decide which <c>wsgm.gpu</c> packages apply before any plugin code is
///     loaded. Only PCI adapters count; virtual displays such as remote-desktop or streaming adapters have
///     no PCI identity and never match a vendor.
/// </remarks>
public static partial class DisplayAdapterInventory
{
    private const string DisplayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const uint FilterClass = 0x00000200;
    private const uint FilterPresent = 0x00000100;
    private const int Success = 0;

    /// <summary>Lists present PCI display adapters, or an empty list when Windows cannot answer.</summary>
    /// <returns>Adapters with parseable PCI vendor and device ids; virtual or malformed entries are omitted.</returns>
    public static IReadOnlyList<DisplayAdapterIdentity> Collect()
    {
        const uint flags = FilterClass | FilterPresent;
        if (CM_Get_Device_ID_List_SizeW(out var length, DisplayClass, flags) != Success || length == 0)
        {
            return [];
        }

        var buffer = new char[length];
        if (CM_Get_Device_ID_ListW(DisplayClass, buffer, length, flags) != Success)
        {
            return [];
        }

        return Parse(new string(buffer));
    }

    /// <summary>Whether any present adapter carries one of the vendor ids.</summary>
    /// <param name="adapters">Present adapters.</param>
    /// <param name="vendorIds">Four-digit hexadecimal PCI vendor ids.</param>
    /// <returns>Whether any adapter matches a supplied vendor, using a case-insensitive comparison.</returns>
    public static bool AnyVendor(IReadOnlyList<DisplayAdapterIdentity> adapters, IEnumerable<string> vendorIds)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(vendorIds);
        foreach (var vendor in vendorIds)
        {
            foreach (var adapter in adapters)
            {
                if (string.Equals(adapter.PciVendorId, vendor, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Parses a double-null-terminated instance id list.</summary>
    /// <param name="multiString">Instance ids separated by NUL characters.</param>
    /// <returns>Only entries carrying parseable PCI vendor and device ids.</returns>
    internal static IReadOnlyList<DisplayAdapterIdentity> Parse(string multiString)
    {
        var adapters = new List<DisplayAdapterIdentity>();
        foreach (var instanceId in multiString.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseInstance(instanceId, out var adapter))
            {
                adapters.Add(adapter);
            }
        }

        return adapters;
    }

    private static bool TryParseInstance(string instanceId, out DisplayAdapterIdentity adapter)
    {
        adapter = null!;
        if (!instanceId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var vendor = Field(instanceId, "VEN_");
        var device = Field(instanceId, "DEV_");
        if (vendor is null || device is null)
        {
            return false;
        }

        adapter = new DisplayAdapterIdentity(vendor, device, instanceId);
        return true;
    }

    private static string? Field(string instanceId, string prefix)
    {
        var start = instanceId.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0 || start + prefix.Length + 4 > instanceId.Length)
        {
            return null;
        }

        var digits = instanceId.Substring(start + prefix.Length, 4);
        return int.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)
            ? digits.ToUpperInvariant()
            : null;
    }

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint length, uint flags);
}
