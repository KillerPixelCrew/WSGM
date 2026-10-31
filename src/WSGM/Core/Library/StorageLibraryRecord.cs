namespace WSGM.Core;

/// <summary>The device's binding of one portable library to the local volume it accepted.</summary>
/// <remarks>
///     Like Steam's libraryfolders.vdf entry beside a card's own marker: the card's <c>.wsgm\library.json</c>
///     carries the titles, and this record says which volume this device launches them from. A record's titles
///     are accepted exactly while their volume equals <see cref="VolumeId" />.
/// </remarks>
public sealed class StorageLibraryRecord
{
    /// <summary>The durable portable UUID.</summary>
    public string Id { get; set; } = "";
    /// <summary>The last known friendly card name, shown while it is disconnected.</summary>
    public string Name { get; set; } = "";
    /// <summary>The explicitly accepted local Windows volume GUID path.</summary>
    public string VolumeId { get; set; } = "";
    /// <summary>The manifest generation last written or read here, for optimistic concurrency only.</summary>
    public long ManifestRevision { get; set; }
    /// <summary>Whether a missing or damaged manifest blocks launch until the user repairs it.</summary>
    public bool RepairRequired { get; set; }

    /// <summary>Returns a detached checkpoint.</summary>
    public StorageLibraryRecord Copy() => (StorageLibraryRecord)MemberwiseClone();
}
