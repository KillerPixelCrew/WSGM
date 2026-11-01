namespace WSGM.Shell;

/// <summary>One category heading of a plugin-declared overlay section.</summary>
/// <param name="Id">Stable category ID within its section.</param>
/// <param name="Title">Resolved display heading.</param>
internal sealed record DeviceOverlayCategory(string Id, string Title);
