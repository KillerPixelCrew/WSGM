namespace WSGM.Shell;

/// <summary>One way the Browse tab orders the repository's list.</summary>
/// <param name="Id">What the page sends back.</param>
/// <param name="Label">What it shows, as Animation Changer names it.</param>
internal sealed record SteamAnimationsSort(string Id, string Label);
