using System.Runtime.InteropServices;
using WSGM.Plugin.IntelGpu.Controls;
using WSGM.Plugin.IntelGpu.Igcl;

namespace WSGM.Plugin.IntelGpu.Display;

/// <summary>Who a display output is, in terms that survive a reboot and a reconnect.</summary>
/// <param name="InstanceId">The stable capability instance id.</param>
/// <param name="Name">The plain name for its section, at most 48 characters.</param>
/// <param name="Internal">Whether Windows reports the output as the built-in panel.</param>
internal sealed record DisplayIdentity(string InstanceId, string Name, bool Internal);

/// <summary>
///     Resolves an IGCL output to a stable identity through Windows' own display configuration.
/// </summary>
/// <remarks>
///     IGCL hands over the adapter LUID and the Windows target id. Both are session keys: the LUID
///     changes every boot and the target id belongs to the connector. Windows maps the pair to the
///     monitor's EDID manufacturer and product codes and to its monitor device path
///     (<c>\\?\DISPLAY#BOE0B78#4&amp;...&amp;UID8388688#{...}</c>), whose instance part the PnP manager
///     keeps for the same monitor on the same connector across reboots and replugging.
///     <para>
///         So the instance id is <c>edid-</c> plus the three-letter manufacturer and four-digit product
///         code, then an FNV-1a fingerprint of the device path: <c>edid-boe0b78-1a2b3c4d</c>. The EDID
///         part keeps the id readable and tells two monitors apart even if Windows ever renumbered a
///         connector; the fingerprint keeps two identical monitors apart. Intel's own Arc Sync registry
///         key (<c>IntelArcSyncProfile_165_09e500780b0000000018</c>) is EDID-derived in the same spirit.
///         A monitor moved to another connector is a new instance, as it is for Windows itself.
///     </para>
///     <para>
///         The built-in panel's id starts with <c>internal-</c>. IGCL's encoder flag
///         (<c>CTL_ENCODER_CONFIG_FLAG_INTERNAL_DISPLAY</c>) decides, and Windows' output technology
///         (internal, embedded DisplayPort, LVDS or embedded UDI) answers only when IGCL does not.
///     </para>
/// </remarks>
internal static unsafe partial class DisplayIdentityResolver
{
    /// <summary>The prefix of the built-in panel's instance id.</summary>
    public const string InternalPrefix = "internal-";

    private const int DeviceInfoGetTargetName = 2;
    private const uint FlagEdidIdsValid = 1 << 2;

    /// <summary><c>DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL</c>.</summary>
    private const uint OutputInternal = 0x80000000;

    /// <summary><c>DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED</c>.</summary>
    private const uint OutputDisplayPortEmbedded = 11;

    /// <summary><c>DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS</c>.</summary>
    private const uint OutputLvds = 6;

    /// <summary><c>DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED</c>.</summary>
    private const uint OutputUdiEmbedded = 13;

    /// <summary>Size of <c>DISPLAYCONFIG_TARGET_DEVICE_NAME</c>, for a layout test.</summary>
    internal static int TargetNameSize => sizeof(TargetDeviceName);

    /// <summary>Resolves one output.</summary>
    /// <param name="output">The IGCL output.</param>
    /// <param name="log">Receives the fallback decisions.</param>
    /// <returns>The identity; never null, falling back to a session-local id when Windows says nothing.</returns>
    public static DisplayIdentity Resolve(IgclOutput output, IntelLog log)
    {
        TargetDeviceName name = default;
        name.Header.Type = DeviceInfoGetTargetName;
        name.Header.Size = (uint)sizeof(TargetDeviceName);
        name.Header.AdapterLow = (uint)(output.Adapter.Luid & 0xffffffff);
        name.Header.AdapterHigh = (int)(output.Adapter.Luid >> 32);
        name.Header.Id = output.TargetId;
        var status = DisplayConfigGetDeviceInfo(ref name);
        if (status != 0)
        {
            log.Warn(
                "display",
                $"Windows has no name for target {output.TargetId} on adapter {output.Adapter.Index} "
                + $"(error {status}); its settings are not remembered across reboots.");
            var fallbackInternal = output.Internal == true;
            return new DisplayIdentity(
                (fallbackInternal ? InternalPrefix : "") + $"adapter{output.Adapter.Index}-target{output.TargetId}",
                fallbackInternal ? "Built-in display" : $"Display {output.Index + 1}",
                fallbackInternal);
        }

        var path = Text(name.DevicePath, 128);
        var friendly = Text(name.FriendlyName, 64).Trim();

        // IGCL's encoder flag decides; Windows' output technology only answers when IGCL does not.
        var isInternal = output.Internal
                         ?? name.OutputTechnology is OutputInternal or OutputDisplayPortEmbedded or OutputLvds
                             or OutputUdiEmbedded;
        var manufacturer = (name.Flags & FlagEdidIdsValid) != 0
            ? ValueMapping.DecodeManufacturer(name.EdidManufactureId)
            : null;
        var instance = InstanceId(isInternal, manufacturer, name.EdidProductCodeId,
            path.Length > 0 ? path : $"target-{output.TargetId}");
        var label = isInternal
            ? "Built-in display"
            : friendly.Length > 0
                ? friendly
                : $"Display {output.Index + 1}";
        return new DisplayIdentity(instance, Descriptors.Label(label), isInternal);
    }

    /// <summary>Builds the instance id of a display.</summary>
    /// <param name="isInternal">Whether it is the built-in panel.</param>
    /// <param name="manufacturer">The decoded EDID manufacturer, or null.</param>
    /// <param name="product">The EDID product code.</param>
    /// <param name="path">The monitor device path the fingerprint is taken from.</param>
    /// <returns>
    ///     <c>edid-&lt;mfg&gt;&lt;product&gt;-&lt;fingerprint&gt;</c>, or <c>display-&lt;fingerprint&gt;</c>
    ///     without EDID codes, prefixed with <c>internal-</c> for the built-in panel. WSGM's host gives
    ///     Valve's single variable refresh row to an instance starting with <c>internal</c>.
    /// </returns>
    internal static string InstanceId(bool isInternal, string? manufacturer, ushort product, string path)
    {
        var fingerprint = ValueMapping.Fingerprint(path);
        var id = manufacturer is null ? $"display-{fingerprint}" : $"edid-{manufacturer}{product:x4}-{fingerprint}";
        return isInternal ? InternalPrefix + id : id;
    }

    private static string Text(char* buffer, int length)
    {
        var span = new ReadOnlySpan<char>(buffer, length);
        var end = span.IndexOf('\0');
        return new string(end < 0 ? span : span[..end]);
    }

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigGetDeviceInfo(ref TargetDeviceName request);

    /// <summary><c>DISPLAYCONFIG_DEVICE_INFO_HEADER</c>, 20 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public int Type;
        public uint Size;
        public uint AdapterLow;
        public int AdapterHigh;
        public uint Id;
    }

    /// <summary><c>DISPLAYCONFIG_TARGET_DEVICE_NAME</c>, 420 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TargetDeviceName
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        public fixed char FriendlyName[64];
        public fixed char DevicePath[128];
    }
}
