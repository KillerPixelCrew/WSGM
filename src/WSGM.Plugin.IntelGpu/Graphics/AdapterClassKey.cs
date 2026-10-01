using System.Globalization;
using System.Security;

namespace WSGM.Plugin.IntelGpu.Graphics;

/// <summary>One installed display adapter under the display adapter class key.</summary>
/// <param name="Path">The adapter's key path below the hive, <c>&lt;class&gt;\NNNN</c>.</param>
/// <param name="MatchingDeviceId">Its <c>MatchingDeviceId</c>, for example <c>pci\ven_8086&amp;dev_4688</c>.</param>
internal sealed record AdapterClassEntry(string Path, string? MatchingDeviceId);

/// <summary>The display adapter class key, whose numbered subkeys are the installed adapters.</summary>
/// <remarks>
///     Every read here is contained to one subkey. Measured on the Claw on 2026-09-10: the class holds an
///     unreadable <c>0000</c> alongside the Intel adapter, so an access failure on one sibling must not
///     hide the rest.
/// </remarks>
internal static class AdapterClassKey
{
    /// <summary>The display adapter class below HKLM.</summary>
    public const string ClassPath =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>Lists the adapters: every four-digit subkey, with its <c>MatchingDeviceId</c>.</summary>
    /// <param name="root">The hive.</param>
    /// <param name="classPath">The class key path below it.</param>
    /// <param name="log">Receives an enumeration failure.</param>
    /// <returns>The adapters in key order; an unreadable one has a null device id.</returns>
    public static IReadOnlyList<AdapterClassEntry> Enumerate(IRegistryNode root, string classPath, IntelLog log)
    {
        List<AdapterClassEntry> entries = [];
        try
        {
            using var adapters = root.OpenSubKey(classPath);
            if (adapters is null)
            {
                return entries;
            }

            foreach (var name in adapters.GetSubKeyNames())
            {
                if (name.Length != 4 || !int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    continue;
                }

                string? matching = null;
                try
                {
                    using var adapter = adapters.OpenSubKey(name);
                    matching = adapter?.GetValue("MatchingDeviceId") as string;
                }
                catch (Exception error) when (IsRegistryFailure(error))
                {
                    // An unreadable sibling is listed without an id; it matches no device.
                }

                entries.Add(new AdapterClassEntry($@"{classPath}\{name}", matching));
            }
        }
        catch (Exception error) when (IsRegistryFailure(error))
        {
            log.Warn("registry", $"Enumerating display adapters failed: {IntelLog.Describe(error)}");
        }

        return entries;
    }

    /// <summary>Whether a <c>MatchingDeviceId</c> names an Intel PCI device.</summary>
    /// <param name="matchingDeviceId">The adapter's id.</param>
    /// <param name="pciDeviceId">The PCI device id.</param>
    /// <returns><see langword="true" /> when it names <c>ven_8086&amp;dev_&lt;id&gt;</c>.</returns>
    public static bool Matches(string? matchingDeviceId, uint pciDeviceId)
    {
        return matchingDeviceId is not null
               && matchingDeviceId.Contains(
                   string.Create(CultureInfo.InvariantCulture, $"ven_8086&dev_{pciDeviceId:x4}"),
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether an exception is a registry access failure, which is contained rather than raised.</summary>
    /// <param name="error">The exception.</param>
    /// <returns><see langword="true" /> for access, I/O and disposed-key failures.</returns>
    public static bool IsRegistryFailure(Exception error)
    {
        return error is SecurityException or UnauthorizedAccessException or IOException or ObjectDisposedException;
    }
}
