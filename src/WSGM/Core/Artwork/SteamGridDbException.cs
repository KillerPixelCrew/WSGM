namespace WSGM.Core;

/// <summary>A SteamGridDB request failed for a reason the UI should surface.</summary>
/// <param name="message">A user-facing message.</param>
internal sealed class SteamGridDbException(string message) : ArtworkProviderException(message, "steamgriddb");
