namespace WSGM.Shell;

/// <summary>Current custom artwork for one manageable slot.</summary>
/// <param name="Id">Stable artwork-slot id accepted by clear operations.</param>
/// <param name="Label">Displayed slot name.</param>
/// <param name="HasCustomArtwork">Whether a custom file currently exists for this slot.</param>
/// <param name="ImageUrl">Preview data URL, or null when absent, unreadable or too large.</param>
internal sealed record SteamArtworkManagedSlot(string Id, string Label, bool HasCustomArtwork, string? ImageUrl = null);
