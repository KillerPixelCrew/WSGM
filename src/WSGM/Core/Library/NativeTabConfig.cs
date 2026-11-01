namespace WSGM.Core;

/// <summary>
///     One of Steam's own library tabs as last observed in the tab strip,
///     remembered so the tab-order UI can list native tabs with their real (localized)
///     titles even while Steam is closed.
/// </summary>
public sealed class NativeTabConfig
{
    /// <summary>Steam's stable tab id (e.g. <c>AllGames</c>, <c>Collections</c>).</summary>
    public string Id { get; set; } = "";

    /// <summary>The tab's display title as Steam rendered it.</summary>
    public string Title { get; set; } = "";
}
