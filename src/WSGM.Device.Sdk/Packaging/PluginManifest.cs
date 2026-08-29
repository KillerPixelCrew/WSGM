namespace WSGM.Device.Sdk.Packaging;

/// <summary>The complete metadata contract for one installed device plugin.</summary>
/// <remarks>
/// Hardware identity, dependencies, capabilities, glyphs, and recovery policy belong to plugin
/// code or fixed package data. The manifest only identifies the assembly and exact SDK API it was
/// compiled against.
/// </remarks>
public sealed record PluginManifest
{
    /// <summary>Stable package identifier, for example <c>wsgm.device.msi.claw-8-a2vm</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable package name.</summary>
    public required string Name { get; init; }

    /// <summary>Package version as a dotted numeric version.</summary>
    public required string Version { get; init; }

    /// <summary>Exact <see cref="DeviceApi.Version"/> required by this package.</summary>
    public required int ApiVersion { get; init; }

    /// <summary>Package-relative plugin assembly path.</summary>
    public required string EntryAssembly { get; init; }

    /// <summary>Namespace-qualified type name implementing the plugin lifecycle.</summary>
    public required string EntryType { get; init; }
}
