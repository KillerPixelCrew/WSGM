namespace WSGM.Core;

/// <summary>One release with its selected asset.</summary>
/// <param name="Version">The tag without a leading <c>v</c>.</param>
/// <param name="Id">An identity that changes when the tag, release or asset digest changes.</param>
/// <param name="NotesUrl">The release page.</param>
/// <param name="Provider">The provider that published it.</param>
/// <param name="Asset">The selected asset.</param>
internal sealed record HostedRelease(string Version, string Id, string NotesUrl, string Provider, ReleaseAsset Asset);
