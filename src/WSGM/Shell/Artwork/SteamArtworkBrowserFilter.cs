using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>The active artwork filters, matching SteamGridDB's public query vocabulary.</summary>
/// <param name="Styles">Provider style tags to include; an empty list imposes no style restriction.</param>
/// <param name="Dimensions">Provider dimension tokens to include; an empty list imposes no dimension restriction.</param>
/// <param name="Mimes">Allowed MIME types; an empty list imposes no MIME restriction.</param>
/// <param name="Static">Whether static images are included; Static or Animated must be true.</param>
/// <param name="Animated">Whether animated images are included.</param>
/// <param name="Adult">Whether adult-tagged images are included.</param>
/// <param name="Humor">Whether humor-tagged images are included.</param>
/// <param name="Epilepsy">Whether flashing-imagery-tagged images are included.</param>
/// <param name="Untagged">Whether images without content tags are included.</param>
internal sealed record SteamArtworkBrowserFilter(
    IReadOnlyList<string> Styles,
    IReadOnlyList<string> Dimensions,
    IReadOnlyList<string> Mimes,
    bool Static = true,
    bool Animated = true,
    bool Adult = false,
    bool Humor = true,
    bool Epilepsy = true,
    bool Untagged = true);
