namespace WSGM.Shell;

/// <summary>A card the SD Card filter can name.</summary>
/// <param name="ContentId">The card's identity.</param>
/// <param name="Name">Its display name.</param>
internal sealed record LibraryTabCardChoice(string ContentId, string Name);
