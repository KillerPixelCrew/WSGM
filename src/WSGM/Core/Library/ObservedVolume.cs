using System;

namespace WSGM.Core;

/// <summary>One mounted local volume as one availability pass identified it.</summary>
/// <param name="VolumeId">The volume GUID path, or empty for a drive Windows gives no volume, such as a network drive.</param>
/// <param name="Mount">Where it is mounted now, such as <c>D:\</c>. Only an address, never an identity.</param>
/// <param name="Name">
///     What to call it: a card's own Steam library marker label or volume label, a fixed disk's label, or its
///     drive when it has none.
/// </param>
/// <param name="CardId">
///     The card's own identity, read through its volume: the WSGM manifest UUID, else Steam's marker content id.
///     Empty for fixed disks and unmarked media.
/// </param>
/// <param name="Removable">Whether the volume is removable or hot-pluggable storage.</param>
public sealed record ObservedVolume(string VolumeId, string Mount, string Name, string CardId, bool Removable)
{
    /// <summary>Whether this volume is the card a binding names, by volume or by the card's own identity.</summary>
    public bool Holds(ShortcutBinding binding)
    {
        return VolumeId.Length > 0 && string.Equals(VolumeId, binding.VolumeId, StringComparison.OrdinalIgnoreCase)
               || CardId.Length > 0 && string.Equals(CardId, binding.CardId, StringComparison.OrdinalIgnoreCase);
    }
}
