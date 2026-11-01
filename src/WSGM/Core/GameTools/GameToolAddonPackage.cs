namespace WSGM.Core;

/// <summary>A verified add-on file ready to be copied with its tool.</summary>
/// <param name="Addon">The add-on.</param>
/// <param name="Release">The release it came from, with the normalized SHA-256 of its asset.</param>
/// <param name="Source">The verified file in the package cache.</param>
internal sealed record GameToolAddonPackage(GameToolAddon Addon, HostedRelease Release, string Source)
{
    /// <summary>The record kept in the manifest once the file is installed.</summary>
    /// <param name="relativePath">Where it was installed, relative to the target directory.</param>
    /// <returns>The record.</returns>
    internal GameToolAddonRecord Record(string relativePath)
    {
        return new GameToolAddonRecord
        {
            Id = Addon.Id,
            RelativePath = relativePath,
            ReleaseId = Release.Id,
            ReleaseVersion = Release.Version,
            SourceUrl = Release.Asset.Url,
            AssetName = Release.Asset.Name,
            AssetSha256 = Release.Asset.Sha256,
            BuildDate = GameToolVersions.BuildDate(Release.Asset) ?? ""
        };
    }
}
