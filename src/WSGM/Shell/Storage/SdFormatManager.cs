using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>
///     Formats removable disks as single-volume NTFS Steam libraries.
///     UI-thread owner; disk work runs on workers. Destructive stages recheck physical-disk identity,
///     including letterless media, before writing. Formatting erases the disk and has no rollback.
///     <see cref="Diskpart" /> runs the disk stages, <see cref="Libraries" /> puts the Steam library on
///     the formatted volume and also registers existing folders, and <see cref="Progress" /> serializes
///     both kinds of run and publishes their stage and outcome.
/// </summary>
public sealed class SdFormatManager : ObservableObject
{
    /// <summary>The volume/library label used when the user names nothing.</summary>
    internal const string DefaultLabel = "Games";

    /// <summary>
    ///     The longest NTFS volume label, because the format script passes the name to
    ///     <c>format fs=ntfs label=</c>.
    /// </summary>
    internal const int MaximumLabelLength = 32;

    /// <summary>
    ///     How many times the format run is attempted; each failure waits
    ///     <see cref="FormatRetryDelayMs" /> before the next try.
    /// </summary>
    private const int FormatAttempts = 3;

    /// <summary>The pause between format-run attempts.</summary>
    private const int FormatRetryDelayMs = 2_000;

    /// <summary>
    ///     The refusal when a re-verification BETWEEN the destructive runs finds a
    ///     different card: the run stops where it is rather than quick-formatting media the
    ///     user never picked, or pinning the old card's drive letter onto it.
    /// </summary>
    private const string CardChangedMidRunMessage =
        "The card changed while it was being formatted, so WSGM stopped. Reinsert the card "
        + "you want to format and start again.";

    /// <summary>
    ///     How many 500 ms polls <see cref="Diskpart.WaitForLetterAsync" /> spends when it
    ///     is only checking whether automount already mounted the formatted volume,
    ///     before diskpart is asked to assign the letter.
    /// </summary>
    private const int LetterProbeAttempts = 6;

    /// <summary>Session cancellation used by cancellable format stages; it cannot undo disk writes.</summary>
    private readonly CancellationToken _lifetime;

    private readonly SdFormatOperations _operations;

    /// <summary>
    ///     Reads the candidate disks on a worker thread, projecting the storage read it is handed or a fresh
    ///     one when it is handed none.
    /// </summary>
    private readonly Func<StorageInventory?, List<FormatTarget>> _readTargets;

    /// <summary>Borrowed configuration store used to persist library preferences.</summary>
    private readonly ConfigStore _store;

    private int _refreshing;

    /// <summary>Creates the session's format manager over the real disks.</summary>
    /// <param name="store">Borrowed configuration store used to persist library preferences.</param>
    /// <param name="lifetime">
    ///     Session cancellation token used by cancellable format stages; cancellation cannot undo disk
    ///     writes.
    /// </param>
    /// <param name="steam">Borrowed Steam client for live library registration, or null to use the offline path.</param>
    public SdFormatManager(ConfigStore store, CancellationToken lifetime = default, SteamClient? steam = null)
        : this(store, CreateOperations(store, steam),
            inventory => ReadTargets(inventory ?? StorageInventory.Read()), lifetime)
    {
    }

    /// <summary>Creates a manager that enumerates through the given reader instead of the disks.</summary>
    /// <param name="store">The process-owned configuration persistence.</param>
    /// <param name="readTargets">Reads the candidate list on a worker thread.</param>
    /// <param name="lifetime">Cancels format runs when the session ends.</param>
    internal SdFormatManager(ConfigStore store, Func<List<FormatTarget>> readTargets,
        CancellationToken lifetime = default)
        : this(store, CreateOperations(store, null), Project(readTargets), lifetime)
    {
    }

    /// <summary>Creates a manager over the given external operations and candidate reader.</summary>
    /// <param name="store">The process-owned configuration persistence.</param>
    /// <param name="operations">The disk, Steam and log operations the run uses.</param>
    /// <param name="readTargets">Reads the candidate list on a worker thread.</param>
    /// <param name="lifetime">Cancels format runs when the session ends.</param>
    internal SdFormatManager(ConfigStore store, SdFormatOperations operations, Func<List<FormatTarget>> readTargets,
        CancellationToken lifetime = default)
        : this(store, operations ?? throw new ArgumentNullException(nameof(operations)), Project(readTargets),
            lifetime)
    {
    }

    private SdFormatManager(ConfigStore store, SdFormatOperations operations,
        Func<StorageInventory?, List<FormatTarget>> readTargets, CancellationToken lifetime)
    {
        _store = store;
        _operations = operations;
        _readTargets = readTargets;
        _lifetime = lifetime;
        Progress = new StorageRunProgress(operations.WriteLog);
        Libraries = new SteamLibraryProvisioner(operations, Progress);
    }

    /// <summary>Gets the candidate drives, one row per physical disk.</summary>
    public ObservableCollection<FormatTargetEntry> Targets { get; } = [];

    /// <summary>Gets whether any formattable drive is present.</summary>
    public bool HasTargets
    {
        get;
        private set => SetFieldIfChanged(ref field, value, nameof(HasTargets));
    }

    /// <summary>Gets the storage run the format and Add Library flows share: busy state, stage and outcome.</summary>
    public StorageRunProgress Progress { get; }

    /// <summary>Gets the owner that puts Steam libraries on formatted cards and on folders the user picks.</summary>
    internal SteamLibraryProvisioner Libraries { get; }

    /// <summary>The session's card manifest watcher, stood down for the whole run. Set by the session.</summary>
    internal CardAcfWatcher? CardWatcher { get; set; }

    /// <summary>The most recently started format run, completed when none has started.</summary>
    internal Task Completion { get; private set; } = Task.CompletedTask;

    private static Func<StorageInventory?, List<FormatTarget>> Project(Func<List<FormatTarget>> readTargets)
    {
        ArgumentNullException.ThrowIfNull(readTargets);
        return _ => readTargets();
    }

    private static SdFormatOperations CreateOperations(ConfigStore store, SteamClient? client)
    {
        return new SdFormatOperations
        {
            IsElevated = ElevationCheck.IsCurrentProcessElevated,
            ReadDiskIdentity = ReadDiskIdentity,
            RunDiskpart = (script, token) => Diskpart.RunAsync(store, script, token),
            WaitForVolume = (disk, token) => Task.Run(() => Diskpart.WaitForVolumeAsync(disk, token), token),
            WaitForLetter = (disk, attempts, token) =>
                Task.Run(() => Diskpart.WaitForLetterAsync(disk, attempts, token), token),
            ReadVolume = Diskpart.ReadVolume,
            LettersOnDisk = Diskpart.LettersOnDisk,
            LetterRoot = letter => $@"{letter}:\",
            ReadSteam = () => (Steam.IsRunning, Steam.ExePath),
            ReadLibraryFolders = () =>
            {
                Steam.TryReadLibraryFolders(out var path, out var text);
                return (path, text);
            },
            ReadMarker = path =>
            {
                SteamLibraryMarker.TryRead(path, out var id, out var label);
                return (id, label);
            },
            RemoveLibrary = (id, text, token) => client is null
                ? Task.FromResult(new SteamLibraryRemoveResult(SteamLibraryRemoveStatus.NotSent,
                    "No Steam client in this window."))
                : SteamLibraryFolders.RemoveLibraryByContentIdAsync(client, id, text, token),
            AddLibrary = (path, label, replace) => client is null
                ? new SteamLibraryAddResult(SteamLibraryAddStatus.NotSent, "No Steam client in this window.")
                : client.InstallFolders.AddAsync(path, label, replace).GetAwaiter().GetResult(),
            Retrim = Diskpart.RetrimAsync,
            BroadcastVolumeArrival = NativeStorage.BroadcastVolumeArrival,
            Delay = (milliseconds, token) => Task.Delay(milliseconds, token),
            WriteLog = (level, message, exception) =>
            {
                if (exception is not null)
                {
                    Log.Error(message, exception);
                }
                else if (level == LogLevel.Warn)
                {
                    Log.Warn(message);
                }
                else
                {
                    Log.Info(message);
                }
            }
        };
    }

    /// <summary>
    ///     Sanitizes a user-typed name into a value safe as both an NTFS
    ///     volume label and a Steam library label: trims, keeps ASCII letters, digits,
    ///     space, dash and underscore (so the diskpart script stays plain ASCII and no
    ///     quote can break out of the label token), caps at <see cref="MaximumLabelLength" />
    ///     characters, and falls back to <see cref="DefaultLabel" /> when nothing usable remains.
    /// </summary>
    /// <param name="name">The raw name, or null.</param>
    /// <returns>A nonempty ASCII label no longer than MaximumLabelLength; DefaultLabel when no usable text remains.</returns>
    internal static string SanitizeLabel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return DefaultLabel;
        }

        var kept = new string(name.Trim()
                .Where(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'
                    or ' ' or '-' or '_')
                .Take(MaximumLabelLength)
                .ToArray())
            .Trim();
        return kept.Length == 0 ? DefaultLabel : kept;
    }

    // ---- enumeration ----

    /// <summary>
    ///     Re-enumerates the candidate disks off-thread and reconciles the
    ///     bound list. Called when the flow opens and from its refresh button.
    /// </summary>
    public void Refresh()
    {
        Refresh(null);
    }

    /// <summary>
    ///     Reconciles the bound list from a storage read someone else already made, such as the one behind
    ///     the eject list's latest change, so the same change is not read twice. Null reads fresh.
    /// </summary>
    /// <param name="inventory">The storage read to project, or null for a fresh one.</param>
    internal void Refresh(StorageInventory? inventory)
    {
        if (Progress.Busy)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var targets = _readTargets(inventory);
                Dispatcher.UIThread.Post(() => Apply(targets));
            }
            catch (Exception ex)
            {
                Warn($"Format: enumeration failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        });
    }

    /// <summary>Projects one storage read into the candidate list: every external disk with an interface.</summary>
    /// <param name="inventory">The storage read to project.</param>
    /// <returns>One target per disk in interface order, with its letters; a letterless Deck card has none.</returns>
    internal static List<FormatTarget> ReadTargets(StorageInventory inventory)
    {
        return
        [
            .. inventory.ExternalDisks
                .Where(disk => disk.InterfacePath.Length > 0)
                .Select(disk => new FormatTarget(
                    disk.InstanceId.Length > 0 ? disk.InstanceId : $"disk:{disk.Number}",
                    disk.Number, disk.Product, disk.CapacityBytes, disk.BusType,
                    [
                        .. inventory.Volumes.Where(volume => volume.Disk == disk.Number)
                            .Select(volume => volume.Letter).OrderBy(letter => letter)
                    ],
                    disk.HasLinuxPartitions))
        ];
    }

    /// <summary>The row's detail line: capacity — bus kind — letters — hint.</summary>
    /// <param name="target">The enumerated disk.</param>
    /// <returns>Nonempty capacity, bus, drive-letter and Linux-partition details joined for display.</returns>
    internal static string DescribeTarget(FormatTarget target)
    {
        var parts = new List<string>
        {
            RemovableDriveManager.FormatSize(target.SizeBytes),
            DescribeBus(target.BusType)
        };
        if (target.Letters.Count > 0)
        {
            parts.Add(RemovableDriveManager.FormatLetters([.. target.Letters]));
        }

        if (target.HasLinuxPartitions)
        {
            parts.Add("Linux partitions — looks like a Steam Deck card");
        }

        return string.Join(" — ", parts.Where(p => p.Length > 0));
    }

    /// <summary>
    ///     Names the bus for the row and confirm views. USB stays generic:
    ///     a USB-bridged internal card reader and a stick both say USB, and the
    ///     product name is what tells them apart.
    /// </summary>
    /// <param name="busType">The STORAGE_BUS_TYPE value.</param>
    /// <returns>SD card for SD/MMC, USB for USB, or empty for other and unknown bus values.</returns>
    internal static string DescribeBus(int busType)
    {
        return busType switch
        {
            NativeStorage.BusTypeSd or NativeStorage.BusTypeMmc => "SD card",
            NativeStorage.BusTypeUsb => "USB",
            _ => ""
        };
    }

    /// <summary>
    ///     Merges a fresh target list into the bound collection without
    ///     replacing surviving rows.
    /// </summary>
    /// <param name="fresh">
    ///     Current target snapshot to reconcile on the UI thread; matching identities retain rows unless their
    ///     disk number changed.
    /// </param>
    internal void Apply(List<FormatTarget> fresh)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in fresh)
        {
            seen.Add(target.Id);
            var row = FindTarget(target.Id);
            if (row is null || row.DiskNumber != target.DiskNumber)
            {
                if (row is not null)
                {
                    // Same device, new disk number (re-enumerated by Windows):
                    // replace the row — the number is part of the safety check.
                    Targets.Remove(row);
                }

                row = new FormatTargetEntry(target.Id, target.DiskNumber);
                Targets.Add(row);
                Info($"Format: candidate {target.Name} disk={target.DiskNumber} "
                     + $"bus={target.BusType} size={target.SizeBytes} "
                     + $"letters={string.Concat(target.Letters)} linux={target.HasLinuxPartitions}");
            }

            row.Name = target.Name;
            row.SizeBytes = target.SizeBytes;
            row.BusType = target.BusType;
            // The card's current letter, pinned so the format reassigns exactly
            // it — a card reader must keep its letter across reformats and swaps
            // (emulator/library paths depend on it). '\0' only for a card with no
            // letter at all (raw / ext4 Deck card being set up the first time).
            row.PreferredLetter = target.Letters.Count > 0 ? target.Letters[0] : '\0';
            row.Detail = DescribeTarget(target);
        }

        for (var i = Targets.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Targets[i].Id))
            {
                Targets.RemoveAt(i);
            }
        }

        HasTargets = Targets.Count > 0;
    }

    private FormatTargetEntry? FindTarget(string id)
    {
        return Targets.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
    }

    // ---- the format run ----

    /// <summary>
    ///     Erases and formats one target and puts a Steam library on it.
    ///     Serialized; progress lands in <see cref="Progress" />; the terminal
    ///     message also fires <see cref="StorageRunProgress.Finished" />.
    /// </summary>
    /// <param name="entry">The target to format.</param>
    /// <param name="name">The user-chosen volume/library name, or null for the default.</param>
    /// <returns>The run, also published as <see cref="Completion" />.</returns>
    public Task FormatAsync(FormatTargetEntry entry, string? name = null)
    {
        var target = new FormatRunTarget(entry.Id, entry.DiskNumber, entry.Name, entry.SizeBytes,
            entry.BusType, entry.PreferredLetter);
        if (Progress.Busy || _lifetime.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        Completion = FormatCoreAsync(target, name);
        return Completion;
    }

    private async Task FormatCoreAsync(FormatRunTarget target, string? name)
    {
        var label = SanitizeLabel(name);
        // Declared out here so the catch can compensate: once the Steam registration is
        // removed, ANY later failure must try to put it back, not just a non-zero
        // diskpart exit. The restore no-ops when the card's marker is gone (the erase
        // did happen), so it is safe on every path. Only a registration that was
        // ACTUALLY removed lands here — restoring one the user had deleted themselves
        // would ADD a library the format never took away.
        string? removedContentId = null;
        var removedLabel = "";
        await Progress.EnterAsync(_lifetime);
        try
        {
            Progress.StatusText = $"Erasing {target.Name}...";
            Info($"Format: starting for {target.Name} (disk {target.DiskNumber}, "
                 + $"{target.SizeBytes} bytes, bus {target.BusType}).");

            // Only a definite "not elevated" blocks; unknown proceeds and lets
            // diskpart's own error surface (shell mode is elevated in practice).
            if (_operations.IsElevated() == false)
            {
                Progress.Finish("Formatting needs administrator rights, which WSGM does not have "
                       + "right now.", false);
                return;
            }

            var verify = await Task.Run(() => VerifyTarget(target));
            if (verify is not null)
            {
                Progress.Finish(verify, false);
                return;
            }

            // Diskpart locks the volume; the ACF watcher's directory handle on the
            // card would make that lock fail. Stand it down until the run ends.
            using var cardWatch = CardWatcher?.Suspend();

            // A card reader reuses its drive letter. If this is a WSGM-formatted
            // card, first remove its existing Steam library by the marker's stable
            // content id; otherwise the live client keeps the old app list as
            // ghost entries after diskpart has erased the manifests. The removal
            // also reports the marker's id for the post-erase card retirement, so
            // the marker is read once.
            (var failure, removedContentId, removedLabel, var retiredContentId) =
                await Task.Run(() => RemoveExistingLibraryAsync(target));
            if (failure is not null)
            {
                Progress.Finish(failure, false);
                return;
            }

            var keepLetter = target.PreferredLetter;
            _lifetime.ThrowIfCancellationRequested();

            // Re-verify on fresh handles right before the only irreversible verb —
            // the removal above can outlast a card swap (Shell\AGENTS.md).
            var beforeErase = await Task.Run(() => ReadTargetIdentity(target));
            LogReverification(target, beforeErase, "clean/partition");
            if (beforeErase.Identity == FormatTargetIdentity.Changed)
            {
                // Nothing has been erased yet, so the registration this run removed
                // belongs to a card that is still intact: put it back, exactly as the
                // diskpart-failure branch below does.
                await Task.Run(() => RestoreRemovedLibraryIfCardSurvived(
                    target, removedContentId, removedLabel));
                Progress.Finish("The drive changed since it was listed — refresh and pick it again.",
                    false);
                return;
            }

            _lifetime.ThrowIfCancellationRequested();
            var partition = await _operations.RunDiskpart(
                Diskpart.BuildPartitionScript(target.DiskNumber), _lifetime);
            if (partition.Outcome == ConsoleToolRunOutcome.Unknown)
            {
                await Task.Run(() => RestoreRemovedLibraryIfCardSurvived(target, removedContentId, removedLabel));
                if (await Task.Run(() => FindExistingMarker(target)) is null && !string.IsNullOrEmpty(retiredContentId))
                {
                    _store.Update(config => config.CardLibraries.RemoveAll(card =>
                        string.Equals(card.ContentId, retiredContentId, StringComparison.Ordinal)) > 0);
                }

                Progress.Finish("The card may have been erased. Reinsert it and check before using it.", false);
                return;
            }

            if (partition.Outcome != ConsoleToolRunOutcome.Succeeded)
            {
                await Task.Run(() => RestoreRemovedLibraryIfCardSurvived(
                    target, removedContentId, removedLabel));
                Warn($"Format: diskpart clean/partition failed (exit {partition.ExitCode}). "
                     + $"Output:\n{partition.Output}");
                Progress.Finish("Formatting failed — Windows could not rebuild the drive. "
                       + "Reinsert the card and try again.", false);
                return;
            }

            Info($"Format: disk {target.DiskNumber} erased and repartitioned.");

            // The erase destroyed the old library, so there is nothing left to
            // compensate: a later failure must not splice its identity back in beside
            // the fresh marker CreateSteamLibrary is about to write.
            removedContentId = null;
            removedLabel = "";

            // The old card provably no longer exists from here on, so retire it now:
            // a later failure (unmounted volume, lost drive letter) must not leave it
            // in the card database forever with an identity nothing can rediscover.
            if (!string.IsNullOrEmpty(retiredContentId))
            {
                _store.Update(config => config.CardLibraries.RemoveAll(card =>
                    string.Equals(card.ContentId, retiredContentId, StringComparison.Ordinal)) > 0);
            }

            // Give the volume manager time to surface the new partition's volume
            // before diskpart is asked to format it (see Diskpart.BuildFormatScript);
            // a wait that runs out is logged and the format still attempted, since
            // diskpart itself may see the volume by then.
            Progress.StatusText = $"Formatting {target.Name}...";
            var volumeWaitMs = await _operations.WaitForVolume(target.DiskNumber, _lifetime);
            if (volumeWaitMs < 0)
            {
                Warn($"Format: no volume appeared on disk {target.DiskNumber} within "
                     + $"{Diskpart.VolumeWaitMs / 1000} s; attempting the format anyway.");
            }
            else
            {
                Info($"Format: volume on disk {target.DiskNumber} appeared after "
                     + $"{volumeWaitMs} ms.");
            }

            var formatScript = Diskpart.BuildFormatScript(target.DiskNumber, label);
            var (formatExit, formatOutput) = (-1, "");
            for (var attempt = 1; attempt <= FormatAttempts; attempt++)
            {
                if (attempt > 1)
                {
                    Warn($"Format: diskpart format attempt {attempt - 1} of {FormatAttempts} "
                         + $"failed (exit {formatExit}); retrying in {FormatRetryDelayMs} ms. "
                         + $"Output:\n{formatOutput}");
                    await _operations.Delay(FormatRetryDelayMs, _lifetime);
                }

                // Re-verify per attempt: each is a fresh diskpart resolving
                // `select disk N` after waits long enough for a swap (Shell\AGENTS.md).
                _lifetime.ThrowIfCancellationRequested();
                var beforeFormat = await Task.Run(() => ReadTargetIdentity(target));
                LogReverification(target, beforeFormat, "format");
                if (beforeFormat.Identity == FormatTargetIdentity.Changed)
                {
                    // No compensation on this path: the erase already destroyed the old
                    // library, which is why removedContentId/removedLabel were cleared.
                    Progress.Finish(CardChangedMidRunMessage, false);
                    return;
                }

                _lifetime.ThrowIfCancellationRequested();
                var formatted = await _operations.RunDiskpart(formatScript, _lifetime);
                formatExit = formatted.ExitCode ?? -1;
                formatOutput = formatted.Output;
                if (formatted.Outcome == ConsoleToolRunOutcome.Unknown)
                {
                    Progress.Finish("The card's format result is unknown. Reinsert it and check before using it.", false);
                    return;
                }

                if (formatted.Outcome == ConsoleToolRunOutcome.Succeeded)
                {
                    break;
                }
            }

            if (formatExit != 0)
            {
                Warn($"Format: diskpart format failed after {FormatAttempts} attempts "
                     + $"(exit {formatExit}). Output:\n{formatOutput}");
                Progress.Finish("Formatting failed — Windows could not format the new drive. "
                       + "Reinsert the card and try again.", false);
                return;
            }

            Info($"Format: diskpart formatted disk {target.DiskNumber}.");

            // Automount normally hands the new volume its letter the moment it
            // arrives — usually the card's own, freed by the erase. Only when the
            // card is not sitting on that letter now does diskpart assign it.
            Progress.StatusText = "Waiting for the new drive...";
            var letter = await _operations.WaitForLetter(target.DiskNumber, LetterProbeAttempts, _lifetime);
            if (letter is null || (keepLetter is >= 'A' and <= 'Z' && letter.Value != keepLetter))
            {
                Info($"Format: disk {target.DiskNumber} is on "
                     + (letter is null ? "no letter" : $"{letter}:")
                     + " after the format; assigning "
                     + (keepLetter is >= 'A' and <= 'Z' ? $"{keepLetter}:." : "a letter."));

                // Last re-verify before the assign pins the old card's letter onto
                // whatever is in the reader (Shell\AGENTS.md). No compensation here either.
                _lifetime.ThrowIfCancellationRequested();
                var beforeAssign = await Task.Run(() => ReadTargetIdentity(target));
                LogReverification(target, beforeAssign, "assign");
                if (beforeAssign.Identity == FormatTargetIdentity.Changed)
                {
                    Progress.Finish(CardChangedMidRunMessage, false);
                    return;
                }

                _lifetime.ThrowIfCancellationRequested();
                var assigned = await _operations.RunDiskpart(
                    Diskpart.BuildAssignScript(target.DiskNumber, keepLetter), _lifetime);
                if (assigned.Outcome != ConsoleToolRunOutcome.Succeeded)
                {
                    Warn($"Format: diskpart assign failed (exit {assigned.ExitCode}). "
                         + $"Output:\n{assigned.Output}");
                }

                letter = await _operations.WaitForLetter(target.DiskNumber, 30, _lifetime);
            }

            if (letter is null)
            {
                Progress.Finish("The drive was formatted, but Windows did not mount it. "
                       + "Reinsert the card.", false);
                return;
            }

            // The card reader must keep its letter. If diskpart could not put it
            // back (letter held by something else), stop rather than silently
            // leaving the card on a different one that would break every path
            // pointing at it (emulators, libraries).
            if (keepLetter is >= 'A' and <= 'Z' && letter.Value != keepLetter)
            {
                Warn($"Format: expected to keep letter {keepLetter}: but disk "
                     + $"{target.DiskNumber} mounted as {letter}:.");
                Progress.Finish($"Formatted, but Windows could not keep drive letter {keepLetter}:. "
                       + $"It is now {letter}: — free {keepLetter}: and reformat, or reassign "
                       + $"the letter in Disk Management.", false);
                return;
            }

            var volumeRoot = await Task.Run(() => VerifiedVolumeRoot(target, letter.Value));
            if (volumeRoot is null)
            {
                Progress.Finish(CardChangedMidRunMessage, false);
                return;
            }

            Info($"Format: disk {target.DiskNumber} mounted as {letter}: "
                 + $"(letter preserved={keepLetter is >= 'A' and <= 'Z'}).");

            // TRIM the fresh (near-empty) volume so the flash controller learns
            // almost the whole card is free — proven to restore SD write speed.
            // Best-effort: a reader that does not pass TRIM just logs and moves on.
            Progress.StatusText = "Optimizing the card...";
            _lifetime.ThrowIfCancellationRequested();
            await _operations.Retrim(letter.Value, _lifetime);

            Progress.StatusText = "Creating Steam library...";
            _lifetime.ThrowIfCancellationRequested();
            var summary = await Task.Run(() => Libraries.CreateSteamLibrary(letter.Value, volumeRoot, target.SizeBytes, label));
            Progress.Finish(summary, true);
        }
        catch (Exception ex)
        {
            Error("Format: run failed.", ex);
            try
            {
                await Task.Run(() => RestoreRemovedLibraryIfCardSurvived(
                    target, removedContentId, removedLabel));
            }
            catch (Exception restoreEx)
            {
                Error("Format: could not restore the removed library.", restoreEx);
            }

            Progress.Finish(_lifetime.IsCancellationRequested
                ? "Formatting stopped during shutdown. Reinsert the card and check before using it."
                : "Formatting failed unexpectedly — see the log.", false);
        }
        finally
        {
            Progress.Exit();
        }
    }

    /// <summary>
    ///     Verifies the target on fresh handles before the run starts: the
    ///     disk number must still belong to a device with the same size and bus,
    ///     still hot-pluggable, still not a system disk. Returns null when safe, else
    ///     the refusal message. Deliberately STRICTER than the mid-run re-checks: at
    ///     this point nothing has been erased, so an unopenable or unreadable disk
    ///     aborts up front — the Unreadable-continues tolerance belongs only to the
    ///     re-verifications after `clean` (see <see cref="CompareIdentity" />).
    /// </summary>
    private string? VerifyTarget(FormatRunTarget entry)
    {
        var snapshot = ReadTargetIdentity(entry);
        if (snapshot.SystemDisk)
        {
            return "This drive hosts Windows or WSGM and cannot be formatted.";
        }

        if (!snapshot.HandleOpened)
        {
            return "The drive is no longer reachable. Reinsert it and try again.";
        }

        if (!snapshot.Removable)
        {
            return "The drive no longer reports as removable — not formatting it.";
        }

        if (snapshot.SizeBytes == entry.SizeBytes && snapshot.BusType == entry.BusType)
        {
            return null;
        }

        Warn($"Format: disk {entry.DiskNumber} changed identity "
             + $"(size {entry.SizeBytes}->{snapshot.SizeBytes}, "
             + $"bus {entry.BusType}->{snapshot.BusType}).");
        return "The drive changed since it was listed — refresh and pick it again.";
    }

    /// <summary>
    ///     Decides whether the disk behind the target's number is still the card
    ///     the user picked. Pure, so the ordering below is testable. Identity predicates
    ///     only, a query failure is never a mismatch, and a same-capacity swap stays
    ///     invisible — the device evidence is in <c>Shell\AGENTS.md</c>.
    /// </summary>
    /// <param name="opened">The disk handle opened and answered its hotplug query.</param>
    /// <param name="systemDisk">The disk number now hosts Windows or WSGM.</param>
    /// <param name="removable">It still classifies as hot-pluggable/removable media.</param>
    /// <param name="size">The disk length just read; 0 means the query failed.</param>
    /// <param name="busType">The STORAGE_BUS_TYPE just read; -1 means the query failed.</param>
    /// <param name="expectedSize">The size the run started from.</param>
    /// <param name="expectedBusType">The bus type the run started from.</param>
    /// <returns>
    ///     Changed for a guarded disk or conflicting known identity; Unreadable for failed handle/size reads; otherwise
    ///     Same, which cannot detect equal-capacity swaps.
    /// </returns>
    internal static FormatTargetIdentity CompareIdentity(
        bool opened, bool systemDisk, bool removable, long size, int busType,
        long expectedSize, int expectedBusType)
    {
        if (systemDisk)
        {
            // First and unconditional: a disk number that now points at system
            // storage must abort even when nothing else about it could be read.
            return FormatTargetIdentity.Changed;
        }

        if (!opened || size <= 0)
        {
            return FormatTargetIdentity.Unreadable;
        }

        // The sentinel tolerance is SYMMETRIC: size 0 / bus -1 are query-failure
        // values on BOTH sides (the enumeration baseline can carry them too), and
        // a fact we never had cannot contradict one we just read (Shell\AGENTS.md).
        return !removable
               || (expectedSize > 0 && size != expectedSize)
               || (busType >= 0 && expectedBusType >= 0 && busType != expectedBusType)
            ? FormatTargetIdentity.Changed
            : FormatTargetIdentity.Same;
    }

    /// <summary>
    ///     Re-reads the target's identity on FRESH handles immediately before one
    ///     destructive diskpart run. Disk handle only — never a volume handle: an open
    ///     volume handle is exactly what makes diskpart's own volume lock fail, which is
    ///     why <see cref="CardAcfWatcher.Suspend" /> is called before the erase at all.
    ///     Identity predicates only (see <see cref="CompareIdentity" />); nothing here may
    ///     read the filesystem, because `clean` legitimately erases it before the second
    ///     and third runs. Worker thread.
    /// </summary>
    /// <param name="entry">The target being formatted.</param>
    private TargetIdentitySnapshot ReadTargetIdentity(FormatRunTarget entry)
    {
        var snapshot = _operations.ReadDiskIdentity(entry.DiskNumber);
        return new TargetIdentitySnapshot(
            CompareIdentity(snapshot.HandleOpened, snapshot.SystemDisk, snapshot.Removable,
                snapshot.SizeBytes, snapshot.BusType, entry.SizeBytes, entry.BusType),
            snapshot.SystemDisk, snapshot.HandleOpened, snapshot.Removable, snapshot.SizeBytes, snapshot.BusType);
    }

    private static DiskIdentitySnapshot ReadDiskIdentity(int diskNumber)
    {
        var systemDisk = RemovableDriveManager.ResolveSystemDisks().Contains(diskNumber);
        using var handle = NativeStorage.OpenDiskForRead(diskNumber);
        var handleOpened = !handle.IsInvalid;
        if (!handleOpened
            || !NativeStorage.TryGetHotplugInfo(handle, out var media, out var hotplug))
        {
            return new DiskIdentitySnapshot(systemDisk, handleOpened, false, 0, -1);
        }

        var removable = RemovableDriveManager.Classify(hotplug, media) is not null;
        var size = NativeStorage.GetDiskLength(handle);
        NativeStorage.TryGetDeviceDescriptor(handle, out var busType, out _);
        return new DiskIdentitySnapshot(systemDisk, handleOpened, removable, size, busType);
    }

    /// <summary>
    ///     Logs one re-verification verdict. A mismatch is an abort reason and
    ///     names every fact that differs; an unreadable disk does NOT stop the run but is
    ///     logged too, so a pasted log always says which of the two happened.
    /// </summary>
    /// <param name="entry">The target being formatted.</param>
    /// <param name="snapshot">What the re-verification saw.</param>
    /// <param name="stage">The diskpart run that was about to be issued.</param>
    private void LogReverification(
        FormatRunTarget entry, TargetIdentitySnapshot snapshot, string stage)
    {
        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
        switch (snapshot.Identity)
        {
            case FormatTargetIdentity.Changed:
                Warn($"Format: disk {entry.DiskNumber} is not the card that was picked — "
                     + $"aborting before the {stage} run (system disk {snapshot.SystemDisk}, "
                     + $"removable {snapshot.Removable}, size {entry.SizeBytes}->{snapshot.SizeBytes}, "
                     + $"bus {entry.BusType}->{snapshot.BusType}).");
                break;
            case FormatTargetIdentity.Unreadable:
                Info($"Format: disk {entry.DiskNumber} did not answer the identity re-check "
                     + $"before the {stage} run (size {snapshot.SizeBytes}, bus {snapshot.BusType}); "
                     + "continuing — a reader that is not ready yet is not a swapped card.");
                break;
        }
    }

    /// <summary>
    ///     Removes the existing WSGM library on a card before it is erased.
    ///     The card marker's content id selects the registration, so a fixed reader
    ///     drive letter cannot remove the library belonging to another card. A live
    ///     Steam is changed only through CEF; when Steam is closed its next-start
    ///     configuration is cleaned directly.
    /// </summary>
    /// <param name="entry">The re-verified card selected for formatting.</param>
    /// <returns>The refusal and what was actually removed.</returns>
    private async Task<LibraryRemoval> RemoveExistingLibraryAsync(FormatRunTarget entry)
    {
        if (entry.PreferredLetter is < 'A' or > 'Z')
        {
            return LibraryRemoval.Nothing();
        }

        var marker = FindExistingMarker(entry);
        if (marker is null)
        {
            return LibraryRemoval.Nothing();
        }

        string contentId;
        try
        {
            if (!SteamLibraryMarker.TryRead(Path.GetDirectoryName(marker)!, out var markerId, out _)
                || string.IsNullOrWhiteSpace(markerId))
            {
                Warn($"Format: existing marker at {marker} has no content id.");
                return LibraryRemoval.Refused(
                    "The existing Steam library marker has no content identity. "
                    + "Formatting was stopped to avoid leaving ghost games in Steam.");
            }

            contentId = markerId;
        }
        catch (Exception ex)
        {
            Warn($"Format: could not read existing marker {marker}: {ex.Message}");
            return LibraryRemoval.Refused(
                "Could not verify the existing Steam library on this card. "
                + "Close Steam and try again.");
        }

        if (_operations.ReadSteam().ExePath is null)
        {
            Info("Format: Steam is not installed; no registration needs removal.");
            return LibraryRemoval.Nothing(contentId);
        }

        var (configPath, configText) = _operations.ReadLibraryFolders();
        if (configPath is null || configText is null)
        {
            Info($"Format: Steam has no libraryfolders config; {contentId} is not registered.");
            return LibraryRemoval.Nothing(contentId);
        }

        // Identity gate — runs for BOTH the live and the closed-Steam path. Removal is
        // keyed on the content id (a reused reader letter means several cards legitimately
        // share one path), but the registration that id points at must still live on the
        // card being erased: a card carrying a copied marker would otherwise de-register
        // an internal library. Never resolve this by moving a card to another letter —
        // the reader's letter is fixed, because emulator configs store absolute paths.
        var registeredPath = SteamLibraryVdf.PathForContentId(configText, contentId);
        if (registeredPath is null)
        {
            Info($"Format: content id {contentId} is not registered with Steam; "
                 + "nothing to remove.");
            return LibraryRemoval.Nothing(contentId);
        }

        var cardPath = FullPathOrNull(Path.GetDirectoryName(marker));
        var registeredFullPath = FullPathOrNull(registeredPath);
        if (cardPath is null || registeredFullPath is null
                             || !string.Equals(registeredFullPath, cardPath, StringComparison.OrdinalIgnoreCase))
        {
            Warn($"Format: content id {contentId} is registered at "
                 + $"{registeredPath}, not at the card path {Path.GetDirectoryName(marker)}.");
            return LibraryRemoval.Refused(
                "The card marker does not match the Steam library on this drive. "
                + "Formatting was stopped to protect your other libraries.");
        }

        // Captured before the removal so a failed format can put the registration back
        // with the name the user gave it, not as an unnamed library.
        var registeredLabel = SteamLibraryVdf.LabelForContentId(configText, contentId) ?? "";

        if (_operations.ReadSteam().Running)
        {
            var pathMatches = SteamLibraryVdf.ValuesOf(configText, "path")
                .Count(path => string.Equals(Path.GetFullPath(path), cardPath,
                    StringComparison.OrdinalIgnoreCase));
            if (pathMatches > 1)
            {
                return LibraryRemoval.Refused(
                    "Several card libraries share this reader path. Close Steam and try again "
                    + "so WSGM can remove only this card's content identity.");
            }

            var result = await _operations.RemoveLibrary(contentId, configText, _lifetime).ConfigureAwait(false);
            if (result.Status == SteamLibraryRemoveStatus.Removed)
            {
                Info($"Format: removed existing live Steam library (content id {contentId}, "
                     + $"status {result.Status}).");
                return new LibraryRemoval(null, contentId, registeredLabel, contentId);
            }

            Warn($"Format: could not remove existing live library {contentId} "
                 + $"({result.Status}: {result.Detail ?? "no detail"}).");
            return LibraryRemoval.Refused(
                "Steam could not remove this card's existing library. "
                + "Close Steam completely and try again.");
        }

        // Reached only after the identity gate above proved this content id is registered
        // at the card's own path, so removing by content id cannot touch another library.
        if (!SteamLibraryVdf.TryRemoveContentId(configText, contentId, out var updated)
            || updated is null)
        {
            Info($"Format: no closed-Steam registration found for content id {contentId}.");
            return LibraryRemoval.Nothing(contentId);
        }

        if (_operations.ReadSteam().Running)
        {
            return LibraryRemoval.Refused(
                "Steam started while the card library was being prepared for removal. "
                + "Close Steam and try formatting again.");
        }

        SteamLibraryProvisioner.BackupOnce(configPath);
        AtomicFile.WriteText(configPath, updated, true);
        Info($"Format: removed closed-Steam library registration for content id {contentId}.");
        return new LibraryRemoval(null, contentId, registeredLabel, contentId);
    }

    /// <summary>
    ///     Normalizes a path for comparison, treating a malformed or empty one as
    ///     unknown so the caller refuses rather than proceeding on a bad match.
    /// </summary>
    /// <param name="path">The path to normalize.</param>
    private string? FullPathOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            Warn($"Format: could not normalize path '{path}': {ex.Message}");
            return null;
        }
    }

    private string? FindExistingMarker(FormatRunTarget entry)
    {
        if (entry.PreferredLetter is < 'A' or > 'Z')
        {
            return null;
        }

        var letters = _operations.LettersOnDisk(entry.DiskNumber);
        if (letters.Count == 0)
        {
            letters.Add(entry.PreferredLetter);
        }

        var roots = letters.Select(_operations.LetterRoot).ToList();
        var candidates = roots
            .Select(root => Path.Combine(root, SteamLibraryVdf.CardFolderName, SteamLibraryMarker.MarkerFileName))
            .ToList();
        var (_, configText) = _operations.ReadLibraryFolders();
        if (configText is not null)
        {
            candidates.AddRange(SteamLibraryVdf.ValuesOf(configText, "path")
                .Where(path => roots.Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                .Select(path => Path.Combine(path, SteamLibraryMarker.MarkerFileName)));
        }

        var marker = candidates.Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(File.Exists);
        if (marker is not null
            && !marker.StartsWith(_operations.LetterRoot(entry.PreferredLetter), StringComparison.OrdinalIgnoreCase))
        {
            Info($"Format: existing library marker found at {marker} — another volume of "
                 + $"disk {entry.DiskNumber}, which the erase destroys as well.");
        }

        return marker;
    }

    /// <summary>
    ///     Puts a removed library registration back after a failed format, with
    ///     the label it carried — the card is still there (its marker survived), so it
    ///     must come back exactly as it was and not as an unnamed library.
    /// </summary>
    /// <param name="entry">The card the format ran against.</param>
    /// <param name="contentId">The identity that was actually removed, or null.</param>
    /// <param name="label">The removed registration's label, empty for none.</param>
    private void RestoreRemovedLibraryIfCardSurvived(
        FormatRunTarget entry, string? contentId, string label)
    {
        if (string.IsNullOrEmpty(contentId))
        {
            return;
        }

        var marker = FindExistingMarker(entry);
        if (marker is null)
        {
            return;
        }

        var libraryPath = Path.GetDirectoryName(marker)!;
        // FindExistingMarker resolves by LOCATION (the letters currently on this disk
        // number), not by identity. The pre-erase abort above reaches this after
        // PROVING the media behind that disk number is a different card, so without
        // this gate the compensation would register the swapped-in card under the
        // removed card's contentid, label and size. A no-op for the diskpart-failure
        // callers: their removedContentId was read out of this very marker.
        string? markerContentId = null;
        try
        {
            // The card can be gone by now — that is the scenario this guard exists for,
            // so an unreadable marker must refuse the restore rather than fault the run.
            (markerContentId, _) = _operations.ReadMarker(libraryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"Format: could not read {marker} to confirm the library identity: {ex.Message}");
        }

        if (!string.Equals(markerContentId, contentId, StringComparison.Ordinal))
        {
            Warn(
                $"Format: not restoring library {contentId} — the marker now on disk "
                + $"{entry.DiskNumber} reports contentid {markerContentId ?? "(none)"}.");
            return;
        }

        if (_operations.ReadSteam().Running)
        {
            var liveRestore = _operations.AddLibrary(libraryPath, label, false);
            Info($"Format: compensation after diskpart failure returned {liveRestore.Status}.");
            return;
        }

        var (configPath, current) = _operations.ReadLibraryFolders();
        if (configPath is null || current is null)
        {
            return;
        }

        if (SteamLibraryVdf.IsContentIdRegistered(current, contentId)
            || !SteamLibraryVdf.TrySplice(current, libraryPath, contentId, entry.SizeBytes,
                out var restored, label) || restored is null)
        {
            return;
        }

        AtomicFile.WriteText(configPath, restored, true);
        Info($"Format: restored library registration {contentId} after diskpart failure.");
    }

    /// <summary>
    ///     Resolves the letter the formatted volume mounted on to its volume GUID path and
    ///     confirms that volume still sits on the run's disk. Every later file write goes
    ///     through this root, so a letter re-pointed after the wait cannot redirect them.
    /// </summary>
    /// <returns>The volume GUID root, or null when the letter no longer maps to the run's disk.</returns>
    private string? VerifiedVolumeRoot(FormatRunTarget target, char letter)
    {
        var (root, disk) = _operations.ReadVolume(letter);
        return disk == target.DiskNumber ? root : null;
    }

    private void Info(string message)
    {
        _operations.WriteLog(LogLevel.Info, message, null);
    }

    private void Warn(string message)
    {
        _operations.WriteLog(LogLevel.Warn, message, null);
    }

    private void Error(string message, Exception exception)
    {
        _operations.WriteLog(LogLevel.Error, message, exception);
    }

    private sealed record FormatRunTarget(
        string Id,
        int DiskNumber,
        string Name,
        long SizeBytes,
        int BusType,
        char PreferredLetter);

    /// <summary>
    ///     One (re-)verification pass: the verdict plus the raw facts behind
    ///     it, so an abort can name what differed in a pasted log.
    /// </summary>
    /// <param name="Identity">The verdict.</param>
    /// <param name="SystemDisk">Whether the disk number now hosts Windows or WSGM.</param>
    /// <param name="HandleOpened">
    ///     Whether the disk handle opened at all — the
    ///     up-front check refuses on this where the mid-run checks tolerate it.
    /// </param>
    /// <param name="Removable">Whether it still reports as removable media.</param>
    /// <param name="SizeBytes">The size just read, 0 when the query failed.</param>
    /// <param name="BusType">The bus type just read, -1 when the query failed.</param>
    private readonly record struct TargetIdentitySnapshot(
        FormatTargetIdentity Identity,
        bool SystemDisk,
        bool HandleOpened,
        bool Removable,
        long SizeBytes,
        int BusType);

    /// <summary>
    ///     What the pre-format removal did: the user-facing refusal when the
    ///     old library cannot safely be removed, and — only when a registration was
    ///     ACTUALLY taken out of Steam — its identity and label, so the failure
    ///     compensation restores exactly what it removed and never invents a
    ///     registration the user had deleted themselves. The marker id rides along
    ///     so the post-erase retirement does not re-read the card's marker.
    /// </summary>
    /// <param name="Failure">The refusal message, or null when the run may proceed.</param>
    /// <param name="RemovedContentId">The removed registration's content id, or null.</param>
    /// <param name="RemovedLabel">The removed registration's label, empty when it had none.</param>
    /// <param name="MarkerContentId">
    ///     The content id the card's own marker carried,
    ///     whether or not Steam had it registered; null when no marker was found.
    /// </param>
    private readonly record struct LibraryRemoval(
        string? Failure,
        string? RemovedContentId,
        string RemovedLabel,
        string? MarkerContentId = null)
    {
        internal static LibraryRemoval Nothing(string? markerContentId = null)
        {
            return new LibraryRemoval(null, null, "", markerContentId);
        }

        internal static LibraryRemoval Refused(string message)
        {
            return new LibraryRemoval(message, null, "");
        }
    }
}
