namespace WSGM.Core;

/// <summary>
///     One user-built custom library tab: a WSGM-owned Steam collection whose
///     membership is recomputed by evaluating <see cref="FilterTree" /> over the library.
///     The TabMaster analog, materialized as a native Steam collection.
/// </summary>
public sealed class CustomTabConfig
{
    /// <summary>Stable unique identity, independent of the editable display name.</summary>
    /// <remarks>
    ///     The tab editor assigns a new tab its id. A stored tab without one gets a deterministic id on read
    ///     (<see cref="AppConfigRules" />), so it keeps the same identity on every load until it is saved.
    /// </remarks>
    public string Id { get; set; } = "";

    /// <summary>Display name (also the Steam collection's name).</summary>
    public string Name { get; set; } = "";

    /// <summary>Whether the tab is synced (a disabled tab's collection is removed).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Sort order in the builder list (ascending).</summary>
    public int Position { get; set; }

    /// <summary>
    ///     Category prefilter bitfield (see <see cref="LibraryFilterCategories" />);
    ///     0 defaults to Games at evaluation time.
    /// </summary>
    public int Categories { get; set; } = (int)LibraryFilterCategories.Games;

    /// <summary>
    ///     The top-level filter group. Its <see cref="FilterNode.Mode" /> is the
    ///     tab's AND/OR; its children are the filters.
    /// </summary>
    public FilterNode FilterTree { get; set; } = new() { Kind = FilterKind.Merge };

    /// <summary>Copies the tab and its filter tree.</summary>
    /// <returns>A detached draft suitable for editing without mutating the current configuration.</returns>
    internal CustomTabConfig Copy()
    {
        var copy = (CustomTabConfig)MemberwiseClone();
        copy.FilterTree = FilterTree.Clone();
        return copy;
    }
}
