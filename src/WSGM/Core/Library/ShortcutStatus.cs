namespace WSGM.Core;

/// <summary>Whether one non-Steam shortcut can start, and where its files live.</summary>
/// <param name="AppId">Steam's shortcut id.</param>
/// <param name="Name">The name Steam shows.</param>
/// <param name="Owned">Whether WSGM created it; the user's own shortcuts are observed the same way.</param>
/// <param name="ContentId">The managed content id its Target names, or empty.</param>
/// <param name="LibraryId">The card library that content belongs to, or empty.</param>
/// <param name="State">One of the three published states.</param>
/// <param name="Location">The card, disk or library holding the files, by name.</param>
/// <param name="Detail">What to do when it is unavailable, naming the card or the missing file.</param>
public sealed record ShortcutStatus(
    uint AppId,
    string Name,
    bool Owned,
    string ContentId,
    string LibraryId,
    ContentAvailability State,
    string Location,
    string Detail)
{
    /// <summary>Whether every required file is present.</summary>
    public bool Available => State == ContentAvailability.Available;

    /// <summary>The state as both surfaces word it.</summary>
    public string Label => ShortcutStatusPresentation.Label(State);
}
