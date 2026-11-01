namespace WSGM.Core;

/// <summary>An add-on installed with a tool; its file is one of the manifest's owned files.</summary>
internal sealed record GameToolAddonRecord
{
    /// <summary>The add-on id.</summary>
    public string Id { get; init; } = "";

    /// <summary>The installed file, relative to the target directory.</summary>
    public string RelativePath { get; init; } = "";

    /// <summary>The release identity, which changes with every rebuilt asset.</summary>
    public string ReleaseId { get; init; } = "";

    /// <summary>The release tag version, such as <c>rolling</c>.</summary>
    public string ReleaseVersion { get; init; } = "";

    /// <summary>The asset download URL.</summary>
    public string SourceUrl { get; init; } = "";

    /// <summary>The asset file name.</summary>
    public string AssetName { get; init; } = "";

    /// <summary>The asset SHA-256.</summary>
    public string AssetSha256 { get; init; } = "";

    /// <summary>The asset's build date, <c>yyyy-MM-dd</c>, or empty when the provider named none.</summary>
    public string BuildDate { get; init; } = "";
}
