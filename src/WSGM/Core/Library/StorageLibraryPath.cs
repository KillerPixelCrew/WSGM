using System.Text.Json.Serialization;

namespace WSGM.Core;

/// <summary>A volume-relative path with an explicit file/directory requirement.</summary>
public sealed class StorageLibraryPath
{
    /// <summary>The contained relative path; empty means the volume root.</summary>
    [JsonRequired]
    public string RelativePath { get; set; } = "";
    /// <summary>Whether the path must be a directory.</summary>
    [JsonRequired]
    public bool Directory { get; set; }
}
