namespace WSGM.Core;

/// <summary>An image the user picked for one artwork type of one title.</summary>
/// <remarks>
///     Kept by URL rather than by position, because the candidates are fetched again after every scan
///     and their order is the provider's to change. An empty URL means the user cleared the slot: no
///     image is applied for that type.
/// </remarks>
public sealed class ArtworkPick
{
    /// <summary>Identifies this explicit selection, so selecting the same URL again remains pending.</summary>
    public string Revision { get; set; } = "";

    /// <summary>Which artwork type.</summary>
    public ArtworkAsset Asset { get; set; }

    /// <summary>The full-size image, or empty for "apply nothing".</summary>
    public string Url { get; set; } = "";

    /// <summary>The thumbnail shown for it.</summary>
    public string Thumb { get; set; } = "";

    /// <summary>Who supplied it, as shown on screen.</summary>
    public string Provider { get; set; } = "";
}
