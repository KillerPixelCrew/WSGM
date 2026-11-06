namespace WSGM.Core;

/// <summary>Where a tool's packages are published.</summary>
internal sealed record GameToolSource
{
    /// <summary>The hosted provider, one of <see cref="HostedReleases.Providers" />.</summary>
    public string Provider { get; init; } = "";

    /// <summary>The HTTPS API root.</summary>
    public string BaseUrl { get; init; } = "";

    /// <summary>The <c>owner/name</c> repository.</summary>
    public string Repository { get; init; } = "";

    /// <summary>A pattern that selects exactly one asset per release.</summary>
    public string AssetPattern { get; init; } = "";

    /// <summary>The hosted channel filter; <c>stable</c> skips prereleases and any other value accepts them.</summary>
    public string Channel { get; init; } = "stable";

    /// <summary>The archive type the selected asset must have.</summary>
    public string PackageType { get; init; } = "";

    /// <summary>Official download page for an upstream website source.</summary>
    public string DownloadPage { get; init; } = "";

    /// <summary>The asset URL root for a website source.</summary>
    public string DownloadBaseUrl { get; init; } = "";

    /// <summary>Optional version capture over the release tag; underscores in the capture become dots.</summary>
    public string TagVersionPattern { get; init; } = "";

    /// <summary>Declared verification when the publisher does not provide an archive checksum.</summary>
    public string Verification { get; init; } = "";

    /// <summary>Pinned Authenticode signer thumbprint published by the source.</summary>
    public string SignerThumbprint { get; init; } = "";

    /// <summary>Maximum bytes for downloads without a published size.</summary>
    public long MaximumDownloadBytes { get; init; } = 128 * 1024 * 1024;

    /// <summary>Builds the source the shared hosted-release lookup consumes.</summary>
    /// <returns>The hosted source.</returns>
    internal HostedReleaseSource Hosted()
    {
        return new HostedReleaseSource
        {
            Provider = Provider,
            BaseUrl = BaseUrl,
            Repository = Repository,
            AssetPattern = AssetPattern,
            Channel = Channel
        };
    }
}
