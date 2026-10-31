using System;
using System.Collections.Generic;
using System.IO;

namespace WSGM.Core;

/// <summary>A verified, extracted tool package ready to be copied into a game.</summary>
/// <param name="Release">The release it came from, carrying the normalized SHA-256 of its asset.</param>
/// <param name="Root">The payload root, after unwrapping a single top-level folder.</param>
/// <param name="Version">The primary file's product version.</param>
/// <param name="Family">The primary file's version family.</param>
/// <param name="Architecture">The primary file's PE architecture.</param>
internal sealed record GameToolPackage(HostedRelease Release, string Root, string Version, string Family,
    string Architecture)
{
    /// <summary>File hashes recorded at extraction, by forward-slash path relative to <see cref="Root" />.</summary>
    internal IReadOnlyDictionary<string, (string Sha256, long Size)> Hashes { get; init; } =
        new Dictionary<string, (string Sha256, long Size)>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The SHA-256 of a payload file, from the extraction record while its size still matches.</summary>
    /// <param name="relativePath">The forward-slash path relative to <see cref="Root" />.</param>
    /// <param name="source">The file's absolute path.</param>
    /// <returns>The lowercase SHA-256.</returns>
    internal string Sha256(string relativePath, string source)
    {
        return Hashes.TryGetValue(relativePath, out var recorded) && new FileInfo(source).Length == recorded.Size
            ? recorded.Sha256
            : GameToolInstaller.Sha256(source);
    }
}
