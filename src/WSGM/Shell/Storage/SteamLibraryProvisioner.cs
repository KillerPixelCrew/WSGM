using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Puts a Steam library on a folder: writes the card-side marker and Steam's client dll, and
///     registers the library with the live client or, when Steam is closed, in its
///     libraryfolders.vdf. Serves both the freshly formatted card and a folder the user picks
///     through Add Library, which formats nothing.
/// </summary>
/// <param name="operations">The format flow's external operations; this uses the Steam, marker and log ones.</param>
/// <param name="progress">The storage run an Add Library publishes its progress and outcome on.</param>
internal sealed class SteamLibraryProvisioner(SdFormatOperations operations, StorageRunProgress progress)
{
    /// <summary>
    ///     Turns a user-chosen folder into a registered Steam library
    ///     WITHOUT formatting anything — for network shares, second internal drives
    ///     (DIY Steam machines), and existing libraries. A drive root becomes
    ///     <c>&lt;root&gt;SteamLibrary</c> (Steam's own layout); any other folder is
    ///     used as the library root directly. An existing library (marker present)
    ///     keeps its contentid untouched and is only registered.
    /// </summary>
    /// <param name="folderPath">The folder the user picked.</param>
    /// <returns>
    ///     Completion of library creation and registration, or immediately when busy. Inspect the run's
    ///     StatusText and the published result for failure.
    /// </returns>
    internal async Task AddLibraryAsync(string folderPath)
    {
        if (progress.Busy)
        {
            return;
        }

        await progress.EnterAsync(CancellationToken.None);
        try
        {
            progress.StatusText = "Adding Steam library...";
            var summary = await Task.Run(() => AddLibrary(folderPath));
            progress.Finish(summary.Message, summary.Success);
        }
        catch (Exception ex)
        {
            Error("Format: add-library failed.", ex);
            progress.Finish("Could not add the library — see the log.", false);
        }
        finally
        {
            progress.Exit();
        }
    }

    /// <summary>
    ///     Resolves the library root for a picked folder: drive roots get
    ///     the conventional SteamLibrary subfolder, everything else is taken as-is.
    /// </summary>
    /// <param name="folderPath">The folder the user picked.</param>
    /// <returns>
    ///     SteamLibrary below a drive root, otherwise the folder without trailing separators; an all-separator input is
    ///     preserved.
    /// </returns>
    internal static string ResolveLibraryRoot(string folderPath)
    {
        var trimmed = folderPath.TrimEnd('\\', '/');
        // "D:" / "D:\" → the conventional <root>\SteamLibrary.
        return trimmed is [_, ':']
            ? $@"{trimmed}\{SteamLibraryVdf.CardFolderName}"
            : trimmed.Length == 0
                ? folderPath
                : trimmed;
    }

    /// <summary>
    ///     Creates the card-side Steam library (marker VDF, steamapps,
    ///     steam.dll) through the verified volume root, registers it in Steam's config
    ///     when possible, and pokes drive watchers with a synthetic volume-arrival
    ///     broadcast — the real arrival fired when the volume was still empty, so a
    ///     running Steam has already looked and found nothing. Worker thread.
    /// </summary>
    /// <param name="letter">The drive letter the formatted volume mounted on.</param>
    /// <param name="volumeRoot">The verified volume GUID root every file write goes through.</param>
    /// <param name="sizeBytes">The card's capacity, recorded in a closed-Steam registration.</param>
    /// <param name="label">The sanitized library label.</param>
    /// <returns>The user-facing summary.</returns>
    internal string CreateSteamLibrary(char letter, string volumeRoot, long sizeBytes, string label)
    {
        var libraryPath = Path.Combine(operations.LetterRoot(letter), SteamLibraryVdf.CardFolderName);
        var writePath = Path.Combine(volumeRoot, SteamLibraryVdf.CardFolderName);
        Directory.CreateDirectory(Path.Combine(writePath, "steamapps"));

        var steamExe = operations.ReadSteam().ExePath;
        var (configPath, configText) = operations.ReadLibraryFolders();
        var contentId = NewContentId(configText);

        // Steam drops a copy of its current client dll into every secondary
        // library root; version skew is tolerated, so this is create-time only.
        WriteMarkerAndClientDll(writePath, contentId, steamExe, label);

        // Steam stores the letter path, so the registration is the one step that cannot go
        // through the volume root. Re-read the marker on that letter right before the call
        // and register only when it is still this library (Shell\AGENTS.md).
        string? onLetter = null;
        try
        {
            (onLetter, _) = operations.ReadMarker(libraryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"Format: could not re-read {libraryPath} before registering it: {ex.Message}");
        }

        if (!string.Equals(onLetter, contentId, StringComparison.Ordinal))
        {
            Warn($"Format: {libraryPath} no longer holds library {contentId} "
                 + $"(found {onLetter ?? "none"}); not registering it with Steam.");
            return $"The card was formatted, but {letter}: changed before it could be added to Steam. "
                   + "Add it in Steam under Settings > Storage.";
        }

        var registration = RegisterLibrary(configPath, configText, libraryPath, contentId,
            sizeBytes, label);

        // Now that the library exists, make drive watchers look at the volume
        // again (best effort; harmless when nobody listens).
        operations.BroadcastVolumeArrival(letter);
        Info($"Format: volume-arrival broadcast sent for {letter}:.");

        return $"{letter}: is ready as a Steam library. {registration}";
    }

    /// <summary>Copies Steam's library config beside itself, once, before WSGM first edits it.</summary>
    /// <param name="path">Steam's libraryfolders.vdf.</param>
    internal static void BackupOnce(string path)
    {
        var backup = path + ".wsgm-bak";
        if (!File.Exists(backup))
        {
            File.Copy(path, backup, false);
        }
    }

    /// <summary>A content id no library in Steam's libraryfolders.vdf already uses.</summary>
    /// <param name="configText">Steam's libraryfolders.vdf, or null when it could not be read.</param>
    private static string NewContentId(string? configText)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        if (configText is not null)
        {
            foreach (var id in SteamLibraryVdf.ValuesOf(configText, "contentid"))
            {
                taken.Add(id);
            }
        }

        return SteamLibraryVdf.GenerateContentId(taken);
    }

    private (string Message, bool Success) AddLibrary(string folderPath)
    {
        var libraryPath = ResolveLibraryRoot(folderPath);
        Info($"Format: adding library at {libraryPath} (picked: {folderPath}).");
        try
        {
            Directory.CreateDirectory(Path.Combine(libraryPath, "steamapps"));
        }
        catch (Exception ex)
        {
            Warn($"Format: cannot create {libraryPath}: {ex.Message}");
            return ($"Could not create a library at {libraryPath}.", false);
        }

        var steamExe = operations.ReadSteam().ExePath;
        var (configPath, configText) = operations.ReadLibraryFolders();

        // An existing library keeps its identity; only a fresh folder gets a
        // marker (and Steam's client dll) written.
        string contentId;
        if (operations.ReadMarker(libraryPath) is ({ Length: > 0 } existingId, _))
        {
            contentId = existingId;
            Info($"Format: existing library found (contentid {contentId}).");
        }
        else
        {
            contentId = NewContentId(configText);
            WriteMarkerAndClientDll(libraryPath, contentId, steamExe, "");
        }

        long totalSize = 0;
        try
        {
            var root = Path.GetPathRoot(libraryPath);
            if (root is { Length: > 0 } && root[0] != '\\')
            {
                totalSize = new DriveInfo(root).TotalSize;
            }
        }
        catch (Exception)
        {
            // Network shares have no DriveInfo; Steam fills totalsize itself.
        }

        // The Add-Library flow has no name field; an empty label leaves an existing
        // library's own label untouched and gives a fresh folder Steam's default.
        var registration = RegisterLibrary(configPath, configText, libraryPath, contentId,
            totalSize, "");
        return ($"{libraryPath} is set up as a Steam library. {registration}", true);
    }

    /// <summary>
    ///     Registers the library with Steam. When Steam is RUNNING this
    ///     drives Steam's own front-end API over its CEF debug port
    ///     (<see cref="SteamInstallFolders.AddAsync" />): Steam adopts, persists, mounts and scans it with
    ///     no restart, which a file edit cannot do against a live client (Steam holds
    ///     libraries in memory and rewrites the file on exit). When Steam is CLOSED
    ///     (or its debug port is unreachable), the entry is spliced into
    ///     config\libraryfolders.vdf so Steam reads it on next start; dedup there is by
    ///     CONTENT ID (a card reader reuses its drive letter, so the path repeats per
    ///     card). Returns the summary sentence.
    /// </summary>
    private string RegisterLibrary(
        string? configPath, string? configText, string libraryPath, string contentId,
        long sizeBytes, string label)
    {
        // Live client: drive Steam's own front-end API over its CEF debug port so
        // Steam adds, persists, mounts and scans the library with no restart — the
        // only thing that makes a running Steam show a library live. Falls through
        // to the config write only when the debug channel cannot be reached.
        if (operations.ReadSteam().Running)
        {
            // replaceExisting: the library at this path was created seconds ago on a
            // freshly wiped card, so any registration Steam still holds there belongs
            // to a card that is gone. Left in place, Steam lists the previous card's
            // games beside the new card's capacity until it is restarted.
            var live = operations.AddLibrary(libraryPath, label, true);
            switch (live.Status)
            {
                case SteamLibraryAddStatus.Added:
                    return "Added to Steam.";
                case SteamLibraryAddStatus.AlreadyPresent:
                    return "This library is already in Steam.";
                case SteamLibraryAddStatus.Partial:
                    Warn($"Format: Steam added the library partly ({live.Detail}).");
                    return "Added to Steam.";
                case SteamLibraryAddStatus.Rejected:
                    Warn($"Format: Steam refused the library add ({live.Detail}).");
                    return $"Steam did not accept it: {live.Detail}.";
                case SteamLibraryAddStatus.Unknown:
                    // Sent, but the answer was lost: the library may be in Steam already, so the live
                    // config is not edited and the add is not repeated.
                    Warn($"Format: Steam did not answer the library add ({live.Detail}).");
                    return
                        "Steam did not answer. Check Settings > Storage, and add the library there if it is missing.";
                case SteamLibraryAddStatus.NotSent:
                default:
                    Warn("Format: Steam debug port unavailable — not editing its live config.");
                    return "Restart Steam, then add the library under Settings > Storage.";
            }
        }

        // Steam closed (or its debug port was unreachable): write the config; it is
        // read on next start.
        if (configPath is null || configText is null)
        {
            Warn("Format: Steam config not found — skipping registration.");
            return "Add it in Steam under Settings > Storage.";
        }

        if (SteamLibraryVdf.IsContentIdRegistered(configText, contentId))
        {
            Info($"Format: content id {contentId} already in libraryfolders.vdf.");
            return "This library is already in Steam.";
        }

        // Same staleness rule as the live path, applied to the file: dedup below is
        // by CONTENT ID, which cannot see a registration the previous card left at
        // this reader's drive letter under its own id. Splicing next to it would put
        // two entries at one path into the file Steam reads on next start.
        var stale = SteamLibraryVdf.TryRemovePath(configText, libraryPath, out var purged);
        if (stale > 0 && purged is not null)
        {
            Info($"Format: dropped {stale} stale registration(s) at {libraryPath} "
                 + "from libraryfolders.vdf before adding the new card.");
            configText = purged;
        }

        if (!SteamLibraryVdf.TrySplice(configText, libraryPath, contentId, sizeBytes,
                out var updated, label))
        {
            Warn("Format: libraryfolders.vdf has an unexpected shape — not editing it.");
            return "Add it in Steam under Settings > Storage.";
        }

        BackupOnce(configPath);
        AtomicFile.WriteText(configPath, updated!, true);
        Info($"Format: {libraryPath} registered in libraryfolders.vdf (backup written).");
        return "Added to Steam's library list (on next start).";
    }

    /// <summary>
    ///     Writes the marker VDF (Steam's exact dialect: UTF-8 no BOM,
    ///     LF-only) and copies Steam's client dll beside it.
    /// </summary>
    private void WriteMarkerAndClientDll(
        string libraryPath, string contentId, string? steamExe, string label)
    {
        var utf8NoBom = new UTF8Encoding(false);
        File.WriteAllText(
            Path.Combine(libraryPath, SteamLibraryMarker.MarkerFileName),
            SteamLibraryVdf.BuildMarker(contentId, steamExe ?? "", label),
            utf8NoBom);
        Info($"Format: library marker written ({libraryPath}, contentid {contentId}).");
        if (steamExe is null)
        {
            return;
        }

        var sourceDll = Path.Combine(Path.GetDirectoryName(steamExe)!, "steam.dll");
        if (File.Exists(sourceDll))
        {
            File.Copy(sourceDll, Path.Combine(libraryPath, "steam.dll"), true);
        }
        else
        {
            Warn($"Format: steam.dll not found at {sourceDll} — library still mounts.");
        }
    }

    private void Info(string message)
    {
        operations.WriteLog(LogLevel.Info, message, null);
    }

    private void Warn(string message)
    {
        operations.WriteLog(LogLevel.Warn, message, null);
    }

    private void Error(string message, Exception exception)
    {
        operations.WriteLog(LogLevel.Error, message, exception);
    }
}
