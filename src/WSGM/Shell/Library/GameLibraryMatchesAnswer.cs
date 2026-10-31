using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>The games a search found.</summary>
/// <param name="Matches">The matches, exact first, from every provider.</param>
public sealed record GameLibraryMatchesAnswer(IReadOnlyList<GameLibraryMatchAnswer> Matches);
