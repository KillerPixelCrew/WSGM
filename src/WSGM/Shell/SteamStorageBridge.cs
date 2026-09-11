using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
/// Puts WSGM's own storage operations behind Steam's revived storage pages.
/// </summary>
/// <remarks>
/// Every action here delegates to the manager that already owns it: <see cref="RemovableDriveManager"/>
/// for eject and <see cref="SdFormatManager"/> for format and library registration. Nothing is
/// reimplemented for Steam's benefit, so the identity checks, the volume-GUID resolution, the
/// backup-and-splice of Steam's library file and the staged destructive sequence are the same code
/// whichever surface asked — which is the whole point of the issue this satisfies.
/// <para>
/// Formatting stays behind its own switch, defaulting off. Steam's page can offer it to a controller
/// press, and this repository treats erasing a card as an attended operation; the switch is what
/// keeps "the pages are alive" from silently also meaning "anyone holding the pad can wipe a drive".
/// A refusal is reported rather than swallowed, so the button says why instead of doing nothing.
/// </para>
/// </remarks>
public sealed class SteamStorageBridge : ISteamStorageBackend, IDisposable
{
    private readonly RemovableDriveManager _drives;
    private readonly SdFormatManager _formats;
    private readonly Func<bool> _formatAllowed;

    // Steam addresses drives and volumes by uint32 and both managers key on strings. These hold the
    // mapping for the life of the session so an id Steam saw stays the same id on the next read —
    // a renumbered row is a row the user's selection jumps off.
    private readonly Dictionary<string, uint> _driveIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, uint> _deviceIds = new(StringComparer.Ordinal);
    private string _loggedProjection = "";

    /// <summary>Creates the bridge over the managers that already own these operations.</summary>
    /// <param name="drives">The removable-drive manager, which owns safe eject.</param>
    /// <param name="formats">The format manager, which owns erase and library registration.</param>
    /// <param name="formatAllowed">Whether formatting from Steam's own pages is permitted.</param>
    /// <remarks>
    /// The format manager enumerates on demand and has no timer of its own, because until now the
    /// only thing that opened it was the overlay's format page. Steam's pages have no such moment:
    /// they ask for the state, and a state with no drive rows leaves the block device Steam did get
    /// pointing at a parent that is not there. So the enumeration is driven from the one signal that
    /// already exists — the drive manager's own list, which it reconciles on a 2 s signature — and
    /// once at construction for whatever is already inserted. That keeps this off a poll: disks are
    /// re-read when storage changed, not every time Steam asks.
    /// </remarks>
    public SteamStorageBridge(
        RemovableDriveManager drives, SdFormatManager formats, Func<bool> formatAllowed)
    {
        _drives = drives ?? throw new ArgumentNullException(nameof(drives));
        _formats = formats ?? throw new ArgumentNullException(nameof(formats));
        _formatAllowed = formatAllowed ?? throw new ArgumentNullException(nameof(formatAllowed));
        _drives.Drives.CollectionChanged += OnDrivesChanged;
        _formats.Refresh();
    }

    /// <summary>Stops following the drive manager's list.</summary>
    public void Dispose() => _drives.Drives.CollectionChanged -= OnDrivesChanged;

    private void OnDrivesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        _formats.Refresh();

    /// <summary>Projects what the managers currently see into Steam's own state shape.</summary>
    /// <returns>The state, or null while neither manager has anything to report.</returns>
    /// <remarks>
    /// Null and empty mean different things on this wire. Null is "nothing to say", which leaves
    /// Steam's page as it was; an empty state is the assertion that the machine has no removable
    /// storage, which the page renders as such. Reporting empty before the managers have scanned
    /// would show "no drives" to someone holding a card.
    /// </remarks>
    public SteamStorageState? ReadState()
    {
        var ejectable = _drives.Drives.ToArray();
        var formattable = _formats.Targets.ToArray();
        if (ejectable.Length == 0 && formattable.Length == 0)
        {
            return null;
        }

        // One read of what Windows says about mounted volumes, shared by every row below. It is the
        // only thing that relates a volume to the disk under it: the format manager knows a card by
        // its disk number and the drive manager knows it by its device instance path, and nothing
        // else can say those describe the same card.
        IReadOnlyList<StorageVolume> volumes = WindowsStorage.DescribeVolumes();

        // One drive row per format target, because that is the manager that knows a disk by number
        // and can say whether it is erasable at all.
        var drives = formattable
            .Select(target => new SteamStorageDrive(
                Id: DriveId(target.Id),
                Model: target.Name,
                Vendor: "",
                SizeBytes: target.SizeBytes,
                Ejectable: true,
                Formattable: true,
                Unformatted: IsUnformatted(volumes, target)))
            .ToList();

        // Ejectable volumes, which may or may not correspond to a format target. An eject row with
        // no matching drive still has to appear: a USB stick is ejectable and not a format target.
        var devices = new List<SteamStorageBlockDevice>();
        foreach (RemovableDriveEntry entry in ejectable)
        {
            if (entry.Ejected)
            {
                continue;
            }

            // The parent drive is zero rather than a guess when nothing erasable matches: Steam
            // reads it to decide which drive a volume belongs under, and a wrong parent puts the
            // volume on the wrong row. A USB stick with no format target has no parent here.
            IReadOnlyList<string> paths = SplitLetters(entry.Letters);
            // Label is the volume's, not the device's product name: Steam shows it as the row's own
            // name under the drive carrying it, so "SDCard1" belongs here and "Realtek PCIE
            // CardReader" on the drive above. Size is the volume's for the same reason — the
            // entry's is the whole device's, which would report one size on every partition.
            StorageVolume? volume = paths.Count == 0 ? null : FindVolume(volumes, paths[0]);
            devices.Add(new SteamStorageBlockDevice(
                Id: DeviceId(entry.Id),
                DriveId: MatchingDrive(volume, formattable),
                Label: volume is null || volume.Label.Length == 0 ? entry.Name : volume.Label,
                FriendlyPath: paths.Count > 0 ? paths[0] : "",
                SizeBytes: volume?.CapacityBytes ?? entry.SizeBytes,
                MountPaths: paths,
                HasSteamLibrary: paths.Any(HasSteamLibrary)));
        }

        LogProjection(drives, devices);

        // Trim is reported unsupported because neither manager exposes one. Claiming otherwise would
        // put a button on Steam's page that could never do anything.
        return new SteamStorageState(
            drives,
            devices,
            AdoptSupported: true,
            UnmountSupported: ejectable.Length > 0,
            TrimSupported: false,
            TrimRunning: false);
    }

    /// <summary>The drive number Steam addresses a format target by.</summary>
    /// <param name="id">The format manager's own identifier for the target.</param>
    /// <returns>The number, or zero when the identifier is not one this bridge issued.</returns>
    /// <remarks>
    /// Steam's identifiers are <c>uint32</c> and both managers key on strings, so the two have to
    /// be bridged somewhere. Assigning numbers here, stably and from one, keeps the managers
    /// unchanged and keeps the mapping in the one place that also resolves it back.
    /// </remarks>
    private uint DriveId(string id) => _driveIds.TryGetValue(id, out uint existing)
        ? existing
        : _driveIds[id] = (uint)(_driveIds.Count + _deviceIds.Count + 1);

    private uint DeviceId(string id) => _deviceIds.TryGetValue(id, out uint existing)
        ? existing
        : _deviceIds[id] = (uint)(_driveIds.Count + _deviceIds.Count + 1);

    /// <summary>Finds the erasable disk a volume sits on, by the disk number Windows reports.</summary>
    /// <param name="volume">What Windows says about the volume, or null when it said nothing.</param>
    /// <param name="targets">The current format targets.</param>
    /// <returns>That drive's number, or zero when none matches.</returns>
    /// <remarks>
    /// The two managers identify the same card differently — the format manager by its disk number,
    /// the drive manager by its device instance path or mounted media — so their own identifiers
    /// never match and comparing them parented nothing. Steam then saw a card reader with no volume
    /// on it, which is its definition of "unusable until formatted", and offered exactly that on a
    /// card holding a library. The disk number is the fact both sides are really describing, which
    /// is why reading it is <see cref="WindowsStorage" />'s job rather than either manager's.
    /// </remarks>
    private uint MatchingDrive(StorageVolume? volume, IReadOnlyList<FormatTargetEntry> targets)
    {
        if (volume is null || volume.DiskNumber < 0)
        {
            return 0;
        }

        FormatTargetEntry? match = targets.FirstOrDefault(
            target => target.DiskNumber == volume.DiskNumber);
        return match is null ? 0 : DriveId(match.Id);
    }

    /// <summary>Records what this state says, once per change.</summary>
    /// <param name="drives">The drive rows about to be published.</param>
    /// <param name="devices">The volume rows about to be published.</param>
    /// <remarks>
    /// This exists because the alternative was reading the projection out of a running Steam, and
    /// attaching a debugger to Steam's renderer while it is still building its UI wedges
    /// steamwebhelper. Every field Steam renders a decision from is here — the parent drive, the
    /// library flag, the sizes — so the published state can be checked from the log while Steam is
    /// left alone. Logged only when it changes, because it is produced on a poll.
    /// </remarks>
    private void LogProjection(
        IReadOnlyList<SteamStorageDrive> drives, IReadOnlyList<SteamStorageBlockDevice> devices)
    {
        var summary = string.Join("; ", drives.Select(drive =>
                $"drive {drive.Id} '{drive.Model}' {drive.SizeBytes}B "
                + $"unformatted={drive.Unformatted}")
            .Concat(devices.Select(device =>
                $"volume {device.Id} '{device.Label}' {device.FriendlyPath} {device.SizeBytes}B "
                + $"onDrive={device.DriveId} steamLibrary={device.HasSteamLibrary}")));
        if (summary == _loggedProjection)
        {
            return;
        }

        _loggedProjection = summary;
        Log.Info($"Steam storage: {(summary.Length == 0 ? "nothing to publish" : summary)}");
    }

    /// <summary>Whether a disk carries no filesystem Windows could mount.</summary>
    /// <param name="volumes">The volumes read for this state.</param>
    /// <param name="target">The erasable disk.</param>
    /// <returns>True when nothing readable is mounted from it.</returns>
    /// <remarks>
    /// Read from the volumes rather than assumed false, which is what it used to be. Steam offers
    /// to adopt an unformatted drive and leaves a formatted one alone, so getting this wrong either
    /// hides the offer on a blank card or makes it on a card holding a library.
    /// </remarks>
    private static bool IsUnformatted(
        IReadOnlyList<StorageVolume> volumes, FormatTargetEntry target) =>
        !volumes.Any(volume => volume.DiskNumber == target.DiskNumber && volume.Ready);

    /// <summary>Matches a mount path against what Windows reported for it.</summary>
    /// <param name="volumes">The volumes read for this state.</param>
    /// <param name="path">The mount path to find.</param>
    /// <returns>The volume, or null when Windows did not describe it.</returns>
    private static StorageVolume? FindVolume(IReadOnlyList<StorageVolume> volumes, string path) =>
        volumes.FirstOrDefault(volume => volume.MountPath.Length > 0 && path.Length > 0
            && char.ToUpperInvariant(volume.MountPath[0]) == char.ToUpperInvariant(path[0]));

    /// <summary>Whether a mounted path carries a Steam library.</summary>
    /// <param name="path">The mount path, for example <c>D:\</c>.</param>
    /// <returns>True when Steam's library directory is present on it.</returns>
    /// <remarks>
    /// Read off the volume rather than out of Steam's library file. Steam asks this per volume, and
    /// a library is a <c>steamapps</c> directory on it; going through the library file would mean
    /// resolving each registered path back to a volume to answer a question the volume already
    /// answers. Reported false when the volume cannot be read, which is the safe direction: Steam
    /// then offers to adopt a drive that is already a library rather than hiding a drive that is
    /// not one.
    /// </remarks>
    private static bool HasSteamLibrary(string path)
    {
        try
        {
            return path.Length > 0 && Directory.Exists(Path.Combine(path, "steamapps"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> AdoptAsync(uint driveId, CancellationToken cancellationToken)
    {
        FormatTargetEntry? target = FindTarget(driveId);
        string? path = FirstMountPath(driveId);
        if (target is null && path is null)
        {
            return Refuse("That drive is no longer present.");
        }

        try
        {
            // The manager registers the folder with the running client and reconciles Steam's own
            // library file; this only names the path.
            await _formats.AddLibraryAsync(path ?? "").ConfigureAwait(false);
            return SteamUiCommandResult.Applied;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Steam storage: adopting {driveId} failed: {ex.Message}");
            return Refuse("Could not register that drive as a Steam library.");
        }
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> EjectAsync(
        uint blockDeviceId, uint driveId, CancellationToken cancellationToken)
    {
        // The volume is preferred: it is what Windows ejects. A drive-level press is resolved to
        // the volume sitting on it, because the managers only eject volumes and devices.
        string? id = ResolveDevice(blockDeviceId) ?? ResolveDrive(driveId);
        RemovableDriveEntry? entry = id is null
            ? null
            : _drives.Drives.FirstOrDefault(drive => drive.Id == id);
        if (entry is null)
        {
            return Refuse("That drive is no longer present.");
        }
        if (!entry.ActionEnabled)
        {
            // Busy or already gone. Both are refusals with a reason rather than a silent no-op.
            return Refuse(entry.Ejected ? "That drive is already ejected." : "That drive is busy.");
        }

        try
        {
            await _drives.EjectAsync(entry).ConfigureAwait(false);
            return entry.Ejected
                ? SteamUiCommandResult.Applied
                : Refuse(entry.ResultText.Length > 0
                    ? entry.ResultText
                    : "Windows would not release that drive.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Steam storage: ejecting {id} failed: {ex.Message}");
            return Refuse("Could not eject that drive.");
        }
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> FormatAsync(uint driveId, CancellationToken cancellationToken)
    {
        if (!_formatAllowed())
        {
            return Refuse(
                "Formatting from Steam's pages is switched off in WSGM Settings.");
        }

        FormatTargetEntry? target = FindTarget(driveId);
        if (target is null)
        {
            return Refuse("That drive is no longer present.");
        }
        if (_formats.Busy)
        {
            return Refuse("Another format is already running.");
        }

        try
        {
            // The manager re-reads the disk's identity immediately before every destructive step,
            // so a card swapped between this call and the erase aborts there rather than here.
            await _formats.FormatAsync(target).ConfigureAwait(false);
            return SteamUiCommandResult.Applied;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Steam storage: formatting {driveId} failed: {ex.Message}");
            return Refuse("The format did not complete.");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Refused rather than faked. Neither manager exposes a trim, and the published state says
    /// <c>TrimSupported: false</c> so Steam should not offer it; answering anything else here would
    /// make a button that reports success and does nothing.
    /// </remarks>
    public Task<SteamUiCommandResult> TrimAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Refuse("WSGM does not run drive trimming."));

    /// <summary>A refusal that carries its reason, which the contract requires of every failure.</summary>
    /// <param name="reason">What the user is told, on the control they pressed.</param>
    /// <returns>The failed result.</returns>
    private static SteamUiCommandResult Refuse(string reason) => new(false, reason);

    /// <summary>Resolves one of Steam's drive numbers back to the format manager's identifier.</summary>
    /// <param name="driveId">The number Steam sent.</param>
    /// <returns>The identifier, or null when this bridge never issued that number.</returns>
    private string? ResolveDrive(uint driveId) => driveId == 0
        ? null
        : _driveIds.FirstOrDefault(pair => pair.Value == driveId).Key;

    private string? ResolveDevice(uint blockDeviceId) => blockDeviceId == 0
        ? null
        : _deviceIds.FirstOrDefault(pair => pair.Value == blockDeviceId).Key;

    private FormatTargetEntry? FindTarget(uint driveId)
    {
        string? id = ResolveDrive(driveId);
        return id is null ? null : _formats.Targets.FirstOrDefault(target => target.Id == id);
    }

    private string? FirstMountPath(uint driveId)
    {
        string? id = ResolveDrive(driveId);
        RemovableDriveEntry? entry = id is null
            ? null
            : _drives.Drives.FirstOrDefault(drive => drive.Id == id);
        IReadOnlyList<string> paths = entry is null ? [] : SplitLetters(entry.Letters);
        return paths.Count > 0 ? paths[0] : null;
    }

    /// <summary>Turns the manager's display string of drive letters into mount paths.</summary>
    /// <param name="letters">The entry's letters as it shows them, for example "D:, E:".</param>
    /// <returns>One path per letter, in the form Windows uses.</returns>
    internal static IReadOnlyList<string> SplitLetters(string letters) => (letters ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(part => part.EndsWith('\\') ? part : part + "\\")
        .ToArray();
}
