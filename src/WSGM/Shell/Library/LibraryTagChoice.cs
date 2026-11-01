namespace WSGM.Shell;

/// <summary>A store tag the Tag filter can pick.</summary>
/// <param name="TagId">Steam's tag id.</param>
/// <param name="Name">The tag's name.</param>
/// <param name="Count">How many library games carry it.</param>
internal sealed record LibraryTagChoice(int TagId, string Name, int Count);
