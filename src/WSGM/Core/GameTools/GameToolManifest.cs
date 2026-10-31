using System;
using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>The ownership manifest of one tool installed into one game.</summary>
internal sealed record GameToolManifest
{
    /// <summary>The tool id.</summary>
    public string ToolId { get; init; } = "";

    /// <summary>A unique id for this installation, naming its backup folder.</summary>
    public string InstallId { get; init; } = "";

    /// <summary>The installed product version.</summary>
    public string Version { get; init; } = "";

    /// <summary>The installed version family.</summary>
    public string Family { get; init; } = "";

    /// <summary>The release identity from the provider.</summary>
    public string ReleaseId { get; init; } = "";

    /// <summary>The release tag version.</summary>
    public string ReleaseVersion { get; init; } = "";

    /// <summary>The asset download URL.</summary>
    public string SourceUrl { get; init; } = "";

    /// <summary>The asset file name.</summary>
    public string AssetName { get; init; } = "";

    /// <summary>The asset SHA-256.</summary>
    public string AssetSha256 { get; init; } = "";

    /// <summary>The name the primary file is installed as.</summary>
    public string ProxyName { get; init; } = "";

    /// <summary>The game's install root when this manifest was last written, used to detect a moved game.</summary>
    public string InstallRoot { get; init; } = "";

    /// <summary>The target directory relative to the install root.</summary>
    public string TargetDirectory { get; init; } = "";

    /// <summary>The runtime executable relative to the install root.</summary>
    public string RuntimeExecutable { get; init; } = "";

    /// <summary>The runtime architecture.</summary>
    public string Architecture { get; init; } = "";

    /// <summary>The detected engine id.</summary>
    public string Engine { get; init; } = "";

    /// <summary>Files WSGM owns.</summary>
    public List<GameToolOwnedFile> Files { get; init; } = [];

    /// <summary>Game files WSGM backed up and replaced.</summary>
    public List<GameToolReplacedFile> Replaced { get; init; } = [];

    /// <summary>Directories WSGM created, relative to the target directory.</summary>
    public List<string> CreatedDirectories { get; init; } = [];

    /// <summary>Applied setting values by setting id; the proxy name is recorded separately.</summary>
    public Dictionary<string, string> Settings { get; init; } = new(StringComparer.Ordinal);

    /// <summary>When the tool was first installed.</summary>
    public DateTime InstalledUtc { get; init; }

    /// <summary>When the manifest last changed.</summary>
    public DateTime UpdatedUtc { get; init; }

    /// <summary>The transaction state.</summary>
    public GameToolManifestState State { get; init; }

    /// <summary>The in-flight or failed transaction's changes.</summary>
    public List<GameToolJournalEntry> Journal { get; init; } = [];

    /// <summary>Directories the in-flight transaction created.</summary>
    public List<string> JournalDirectories { get; init; } = [];

    /// <summary>The manifest restored when the transaction rolls back; null for a fresh install.</summary>
    public GameToolManifest? Previous { get; init; }

    /// <summary>
    ///     The transaction kind: <c>install</c>, <c>replaceExisting</c>, <c>update</c>, <c>repair</c> or <c>configure</c>.
    /// </summary>
    public string Operation { get; init; } = "";
}
