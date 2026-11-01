using System.Collections.Generic;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The library manager's whole projection, replaced as one so a reader never sees half a change.</summary>
/// <param name="Cards">Every tracked card, inserted or remembered.</param>
/// <param name="Tabs">The custom tabs, by position.</param>
/// <param name="Order">The whole tab strip, left to right.</param>
/// <param name="CardChoices">The tracked cards the SD Card filter can name.</param>
/// <param name="Tags">The store tags for the Tag filter.</param>
/// <param name="Collections">The collections for the Collection filter.</param>
/// <param name="Games">The library for the whitelist and blacklist filters.</param>
/// <param name="CardGames">The games of the card last inspected, or null.</param>
/// <param name="Loaded">Whether a reading has completed since the service started.</param>
/// <param name="Error">Why the last operation failed, or null.</param>
/// <param name="Notice">Something the user should read, or null.</param>
internal sealed record LibraryManagerSnapshot(
    IReadOnlyList<LibraryCardView> Cards,
    IReadOnlyList<CustomTabConfig> Tabs,
    IReadOnlyList<LibraryTabOrderView> Order,
    IReadOnlyList<LibraryTabCardChoice> CardChoices,
    LibraryTagPicker Tags,
    LibraryCollectionPicker Collections,
    LibraryGamePicker Games,
    LibraryCardGames? CardGames,
    bool Loaded,
    string? Error,
    string? Notice)
{
    /// <summary>The projection before the first reading.</summary>
    internal static LibraryManagerSnapshot Empty { get; } = new(
        [], [], [], [],
        new LibraryTagPicker([], false, false, null),
        new LibraryCollectionPicker([], false, false, null),
        new LibraryGamePicker([], false, false, null),
        null, false, null, null);
}
