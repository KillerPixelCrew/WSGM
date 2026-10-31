namespace WSGM.Core;

/// <summary>One artwork image the Store offers for a title.</summary>
/// <param name="Purpose">The Store's own name for what the image is.</param>
/// <param name="Url">Where to fetch it.</param>
/// <param name="Width">Its width in pixels, or zero when the Store did not say.</param>
/// <param name="Height">Its height in pixels, or zero when the Store did not say.</param>
public sealed record StoreCatalogImage(string Purpose, string Url, int Width, int Height);
