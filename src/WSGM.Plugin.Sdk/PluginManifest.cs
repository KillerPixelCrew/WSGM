using System.Collections.Generic;

namespace WSGM.Plugin.Sdk;

/// <summary>Compatibility boundary for the common plugin contracts.</summary>
public static class PluginApi
{
    /// <summary>Current common contract revision, independent of Device SDK revisions.</summary>
    public const int Version = 1;
}

/// <summary>Known categories. Other stable category strings remain valid.</summary>
public static class PluginCategories
{
    /// <summary>The selected host-device specialization, with at most one active instance.</summary>
    public const string Device = "wsgm.device";
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

/// <summary>Common package metadata. Device-specific identity and capabilities belong to its specialization.</summary>
public sealed record PluginManifest
{
    /// <summary>Stable lowercase package identity.</summary>
    public required string Id { get; init; }
    /// <summary>Plain display name.</summary>
    public required string Name { get; init; }
    /// <summary>Dotted numeric package version.</summary>
    public required string Version { get; init; }
    /// <summary>Open category identity; category multiplicity is decided by the host.</summary>
    public required string Category { get; init; }
    /// <summary>Minimum accepted common SDK revision.</summary>
    public int MinimumApiVersion { get; set; } = PluginApi.Version;
    /// <summary>Maximum accepted common SDK revision.</summary>
    public int MaximumApiVersion { get; set; } = PluginApi.Version;
    /// <summary>Assembly filename at the package root, never an absolute or parent-relative path.</summary>
    public required string EntryAssembly { get; init; }
    /// <summary>Namespace-qualified type implementing the common lifecycle.</summary>
    public required string EntryType { get; init; }
    /// <summary>Required plugin packages, checked before activation.</summary>
    public IReadOnlyList<PluginDependency> Dependencies { get; set; } = [];
    /// <summary>Declared external access requirements. Declarations are not grants or sandbox boundaries.</summary>
    public IReadOnlyList<string> Permissions { get; set; } = [];
}
