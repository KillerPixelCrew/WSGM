namespace WSGM.Shell;

/// <summary>One way a title can be launched, as the review offers it.</summary>
/// <param name="Id">The route's identity within the title.</param>
/// <param name="Label">What to call it.</param>
/// <param name="Follows">Whether it starts through the title's launcher, which WSGM then follows.</param>
public sealed record GameLibraryRoute(string Id, string Label, bool Follows);
