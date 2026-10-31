using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>Provider-neutral artwork paging and content filters.</summary>
/// <param name="Page">Zero-based result page.</param>
/// <param name="Styles">Provider style identifiers, or null for that provider's defaults.</param>
/// <param name="Dimensions">Accepted dimensions such as <c>600x900</c>.</param>
/// <param name="Mimes">Accepted MIME types.</param>
/// <param name="Static">Whether static images are included.</param>
/// <param name="Animated">Whether animated images are included.</param>
/// <param name="Adult">Whether adult-tagged results are included.</param>
/// <param name="Humor">Whether humor-tagged results are included.</param>
/// <param name="Epilepsy">Whether flashing/epilepsy-tagged results are included.</param>
/// <param name="Untagged">Whether results without those tags are included.</param>
internal sealed record ArtworkQuery(
    int Page = 0,
    IReadOnlyList<string>? Styles = null,
    IReadOnlyList<string>? Dimensions = null,
    IReadOnlyList<string>? Mimes = null,
    bool Static = true,
    bool Animated = true,
    bool Adult = false,
    bool Humor = true,
    bool Epilepsy = true,
    bool Untagged = true);
