using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>The "Fix match" search of one title: what was asked, whether it still runs, and what came back.</summary>
/// <param name="Id">The title the search is for.</param>
/// <param name="Query">The name searched for.</param>
/// <param name="Searching">Whether the providers are still being asked.</param>
/// <param name="Matches">The games found, exact first, from every provider.</param>
/// <param name="Providers">What each provider said, once the search finished.</param>
/// <param name="Error">Why the search itself failed, or null.</param>
/// <remarks>
///     Published with the library rather than answered by the command: a provider can take far longer
///     than the Steam bridge waits for a command, so the command returns once the search is admitted
///     and both surfaces draw its progress and outcome from here.
/// </remarks>
public sealed record GameLibraryMatchSearchState(
    string Id,
    string Query,
    bool Searching,
    IReadOnlyList<GameLibraryMatchAnswer> Matches,
    IReadOnlyList<ArtworkMatchProvider> Providers,
    string? Error = null);
