using System.Text.Json.Serialization;

namespace WSGM.Device.Contracts.Identity;

/// <summary>
/// The exact machine-readable facts a device definition may match against.
/// </summary>
/// <remarks>
/// Every signal names its exact source, because "SMBIOS product" is ambiguous and getting it wrong
/// silently breaks detection: on the MSI Claw 8 AI+ A2VM, <c>MS-1T52</c> is the *baseboard* product
/// (SMBIOS Type 2) while the *system* product (Type 1) is the marketing string
/// <c>Claw 8 AI+ A2VM</c>. A matcher that reads "product" as Type 1 never matches that device at all.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<IdentitySignal>))]
public enum IdentitySignal
{
    /// <summary>SMBIOS Type 1 manufacturer.</summary>
    SmbiosSystemManufacturer,

    /// <summary>SMBIOS Type 1 product name. Marketing text: weak display evidence only.</summary>
    SmbiosSystemProduct,

    /// <summary>SMBIOS Type 1 SKU number.</summary>
    SmbiosSystemSku,

    /// <summary>SMBIOS Type 1 family. A coarse family predicate, never an exact gate on its own.</summary>
    SmbiosSystemFamily,

    /// <summary>SMBIOS Type 2 baseboard product — the exact board identifier.</summary>
    SmbiosBaseboardProduct,

    /// <summary>SMBIOS Type 2 baseboard version or revision.</summary>
    SmbiosBaseboardVersion,

    /// <summary>System BIOS version string.</summary>
    BiosVersion,

    /// <summary>Embedded-controller firmware version.</summary>
    /// <remarks>
    /// Sourced from the vendor provider, not SMBIOS: <c>Win32_BIOS.EmbeddedControllerMajorVersion</c>
    /// and its minor counterpart return the SMBIOS "unknown" encoding on real hardware.
    /// </remarks>
    EcFirmwareVersion,

    /// <summary>Controller or MCU firmware version.</summary>
    McuFirmwareVersion,

    /// <summary>CPU family, model, and stepping.</summary>
    CpuIdentity,

    /// <summary>USB vendor ID of a declared endpoint.</summary>
    UsbVendorId,

    /// <summary>USB product ID of a declared endpoint.</summary>
    UsbProductId,

    /// <summary>USB interface number of a declared endpoint.</summary>
    UsbInterfaceNumber,

    /// <summary>USB <c>bcdDevice</c> of a declared endpoint — the controller firmware gate.</summary>
    UsbDeviceRelease,

    /// <summary>Hash of a HID report descriptor.</summary>
    HidReportDescriptorHash,

    /// <summary>Length of a HID input, output, or feature report.</summary>
    HidReportLength,

    /// <summary>Presence of a named WMI class, method, or provider version.</summary>
    WmiProviderSignature,
}

/// <summary>
/// How strongly a device definition binds to one <see cref="IdentitySignal"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<IdentityStrength>))]
public enum IdentityStrength
{
    /// <summary>The observed value must match. A mismatch or absence rejects the definition outright.</summary>
    /// <remarks>
    /// Hard constraints are evaluated before any scoring, so a wrong report length, an excluded
    /// firmware version, or a missing endpoint removes the candidate rather than lowering its rank.
    /// </remarks>
    Required,

    /// <summary>The observed value must not match. Matching rejects the definition outright.</summary>
    Excluded,

    /// <summary>Contributes <see cref="IdentityObservation.Weight"/> to ranking when it matches.</summary>
    /// <remarks>Ordering only. A weighted observation can never rescue a failed hard constraint.</remarks>
    Weighted,

    /// <summary>Recorded for diagnostics and display. Never affects matching or ranking.</summary>
    Informational,
}
