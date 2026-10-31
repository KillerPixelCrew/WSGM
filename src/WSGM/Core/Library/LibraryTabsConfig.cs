using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>
///     What one game's launch configuration looked like before WSGM changed it.
///     Restoring the launch action writes these values back.
/// </summary>
/// <remarks>
///     Load-bearing for non-Steam shortcuts: configuring one overwrites its Target with
///     the wrapper path, so without this snapshot the real program's path would survive
///     only inside the arguments string WSGM itself generated. Steam apps only need
///     <see cref="OriginalLaunchOptions" />, which is usually empty.
/// </remarks>
public sealed class LaunchWrapperConfig
{
    /// <summary>The configured app id (a shortcut's generated id, or a Steam app id).</summary>
    public long AppId { get; set; }

    /// <summary>Whether this entry is a non-Steam shortcut rather than a Steam title.</summary>
    public bool IsShortcut { get; set; }

    /// <summary>Which wrapper behaviours were applied.</summary>
    public LaunchWrapperMode Mode { get; set; }

    /// <summary>Whether WSGM applied its launch wrapper or a Steam-native custom action.</summary>
    public LaunchConfigurationKind Kind { get; set; }

    /// <summary>The executable or script selected for a Steam-native custom action.</summary>
    public string CustomActionPath { get; set; } = "";

    /// <summary>User-supplied native arguments appended to the custom action.</summary>
    public string CustomArguments { get; set; } = "";

    /// <summary>The shortcut's Target before WSGM replaced it (shortcuts only).</summary>
    public string OriginalTarget { get; set; } = "";

    /// <summary>The launch options / launch arguments before WSGM replaced them.</summary>
    public string OriginalLaunchOptions { get; set; } = "";

    /// <summary>
    ///     The shortcut's start directory at configuration time, recorded for
    ///     diagnostics; WSGM deliberately never changes it.
    /// </summary>
    public string OriginalStartDir { get; set; } = "";

    /// <summary>
    ///     Display name at configuration time, so the overlay can name an entry
    ///     whose game Steam can no longer resolve.
    /// </summary>
    public string Name { get; set; } = "";
}

/// <summary>Identifies the kind of per-game launch configuration WSGM owns.</summary>
public enum LaunchConfigurationKind
{
    /// <summary>The existing de-elevation or Steam Input wrapper.</summary>
    Wrapper,

    /// <summary>A Steam-native executable or script launch action.</summary>
    CustomAction
}

/// <summary>
///     One Steam library on a removable drive (a MicroSD card or external
///     drive), tracked so WSGM can render it as a Steam collection ("library tab").
///     Keyed by the card's <c>libraryfolder.vdf</c> content id, which is stable across
///     drive-letter changes and reinserts. Games are remembered so the tab persists
///     while the card is ejected.
/// </summary>
public sealed class CardLibraryConfig
{
    /// <summary>The card's library content id — its stable identity.</summary>
    public string ContentId { get; set; } = "";

    /// <summary>
    ///     Display/collection name. A cache of the label in the card's own
    ///     <c>libraryfolder.vdf</c>, which is what names the card while it is ejected
    ///     and nothing can be read from it. The marker wins whenever the card is
    ///     present; see <c>docs\sd-cards.md</c> for why Steam's config label may not.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>Whether a Steam collection ("tab") is maintained for this card.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Hidden from the card manager list (still tracked, no tab). Mirrors
    ///     MicroSDeck's per-card hide.
    /// </summary>
    public bool Hidden { get; set; }

    /// <summary>App ids installed on the card (remembered while it is ejected).</summary>
    public List<long> AppIds { get; set; } = [];

    /// <summary>The volume size last read while mounted; zero when no reading is available.</summary>
    public long CapacityBytes { get; set; }

    /// <summary>Free bytes from the same mounted reading, meaningful when capacity is known.</summary>
    public long FreeBytes { get; set; }
}

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
    ///     Category prefilter bitfield (see <see cref="LibraryFilter.Categories" />);
    ///     0 defaults to Games at evaluation time.
    /// </summary>
    public int Categories { get; set; } = (int)LibraryFilter.Categories.Games;

    /// <summary>
    ///     The top-level filter group. Its <see cref="FilterNode.Mode" /> is the
    ///     tab's AND/OR; its children are the filters.
    /// </summary>
    public FilterNode FilterTree { get; set; } = new() { Kind = FilterKind.Merge };
}

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
