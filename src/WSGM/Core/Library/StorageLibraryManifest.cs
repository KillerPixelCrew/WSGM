using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WSGM.Core;

/// <summary>Schema-v1 portable inventory. It contains no account or host launch dependencies.</summary>
public sealed class StorageLibraryManifest
{
    /// <summary>The supported card schema.</summary>
    [JsonRequired]
    public int Version { get; set; } = 1;
    /// <summary>The durable library UUID.</summary>
    [JsonRequired]
    public string Id { get; set; } = "";
    /// <summary>The cached storage label.</summary>
    [JsonRequired]
    public string Name { get; set; } = "";
    /// <summary>The positive monotonically increasing generation.</summary>
    [JsonRequired]
    public long Revision { get; set; } = 1;
    /// <summary>The authoritative portable title inventory.</summary>
    [JsonRequired]
    public List<StorageLibraryContent> Entries { get; set; } = [];
}
