using System.Collections.Generic;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Identity;

namespace WSGM.Device.Sdk.Packaging;

/// <summary>The complete metadata contract for one installed device plugin.</summary>
/// <remarks>
///     The manifest identifies the assembly and the exact SDK API it was compiled against, and declares
///     as data what a host must know before loading any code: which hardware the package is for and
///     which capability roles it may publish. Setup reads both to decide whether to install the
///     package and which system components it needs. The plugin's own detection still confirms the
///     machine, and the host refuses a publication whose role the manifest did not declare.
/// </remarks>
public sealed record PluginManifest
{
    /// <summary>Stable package identifier, for example <c>wsgm.device.msi.claw-8-a2vm</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable package name.</summary>
    public required string Name { get; init; }

    /// <summary>Package version as a dotted numeric version.</summary>
    public required string Version { get; init; }

    /// <summary>Exact <see cref="DeviceApi.Version" /> required by this package.</summary>
    public required int ApiVersion { get; init; }

    /// <summary>Package-relative plugin assembly path.</summary>
    public required string EntryAssembly { get; init; }

    /// <summary>Namespace-qualified type name implementing the plugin lifecycle.</summary>
    public required string EntryType { get; init; }

    /// <summary>
    ///     The hardware this package is for. The first exact rule that matches wins; fallbacks rank
    ///     below every exact rule. An empty list matches no machine.
    /// </summary>
    public IReadOnlyList<HardwareMatchRule> Hardware { get; init; } = [];

    /// <summary>
    ///     Every capability role the plugin may publish. The host refuses a descriptor whose role is not
    ///     listed, and setup derives the system components to install from it.
    /// </summary>
    public IReadOnlyList<CapabilityRole> Capabilities { get; init; } = [];

    /// <summary>
    ///     The exact WSGM version the package was built for. Packing writes it; a source manifest omits
    ///     it. The host refuses a package built for another version.
    /// </summary>
    public string? WsgmVersion { get; init; }
}
