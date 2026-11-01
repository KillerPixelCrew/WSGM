namespace WSGM.Shell;

/// <summary>One game on a card, named from Steam's library when it answered.</summary>
/// <param name="AppId">The app id.</param>
/// <param name="Name">Steam's title, or the id as text when the library was not readable.</param>
internal sealed record LibraryCardGame(long AppId, string Name);
