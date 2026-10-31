using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace WSGM.Core;

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
