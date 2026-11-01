using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>What a GDK title's <c>MicrosoftGame.config</c> says.</summary>
/// <param name="Executables">The executables it names, by file name.</param>
/// <param name="DisplayName">Its shell display name, or empty.</param>
/// <param name="StoreId">Its Store id, or empty.</param>
/// <param name="TitleId">Its Xbox title id, or empty.</param>
/// <param name="UnrecognisedElements">
///     Top-level elements this parser does not model, in document order.
/// </param>
/// <param name="Readable">Whether the file could be read and parsed at all.</param>
public sealed record MicrosoftGameFacts(
    IReadOnlyList<string> Executables,
    string DisplayName,
    string StoreId,
    string TitleId,
    IReadOnlyList<string> UnrecognisedElements,
    bool Readable = true);
