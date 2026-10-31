namespace WSGM.Core;

/// <summary>One official image a source's catalog offers for a title.</summary>
/// <param name="Asset">Which Steam capsule it fills.</param>
/// <param name="Url">Where to fetch it, always HTTPS.</param>
/// <remarks>
///     Carried from discovery rather than looked up again at apply time, because the one catalog
///     response that answers "is this a game?" and "does it have multiplayer?" carries the images
///     too, and the catalog is paced at one request every 500 ms.
/// </remarks>
public sealed record DiscoveredArtwork(ArtworkAsset Asset, string Url);
