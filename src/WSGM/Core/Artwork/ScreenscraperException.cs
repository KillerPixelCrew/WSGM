namespace WSGM.Core;

/// <summary>A Screenscraper request failed for a reason the UI should surface.</summary>
/// <param name="message">A user-facing message.</param>
internal sealed class ScreenscraperException(string message) : ArtworkProviderException(message, "screenscraper");
