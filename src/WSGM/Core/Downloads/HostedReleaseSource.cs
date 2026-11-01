using System;
using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>Where a GitHub or Forgejo repository publishes its releases and which asset to take.</summary>
internal sealed record HostedReleaseSource
{
    /// <summary>The provider, one of <see cref="HostedReleases.Providers" />.</summary>
    public string Provider { get; init; } = "";

    /// <summary>The HTTPS API root, such as <c>https://api.github.com/</c>.</summary>
    public string BaseUrl { get; init; } = "";

    /// <summary>The <c>owner/name</c> repository.</summary>
    public string Repository { get; init; } = "";

    /// <summary>Repositories that replace <see cref="Repository" /> for an architecture.</summary>
    public IReadOnlyDictionary<string, string> Repositories { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    ///     The pattern that must select exactly one asset per release; <c>{arch}</c> and <c>{variant}</c> are
    ///     substituted.
    /// </summary>
    public string AssetPattern { get; init; } = "";

    /// <summary>The <c>{arch}</c> text per architecture, defaulting to the architecture name.</summary>
    public IReadOnlyDictionary<string, string> AssetArchitectureNames { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The <c>{variant}</c> text per architecture, defaulting to empty.</summary>
    public IReadOnlyDictionary<string, string> AssetVariants { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The release channel; <c>stable</c> skips prereleases and any other value accepts them.</summary>
    public string Channel { get; init; } = "stable";

    /// <summary>Whether a release without assets may offer <c>.zip</c> links from its body text.</summary>
    public bool BodyAssets { get; init; }

    /// <summary>
    ///     A sibling checksum file naming the asset's SHA-256, with <c>{asset}</c> substituted; empty to rely on the
    ///     provider's published digest.
    /// </summary>
    public string ChecksumFile { get; init; } = "";
}
