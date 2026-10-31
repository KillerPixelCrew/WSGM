using System.Collections.Generic;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>One filter type the tab builder offers.</summary>
/// <param name="Kind">The <see cref="FilterKind" /> name.</param>
/// <param name="Label">The short name.</param>
/// <param name="Description">What it matches.</param>
/// <param name="CanInvert">Whether its NOT toggle means anything.</param>
internal sealed record LibraryTabFilterKind(string Kind, string Label, string Description, bool CanInvert)
{
    /// <summary>The filter types the builder offers, in the order both surfaces list them.</summary>
    internal static IReadOnlyList<LibraryTabFilterKind> All { get; } =
    [
        Of(FilterKind.Tag, "Tag / Genre", "Games with a store tag"),
        Of(FilterKind.Installed, "Installed", "Installed or not"),
        Of(FilterKind.Collection, "Collection", "In a Steam collection"),
        Of(FilterKind.Regex, "Title", "Title matches a pattern"),
        Of(FilterKind.SdCard, "SD Card", "Installed on a card"),
        Of(FilterKind.TimePlayed, "Playtime", "Above/below hours played"),
        Of(FilterKind.SizeOnDisk, "Size", "Above/below install size"),
        Of(FilterKind.ReviewScore, "Review score", "Above/below a score"),
        Of(FilterKind.ReleaseDate, "Release date", "Before/after a date"),
        Of(FilterKind.LastPlayed, "Last played", "Before/after a date"),
        Of(FilterKind.Platform, "Platform", "Steam or non-Steam"),
        Of(FilterKind.Whitelist, "Whitelist", "Only these games"),
        Of(FilterKind.Blacklist, "Blacklist", "Exclude these games"),
        Of(FilterKind.Merge, "Merge group", "Nested AND/OR of filters")
    ];

    private static LibraryTabFilterKind Of(FilterKind kind, string label, string description)
    {
        return new LibraryTabFilterKind(kind.ToString(), label, description, LibraryFilter.CanInvert(kind));
    }
}
