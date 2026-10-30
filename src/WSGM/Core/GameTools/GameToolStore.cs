using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace WSGM.Core.GameTools;

/// <summary>Whether a manifest describes a completed installation.</summary>
internal enum GameToolManifestState
{
    /// <summary>A transaction was persisted before touching game files and has not completed.</summary>
    Pending,

    /// <summary>The installation completed and its files are recorded.</summary>
    Installed,

    /// <summary>A rollback could not undo every change; the remaining journal waits for a user action.</summary>
    Failed
}

/// <summary>The kind of an owned file.</summary>
internal enum GameToolFileKind
{
    /// <summary>A binary removed on uninstall only while its hash still matches.</summary>
    Binary,

    /// <summary>A configuration file the tool edits itself; preserved on update and always removed on uninstall.</summary>
    Config
}

/// <summary>The change one journal entry made to a game file.</summary>
internal enum GameToolJournalAction
{
    /// <summary>The file did not exist and was created.</summary>
    Create,

    /// <summary>An existing file was backed up and overwritten.</summary>
    Replace,

    /// <summary>An existing file was backed up and deleted.</summary>
    Delete
}

/// <summary>A file WSGM wrote into a game folder.</summary>
internal sealed record GameToolOwnedFile
{
    /// <summary>The path relative to the target directory, with forward slashes.</summary>
    public string RelativePath { get; init; } = "";

    /// <summary>The SHA-256 of what WSGM wrote.</summary>
    public string Sha256 { get; init; } = "";

    /// <summary>The size WSGM wrote.</summary>
    public long Size { get; init; }

    /// <summary>The last write time after WSGM wrote it, for the quick revalidation.</summary>
    public DateTime LastWriteUtc { get; init; }

    /// <summary>Binary or configuration.</summary>
    public GameToolFileKind Kind { get; init; }
}

/// <summary>A game-shipped file WSGM backed up before replacing it.</summary>
internal sealed record GameToolReplacedFile
{
    /// <summary>The path relative to the target directory, with forward slashes.</summary>
    public string RelativePath { get; init; } = "";

    /// <summary>The SHA-256 of the original.</summary>
    public string OriginalSha256 { get; init; } = "";

    /// <summary>The absolute backup path under WSGM's data folder.</summary>
    public string BackupPath { get; init; } = "";
}

/// <summary>One recorded change of an in-flight transaction, enough to undo it after a crash.</summary>
internal sealed record GameToolJournalEntry
{
    /// <summary>The path relative to the target directory.</summary>
    public string RelativePath { get; init; } = "";

    /// <summary>What the transaction does to the file.</summary>
    public GameToolJournalAction Action { get; init; }

    /// <summary>The hash of the content written, empty for a deletion.</summary>
    public string NewSha256 { get; init; } = "";

    /// <summary>The backup of the previous content, empty for a creation.</summary>
    public string BackupPath { get; init; } = "";

    /// <summary>The hash of the previous content, empty for a creation.</summary>
    public string PreviousSha256 { get; init; } = "";
}

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

/// <summary>The remembered runtime executable of a game.</summary>
internal sealed record GameRuntimeChoice
{
    /// <summary>The executable relative to the install root, or absolute when the user picked a file outside it.</summary>
    public string RelativePath { get; init; } = "";

    /// <summary>The size when confirmed.</summary>
    public long Size { get; init; }

    /// <summary>The last write time when confirmed.</summary>
    public DateTime LastWriteUtc { get; init; }
}

/// <summary>Everything WSGM remembers about Game Tools for one AppID.</summary>
internal sealed record GameToolRecord
{
    /// <summary>The Steam AppID or shortcut id.</summary>
    public uint AppId { get; init; }

    /// <summary>Whether the id is a non-Steam shortcut.</summary>
    public bool IsShortcut { get; init; }

    /// <summary>The game name when last seen.</summary>
    public string Name { get; init; } = "";

    /// <summary>The confirmed runtime executable.</summary>
    public GameRuntimeChoice? Runtime { get; init; }

    /// <summary>Installed tools.</summary>
    public List<GameToolManifest> Tools { get; init; } = [];

    /// <summary>Finds a tool's manifest.</summary>
    /// <param name="toolId">The tool id.</param>
    /// <returns>The manifest, or null when WSGM has not installed the tool for this game.</returns>
    internal GameToolManifest? Manifest(string toolId)
    {
        return Tools.FirstOrDefault(manifest => manifest.ToolId == toolId);
    }

    /// <summary>Every file the manifests own below the install root, so folder scans can leave them out.</summary>
    /// <param name="root">The game's current install root.</param>
    /// <returns>Forward-slash paths relative to the root, compared ignoring case.</returns>
    internal HashSet<string> OwnedPaths(string root)
    {
        HashSet<string> owned = new(StringComparer.OrdinalIgnoreCase);
        foreach (var manifest in Tools)
        {
            var directory = GameToolPaths.Resolve(root, manifest.TargetDirectory);
            foreach (var file in manifest.Files)
            {
                var full = GameToolPaths.Resolve(directory, file.RelativePath);
                if (StoragePaths.IsUnder(root, full))
                {
                    owned.Add(GameToolPattern.Normalize(Path.GetRelativePath(root, full)));
                }
            }
        }

        return owned;
    }
}

/// <summary>The durable Game Tools state.</summary>
internal sealed record GameToolStore
{
    /// <summary>The format version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>The per-game records.</summary>
    public List<GameToolRecord> Games { get; init; } = [];

    /// <summary>Finds a game's record.</summary>
    /// <param name="appId">The AppID.</param>
    /// <returns>The record or null.</returns>
    internal GameToolRecord? Find(uint appId)
    {
        return Games.FirstOrDefault(game => game.AppId == appId);
    }

    /// <summary>Replaces or removes a game's record, dropping empty records.</summary>
    /// <param name="appId">The AppID.</param>
    /// <param name="record">The new record, or null to remove it.</param>
    /// <returns>The changed store.</returns>
    internal GameToolStore With(uint appId, GameToolRecord? record)
    {
        List<GameToolRecord> games = [.. Games.Where(game => game.AppId != appId)];
        if (record is not null && (record.Runtime is not null || record.Tools.Count > 0))
        {
            games.Add(record);
        }

        return this with { Games = games };
    }
}

/// <summary>Reads and writes the Game Tools state file under its own named lock.</summary>
/// <remarks>
///     Follows the emulator store: a separate schema-versioned file, a per-user named mutex, atomic
///     durable replacement, and refusal of an unknown schema rather than overwriting it.
/// </remarks>
internal static class GameToolStorage
{
    // Every read and write takes the gate, so its hashed name is computed once per data root.
    private static readonly ConcurrentDictionary<string, string> GateNames = new(StringComparer.Ordinal);

    /// <summary>Camel-case options with string enums for the store and the catalog.</summary>
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>The state file.</summary>
    /// <param name="userRoot">The per-user data root.</param>
    /// <returns>The path.</returns>
    internal static string StorePath(string userRoot)
    {
        return Path.Combine(userRoot, "game-tools.json");
    }

    /// <summary>The root of downloaded packages, backups and staging.</summary>
    /// <param name="userRoot">The per-user data root.</param>
    /// <returns>The path.</returns>
    internal static string DataRoot(string userRoot)
    {
        return Path.Combine(userRoot, "GameTools");
    }

    /// <summary>The backup folder of one installation or transaction.</summary>
    /// <param name="userRoot">The per-user data root.</param>
    /// <param name="appId">The AppID.</param>
    /// <param name="toolId">The tool.</param>
    /// <param name="installId">The installation or transaction id.</param>
    /// <returns>The path.</returns>
    internal static string BackupRoot(string userRoot, uint appId, string toolId, string installId)
    {
        return Path.Combine(DataRoot(userRoot), "backups", appId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            toolId, installId);
    }

    /// <summary>Reads the state.</summary>
    /// <param name="userRoot">The per-user data root.</param>
    /// <returns>The state, empty when the file does not exist.</returns>
    /// <exception cref="InvalidDataException">The file has an unsupported schema.</exception>
    internal static GameToolStore Read(string userRoot)
    {
        var path = StorePath(userRoot);
        if (!File.Exists(path))
        {
            return new GameToolStore();
        }

        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var store = JsonSerializer.Deserialize<GameToolStore>(file, JsonOptions)
                    ?? throw new InvalidDataException("The Game Tools state is empty.");
        if (store.SchemaVersion != 1 || store.Games is null)
        {
            throw new InvalidDataException("The Game Tools state version is unsupported.");
        }

        return store;
    }

    /// <summary>Reads, changes and atomically writes the state under the named lock.</summary>
    /// <param name="userRoot">The per-user data root.</param>
    /// <param name="change">Produces the new state from fresh state.</param>
    /// <returns>The written state.</returns>
    internal static GameToolStore Mutate(string userRoot, Func<GameToolStore, GameToolStore> change)
    {
        using var lease = NamedMutexLease.Acquire(GateName(userRoot), CancellationToken.None);
        var changed = change(Read(userRoot));
        Directory.CreateDirectory(userRoot);
        AtomicFile.WriteText(StorePath(userRoot), JsonSerializer.Serialize(changed, JsonOptions), true);
        return changed;
    }

    /// <summary>Reads under the named lock so a concurrent writer is never observed half way.</summary>
    /// <param name="userRoot">The per-user data root.</param>
    /// <returns>The state.</returns>
    internal static GameToolStore ReadLocked(string userRoot)
    {
        using var lease = NamedMutexLease.Acquire(GateName(userRoot), CancellationToken.None);
        return Read(userRoot);
    }

    /// <summary>Records a game's new install root on its manifests after the game moved to another library.</summary>
    /// <param name="userRoot">The per-user data root.</param>
    /// <param name="appId">The game.</param>
    /// <param name="root">The current install root.</param>
    /// <returns>The game's rebased record, or null when it has none.</returns>
    /// <remarks>Manifest paths are relative to the root, so only the recorded root changes.</remarks>
    internal static GameToolRecord? Rebase(string userRoot, uint appId, string root)
    {
        return Mutate(userRoot, store => store.Find(appId) is { } current
            ? store.With(appId, current with
            {
                Tools = [.. current.Tools.Select(manifest => manifest with { InstallRoot = root })]
            })
            : store).Find(appId);
    }

    /// <summary>Remembers the program the user confirmed for a game, with the game's identity.</summary>
    /// <param name="userRoot">The per-user data root.</param>
    /// <param name="appId">The game.</param>
    /// <param name="isShortcut">Whether the id is a non-Steam shortcut.</param>
    /// <param name="name">The game's name.</param>
    /// <param name="runtime">The confirmed program.</param>
    internal static void RememberRuntime(string userRoot, uint appId, bool isShortcut, string name,
        GameRuntimeChoice runtime)
    {
        Mutate(userRoot, store =>
        {
            var current = store.Find(appId) ?? new GameToolRecord { AppId = appId };
            return store.With(appId, current with { IsShortcut = isShortcut, Name = name, Runtime = runtime });
        });
    }

    /// <summary>Refreshes a game's identity on its existing record; a game without a record gets none.</summary>
    /// <param name="userRoot">The per-user data root.</param>
    /// <param name="appId">The game.</param>
    /// <param name="isShortcut">Whether the id is a non-Steam shortcut.</param>
    /// <param name="name">The game's name.</param>
    internal static void RememberIdentity(string userRoot, uint appId, bool isShortcut, string name)
    {
        Mutate(userRoot, store => store.Find(appId) is { } record
            ? store.With(appId, record with { IsShortcut = isShortcut, Name = name })
            : store);
    }

    /// <summary>Removes a WSGM-owned folder such as a backup or staging tree, logging instead of failing.</summary>
    /// <param name="path">The folder; a missing one is ignored.</param>
    internal static void DeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Game Tools: could not remove {path}: {ex.Message}");
        }
    }

    private static string GateName(string userRoot)
    {
        return GateNames.GetOrAdd(userRoot, static root => StoragePaths.GateName(@"Local\WSGM.GameTools.", root));
    }
}
