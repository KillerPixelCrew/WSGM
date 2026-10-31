using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WSGM.Core;

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
