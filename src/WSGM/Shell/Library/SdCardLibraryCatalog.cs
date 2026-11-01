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

/// <summary>
///     The tracked card model: one entry per removable Steam library (MicroSD card or external drive),
///     keyed by its <c>libraryfolder.vdf</c> content id and remembered while ejected. Scans the mounted
///     drives, merges what they carry into configuration, and renames, switches, hides and forgets a
///     card everywhere it has a name or a preference.
/// </summary>
/// <param name="store">The configuration persistence the cards are kept in.</param>
internal sealed class SdCardLibraryCatalog(ConfigStore store)
{
    /// <summary>
    ///     Scans drives, refreshes the card DB, and returns the current cards with
    ///     live inserted state — the card manager's data source.
    /// </summary>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>Tracked cards with current presence and app IDs, after discovered metadata is merged into configuration.</returns>
    internal async Task<IReadOnlyList<LibraryCardView>> ListCardsAsync(CancellationToken cancellationToken = default)
    {
        var discovered = await Task.Run(ScanLibraries, cancellationToken).ConfigureAwait(false);
        var present = discovered.Select(static card => card.ContentId).ToHashSet(StringComparer.Ordinal);
        return await store.UpdateAsync(config =>
        {
            MergeDiscovery(config, discovered);
            return ProjectCards(config, present);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The tracked cards as the managers show them, from configuration and the mounted cards.</summary>
    /// <param name="config">The configuration holding the card model.</param>
    /// <param name="present">The content ids of the cards mounted right now.</param>
    /// <returns>A detached list; no I/O.</returns>
    internal static IReadOnlyList<LibraryCardView> ProjectCards(AppConfig config, IReadOnlySet<string> present)
    {
        return
        [
            .. config.CardLibraries.Select(c => new LibraryCardView(
                c.ContentId, c.Name, c.Enabled, c.Hidden, present.Contains(c.ContentId), c.AppIds.Count,
                [.. c.AppIds], c.CapacityBytes, c.FreeBytes))
        ];
    }

    /// <summary>
    ///     Renames a tracked card everywhere it has a name: the card's own
    ///     <c>libraryfolder.vdf</c> marker, the WSGM tab, the Steam library label (live
    ///     via CEF when Steam runs, else a byte-preserving vdf edit), and the Windows
    ///     volume label. Identity is always the content id, and every write addresses the
    ///     volume resolved from it — never a drive letter.
    /// </summary>
    /// <remarks>
    ///     Requires mounted media. The on-card marker is written first because later scans treat it
    ///     as authoritative; other writes proceed only after that succeeds. File-system writes use
    ///     the resolved volume GUID path so a reassigned drive letter cannot redirect the rename.
    /// </remarks>
    /// <param name="steam">The session's Steam client, or null on a surface without one.</param>
    /// <param name="contentId">The card's content id.</param>
    /// <param name="name">The new name.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>
    ///     Whether the marker and the stored name now carry the new name, and null when every side applied;
    ///     otherwise a short user-facing note describing what did not.
    /// </returns>
    internal async Task<(bool Renamed, string? Note)> RenameCardAsync(SteamClient? steam, string contentId,
        string name, CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return (false, "The name cannot be empty.");
        }

        var mounted = await Task.Run(() => FindMountedVolume(contentId), cancellationToken)
            .ConfigureAwait(false);
        if (mounted is not { } volume)
        {
            Log.Info($"Card rename: {contentId} is not mounted; nothing can write its name "
                     + "onto the drive, so the rename is refused.");
            return (false, "Connect the drive to rename it.");
        }

        var library = Path.Combine(volume.Root, SteamLibraryVdf.CardFolderName);
        var markerNote = await Task.Run(
                () => TrySetMarkerLabel(library, contentId, trimmed), cancellationToken)
            .ConfigureAwait(false);
        if (markerNote is not null)
        {
            return (false, markerNote);
        }

        await UpdateCardAsync(contentId, c => c.Name = trimmed, cancellationToken)
            .ConfigureAwait(false);

        var notes = new List<string>();
        var steamNote = await PushLabelToSteamAsync(steam, contentId, trimmed, cancellationToken)
            .ConfigureAwait(false);
        if (steamNote is not null)
        {
            notes.Add(steamNote);
        }

        var volumeNote = await Task.Run(
            () => TrySetVolumeLabel(volume, trimmed), cancellationToken).ConfigureAwait(false);
        if (volumeNote is not null)
        {
            notes.Add(volumeNote);
        }

        return (true, notes.Count == 0 ? null : string.Join(" ", notes));
    }

    /// <summary>
    ///     Writes the new label into the card's own <c>libraryfolder.vdf</c>,
    ///     preserving every other byte, and reads it back to confirm the media now says
    ///     what the caller asked for.
    /// </summary>
    /// <remarks>
    ///     Safe against a running Steam: the marker is read at mount time, not
    ///     held open, and WSGM already writes it at format time under a live client.
    /// </remarks>
    /// <param name="libraryPath">
    ///     The card library root, e.g. <c>E:\SteamLibrary</c>.
    /// </param>
    /// <param name="contentId">The identity the marker must still carry.</param>
    /// <param name="label">The new label.</param>
    /// <returns>
    ///     Null when the marker now carries the label, else a user-facing note.
    /// </returns>
    internal static string? TrySetMarkerLabel(
        string libraryPath, string contentId, string label)
    {
        const string markerBehind = "The drive still carries its old name.";
        var marker = Path.Combine(libraryPath, SteamLibraryMarker.MarkerFileName);
        try
        {
            if (!File.Exists(marker))
            {
                Log.Warn($"Card rename: {marker} is gone; the card keeps its old name.");
                return markerBehind;
            }

            // Selected by content id even though the path already matched it: the card
            // can be swapped between FindMountedLetter and here, and relabelling the
            // wrong card is exactly the failure this rename exists to stop.
            if (!SteamLibraryVdf.TrySetLabel(
                    File.ReadAllText(marker), contentId, label, out var updated)
                || updated is null)
            {
                Log.Warn($"Card rename: {marker} does not carry content id {contentId}; "
                         + "the card in the reader changed. Its name is unchanged.");
                return markerBehind;
            }

            AtomicFile.WriteText(marker, updated, true);
            // Read back rather than trust the write: a replace that half-applied, or a
            // volume that went away underneath it, must not be reported as a rename the
            // next scan will contradict.
            if (SteamLibraryMarker.TryRead(libraryPath, out var written, out var name)
                && string.Equals(written, contentId, StringComparison.Ordinal)
                && string.Equals(name, label, StringComparison.Ordinal))
            {
                return null;
            }

            Log.Warn($"Card rename: {marker} reads back as "
                     + $"'{name}' (content id {written ?? "none"}) after the write; "
                     + "the card's name is not what was asked for.");
            return markerBehind;
        }
        catch (Exception ex)
        {
            Log.Warn($"Card rename: could not write {marker}: {ex.Message}");
            return markerBehind;
        }
    }

    /// <summary>
    ///     Finds the volume carrying this content id, verified by reading the
    ///     marker, and returns it as a volume GUID path.
    /// </summary>
    /// <remarks>
    ///     Validate and retain the volume GUID path, not its reassignable drive letter, so later
    ///     writes still address the media whose marker was checked.
    /// </remarks>
    /// <param name="contentId">The library identity to look for.</param>
    private static MountedCardVolume? FindMountedVolume(string contentId)
    {
        foreach (var letter in ExternalVolumeLetters())
        {
            try
            {
                // Resolve the volume BEFORE reading the marker, and read through the
                // volume: validating a letter and then writing to that letter is the
                // race this exists to close.
                if (!NativeStorage.TryGetVolumeGuidPath(letter, out var root) || root is null)
                {
                    Log.Warn($"Card rename: {letter}: has no volume path (Win32 error "
                             + $"{NativeStorage.LastWin32Error()}); it cannot be written safely.");
                    continue;
                }

                if (SteamLibraryMarker.TryRead(
                        Path.Combine(root, SteamLibraryVdf.CardFolderName), out var id, out _)
                    && string.Equals(id, contentId, StringComparison.Ordinal))
                {
                    return new MountedCardVolume(root, letter);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($@"Card rename: could not probe {letter}:\: {ex.Message}");
            }
        }

        return null;
    }

    // Pushes the new label to Steam's own view of the library, so its storage page
    // agrees with the name WSGM shows. Live client: SetFolderLabel over CEF, which
    // Steam persists itself. Closed client: a byte-preserving edit of
    // config\libraryfolders.vdf. The card's own marker is NOT written here — that is
    // TrySetMarkerLabel's job and it runs whether or not Steam is reachable, because
    // it is the copy the next scan reads the name back from.
    private static async Task<string?> PushLabelToSteamAsync(
        SteamClient? steam, string contentId, string label, CancellationToken cancellationToken)
    {
        const string steamBehind = "Steam still shows the old name.";
        var steamExe = Steam.ExePath;
        if (steamExe is null)
        {
            return null;
        }

        var configPath = Path.Combine(
            Path.GetDirectoryName(steamExe)!, "config", "libraryfolders.vdf");

        if (Steam.IsRunning)
        {
            string configText;
            try
            {
                configText = File.Exists(configPath)
                    ? await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false)
                    : "";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"Card rename: could not read Steam config: {ex.Message}");
                return steamBehind;
            }

            if (steam is null)
            {
                return steamBehind;
            }

            var result = await SteamLibraryFolders.SetLibraryLabelByContentIdAsync(
                steam, contentId, configText, label, cancellationToken).ConfigureAwait(false);
            // A relabel Steam received but did not answer is published as written, never repeated.
            if (result.Status is SteamLibraryLabelStatus.Applied
                or SteamLibraryLabelStatus.NotPresent or SteamLibraryLabelStatus.Unknown)
            {
                return null;
            }

            Log.Warn($"Card rename: live relabel {result.Status} "
                     + $"({result.Detail ?? "no detail"}).");
            return steamBehind;
        }

        var edited = await Task.Run(() =>
        {
            try
            {
                if (!File.Exists(configPath)
                    || !SteamLibraryVdf.TrySetLabel(
                        File.ReadAllText(configPath), contentId, label, out var updatedConfig)
                    || updatedConfig is null)
                {
                    return true;
                }

                if (Steam.IsRunning)
                {
                    // Started between the check and the write; its exit rewrite
                    // would clobber ours (and ours could corrupt its view).
                    return false;
                }

                AtomicFile.WriteText(configPath, updatedConfig, true);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"Card rename: vdf label edit failed: {ex.Message}");
                return false;
            }
        }, cancellationToken).ConfigureAwait(false);
        return edited ? null : steamBehind;
    }

    // Windows volume labels are capped by filesystem: 32 chars on NTFS, 11 on
    // FAT32/exFAT — truncate rather than fail, and strip FAT-hostile characters.
    // Addressed by volume GUID path, not by DriveInfo: this runs after a Steam round
    // trip that can take seconds, and a drive letter can name different media by then.
    private static string? TrySetVolumeLabel(MountedCardVolume volume, string name)
    {
        const string labelBehind = "The Windows volume label could not be changed.";
        try
        {
            if (!NativeStorage.TryGetVolumeInformation(
                    volume.Root, out var current, out var fileSystem))
            {
                Log.Warn($"Card rename: {volume.Letter}: could not be queried for its label "
                         + $"(Win32 error {NativeStorage.LastWin32Error()}); it is unchanged.");
                return labelBehind;
            }

            var isNtfs = string.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase);
            var invalid = "*?/\\|,;:+=<>[]\".".ToCharArray();
            var cleaned = new string([.. name.Where(c => Array.IndexOf(invalid, c) < 0)]).Trim();
            if (cleaned.Length == 0)
            {
                return null;
            }

            var capped = cleaned.Length > (isNtfs ? 32 : 11)
                ? cleaned[..(isNtfs ? 32 : 11)]
                : cleaned;
            if (string.Equals(current, capped, StringComparison.Ordinal))
            {
                return null;
            }

            if (!NativeStorage.TrySetVolumeLabel(volume.Root, capped))
            {
                Log.Warn($"Card rename: could not label {volume.Letter}: '{capped}' "
                         + $"(Win32 error {NativeStorage.LastWin32Error()}).");
                return labelBehind;
            }

            Log.Info($"Card rename: volume {volume.Letter}: labeled '{capped}'.");
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Card rename: could not set the volume label on "
                     + $"{volume.Letter}:: {ex.Message}");
            return labelBehind;
        }
    }

    /// <summary>Enables or disables a card's Steam tab.</summary>
    /// <param name="contentId">The card's content id.</param>
    /// <param name="enabled">Whether to maintain a tab.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Completion after the preference is saved.</returns>
    internal Task SetCardEnabledAsync(string contentId, bool enabled, CancellationToken cancellationToken = default)
    {
        return UpdateCardAsync(contentId, c => c.Enabled = enabled, cancellationToken);
    }

    /// <summary>Hides or unhides a card in the manager.</summary>
    /// <param name="contentId">The card's content id.</param>
    /// <param name="hidden">Whether to hide it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Completion after the preference is saved.</returns>
    internal Task SetCardHiddenAsync(string contentId, bool hidden, CancellationToken cancellationToken = default)
    {
        return UpdateCardAsync(contentId, c => c.Hidden = hidden, cancellationToken);
    }

    /// <summary>
    ///     Forgets a card: removes its tab (if any) and its DB entry. If the card is reinserted later it
    ///     is rediscovered fresh.
    /// </summary>
    /// <param name="contentId">The card's content id.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Completion after stored card metadata is removed.</returns>
    internal Task ForgetCardAsync(string contentId, CancellationToken cancellationToken = default)
    {
        return store.UpdateAsync<object?>(config =>
        {
            var card = config.CardLibraries.FirstOrDefault(c =>
                string.Equals(c.ContentId, contentId, StringComparison.Ordinal));
            if (card is null)
            {
                return null;
            }

            config.CardLibraries.Remove(card);
            if (!config.ForgottenInsertedCardIds.Contains(contentId, StringComparer.Ordinal))
            {
                config.ForgottenInsertedCardIds.Add(contentId);
            }

            return null;
        }, cancellationToken);
    }

    private Task<object?> UpdateCardAsync(string contentId, Action<CardLibraryConfig> apply,
        CancellationToken cancellationToken)
    {
        return store.UpdateAsync<object?>(config =>
        {
            var card = config.CardLibraries.FirstOrDefault(c =>
                string.Equals(c.ContentId, contentId, StringComparison.Ordinal));
            if (card is not null)
            {
                apply(card);
            }

            return null;
        }, cancellationToken);
    }

    /// <summary>The content ids of the removable libraries attached right now.</summary>
    /// <remarks>
    ///     What the library badge needs beside the card model, for a reading before the first sync:
    ///     a sync also refreshes the reading, but the badge must not wait for one.
    /// </remarks>
    /// <returns>A new set of content IDs found on currently readable removable libraries.</returns>
    internal static IReadOnlySet<string> PresentCardContentIds()
    {
        // Only the markers: the ids need neither the app manifests nor the volume's size and label.
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var letter in ExternalVolumeLetters())
        {
            try
            {
                if (SteamLibraryMarker.TryRead($@"{letter}:\{SteamLibraryVdf.CardFolderName}", out var contentId,
                        out _)
                    && !string.IsNullOrEmpty(contentId))
                {
                    present.Add(contentId);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($@"Library tabs: could not read {letter}:\: {ex.Message}");
            }
        }

        return present;
    }

    /// <summary>
    ///     Scans every ready external volume for a <c>&lt;X&gt;:\SteamLibrary</c> marker and
    ///     reads its identity, label and installed app ids. The primary Steam install
    ///     has no such subfolder marker, so it is naturally excluded.
    /// </summary>
    /// <remarks>
    ///     Run when a sync, the card manager or a rename needs the answer, never on a timer. It is not
    ///     answered from a snapshot kept since the last volume notification: media slipped into a
    ///     reader whose volume already exists raises none, so such a snapshot would miss the card that
    ///     was just inserted.
    /// </remarks>
    /// <returns>The cards found mounted now.</returns>
    internal static List<DiscoveredCard> ScanLibraries()
    {
        var found = new List<DiscoveredCard>();
        foreach (var letter in ExternalVolumeLetters())
        {
            try
            {
                var root = $@"{letter}:\{SteamLibraryVdf.CardFolderName}";
                if (!SteamLibraryMarker.TryRead(root, out var contentId, out var label)
                    || string.IsNullOrEmpty(contentId))
                {
                    continue;
                }

                var name = ResolveName(label, letter);
                var appIds = ReadAcfAppIds(Path.Combine(root, "steamapps"));
                var (capacity, free) = ReadVolumeSpace(letter);
                found.Add(new DiscoveredCard(contentId, name, appIds, label, capacity, free));
            }
            catch (Exception ex)
            {
                Log.Warn($@"Library tabs: could not read {letter}:\: {ex.Message}");
            }
        }

        return found;
    }

    /// <summary>
    ///     Reads a mounted library volume's size and free space, the figures the card manager shows and
    ///     remembers for the card while it is ejected. Zero when Windows could not answer, which the
    ///     merge treats as "no reading" rather than a full card.
    /// </summary>
    private static (long CapacityBytes, long FreeBytes) ReadVolumeSpace(char letter)
    {
        try
        {
            var drive = new DriveInfo($@"{letter}:\");
            return drive.IsReady ? (drive.TotalSize, drive.AvailableFreeSpace) : (0, 0);
        }
        catch (Exception ex)
        {
            Log.Debug($@"Library tabs: no size for {letter}:\: {ex.Message}");
            return (0, 0);
        }
    }

    /// <summary>
    ///     The letters of every ready volume on external storage, from the same <see cref="StorageInventory" />
    ///     read the eject list and the card volume monitor project: a disk volume whose disk is hot-pluggable
    ///     or holds removable media, and is not one Windows or WSGM runs from.
    /// </summary>
    /// <remarks>
    ///     Query access only: GENERIC_READ on <c>\\.\PhysicalDriveN</c> requires elevation and WSGM is
    ///     asInvoker, so a read handle would be invalid for every disk in a desktop-launched process and
    ///     no card would ever be discovered. <see cref="RemovableDriveManager.ClassifyDisk" /> opens the
    ///     disk that way.
    /// </remarks>
    private static List<char> ExternalVolumeLetters()
    {
        return [.. StorageInventory.Read().ReadyExternalVolumes().Select(volume => volume.Letter)];
    }

    /// <summary>
    ///     Picks a name for a card nothing has tracked yet: its marker label,
    ///     else the volume label, else a drive-letter fallback. Every candidate here is
    ///     read from the media itself. <c>config\libraryfolders.vdf</c> is deliberately
    ///     not consulted — its <c>label</c> belongs to a PATH registration, and a card
    ///     reader hands every card the same path, so Steam carries the previous card's
    ///     label onto the new card's content id (see <c>docs\sd-cards.md</c>).
    /// </summary>
    private static string ResolveName(string markerLabel, char letter)
    {
        if (!string.IsNullOrWhiteSpace(markerLabel))
        {
            return markerLabel.Trim();
        }

        if (NativeStorage.TryGetVolumeInformation($@"{letter}:\", out var volumeLabel, out _)
            && !string.IsNullOrWhiteSpace(volumeLabel)
            && !string.Equals(volumeLabel, SdFormatManager.DefaultLabel, StringComparison.OrdinalIgnoreCase))
        {
            return volumeLabel.Trim();
        }

        return $"Library ({letter}:)";
    }

    /// <summary>
    ///     Reads app ids from <c>appmanifest_&lt;appid&gt;.acf</c> file names — the
    ///     id is in the name, so no VDF parsing is needed for membership.
    /// </summary>
    private static List<long> ReadAcfAppIds(string steamAppsDir)
    {
        var ids = new List<long>();
        if (!Directory.Exists(steamAppsDir))
        {
            return ids;
        }

        foreach (var file in Directory.EnumerateFiles(steamAppsDir, "appmanifest_*.acf"))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            var idText = stem["appmanifest_".Length..];
            if (long.TryParse(idText, out var id) && id > 0 && id != 228980)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    ///     Upserts the scan into the persisted card DB: a new card is added
    ///     (enabled), a known one has its name and app ids refreshed from the media.
    ///     Cards not currently discovered are left untouched (remembered while ejected).
    /// </summary>
    /// <remarks>
    ///     The name follows the card's OWN marker label and nothing else. Steam's
    ///     <c>config\libraryfolders.vdf</c> label was the previous source and is not a
    ///     per-card value: it belongs to the registration at a PATH, and a card reader
    ///     gives every card the same path, so swapping cards left the new card's content
    ///     id carrying the previous card's label — which this merge then adopted, renaming
    ///     one card to the other (device-observed, 2026-09-05; see
    ///     <c>docs\sd-cards.md</c>). A WSGM-side rename writes the marker, so following
    ///     the marker still reflects a rename made here, and there is nothing left for a
    ///     two-way sync to reconcile.
    /// </remarks>
    /// <param name="config">The configuration to upsert into.</param>
    /// <param name="discovered">Cards found mounted by this scan.</param>
    internal static void MergeDiscovery(AppConfig config, List<DiscoveredCard> discovered)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(discovered);
        var db = config.CardLibraries;
        var presentIds = discovered.Select(static card => card.ContentId)
            .ToHashSet(StringComparer.Ordinal);
        config.ForgottenInsertedCardIds.RemoveAll(id => !presentIds.Contains(id));
        foreach (var card in discovered)
        {
            if (config.ForgottenInsertedCardIds.Contains(card.ContentId, StringComparer.Ordinal))
            {
                continue;
            }

            var existing = db.FirstOrDefault(c => string.Equals(c.ContentId, card.ContentId, StringComparison.Ordinal));
            if (existing is null)
            {
                existing = new CardLibraryConfig { ContentId = card.ContentId, Enabled = true };
                db.Add(existing);
            }

            // A labelled marker is the card's name. Only a card that carries no label
            // of its own falls back to the scan's guess, and only while nothing has
            // named it yet — otherwise an unlabelled card would lose a rename made
            // here every time the reader handed it a different drive letter.
            var name = string.IsNullOrWhiteSpace(card.MarkerLabel)
                ? existing.Name
                : card.MarkerLabel;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = card.Name;
            }

            if (!string.Equals(existing.Name, name, StringComparison.Ordinal))
            {
                // Change, not Info: one sync merges twice (once into the snapshot it
                // builds tabs from, once into the freshly locked config it saves), so
                // an unconditional line would report every rename twice.
                Log.Change($"card-name-{card.ContentId}",
                    $"Card {card.ContentId}: name '{existing.Name}' -> '{name}' "
                    + "(from the card's own marker).");
                existing.Name = name;
            }

            existing.AppIds = card.AppIds;
            // The last reading survives ejection: the manager keeps showing how full the card was.
            if (card.CapacityBytes > 0)
            {
                existing.CapacityBytes = card.CapacityBytes;
                existing.FreeBytes = card.FreeBytes;
            }
        }
    }
}
