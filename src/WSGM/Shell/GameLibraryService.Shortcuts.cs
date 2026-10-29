using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

internal sealed partial class GameLibraryService
{
    // Shortcuts the user made in Steam, observed against storage. WSGM never owns or changes them.
    private IReadOnlyList<GameLibraryShortcut> _steamShortcuts = [];

    /// <summary>The recheck id that names one user-made Steam shortcut.</summary>
    internal static string ShortcutCheckId(uint appId) => "shortcut:" + appId;

    /// <summary>Publishes availability for WSGM's own titles and the user's storage-backed shortcuts.</summary>
    private void PublishShortcutAvailability()
    {
        IReadOnlyList<GameLibraryShortcut> manual;
        lock (_gate)
        {
            manual = _steamShortcuts;
        }

        LibraryBadges.UpdateShortcuts(ReadManagedEntries().Where(entry => entry.AppId > 0)
            .Select(entry => new SteamShortcutAvailability(entry.AppId,
                entry.Content.Availability == ManagedContentAvailability.Available,
                entry.Content.Location, entry.Content.AvailabilityDetail))
            .Concat(manual.Select(shortcut => new SteamShortcutAvailability(shortcut.AppId, shortcut.Available,
                shortcut.Location, shortcut.Detail)))
            .ToArray());
    }

    /// <summary>Observes every shortcut WSGM did not create whose program or folder is on storage.</summary>
    /// <param name="snapshot">A Steam shortcut read already taken by the caller, or null to read one.</param>
    /// <param name="token">Cancels the observation.</param>
    /// <remarks>
    ///     Only the Target and Start In the user gave Steam are checked. Arguments are never parsed for
    ///     paths, nothing is launched, and a missing program is reported, never removed: the shortcut is
    ///     the user's own.
    /// </remarks>
    private async Task ObserveSteamShortcutsAsync(IReadOnlyList<ExistingShortcut>? snapshot, CancellationToken token)
    {
        try
        {
            snapshot ??= await _readLibrary(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Log.Change("library.steam-shortcuts.pending", "Steam shortcuts could not be checked: " + ex.Message);
            return;
        }

        var owned = _store.Entries().Where(entry => entry.AppId > 0).Select(entry => entry.AppId).ToHashSet();
        Dictionary<string, string> volumes;
        lock (_gate)
        {
            volumes = new Dictionary<string, string>(_eligibleVolumes, StringComparer.OrdinalIgnoreCase);
        }

        Dictionary<char, string?> drives = [];
        List<GameLibraryShortcut> observed = [];
        foreach (var shortcut in snapshot.Where(shortcut => !owned.Contains(shortcut.AppId)))
        {
            token.ThrowIfCancellationRequested();
            if (ObserveShortcut(shortcut, volumes, drives) is { } result)
            {
                observed.Add(result);
            }
        }

        var changed = false;
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            if (_disposed)
            {
                return;
            }

            observed.Sort((left, right) => StringComparer.CurrentCulture.Compare(left.Name, right.Name));
            if (!_steamShortcuts.SequenceEqual(observed))
            {
                _steamShortcuts = observed;
                changed = true;
                Publish();
            }
        }

        PublishShortcutAvailability();
        if (changed)
        {
            Log.Change("library.steam-shortcuts",
                $"Steam shortcuts on storage: {observed.Count}, unavailable {observed.Count(item => !item.Available)}.");
            Notify();
        }
    }

    private static GameLibraryShortcut? ObserveShortcut(ExistingShortcut shortcut,
        IReadOnlyDictionary<string, string> volumes, Dictionary<char, string?> drives)
    {
        GameLibraryShortcut? available = null;
        foreach (var (path, folder) in new[] { (Unquote(shortcut.Target), false), (Unquote(shortcut.StartDirectory), true) })
        {
            if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/'))
            {
                continue;
            }

            var letter = char.ToUpperInvariant(path[0]);
            var drive = $"{letter}:";
            if (!drives.TryGetValue(letter, out var volume))
            {
                volume = Directory.Exists(drive + "\\")
                    ? NativeStorage.TryGetVolumeGuidPath(letter, out var guid) && guid is not null ? guid : ""
                    : null;
                drives[letter] = volume;
            }

            if (volume is null)
            {
                return new GameLibraryShortcut(shortcut.AppId, shortcut.Name, drive, Available: false,
                    $"Connect the storage for drive {drive} and choose Recheck.");
            }

            // A fixed disk's broken shortcut is not a storage question, so only participating volumes count.
            if (!volumes.TryGetValue(volume, out var name))
            {
                continue;
            }

            if (folder ? !Directory.Exists(path) : !File.Exists(path))
            {
                return new GameLibraryShortcut(shortcut.AppId, shortcut.Name, name, Available: false,
                    $"{path} is missing from {name}. Edit the shortcut in Steam if the game moved.");
            }

            available ??= new GameLibraryShortcut(shortcut.AppId, shortcut.Name, name, Available: true, Detail: "");
        }

        return available;
    }

    private static string Unquote(string value) => value.Trim().Trim('"').Trim();
}
