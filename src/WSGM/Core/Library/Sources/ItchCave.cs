namespace WSGM.Core;

/// <summary>One installed copy of an itch game, as butler's database records it.</summary>
/// <param name="GameId">The game's itch id.</param>
/// <param name="Title">The game's title.</param>
/// <param name="Classification">What itch classifies it as, such as <c>game</c> or <c>tool</c>, or empty.</param>
/// <param name="Verdict">Butler's JSON verdict on what in the install can be launched, or empty.</param>
/// <param name="InstallFolder">The install folder the cave names, or empty.</param>
public sealed record ItchCave(
    string GameId,
    string Title,
    string Classification,
    string Verdict,
    string InstallFolder);
