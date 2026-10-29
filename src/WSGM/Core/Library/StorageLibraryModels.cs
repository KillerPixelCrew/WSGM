using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WSGM.Core;

/// <summary>The device-owned acceptance and validation checkpoint for one portable library.</summary>
public sealed class StorageLibraryRecord
{
    /// <summary>The durable portable UUID.</summary>
    public string Id { get; set; } = "";
    /// <summary>The last accepted friendly storage label.</summary>
    public string Name { get; set; } = "";
    /// <summary>The explicitly accepted local Windows volume GUID path.</summary>
    public string VolumeId { get; set; } = "";
    /// <summary>The last observed mount, for display only.</summary>
    public string LastMountPath { get; set; } = "";
    /// <summary>The last accepted manifest generation.</summary>
    public long ManifestRevision { get; set; }
    /// <summary>The last successful complete content check.</summary>
    public DateTimeOffset? LastValidatedUtc { get; set; }
    /// <summary>Whether legacy inventory still needs its initial safe publication.</summary>
    public bool MigrationPending { get; set; }
    /// <summary>Whether a missing or damaged manifest requires explicit review/Apply.</summary>
    public bool RepairRequired { get; set; }

    /// <summary>Returns a detached checkpoint.</summary>
    public StorageLibraryRecord Copy() => (StorageLibraryRecord)MemberwiseClone();
}

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

/// <summary>A portable title recipe; local emulator and external command dependencies never go here.</summary>
public sealed class StorageLibraryContent
{
    /// <summary>The authoritative managed ID, including unchanged legacy IDs.</summary>
    [JsonRequired]
    public string Id { get; set; } = "";
    /// <summary>The display title.</summary>
    [JsonRequired]
    public string Name { get; set; } = "";
    /// <summary>The import category.</summary>
    [JsonRequired]
    public LibrarySourceKind SourceKind { get; set; }
    /// <summary>The source's friendly grouping metadata, independent of a device source ID.</summary>
    public string SourceName { get; set; } = "";
    /// <summary>The ROM system, resolved to a local emulator only after adoption.</summary>
    public string SystemId { get; set; } = "";
    /// <summary>The primary backing file or directory.</summary>
    [JsonRequired]
    public StorageLibraryPath BackingPath { get; set; } = new();
    /// <summary>The optional configured root on this volume.</summary>
    public StorageLibraryPath? SourceRoot { get; set; }
    /// <summary>Required backing companions, excluding BIOS and emulator dependencies.</summary>
    public List<StorageLibraryPath> RequiredPaths { get; set; } = [];
    /// <summary>The program on this volume, absent for ROMs and local-only recipes.</summary>
    public StorageLibraryPath? Program { get; set; }
    /// <summary>The working directory on this volume, absent for local-only recipes.</summary>
    public StorageLibraryPath? WorkingDirectory { get; set; }
    /// <summary>The PC command's arguments with outer whitespace trimmed.</summary>
    public string RawArguments { get; set; } = "";
    /// <summary>Whether another device must supply its own external command recipe.</summary>
    public bool RequiresLocalRecipe { get; set; }
}

/// <summary>A file query that never conflates access failures with absence.</summary>
public enum StorageLibraryPathState
{
    /// <summary>The required file/directory is present.</summary>
    Present,
    /// <summary>The required path is absent on confirmed readable expected storage.</summary>
    Missing,
    /// <summary>The expected volume is not mounted or ready.</summary>
    StorageUnavailable,
    /// <summary>The query failed or encountered an unsafe path.</summary>
    Unreadable
}

/// <summary>A detached path observation, with an actionable failure explanation.</summary>
public sealed record StorageLibraryPathCheck(StorageLibraryPathState State, string Path, string Detail);

/// <summary>Host-only card serialization; never source-link this parser into the launch helper.</summary>
[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(StorageLibraryManifest))]
internal sealed partial class StorageLibraryJsonContext : JsonSerializerContext;
