using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>Installed Audio Loader metadata and resource mappings, or a visible manifest-read failure.</summary>
/// <param name="Id">Local installation-folder identity.</param>
/// <param name="Name">Display name.</param>
/// <param name="Author">Manifest author text.</param>
/// <param name="Version">Manifest version text.</param>
/// <param name="Description">Manifest description.</param>
/// <param name="StoreId">Store listing identity, or null for a local archive.</param>
/// <param name="Error">Manifest-read failure, or null when accepted.</param>
/// <param name="Mappings">Steam resource names mapped to ordered relative sound paths.</param>
/// <param name="Ignore">Resources intentionally left using Steam defaults.</param>
internal sealed record SoundPack(
    string Id,
    string Name,
    string Author,
    string Version,
    string Description,
    string? StoreId,
    string? Error,
    IReadOnlyDictionary<string, string[]> Mappings,
    IReadOnlySet<string> Ignore)
{
    /// <summary>Distinct supported preview and sound paths, relative to the pack directory.</summary>
    internal string[] Assets { get; init; } = [];

    /// <summary>Author-supplied manifest identity, when present.</summary>
    internal string ManifestId { get; init; } = "";

    /// <summary>Repository/source text from the manifest or installation sidecar.</summary>
    internal string Source { get; init; } = "";

    /// <summary>Most recent comparison against Steam resources, when the caller supplied one.</summary>
    internal SoundPackCompatibility? Compatibility { get; init; }
}
