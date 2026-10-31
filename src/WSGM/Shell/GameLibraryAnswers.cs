using System.Collections.Generic;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>The answer to a command that opens a page: the route to follow.</summary>
/// <param name="Route">The route.</param>
internal sealed record GameLibraryRouteAnswer(string Route);

/// <summary>The answer to a launch change that needs the user to accept a risk first.</summary>
/// <param name="Acknowledge">Always true: ask, then send the mode with the acknowledgement.</param>
internal sealed record GameLibraryAcknowledgeAnswer(bool Acknowledge);

/// <summary>Every candidate for one title's artwork type.</summary>
/// <param name="Asset">The artwork type.</param>
/// <param name="Status">Whether gathering is still going on, and how it ended.</param>
/// <param name="Detail">Why it failed or is unavailable, or empty.</param>
/// <param name="Selected">The position of the image the slot shows, from one, or zero.</param>
/// <param name="Options">The candidates.</param>
internal sealed record GameLibraryOptionsAnswer(
    string Asset,
    string Status,
    string Detail,
    int Selected,
    IReadOnlyList<GameLibraryArtworkOption> Options);

/// <summary>One game an artwork provider matched a search to.</summary>
/// <param name="Provider">The provider's id.</param>
/// <param name="ProviderName">The provider's name, as shown.</param>
/// <param name="Id">Its id for the game.</param>
/// <param name="Name">The game's name.</param>
/// <param name="Exact">Whether the provider calls it an exact match.</param>
public sealed record GameLibraryMatchAnswer(string Provider, string ProviderName, string Id, string Name, bool Exact);

/// <summary>The games a search found.</summary>
/// <param name="Matches">The matches, exact first, from every provider.</param>
public sealed record GameLibraryMatchesAnswer(IReadOnlyList<GameLibraryMatchAnswer> Matches);

/// <summary>The outcome of moving a title to its next launch mode or route.</summary>
/// <param name="Command">Whether the change was made or refused.</param>
/// <param name="NeedsAcknowledgement">
///     Whether nothing changed because the user has to accept the risk of the next mode first.
/// </param>
public sealed record GameLibraryLaunchCycle(SteamUiCommandResult Command, bool NeedsAcknowledgement = false);

/// <summary>The outcome of an artwork game search.</summary>
/// <param name="Command">Whether the search ran or was refused.</param>
/// <param name="Matches">The games found, or null when the search was refused.</param>
public sealed record GameLibraryMatchSearch(SteamUiCommandResult Command, GameLibraryMatchesAnswer? Matches = null);
