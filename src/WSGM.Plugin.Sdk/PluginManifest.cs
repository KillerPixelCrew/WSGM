using System.Collections.Generic;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Plugin.Sdk;

/// <summary>Compatibility boundary for the common plugin contracts.</summary>
public static class PluginApi
{
    /// <summary>Current plugin contract revision.</summary>
    /// <remarks>
    ///     Version 2 made <c>PluginContext.Deadline</c> an active-time <c>Deadline</c>. Version 3 adds the
    ///     <c>wsgm.gpu</c> category, the manifest's <c>displayAdapters</c> and <c>capabilities</c>, and
    ///     <see cref="ICapabilityPlugin" /> with <see cref="ICapabilityHost" />, through which a common plugin
    ///     publishes Device SDK capabilities. Version 4 makes the manifest immutable, moves diagnostic tracing to
    ///     <see cref="IPluginHost" /> so every common plugin can log, replaces <c>PluginText</c> with the Device
    ///     SDK's <see cref="PlainText" />, and takes in the SteamUiToolkit types reachable from
    ///     <see cref="SteamUiToolkit.ISteamUiModule" />, which every plugin shares with the host.
    ///     Version 5 consolidates the shared semantic contracts in this assembly; plugins must rebuild
    ///     against the new type identities rather than reference the retired Device SDK assembly.
    /// </remarks>
    public const int Version = 5;
}

/// <summary>Known categories. Other stable category strings remain valid.</summary>
public static class PluginCategories
{
    /// <summary>The selected host-device specialization, with at most one active instance.</summary>
    public const string Device = "wsgm.device";

    /// <summary>
    ///     A graphics vendor's driver controls. Several may run at once, one per vendor, beside the device
    ///     package and independent of device integration. A package of this category implements
    ///     <see cref="ICapabilityPlugin" /> and declares the display adapters it serves.
    /// </summary>
    public const string Gpu = "wsgm.gpu";

    /// <summary>External peripherals that may coexist independently.</summary>
    public const string Peripheral = "wsgm.peripheral";

    /// <summary>Infrared learning and transmission integrations.</summary>
    public const string Infrared = "wsgm.infrared";
}

/// <summary>Host-owned activation policy for a category; a plugin cannot grant itself a slot.</summary>
/// <param name="MinimumActive">Minimum active instances required by the host's selected policy.</param>
/// <param name="MaximumActive">Maximum active instances, or null for no category-specific maximum.</param>
/// <param name="RequiresSelection">Whether installed instances must be explicitly selected before activation.</param>
public sealed record PluginCategoryPolicy(int MinimumActive, int? MaximumActive, bool RequiresSelection)
{
    /// <summary>The Device category permits zero devices and one selected active device.</summary>
    public static PluginCategoryPolicy Device { get; } = new(0, 1, true);

    /// <summary>Independent integrations may have zero or many active instances.</summary>
    public static PluginCategoryPolicy Multiple { get; } = new(0, null, false);
}

/// <summary>One installed dependency and its accepted numeric version range.</summary>
/// <param name="Id">Dependency package identity.</param>
/// <param name="MinimumVersion">Inclusive dotted numeric version.</param>
/// <param name="MaximumVersionExclusive">Exclusive version bound, or null.</param>
public sealed record PluginDependency(string Id, string MinimumVersion, string? MaximumVersionExclusive = null);

/// <summary>Common package metadata; device packages use the Device SDK's dedicated manifest and lifecycle.</summary>
/// <remarks>
///     Init-only properties do not freeze supplied collections. Retain immutable lists after admission.
///     <see cref="PluginManifestReader" /> validates metadata; constructors alone do not establish validity or trust.
/// </remarks>
public sealed record PluginManifest
{
    /// <summary>Stable lowercase package identity.</summary>
    public required string Id { get; init; }

    /// <summary>Plain display name.</summary>
    public required string Name { get; init; }

    /// <summary>Canonical dotted numeric package version, such as <c>1.2.0</c>.</summary>
    public required string Version { get; init; }

    /// <summary>Open category identity; category multiplicity is decided by the host.</summary>
    public required string Category { get; init; }

    /// <summary>Minimum SDK revision; at least 5 and no greater than <see cref="PluginApi.Version" />.</summary>
    public int MinimumApiVersion { get; init; } = PluginApi.Version;

    /// <summary>Maximum SDK revision; at least <see cref="PluginApi.Version" /> for admission.</summary>
    public int MaximumApiVersion { get; init; } = PluginApi.Version;

    /// <summary>Assembly filename at the package root, never an absolute or parent-relative path.</summary>
    public required string EntryAssembly { get; init; }

    /// <summary>Namespace-qualified type implementing the common lifecycle.</summary>
    public required string EntryType { get; init; }

    /// <summary>Required plugin packages, checked before activation.</summary>
    public IReadOnlyList<PluginDependency> Dependencies { get; init; } = [];

    /// <summary>Declared external access requirements, kept as metadata.</summary>
    /// <remarks>The host validates and records declarations but never grants or enforces them.</remarks>
    public IReadOnlyList<string> Permissions { get; init; } = [];

    /// <summary>
    ///     The exact WSGM version the package was built for. Packing writes it; a source manifest omits
    ///     it. The host refuses a package built for another version.
    /// </summary>
    public string? WsgmVersion { get; init; }

    /// <summary>
    ///     Display adapters the package serves. Setup offers and WSGM enables a <c>wsgm.gpu</c> package only
    ///     on a machine with a matching adapter. Other categories leave it empty.
    /// </summary>
    public IReadOnlyList<DisplayAdapterMatch> DisplayAdapters { get; init; } = [];

    /// <summary>
    ///     Capability roles the package may publish through <see cref="ICapabilityHost" />. The host refuses a
    ///     descriptor whose role is not declared here.
    /// </summary>
    public IReadOnlyList<CapabilityRole> Capabilities { get; init; } = [];

    /// <summary>Whether this package requests unrestricted access to the user's Steam CEF session.</summary>
    public bool SteamCef { get; init; }

    /// <summary>Independent JavaScript/CSS modules shipped inside this package.</summary>
    public IReadOnlyList<PluginFrontendModule> FrontendModules { get; init; } = [];
}

/// <summary>One independently loaded and removable Steam frontend bundle.</summary>
/// <param name="Id">Stable identity within the package.</param>
/// <param name="Script">Package-relative UTF-8 JavaScript filename.</param>
/// <param name="Style">Optional package-relative UTF-8 stylesheet filename.</param>
public sealed record PluginFrontendModule(string Id, string Script, string? Style = null);

/// <summary>One display adapter rule, matched against the PCI identity of every present adapter.</summary>
/// <param name="PciVendorId">Four hexadecimal digits, for example <c>8086</c> for Intel.</param>
public sealed record DisplayAdapterMatch(string PciVendorId);
