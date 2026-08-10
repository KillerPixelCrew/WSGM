using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>The SteamOS-style "Format SD Card" engine: erase a removable drive,
/// give it a single NTFS volume tuned for game libraries, and put a ready
/// Steam library structure on it. Windows Steam has no such flow of its own.
///
/// The main input is a card straight out of a Steam Deck — GPT plus ext4, no
/// Windows drive letter — so the whole job runs at DISK level through one
/// diskpart script (clean → primary partition → NTFS quick, 128K units →
/// assign) rather than on a drive letter. 128K allocation units mirror the
/// user's proven reference card; quick format only (a full format writes every
/// sector of a wear-limited card for nothing).
///
/// Enumeration is disk-level too (the eject list only sees mounted volumes) and
/// runs off-thread on demand — no background polling. Rows reconcile in place
/// (gamepad-cursor discipline). The destructive step re-verifies the target on
/// fresh handles first: same disk number, same size, same bus — closing the
/// card-swap race between picking and confirming.</summary>
public sealed class SdFormatManager : INotifyPropertyChanged
{
    /// <summary>Raised after a status property changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised on the UI thread when a format run finishes, with the
    /// terminal message — the controller surfaces it even when the overlay has
    /// been closed mid-format.</summary>
    public event Action<string, bool>? Finished;

    /// <summary>The volume/library label used when the user names nothing.</summary>
    internal const string DefaultLabel = "Games";

    /// <summary>Sanitizes a user-typed name into a value safe as both an NTFS
    /// volume label and a Steam library label: trims, keeps ASCII letters, digits,
    /// space, dash and underscore (so the diskpart script stays plain ASCII and no
    /// quote can break out of the label token), caps at 32 characters, and falls
    /// back to <see cref="DefaultLabel"/> when nothing usable remains.</summary>
    /// <param name="name">The raw name, or null.</param>
    internal static string SanitizeLabel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return DefaultLabel;
        }
        var kept = new string(name.Trim()
            .Where(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                or ' ' or '-' or '_')
            .Take(32)
            .ToArray())
            .Trim();
        return kept.Length == 0 ? DefaultLabel : kept;
    }

    private int _refreshing;

    /// <summary>Serializes format runs: strictly one at a time.</summary>
    private readonly SemaphoreSlim _formatGate = new(1, 1);

    /// <summary>Gets the candidate drives, one row per physical disk.</summary>
    public ObservableCollection<FormatTargetEntry> Targets { get; } = [];

    private bool _hasTargets;
    /// <summary>Gets whether any formattable drive is present.</summary>
    public bool HasTargets
    {
        get => _hasTargets;
        private set
        {
            if (_hasTargets != value)
            {
                _hasTargets = value;
                Raise(nameof(HasTargets));
            }
        }
    }

    private bool _busy;
    /// <summary>Gets whether a format run is in flight. The flow's buttons and
    /// the target list disable while true.</summary>
    public bool Busy
    {
        get => _busy;
        private set
        {
            if (_busy != value)
            {
                _busy = value;
                Raise(nameof(Busy));
                Raise(nameof(NotBusy));
            }
        }
    }

    /// <summary>Gets the inverse of <see cref="Busy"/>, for IsEnabled bindings.</summary>
    public bool NotBusy => !Busy;

    private string _statusText = "";
    /// <summary>Gets the current stage or terminal outcome of the format run.</summary>
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText != value)
            {
                _statusText = value;
                Raise(nameof(StatusText));
                Raise(nameof(HasStatus));
            }
        }
    }

    /// <summary>Gets whether a status line should be shown.</summary>
    public bool HasStatus => StatusText.Length > 0;

    // ---- enumeration ----

    /// <summary>Re-enumerates the candidate disks off-thread and reconciles the
    /// bound list. Called when the flow opens and from its refresh button.</summary>
    public void Refresh()
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                var targets = ReadTargets();
                Dispatcher.UIThread.Post(() => Apply(targets));
            }
            catch (Exception ex)
            {
                Log.Warn($"Format: enumeration failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        });
    }

    /// <summary>One formattable disk as the background snapshot reports it.</summary>
    /// <param name="Id">The row identity (device instance path or "disk:N").</param>
    /// <param name="DiskNumber">The physical disk number.</param>
    /// <param name="Name">Vendor/product identity.</param>
    /// <param name="SizeBytes">Total disk size.</param>
    /// <param name="BusType">The STORAGE_BUS_TYPE value.</param>
    /// <param name="Letters">Currently mounted letters on this disk, if any.</param>
    /// <param name="HasLinuxPartitions">Whether ext4-style partitions were found.</param>
    internal sealed record FormatTarget(
        string Id, int DiskNumber, string Name, long SizeBytes, int BusType,
        IReadOnlyList<char> Letters, bool HasLinuxPartitions);

    /// <summary>Reads the current candidate list. Worker thread only.</summary>
    private static List<FormatTarget> ReadTargets()
    {
        var systemDisks = RemovableDriveManager.ResolveSystemDisks();

        // Letters per disk, for the detail line (a letterless Deck card is the
        // normal case and simply shows none).
        var lettersByDisk = new Dictionary<int, List<char>>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                {
                    continue;
                }
                var letter = char.ToUpperInvariant(drive.Name[0]);
                using var volume = NativeStorage.OpenVolumeForQuery(letter);
                if (!volume.IsInvalid
                    && NativeStorage.TryGetDeviceNumber(volume, out var type, out var disk)
                    && type == NativeStorage.FileDeviceDisk && disk >= 0)
                {
                    (lettersByDisk.TryGetValue(disk, out var list)
                        ? list
                        : lettersByDisk[disk] = []).Add(letter);
                }
            }
            catch (IOException)
            {
            }
        }

        var result = new List<FormatTarget>();
        var seenDisks = new HashSet<int>();
        foreach (var path in NativeStorage.ListDiskInterfaces())
        {
            using var probe = NativeStorage.OpenVolumeForQueryPath(path);
            if (probe.IsInvalid
                || !NativeStorage.TryGetDeviceNumber(probe, out _, out var disk)
                || disk < 0 || !seenDisks.Add(disk) || systemDisks.Contains(disk))
            {
                continue;
            }
            using var handle = NativeStorage.OpenDiskForRead(disk);
            if (handle.IsInvalid
                || !NativeStorage.TryGetHotplugInfo(handle, out var media, out var hotplug)
                || RemovableDriveManager.Classify(hotplug, media) is null)
            {
                continue;
            }
            var size = NativeStorage.GetDiskLength(handle);
            NativeStorage.TryGetDeviceDescriptor(handle, out var busType, out var product);
            var linux = NativeStorage.TryGetPartitionTypes(handle, out _, out var partitions)
                && partitions.Any(p => p.IsLinux);
            var id = NativeStorage.TryGetDevNode(path, out var devInst)
                ? NativeStorage.GetDeviceInstanceId(devInst)
                : "";
            result.Add(new FormatTarget(
                id.Length > 0 ? id : $"disk:{disk}",
                disk, product, size, busType,
                lettersByDisk.TryGetValue(disk, out var letters)
                    ? [.. letters.OrderBy(l => l)]
                    : [],
                linux));
        }
        return result;
    }

    /// <summary>The row's detail line: capacity — bus kind — letters — hint.</summary>
    /// <param name="target">The enumerated disk.</param>
    internal static string DescribeTarget(FormatTarget target)
    {
        var parts = new List<string>
        {
            RemovableDriveManager.FormatSize(target.SizeBytes),
            DescribeBus(target.BusType),
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

    /// <summary>Names the bus for the row and confirm views. USB stays generic:
    /// a USB-bridged internal card reader and a stick both say USB, and the
    /// product name is what tells them apart.</summary>
    /// <param name="busType">The STORAGE_BUS_TYPE value.</param>
    internal static string DescribeBus(int busType) => busType switch
    {
        NativeStorage.BusTypeSd or NativeStorage.BusTypeMmc => "SD card",
        NativeStorage.BusTypeUsb => "USB",
        _ => "",
    };

    /// <summary>Merges a fresh target list into the bound collection without
    /// replacing surviving rows.</summary>
    private void Apply(List<FormatTarget> fresh)
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
                Log.Info($"Format: candidate {target.Name} disk={target.DiskNumber} "
                    + $"bus={target.BusType} size={target.SizeBytes} "
                    + $"letters={string.Concat(target.Letters)} linux={target.HasLinuxPartitions}");
            }
            row.Name = target.Name;
            row.SizeBytes = target.SizeBytes;
            row.BusType = target.BusType;
            row.HasLinuxPartitions = target.HasLinuxPartitions;
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
        foreach (var entry in Targets)
        {
            if (string.Equals(entry.Id, id, StringComparison.Ordinal))
            {
                return entry;
            }
        }
        return null;
    }

    // ---- the format run ----

    /// <summary>The diskpart script for one target. Quick NTFS format with 128K
    /// allocation units (the proven game-library tuning); `clean` (never
    /// `clean all`) wipes any prior layout — GPT+ext4 Deck cards included —
    /// and MBR is the correct default for removable SD media.
    ///
    /// The letter is PINNED to the card's current one when it has it: a bare
    /// `assign` hands out the next free letter, which device-observed moved a
    /// card from E: to D: across a format and collided with another library's
    /// path. A card reader's letter must stay put across reformats and swaps.
    /// A letterless card (raw / ext4 Deck card) gets a bare `assign`.</summary>
    /// <param name="diskNumber">The physical disk number.</param>
    /// <param name="preferredLetter">The letter to reassign, or '\0' for none.</param>
    /// <param name="label">The volume label (already sanitized); quoted so a name
    /// with spaces stays one token.</param>
    internal static string BuildDiskpartScript(
        int diskNumber, char preferredLetter = '\0', string label = DefaultLabel) =>
        $"select disk {diskNumber.ToString(CultureInfo.InvariantCulture)}\r\n"
        + "clean\r\n"
        + "create partition primary\r\n"
        + $"format fs=ntfs quick unit=128k label=\"{label}\"\r\n"
        + (preferredLetter is >= 'A' and <= 'Z'
            ? $"assign letter={preferredLetter}\r\n"
            : "assign\r\n");

    /// <summary>Erases and formats one target and puts a Steam library on it.
    /// Serialized; progress lands in <see cref="StatusText"/>; the terminal
    /// message also fires <see cref="Finished"/>.</summary>
    /// <param name="entry">The target to format.</param>
    /// <param name="name">The user-chosen volume/library name, or null for the default.</param>
    public async Task FormatAsync(FormatTargetEntry entry, string? name = null)
    {
        if (Busy)
        {
            return;
        }
        var label = SanitizeLabel(name);
        await _formatGate.WaitAsync();
        try
        {
            Busy = true;
            StatusText = $"Erasing {entry.Name}...";
            Log.Info($"Format: starting for {entry.Name} (disk {entry.DiskNumber}, "
                + $"{entry.SizeBytes} bytes, bus {entry.BusType}).");

            // Only a definite "not elevated" blocks; unknown proceeds and lets
            // diskpart's own error surface (shell mode is elevated in practice).
            if (ElevationCheck.IsCurrentProcessElevated() == false)
            {
                Finish("Formatting needs administrator rights, which WSGM does not have "
                    + "right now.", false);
                return;
            }

            var verify = await Task.Run(() => VerifyTarget(entry));
            if (verify is not null)
            {
                Finish(verify, false);
                return;
            }

            var keepLetter = entry.PreferredLetter;
            var (exitCode, output) = await RunDiskpart(entry.DiskNumber, keepLetter, label);
            if (exitCode != 0)
            {
                Log.Warn($"Format: diskpart failed (exit {exitCode}). Output:\n{output}");
                Finish("Formatting failed — Windows could not rebuild the drive. "
                    + "Reinsert the card and try again.", false);
                return;
            }
            Log.Info($"Format: diskpart succeeded for disk {entry.DiskNumber}.");

            StatusText = "Waiting for the new drive...";
            var letter = await Task.Run(() => WaitForLetter(entry.DiskNumber));
            if (letter is null)
            {
                Finish("The drive was formatted, but Windows did not mount it. "
                    + "Reinsert the card.", false);
                return;
            }
            // The card reader must keep its letter. If diskpart could not put it
            // back (letter held by something else), stop rather than silently
            // leaving the card on a different one that would break every path
            // pointing at it (emulators, libraries).
            if (keepLetter is >= 'A' and <= 'Z' && letter.Value != keepLetter)
            {
                Log.Warn($"Format: expected to keep letter {keepLetter}: but disk "
                    + $"{entry.DiskNumber} mounted as {letter}:.");
                Finish($"Formatted, but Windows could not keep drive letter {keepLetter}:. "
                    + $"It is now {letter}: — free {keepLetter}: and reformat, or reassign "
                    + $"the letter in Disk Management.", false);
                return;
            }
            Log.Info($"Format: disk {entry.DiskNumber} mounted as {letter}: "
                + $"(letter preserved={keepLetter is >= 'A' and <= 'Z'}).");

            // TRIM the fresh (near-empty) volume so the flash controller learns
            // almost the whole card is free — proven to restore SD write speed.
            // Best-effort: a reader that does not pass TRIM just logs and moves on.
            StatusText = "Optimizing the card...";
            await RetrimVolume(letter.Value);

            StatusText = "Creating Steam library...";
            var summary = await Task.Run(
                () => CreateSteamLibrary(letter.Value, entry.SizeBytes, label));
            Finish(summary, true);
        }
        catch (Exception ex)
        {
            Log.Error("Format: run failed.", ex);
            Finish("Formatting failed unexpectedly — see the log.", false);
        }
        finally
        {
            Busy = false;
            _formatGate.Release();
        }
    }

    /// <summary>Re-verifies the target on fresh handles immediately before the
    /// destructive work: the disk number must still belong to a device with the
    /// same size and bus, still hot-pluggable, still not a system disk. Returns
    /// null when safe, else the refusal message.</summary>
    private static string? VerifyTarget(FormatTargetEntry entry)
    {
        if (RemovableDriveManager.ResolveSystemDisks().Contains(entry.DiskNumber))
        {
            return "This drive hosts Windows or WSGM and cannot be formatted.";
        }
        using var handle = NativeStorage.OpenDiskForRead(entry.DiskNumber);
        if (handle.IsInvalid)
        {
            return "The drive is no longer reachable. Reinsert it and try again.";
        }
        if (!NativeStorage.TryGetHotplugInfo(handle, out var media, out var hotplug)
            || RemovableDriveManager.Classify(hotplug, media) is null)
        {
            return "The drive no longer reports as removable — not formatting it.";
        }
        var size = NativeStorage.GetDiskLength(handle);
        NativeStorage.TryGetDeviceDescriptor(handle, out var busType, out _);
        if (size != entry.SizeBytes || busType != entry.BusType)
        {
            Log.Warn($"Format: disk {entry.DiskNumber} changed identity "
                + $"(size {entry.SizeBytes}->{size}, bus {entry.BusType}->{busType}).");
            return "The drive changed since it was listed — refresh and pick it again.";
        }
        return null;
    }

    /// <summary>Writes the script beside the log (an elevated diskpart consumes
    /// it — never %TEMP%, same rule as the de-elevation task XML), runs
    /// diskpart, deletes the script.</summary>
    private static async Task<(int ExitCode, string Output)> RunDiskpart(
        int diskNumber, char preferredLetter, string label)
    {
        var script = BuildDiskpartScript(diskNumber, preferredLetter, label);
        Log.Info($"Format: diskpart script:\n{script.TrimEnd()}");
        var scriptPath = Path.Combine(Log.Directory, "format-disk.dp.txt");
        await File.WriteAllTextAsync(scriptPath, script);
        try
        {
            return await ConsoleTool.RunCapturedAsync(
                "diskpart.exe", $"/s \"{scriptPath}\"", timeoutMs: 600_000);
        }
        finally
        {
            try
            {
                File.Delete(scriptPath);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Issues TRIM (retrim) for the volume's free space via
    /// <c>Optimize-Volume -ReTrim</c> so the flash controller marks the freshly
    /// wiped blocks erasable — the proven SD write-speed win. Optimize-Volume is
    /// used over <c>defrag /L</c> because it retrims REMOVABLE media too (plain
    /// defrag skips it). Best-effort: a reader that does not pass TRIM just makes
    /// the cmdlet fail, which is logged and the format continues. Runs elevated
    /// (the format flow already is).</summary>
    /// <param name="letter">The just-mounted drive letter.</param>
    private static async Task RetrimVolume(char letter)
    {
        try
        {
            var (exitCode, output) = await ConsoleTool.RunCapturedAsync(
                "powershell.exe",
                "-NoProfile -NonInteractive -Command \"Optimize-Volume -DriveLetter "
                    + letter + " -ReTrim -ErrorAction Stop\"",
                timeoutMs: 300_000);
            if (exitCode == 0)
            {
                Log.Info($"Format: retrimmed {letter}: (TRIM issued for free space).");
            }
            else
            {
                Log.Info($"Format: retrim of {letter}: not applied (exit {exitCode}); "
                    + $"the reader may not pass TRIM. Output:\n{output.Trim()}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Format: retrim of {letter}: failed: {ex.Message}");
        }
    }

    /// <summary>Polls for the freshly assigned drive letter by matching mounted
    /// volumes back to the disk number. Worker thread; ~15 s cap (typically
    /// 1-3 s after diskpart's assign).</summary>
    private static char? WaitForLetter(int diskNumber)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                    {
                        continue;
                    }
                    var letter = char.ToUpperInvariant(drive.Name[0]);
                    using var volume = NativeStorage.OpenVolumeForQuery(letter);
                    if (!volume.IsInvalid
                        && NativeStorage.TryGetDeviceNumber(volume, out _, out var disk)
                        && disk == diskNumber && drive.IsReady)
                    {
                        return letter;
                    }
                }
                catch (IOException)
                {
                }
            }
            Thread.Sleep(500);
        }
        return null;
    }

    /// <summary>Creates the card-side Steam library (marker VDF, steamapps,
    /// steam.dll), registers it in Steam's config when possible, and pokes
    /// drive watchers with a synthetic volume-arrival broadcast — the real
    /// arrival fired when the volume was still empty, so a running Steam has
    /// already looked and found nothing. Returns the user-facing summary.
    /// Worker thread.</summary>
    private static string CreateSteamLibrary(char letter, long sizeBytes, string label)
    {
        var libraryPath = $@"{letter}:\SteamLibrary";
        Directory.CreateDirectory(Path.Combine(libraryPath, "steamapps"));

        var steamExe = Steam.ExePath;
        var configPath = steamExe is null
            ? null
            : Path.Combine(Path.GetDirectoryName(steamExe)!, "config", "libraryfolders.vdf");
        var taken = new HashSet<string>(StringComparer.Ordinal);
        string? configText = null;
        if (configPath is not null && File.Exists(configPath))
        {
            configText = File.ReadAllText(configPath);
            foreach (var id in SteamLibraryVdf.ValuesOf(configText, "contentid"))
            {
                taken.Add(id);
            }
        }
        var contentId = SteamLibraryVdf.GenerateContentId(taken);

        // Steam drops a copy of its current client dll into every secondary
        // library root; version skew is tolerated, so this is create-time only.
        WriteMarkerAndClientDll(libraryPath, contentId, steamExe, label);

        var registration = RegisterLibrary(configPath, configText, libraryPath, contentId,
            sizeBytes, label);

        // Now that the library exists, make drive watchers look at the volume
        // again (best effort; harmless when nobody listens).
        NativeStorage.BroadcastVolumeArrival(letter);
        Log.Info($"Format: volume-arrival broadcast sent for {letter}:.");

        return $"{letter}: is ready as a Steam library. {registration}";
    }

    /// <summary>Registers the library with Steam. When Steam is RUNNING this
    /// drives Steam's own front-end API over its CEF debug port
    /// (<see cref="SteamCdp"/>) — Steam adopts, persists, mounts and scans it with
    /// no restart, which a file edit cannot do against a live client (Steam holds
    /// libraries in memory and rewrites the file on exit). When Steam is CLOSED
    /// (or its debug port is unreachable), the entry is spliced into
    /// config\libraryfolders.vdf so Steam reads it on next start; dedup there is by
    /// CONTENT ID (a card reader reuses its drive letter, so the path repeats per
    /// card). Returns the summary sentence.</summary>
    private static string RegisterLibrary(
        string? configPath, string? configText, string libraryPath, string contentId,
        long sizeBytes, string label)
    {
        // Live client: drive Steam's own front-end API over its CEF debug port so
        // Steam adds, persists, mounts and scans the library with no restart — the
        // only thing that makes a running Steam show a library live. Falls through
        // to the config write only when the debug channel cannot be reached.
        if (Steam.IsRunning)
        {
            var live = SteamCdp.AddLibrary(libraryPath, label);
            switch (live.Status)
            {
                case SteamLibraryAddStatus.Added:
                    return "Added to Steam.";
                case SteamLibraryAddStatus.AlreadyPresent:
                    return "This library is already in Steam.";
                case SteamLibraryAddStatus.Rejected:
                    Log.Warn($"Format: Steam refused the library add ({live.Detail}).");
                    return $"Steam did not accept it: {live.Detail}.";
                default:
                    Log.Warn("Format: Steam debug port unavailable — writing config for next start.");
                    break;
            }
        }

        // Steam closed (or its debug port was unreachable): write the config; it is
        // read on next start.
        if (configPath is null || configText is null)
        {
            Log.Warn("Format: Steam config not found — skipping registration.");
            return "Add it in Steam under Settings > Storage.";
        }
        if (SteamLibraryVdf.IsContentIdRegistered(configText, contentId))
        {
            Log.Info($"Format: content id {contentId} already in libraryfolders.vdf.");
            return "This library is already in Steam.";
        }
        if (!SteamLibraryVdf.TrySplice(configText, libraryPath, contentId, sizeBytes,
                out var updated, label))
        {
            Log.Warn("Format: libraryfolders.vdf has an unexpected shape — not editing it.");
            return "Add it in Steam under Settings > Storage.";
        }
        File.Copy(configPath, configPath + ".wsgm-bak", overwrite: true);
        var utf8NoBom = new System.Text.UTF8Encoding(false);
        File.WriteAllText(configPath, updated, utf8NoBom);
        Log.Info($"Format: {libraryPath} registered in libraryfolders.vdf (backup written).");
        return "Added to Steam's library list (on next start).";
    }

    // ---- add an existing location as a library (no formatting) ----

    /// <summary>Turns a user-chosen folder into a registered Steam library
    /// WITHOUT formatting anything — for network shares, second internal drives
    /// (DIY Steam machines), and existing libraries. A drive root becomes
    /// <c>&lt;root&gt;SteamLibrary</c> (Steam's own layout); any other folder is
    /// used as the library root directly. An existing library (marker present)
    /// keeps its contentid untouched and is only registered.</summary>
    /// <param name="folderPath">The folder the user picked.</param>
    public async Task AddLibraryAsync(string folderPath)
    {
        if (Busy)
        {
            return;
        }
        await _formatGate.WaitAsync();
        try
        {
            Busy = true;
            StatusText = "Adding Steam library...";
            var summary = await Task.Run(() => AddLibrary(folderPath));
            Finish(summary.Message, summary.Success);
        }
        catch (Exception ex)
        {
            Log.Error("Format: add-library failed.", ex);
            Finish("Could not add the library — see the log.", false);
        }
        finally
        {
            Busy = false;
            _formatGate.Release();
        }
    }

    /// <summary>Resolves the library root for a picked folder: drive roots get
    /// the conventional SteamLibrary subfolder, everything else is taken as-is.</summary>
    /// <param name="folderPath">The folder the user picked.</param>
    internal static string ResolveLibraryRoot(string folderPath)
    {
        var trimmed = folderPath.TrimEnd('\\', '/');
        // "D:" / "D:\" → the conventional <root>\SteamLibrary.
        return trimmed.Length == 2 && trimmed[1] == ':'
            ? $@"{trimmed}\SteamLibrary"
            : trimmed.Length == 0 ? folderPath : trimmed;
    }

    private static (string Message, bool Success) AddLibrary(string folderPath)
    {
        var libraryPath = ResolveLibraryRoot(folderPath);
        Log.Info($"Format: adding library at {libraryPath} (picked: {folderPath}).");
        try
        {
            Directory.CreateDirectory(Path.Combine(libraryPath, "steamapps"));
        }
        catch (Exception ex)
        {
            Log.Warn($"Format: cannot create {libraryPath}: {ex.Message}");
            return ($"Could not create a library at {libraryPath}.", false);
        }

        var steamExe = Steam.ExePath;
        var configPath = steamExe is null
            ? null
            : Path.Combine(Path.GetDirectoryName(steamExe)!, "config", "libraryfolders.vdf");
        var configText = configPath is not null && File.Exists(configPath)
            ? File.ReadAllText(configPath)
            : null;

        // An existing library keeps its identity; only a fresh folder gets a
        // marker (and Steam's client dll) written.
        var markerPath = Path.Combine(libraryPath, "libraryfolder.vdf");
        string contentId;
        if (File.Exists(markerPath)
            && SteamLibraryVdf.ValuesOf(File.ReadAllText(markerPath), "contentid")
                is { Count: > 0 } existing
            && existing[0].Length > 0)
        {
            contentId = existing[0];
            Log.Info($"Format: existing library found (contentid {contentId}).");
        }
        else
        {
            var taken = new HashSet<string>(StringComparer.Ordinal);
            if (configText is not null)
            {
                foreach (var id in SteamLibraryVdf.ValuesOf(configText, "contentid"))
                {
                    taken.Add(id);
                }
            }
            contentId = SteamLibraryVdf.GenerateContentId(taken);
            WriteMarkerAndClientDll(libraryPath, contentId, steamExe, label: "");
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
            totalSize, label: "");
        return ($"{libraryPath} is set up as a Steam library. {registration}", true);
    }

    /// <summary>Writes the marker VDF (Steam's exact dialect: UTF-8 no BOM,
    /// LF-only) and copies Steam's client dll beside it.</summary>
    private static void WriteMarkerAndClientDll(
        string libraryPath, string contentId, string? steamExe, string label)
    {
        var utf8NoBom = new System.Text.UTF8Encoding(false);
        File.WriteAllText(
            Path.Combine(libraryPath, "libraryfolder.vdf"),
            SteamLibraryVdf.BuildMarker(contentId, steamExe ?? "", label),
            utf8NoBom);
        Log.Info($"Format: library marker written ({libraryPath}, contentid {contentId}).");
        if (steamExe is not null)
        {
            var sourceDll = Path.Combine(Path.GetDirectoryName(steamExe)!, "steam.dll");
            if (File.Exists(sourceDll))
            {
                File.Copy(sourceDll, Path.Combine(libraryPath, "steam.dll"), overwrite: true);
            }
            else
            {
                Log.Warn($"Format: steam.dll not found at {sourceDll} — library still mounts.");
            }
        }
    }

    private void Finish(string message, bool success)
    {
        StatusText = message;
        if (success)
        {
            Log.Info($"Format: done — {message}");
        }
        else
        {
            Log.Warn($"Format: failed — {message}");
        }
        Finished?.Invoke(message, success);
    }

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
