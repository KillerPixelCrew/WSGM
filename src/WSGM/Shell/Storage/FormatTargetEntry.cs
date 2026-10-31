using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     One candidate drive in the Format flow's target list. A row instance
///     survives refreshes so the gamepad cursor keeps its place; only its values are
///     updated (the radio/eject row discipline). The verification fields captured at
///     enumeration time are re-checked against fresh handles immediately before the
///     destructive work starts.
/// </summary>
public sealed class FormatTargetEntry : ObservableObject
{
    /// <summary>Creates a row.</summary>
    /// <param name="id">
    ///     The device instance path (or "disk:N" fallback), which
    ///     identifies the row across refreshes.
    /// </param>
    /// <param name="diskNumber">The physical disk number the format targets.</param>
    public FormatTargetEntry(string id, int diskNumber)
    {
        Id = id;
        DiskNumber = diskNumber;
    }

    /// <summary>Gets the row's identity. Immutable.</summary>
    public string Id { get; }

    /// <summary>
    ///     Gets the physical disk number. Immutable: a renumbered disk gets
    ///     a fresh enumeration pass before anything destructive runs.
    /// </summary>
    public int DiskNumber { get; }

    /// <summary>
    ///     The disk size in bytes at enumeration time — re-verified before
    ///     formatting.
    /// </summary>
    internal long SizeBytes { get; set; }

    /// <summary>
    ///     The STORAGE_BUS_TYPE at enumeration time — re-verified before
    ///     formatting.
    /// </summary>
    internal int BusType { get; set; }

    /// <summary>
    ///     The card's current drive letter, reassigned verbatim after the
    ///     format so a card reader never changes letter. '\0' when the card has no
    ///     letter yet (raw / ext4 Deck card).
    /// </summary>
    internal char PreferredLetter { get; set; }

    /// <summary>Gets the device's vendor/product identity, or a placeholder.</summary>
    public string Name
    {
        get => field.Length == 0 ? "Removable drive" : field;
        internal set => SetFieldIfChanged(ref field, value, nameof(Name));
    } = "";

    /// <summary>
    ///     Gets the second line: capacity, bus kind, current letters, and
    ///     the Steam-Deck-card hint when Linux partitions were found.
    /// </summary>
    public string Detail
    {
        get;
        internal set => SetFieldIfChanged(ref field, value, nameof(Detail));
    } = "";
}
