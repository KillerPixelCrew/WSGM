using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>The profiles the importer exposes instead of asking surfaces to invent command lines.</summary>
/// <param name="Id">The emulated system identity.</param>
/// <param name="Name">The user-visible system name.</param>
/// <param name="Extensions">The default accepted file extensions.</param>
/// <param name="ScreenscraperSystemId">The provider's system identity, or zero when unmapped.</param>
/// <param name="ParserKind">The matching behavior determined by this system.</param>
public sealed record RomSystemProfile(
    string Id,
    string Name,
    IReadOnlyList<string> Extensions,
    int ScreenscraperSystemId,
    RomParserKind ParserKind);
