using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>The outcome of an artwork game search.</summary>
/// <param name="Command">Whether the search ran or was refused.</param>
/// <param name="Matches">The games found, or null when the search was refused.</param>
public sealed record GameLibraryMatchSearch(SteamUiCommandResult Command, GameLibraryMatchesAnswer? Matches = null);
