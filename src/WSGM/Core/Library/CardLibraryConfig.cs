using System.Collections.Generic;

namespace WSGM.Core;

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
