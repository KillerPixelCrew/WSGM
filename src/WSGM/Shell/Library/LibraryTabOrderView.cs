namespace WSGM.Shell;

/// <summary>One row of the tab strip as the Library Tab Manager orders it.</summary>
/// <param name="Key">The native id (<c>AllGames</c>) or the injected id (<c>wsgm-…</c>).</param>
/// <param name="Title">The title Steam shows.</param>
/// <param name="Kind"><c>native</c>, <c>card</c> or <c>custom</c>.</param>
/// <param name="Hidden">Whether a native tab is currently left out of the strip.</param>
internal sealed record LibraryTabOrderView(string Key, string Title, string Kind, bool Hidden);
