namespace WSGM.Shell;

/// <summary>One card library this device knows or sees, with what can be done about it now.</summary>
/// <param name="Id">The library's own UUID, as its card carries it.</param>
/// <param name="Name">The card's name, kept while it is out.</param>
/// <param name="Status">The host's short status, such as Connected, Disconnected or Needs repair.</param>
/// <param name="Detail">The current explanation, or empty.</param>
/// <param name="TitleCount">How many titles the card lists, or this device remembers while it is out.</param>
/// <param name="CanRepair">Whether Repair library can rebuild its missing or damaged game list now.</param>
/// <param name="CanAcceptBinding">Whether Use this card can make this device launch its titles from where it is now.</param>
public sealed record GameLibraryStorage(
    string Id,
    string Name,
    string Status,
    string Detail,
    int TitleCount,
    bool CanRepair,
    bool CanAcceptBinding);
