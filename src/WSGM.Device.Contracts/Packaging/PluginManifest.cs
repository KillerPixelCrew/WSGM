using System.Collections.Generic;
using System.Text.Json.Serialization;
using WSGM.Device.Contracts.Identity;

namespace WSGM.Device.Contracts.Packaging;

/// <summary>
/// The deserialized form of a package's <c>plugin.wsgm.json</c>.
/// </summary>
/// <remarks>
/// This is auditable metadata, not authorization by assertion. Resource and risk declarations
/// describe what a package intends to use; they cannot constrain what its code actually does once it
/// is running. WSGM verifies package trust and install-time prerequisites before activation, and the
/// plugin authoritatively probes its own dependencies at activation and reports per-capability
/// availability.
/// </remarks>
public sealed record PluginManifest
{
    /// <summary>Version of the manifest schema this document is written against.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Stable package identifier, for example <c>wsgm.device.msi.claw-8-a2vm</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Package version, as a dotted numeric version.</summary>
    public required string Version { get; init; }

    /// <summary>Human-readable package name shown in Device diagnostics.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Declared publisher. Verified against the package signature, never trusted from here.</summary>
    public required string Publisher { get; init; }

    /// <summary>Lowest runtime contract version this package supports.</summary>
    public required int MinApiVersion { get; init; }

    /// <summary>Highest runtime contract version this package supports.</summary>
    public required int MaxApiVersion { get; init; }

    /// <summary>Package-relative path of the plugin assembly the host loads.</summary>
    public required string EntryPoint { get; init; }

    /// <summary>Device definitions this package can serve. Each is an exact model, never a family.</summary>
    public IReadOnlyList<DeviceDefinition> Devices { get; init; } = [];

    /// <summary>External components the package needs but may never install or repair itself.</summary>
    public IReadOnlyList<DependencyDeclaration> Dependencies { get; init; } = [];

    /// <summary>Classes of hardware risk the package declares, for review and user disclosure.</summary>
    public IReadOnlyList<RiskDeclaration> RiskDeclarations { get; init; } = [];

    /// <summary>Hash-pinned physical glyph profiles carried by a schema-version 2 package.</summary>
    /// <remarks>
    /// The package layout is fixed by WSGM from the manifest hash. No plugin-supplied path or URL is
    /// represented here, and the referenced profile repeats the hash lock for every artwork asset.
    /// </remarks>
    public IReadOnlyList<GlyphProfilePackageReference> GlyphProfiles { get; init; } = [];

    /// <summary>Source and licensing provenance for the package and its bundled assets.</summary>
    public required PackageProvenance Provenance { get; init; }
}

/// <summary>
/// One exact device this package serves: its identity gates and the modules composed for it.
/// </summary>
/// <remarks>
/// A definition names an exact model, board, and firmware envelope. It does not inherit policy from a
/// family or from an older sibling: reusing a transport or protocol must never import another model's
/// ranges, offsets, tables, persistence assumptions, or recovery behaviour. The A1M's larger power
/// limits leaking into an A2VM descriptor is the concrete failure this rule prevents.
/// </remarks>
public sealed record DeviceDefinition
{
    /// <summary>Stable identifier for this device definition within the package.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable device name shown in Device diagnostics.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Identity predicates that select this definition.</summary>
    public IReadOnlyList<IdentityObservation> Identity { get; init; } = [];

    /// <summary>USB endpoints belonging to this one logical handheld.</summary>
    public IReadOnlyList<UsbEndpointDeclaration> UsbEndpoints { get; init; } = [];

    /// <summary>Hardware resources the plugin intends to own for this device.</summary>
    public IReadOnlyList<ResourceDeclaration> Resources { get; init; } = [];

    /// <summary>Version-pinned implementation modules composed for this device.</summary>
    public IReadOnlyList<ModuleReference> Modules { get; init; } = [];

    /// <summary>Semantic capability IDs this device definition may publish.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>Package glyph profile verified for this exact device definition.</summary>
    /// <remarks>
    /// This is a stable identifier only. Automatic selection additionally requires the loaded
    /// profile to be <c>ExactDeviceVerified</c> for this definition; a missing, unverified, or
    /// mismatched profile fails open to native Steam glyphs.
    /// </remarks>
    public string? GlyphProfileId { get; init; }
}

/// <summary>A package-owned physical glyph profile addressed by immutable manifest hash.</summary>
public sealed record GlyphProfilePackageReference
{
    /// <summary>Stable package-scoped profile identifier.</summary>
    public required string ProfileId { get; init; }

    /// <summary>SHA-256 of the canonical profile manifest bytes.</summary>
    public required string ManifestSha256 { get; init; }
}

/// <summary>
/// One USB endpoint of the logical handheld.
/// </summary>
public sealed record UsbEndpointDeclaration
{
    /// <summary>Stable identifier referenced by identity observations and resources.</summary>
    public required string Id { get; init; }

    /// <summary>Role this endpoint plays, for example a gamepad interface or an MCU control channel.</summary>
    public required string Role { get; init; }

    /// <summary>USB vendor ID as four uppercase hexadecimal digits.</summary>
    public required string VendorId { get; init; }

    /// <summary>Accepted USB product IDs as four uppercase hexadecimal digits.</summary>
    public IReadOnlyList<string> ProductIds { get; init; } = [];

    /// <summary>USB interface number, when the endpoint is one interface of a composite device.</summary>
    public int? InterfaceNumber { get; init; }

    /// <summary>
    /// Whether this endpoint may disappear and return without ending the device generation.
    /// </summary>
    /// <remarks>
    /// A controller that changes mode re-enumerates under a different product ID, so continuation is
    /// keyed on physical USB location rather than on identity: container ID and USB serial are both
    /// unusable for this on real hardware.
    /// </remarks>
    public bool Detachable { get; init; }
}

/// <summary>
/// A hardware resource the plugin intends to acquire.
/// </summary>
/// <remarks>
/// Ownership is per resource: a controller conflict must not disable fan, lighting, power, charge, or
/// OEM-event capabilities. Each resource can be active, passive, or degraded independently.
/// </remarks>
public sealed record ResourceDeclaration
{
    /// <summary>Stable identifier for this resource within the device definition.</summary>
    public required string Id { get; init; }

    /// <summary>Transport class this resource represents.</summary>
    public required ResourceKind Kind { get; init; }

    /// <summary>How the plugin intends to hold the resource.</summary>
    public required ResourceAccess Access { get; init; }

    /// <summary>Endpoint this resource binds to, when it is endpoint-scoped.</summary>
    public string? EndpointId { get; init; }
}

/// <summary>Transport class of a declared resource.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ResourceKind>))]
public enum ResourceKind
{
    /// <summary>A vendor WMI provider.</summary>
    Wmi,

    /// <summary>A raw HID endpoint.</summary>
    Hid,

    /// <summary>A physical game controller acquired through DirectInput, XInput, or raw HID.</summary>
    Controller,

    /// <summary>A Windows motion or environmental sensor.</summary>
    Sensor,

    /// <summary>A serial endpoint.</summary>
    Serial,

    /// <summary>An interactive keyboard hook used only for device-specific chord suppression.</summary>
    InteractiveKeyboardHook,
}

/// <summary>How a plugin intends to hold a resource.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ResourceAccess>))]
public enum ResourceAccess
{
    /// <summary>Reads only. Never writes to the device.</summary>
    Read,

    /// <summary>Reads and writes.</summary>
    ReadWrite,

    /// <summary>Held exclusively, but only while WSGM controller management is active.</summary>
    ExclusiveWhenManaged,

    /// <summary>Suppresses input without publishing it as an event source.</summary>
    Suppress,
}

/// <summary>Classes of hardware risk a package declares for review and user disclosure.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RiskDeclaration>))]
public enum RiskDeclaration
{
    /// <summary>Writes processor power limits.</summary>
    HardwarePowerWrites,

    /// <summary>Takes over fan control from firmware.</summary>
    CustomFanControl,

    /// <summary>Changes controller mode, causing PnP re-enumeration.</summary>
    ControllerReenumeration,

    /// <summary>May write lighting state that persists on the device.</summary>
    DevicePersistentLighting,

    /// <summary>Installs a global keyboard hook to suppress a firmware chord.</summary>
    GlobalKeyboardSuppression,

    /// <summary>Writes battery charge policy.</summary>
    ChargePolicyWrites,
}

/// <summary>
/// An external component a package needs.
/// </summary>
/// <remarks>
/// A plugin declares dependencies; it never installs, repairs, registers, or restarts one at runtime.
/// A missing dependency makes the affected capability unavailable and nothing more.
/// </remarks>
public sealed record DependencyDeclaration
{
    /// <summary>Stable identifier of the dependency.</summary>
    public required string Id { get; init; }

    /// <summary>Who is responsible for the component being present.</summary>
    public required DependencyInstallOwner InstallOwner { get; init; }

    /// <summary>Whether the whole package is unusable without it.</summary>
    public bool Required { get; init; }

    /// <summary>Capabilities that become unavailable when the dependency is absent.</summary>
    public IReadOnlyList<string> RequiredByCapabilities { get; init; } = [];
}

/// <summary>Who installs and owns a declared dependency.</summary>
/// <remarks>
/// This is the field that keeps externally installed prerequisites separate from redistributable
/// audited components. Only <see cref="WsgmInstaller"/> entries are installable by WSGM, and a
/// package that declares an install action for anything else fails validation.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<DependencyInstallOwner>))]
public enum DependencyInstallOwner
{
    /// <summary>Present from the factory or vendor image. Never redistributed or installed by WSGM.</summary>
    OemInstalled,

    /// <summary>Installed independently by the user.</summary>
    UserInstalled,

    /// <summary>An audited component catalog entry the WSGM installer may install.</summary>
    WsgmInstaller,
}

/// <summary>
/// A version-pinned reference to a reusable implementation module.
/// </summary>
public sealed record ModuleReference
{
    /// <summary>Module identifier, for example <c>MsiClawMcu</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Pinned module version.</summary>
    public required int Version { get; init; }

    /// <summary>Layer this module implements.</summary>
    public required ModuleLayer Layer { get; init; }
}

/// <summary>
/// The four layers a module may implement, kept distinct so reuse cannot smuggle policy.
/// </summary>
/// <remarks>
/// A device may reuse a vendor WMI transport and a controller protocol while needing a new fan layout
/// and its own power policy. Keeping the layers separate is what makes that expressible without
/// inheriting the donor model's limits.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ModuleLayer>))]
public enum ModuleLayer
{
    /// <summary>Moves bytes or invokes the platform interface.</summary>
    Transport,

    /// <summary>Framing, methods, commands, checksums, and responses.</summary>
    Protocol,

    /// <summary>Addresses, offsets, masks, channels, zones, and axes.</summary>
    Layout,

    /// <summary>Device-specific limits, persistence, ordering, and recovery.</summary>
    Policy,

    /// <summary>A semantic capability implementation composed from the layers above.</summary>
    Capability,
}

/// <summary>
/// Source and licensing provenance carried by every package.
/// </summary>
public sealed record PackageProvenance
{
    /// <summary>Where the implementation came from.</summary>
    public required string Source { get; init; }

    /// <summary>Exact revision of that source, when it has one.</summary>
    public string? SourceRevision { get; init; }

    /// <summary>SPDX license identifier of the package's own content.</summary>
    public required string License { get; init; }

    /// <summary>Package-relative path of the license notice text.</summary>
    public string? LicenseNoticePath { get; init; }

    /// <summary>How the implementation relates to its source.</summary>
    public required ProvenanceClass ProvenanceClass { get; init; }

    /// <summary>
    /// Reference to the recorded approval for copied code or a redistributed binary.
    /// </summary>
    /// <remarks>
    /// Required for <see cref="ProvenanceClass.CopiedCode"/> and
    /// <see cref="ProvenanceClass.RedistributedBinary"/>: those are the two classes where someone
    /// else's expression ships inside the package, and both need a decision on record.
    /// </remarks>
    public string? ApprovalReference { get; init; }
}

/// <summary>How an implementation relates to the source it came from.</summary>
/// <remarks>
/// Protocol facts — a data address, a report prefix, a method ID, a buffer length, a zone count — are
/// facts about the hardware and carry no licensing obligation. Copied expression and redistributed
/// binaries do, which is why they are separate classes rather than degrees of the same one.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ProvenanceClass>))]
public enum ProvenanceClass
{
    /// <summary>Vendor documentation or a published specification.</summary>
    OfficialDocumentation,

    /// <summary>Captured on the target hardware by the package author.</summary>
    IndependentCapture,

    /// <summary>Facts learned from another open-source implementation, implemented independently.</summary>
    OpenSourceReference,

    /// <summary>Behaviour observed from another product, implemented independently.</summary>
    BehavioralReference,

    /// <summary>Source code copied from elsewhere. Requires an approval reference.</summary>
    CopiedCode,

    /// <summary>A third-party binary shipped inside the package. Requires an approval reference.</summary>
    RedistributedBinary,
}
