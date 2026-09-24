using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using WSGM.Device.Sdk.Identity;

namespace WSGM.Install;

/// <summary>Read-only machine identity used before plugin code is loaded.</summary>
public static class DeviceMachineIdentity
{
    /// <summary>Reads stable SMBIOS values exposed by Windows in the hardware registry hive.</summary>
    public static DeviceIdentitySnapshot Collect()
    {
        using var bios = Registry.LocalMachine.OpenSubKey(
            @"HARDWARE\DESCRIPTION\System\BIOS",
            false);
        using var cpu = Registry.LocalMachine.OpenSubKey(
            @"HARDWARE\DESCRIPTION\System\CentralProcessor\0",
            false);
        return new DeviceIdentitySnapshot
        {
            SystemManufacturer = Normalize(bios?.GetValue("SystemManufacturer") as string),
            SystemProduct = Normalize(bios?.GetValue("SystemProductName") as string),
            SystemSku = Normalize(bios?.GetValue("SystemSKU") as string),
            SystemFamily = Normalize(bios?.GetValue("SystemFamily") as string),
            BaseboardManufacturer = Normalize(bios?.GetValue("BaseBoardManufacturer") as string),
            BaseboardProduct = Normalize(bios?.GetValue("BaseBoardProduct") as string),
            BaseboardVersion = Normalize(bios?.GetValue("BaseBoardVersion") as string),
            BiosVersion = Normalize(bios?.GetValue("BIOSVersion") as string),
            CpuIdentity = Normalize(cpu?.GetValue("Identifier") as string),
            ProcessorName = Normalize(cpu?.GetValue("ProcessorNameString") as string)
        };
    }

    /// <summary>Builds a stable, non-secret local key for persisted per-device intent.</summary>
    public static string StableKey(DeviceIdentitySnapshot identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var material = string.Join('|',
            identity.SystemManufacturer ?? string.Empty,
            identity.BaseboardProduct ?? string.Empty,
            identity.BaseboardVersion ?? string.Empty,
            identity.SystemSku ?? string.Empty).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..24];
    }

    private static string? Normalize(string? value)
    {
        return IdentityText.Normalize(value);
    }
}
