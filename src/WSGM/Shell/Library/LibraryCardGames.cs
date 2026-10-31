using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>The games of the card last inspected.</summary>
/// <param name="ContentId">Which card the list belongs to.</param>
/// <param name="Games">The games, in the card's order.</param>
/// <param name="Loading">Whether the names are still being read.</param>
/// <param name="Error">Why the names could not be read, or null; the ids are still listed.</param>
internal sealed record LibraryCardGames(
    string ContentId,
    IReadOnlyList<LibraryCardGame> Games,
    bool Loading,
    string? Error);
