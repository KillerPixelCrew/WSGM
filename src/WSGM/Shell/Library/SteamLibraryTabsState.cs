using System.Collections.Generic;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Everything the Library Tab Manager draws.</summary>
/// <param name="Tabs">The custom tabs, by position.</param>
/// <param name="Order">The whole tab strip, left to right, hidden native tabs included.</param>
/// <param name="FilterKinds">The filter types the builder offers.</param>
/// <param name="Cards">The tracked cards the SD Card filter can name.</param>
/// <param name="Tags">The store tags for the Tag filter.</param>
/// <param name="Collections">The collections for the Collection filter.</param>
/// <param name="Games">The library for the whitelist and blacklist filters.</param>
/// <param name="Loaded">Whether configuration has been read since the service started.</param>
/// <param name="Busy">Whether a save or a read is in flight.</param>
/// <param name="Error">Why the last operation failed, or null.</param>
/// <param name="Notice">Something the user should read, or null.</param>
/// <param name="Revision">Monotonic observation revision, shared with the card manager.</param>
internal sealed record SteamLibraryTabsState(
    IReadOnlyList<CustomTabConfig> Tabs,
    IReadOnlyList<LibraryTabOrderView> Order,
    IReadOnlyList<LibraryTabFilterKind> FilterKinds,
    IReadOnlyList<LibraryTabCardChoice> Cards,
    LibraryTagPicker Tags,
    LibraryCollectionPicker Collections,
    LibraryGamePicker Games,
    bool Loaded,
    bool Busy,
    string? Error,
    string? Notice,
    long Revision);
