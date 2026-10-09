using System;
using System.Security.Cryptography;
using System.Text;
using LibHandheld;
using LibHandheld.Contracts;

namespace WSGM.Install;

/// <summary>Read-only machine identity used before plugin code is loaded.</summary>
public static class DeviceMachineIdentity
{
    /// <summary>Captures the library's read-only Windows identity, including USB interfaces.</summary>
    /// <returns>A normalized identity with null fields for absent registry values.</returns>
    public static DeviceIdentitySnapshot Collect()
    {
        return HandheldDevice.ReadIdentity();
    }

    /// <summary>Builds a stable, non-secret local key for persisted per-device intent.</summary>
    /// <param name="identity">Identity whose manufacturer, baseboard, revision and SKU form the key.</param>
    /// <returns>The first 24 uppercase hexadecimal digits of the normalized identity's SHA-256 hash.</returns>
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
}
