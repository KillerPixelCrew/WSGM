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
internal sealed class SteamStorageBridge : ISteamStorageBackend, IDisposable
{
    private readonly RemovableDriveManager _drives;
    private readonly SdFormatManager _formats;
    private readonly Func<bool> _formatAllowed;

    // Steam addresses drives and volumes by uint32 and both managers key on strings. These hold the
    // mapping for the life of the session so an id Steam saw stays the same id on the next read —
    // a renumbered row is a row the user's selection jumps off.
    private readonly Dictionary<string, uint> _driveIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, uint> _deviceIds = new(StringComparer.Ordinal);

    /// <summary>The session's library policy, or null when this session owns none.</summary>
    private readonly LibraryPolicy? _policy;
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
    /// <param name="policy">
    /// The session's library policy, or null for a session that owns none. Adopting through
    /// Steam's page is the user overriding a standing eject, and this is what tells the policy.
    /// </param>
    internal SteamStorageBridge(
        RemovableDriveManager drives, SdFormatManager formats, Func<bool> formatAllowed,
        LibraryPolicy? policy = null)
    {
        _drives = drives ?? throw new ArgumentNullException(nameof(drives));
        _formats = formats ?? throw new ArgumentNullException(nameof(formats));
        _formatAllowed = formatAllowed ?? throw new ArgumentNullException(nameof(formatAllowed));
        _policy = policy;
        _drives.Drives.CollectionChanged += OnDrivesChanged;
        _formats.Refresh();
    }

    /// <summary>Stops following the drive manager's list.</summary>
    public void Dispose() => _drives.Drives.CollectionChanged -= OnDrivesChanged;

    private void OnDrivesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        _formats.Refresh();

    /// <summary>Projects what the managers currently see into Steam's own state shape.</summary>
    /// <returns>The state, or null until the first enumeration has completed.</returns>
    /// <remarks>
    /// Null and empty mean different things on this wire. Null is "nothing to say", which leaves
    /// Steam's page as it was; an empty state is the assertion that the machine has no removable
    /// storage, which the page renders as such. Reporting empty before the managers have scanned
    /// would show "no drives" to someone holding a card.
    /// <para>
    /// But empty after a scan is a real answer and has to be sent. It was not: an empty result
    /// returned null unconditionally, so when the last card was pulled -- or ejected from Windows
    /// rather than from Steam -- nothing was published, the gate kept the last state it had, and
    /// Steam went on showing a drive that was no longer in the machine. The drive manager's first
    /// completed scan is what draws the line between the two cases.
    /// </para>
    /// </remarks>
    public SteamStorageState? ReadState()
    {
        var ejectable = _drives.Drives.ToArray();
        var formattable = _formats.Targets.ToArray();
        if (ejectable.Length == 0 && formattable.Length == 0)
        {
            if (!_drives.HasScanned)
            {
                return null;
            }

            LogProjection([], [], adoptSupported: true, unmountSupported: false);
            return new SteamStorageState(
                [],
                [],
                AdoptSupported: true,
                UnmountSupported: false,
                TrimSupported: false,
                TrimRunning: false);
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
            IReadOnlyList<string> libraries = paths.Count == 0 ? [] : LibraryPathsOn(paths[0]);
            devices.Add(new SteamStorageBlockDevice(
                Id: DeviceId(entry.Id),
                DriveId: MatchingDrive(volume, formattable),
                Label: volume is null || volume.Label.Length == 0 ? entry.Name : volume.Label,
                FriendlyPath: paths.Count > 0 ? paths[0] : "",
                SizeBytes: volume?.CapacityBytes ?? entry.SizeBytes,
                MountPaths: [.. paths, .. libraries],
                HasSteamLibrary: libraries.Count > 0));
        }

        // Steam gates its two drive-menu entries on these: Eject on unmount support, Format on
        // adopt support. Unmount is reported against the rows that can actually be ejected rather
        // than against the drive list, because a machine with a formattable disk and nothing
        // ejectable would otherwise offer an eject with no row behind it.
        bool unmountSupported = devices.Count > 0;
        LogProjection(drives, devices, adoptSupported: true, unmountSupported);

        // Trim is reported unsupported because neither manager exposes one. Claiming otherwise would
        // put a button on Steam's page that could never do anything.
        return new SteamStorageState(
            drives,
            devices,
            AdoptSupported: true,
            UnmountSupported: unmountSupported,
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
    /// <param name="adoptSupported">Whether Steam may offer Format; it gates that entry on this.</param>
    /// <param name="unmountSupported">Whether Steam may offer Eject; it gates that entry on this.</param>
    /// <remarks>
    /// This exists because the alternative was reading the projection out of a running Steam, and
    /// attaching a debugger to Steam's renderer while it is still building its UI wedges
    /// steamwebhelper. Every field Steam renders a decision from is here — the parent drive, the
    /// library flag, the sizes — so the published state can be checked from the log while Steam is
    /// left alone. Logged only when it changes, because it is produced on a poll.
    /// </remarks>
    private void LogProjection(
        IReadOnlyList<SteamStorageDrive> drives,
        IReadOnlyList<SteamStorageBlockDevice> devices,
        bool adoptSupported,
        bool unmountSupported)
    {
        var summary = string.Join("; ", drives.Select(drive =>
                $"drive {drive.Id} '{drive.Model}' {drive.SizeBytes}B "
                + $"unformatted={drive.Unformatted}")
            .Concat(devices.Select(device =>
                $"volume {device.Id} '{device.Label}' {device.FriendlyPath} {device.SizeBytes}B "
                + $"onDrive={device.DriveId} steamLibrary={device.HasSteamLibrary} "
                + $"mounts=[{string.Join(" ", device.MountPaths)}]")));
        // The support flags decide whether Steam draws its Eject and Format entries at all --
        // eject on unmount, format on adopt -- so a row that is right and a menu that is empty is
        // answered here rather than by reading it out of the client.
        var rows = summary.Length == 0 ? "no removable storage" : summary;
        summary = $"{rows} | adopt={adoptSupported} unmount={unmountSupported}";
        if (summary == _loggedProjection)
        {
            return;
        }

        _loggedProjection = summary;
        Log.Info($"Steam storage: {summary}");
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

    /// <summary>Every Steam library registered on a volume, as Steam spells the path.</summary>
    /// <param name="path">Any path on the volume, for example <c>D:\</c>.</param>
    /// <returns>The registered library paths on it, which may be empty.</returns>
    /// <remarks>
    /// These have to travel in the volume's <c>mount_paths</c>, and that is not cosmetic. Steam's
    /// library-folder row finds the volume behind a folder by looking for a block device whose
    /// mount paths <em>contain the folder path</em> — <c>D:\SteamLibrary</c>, not <c>D:\</c> — and
    /// the row's eject calls <c>Unmount</c> with the id of whatever that lookup returned. Publishing
    /// only the volume root means the lookup finds nothing, so the eject has nothing to call and
    /// the press does nothing at all, with no request leaving the client.
    /// </remarks>
    private static IReadOnlyList<string> LibraryPathsOn(string path)
    {
        string root = SteamLibraryVdf.VolumeRoot(path);
        if (root.Length == 0)
        {
            return [];
        }

        try
        {
            if (!Steam.TryReadLibraryFolders(out _, out string? vdf) || vdf is null)
            {
                return [];
            }

            return SteamLibraryVdf.ValuesOf(vdf, "path")
                .Where(library => string.Equals(SteamLibraryVdf.VolumeRoot(library), root,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException)
        {
            return [];
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Steam's storage page never sends Format. Its Format Drive modal sends Adopt with the name
    /// the user typed, because on SteamOS adopting a drive that carries no filesystem is what
    /// erases it. So this is two operations under one name, and which one is decided by the disk
    /// rather than by the request: no mountable filesystem means erase and register, and goes
    /// behind the same switch as any other erase started from Steam's pages; a filesystem already
    /// there means register what is on it, which is never destructive. The validate flag is
    /// Steam's and is logged, not acted on -- WSGM's format already re-verifies the disk before
    /// every destructive step, so there is no lighter variant of it to offer.
    /// </remarks>
    public async Task<SteamUiCommandResult> AdoptAsync(
        uint driveId, string label, bool validate, CancellationToken cancellationToken)
    {
        Log.Info($"Steam storage: adopt requested (drive {driveId}, label '{label}', "
            + $"validate={validate}).");
        FormatTargetEntry? target = FindTarget(driveId);
        if (target is null)
        {
            Log.Warn($"Steam storage: adopt refused, no disk answers to drive {driveId}.");
            return Refuse("That drive is no longer present.");
        }

        string? path = FirstMountPath(driveId);
        if (path is null)
        {
            // Nothing mountable on it: this is the erase-and-register adopt.
            if (!_formatAllowed())
            {
                return Refuse("Formatting from Steam's pages is switched off in WSGM Settings.");
            }
            if (_formats.Busy)
            {
                return Refuse("Another format is already running.");
            }

            try
            {
                await _formats.FormatAsync(target, label.Length > 0 ? label : null)
                    .ConfigureAwait(false);
                return SteamUiCommandResult.Applied;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"Steam storage: adopting {driveId} by formatting failed: {ex.Message}");
                return Refuse("The format did not complete.");
            }
        }

        try
        {
            // Adopting is the user overriding a standing eject, so it is dropped before the
            // registration rather than after: the monitor must not see the add and the intent at
            // the same time and decide the add was a mistake.
            _policy?.ClearEjected(path);

            // The manager registers the folder with the running client and reconciles Steam's own
            // library file; a drive root becomes <root>SteamLibrary, which is Steam's own layout.
            await _formats.AddLibraryAsync(path).ConfigureAwait(false);
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
        // Logged on arrival, not only on failure. Steam's own UI decides whether to send this at
        // all, so "nothing happened" has two very different causes — the press never left the
        // client, or it arrived and was refused — and only one of them is WSGM's to fix.
        Log.Info($"Steam storage: eject requested (volume {blockDeviceId}, drive {driveId}).");

        // The volume is preferred: it is what Windows ejects. A drive-level press is resolved to
        // the volume sitting on it, because the managers only eject volumes and devices.
        string? id = ResolveDevice(blockDeviceId) ?? ResolveDrive(driveId);
        RemovableDriveEntry? entry = id is null
            ? null
            : _drives.Drives.FirstOrDefault(drive => drive.Id == id);
        if (entry is null)
        {
            Log.Warn($"Steam storage: eject refused, no row answers to volume {blockDeviceId} "
                + $"or drive {driveId}.");
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

    /// <summary>The mount path of the first readable volume on one of Steam's drives.</summary>
    /// <param name="driveId">The drive number Steam sent.</param>
    /// <returns>A mount path such as <c>D:\</c>, or null when nothing mountable is on the disk.</returns>
    /// <remarks>
    /// Resolved through the disk number, the same join the published state uses. The earlier
    /// version looked the format target's identifier up in the drive manager's list, which keys on
    /// a different identifier for the same card, so it never matched and every adopt of a mounted
    /// drive was refused as no longer present.
    /// </remarks>
    private string? FirstMountPath(uint driveId)
    {
        FormatTargetEntry? target = FindTarget(driveId);
        if (target is null)
        {
            return null;
        }

        StorageVolume? volume = WindowsStorage.DescribeVolumes()
            .FirstOrDefault(candidate => candidate.Ready && candidate.DiskNumber == target.DiskNumber);
        return volume?.MountPath;
    }

    /// <summary>Turns the manager's display string of drive letters into mount paths.</summary>
    /// <param name="letters">The entry's letters as it shows them, for example "D:, E:".</param>
    /// <returns>One path per letter, in the form Windows uses.</returns>
    internal static IReadOnlyList<string> SplitLetters(string letters) => (letters ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(part => part.EndsWith('\\') ? part : part + "\\")
        .ToArray();
}
