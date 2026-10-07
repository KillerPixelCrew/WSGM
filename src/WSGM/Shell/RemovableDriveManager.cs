using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Win32.SafeHandles;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>
///     Owns the UI-bound removable-storage inventory and serialized device or media eject workflow.
/// </summary>
/// <remarks>
///     Invoke lifecycle and row-mutating actions on the Avalonia UI thread. Native enumeration and
///     eject run on workers; volume notifications, explicit refresh and a ten-second fallback refresh
///     the inventory. Hot-pluggable disks use PnP device eject; built-in readers use media eject to
///     preserve the reader. Dispose releases timers and notifications but does not cancel an in-flight eject.
/// </remarks>
public sealed class RemovableDriveManager : ObservableObject, IDisposable
{
    /// <summary>
    ///     Total attempts for PnP vetoes or a lettered volume lock, with 500 ms between attempts.
    /// </summary>
    private const int EjectAttempts = 3;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    ///     Serializes eject attempts: one device at a time, so two rows
    ///     cannot interleave their lock/eject sequences.
    /// </summary>
    private readonly SemaphoreSlim _ejectGate = new(1, 1);

    private readonly MessageWindow? _messages;

    /// <summary>The storage read behind the current list, shared with the format flow and Steam's pages.</summary>
    private StorageInventory _inventory = StorageInventory.Empty;

    private int _refreshing;
    private DispatcherTimer? _settle;
    private DispatcherTimer? _timer;
    private MessageWindow? _window;

    /// <summary>Creates a drive manager that follows volume notifications on the composition's window.</summary>
    /// <param name="messages">
    ///     The composition's message window, or null for a surface that owns none (the Settings
    ///     preview), which then relies on the explicit and 10 s fallback refreshes alone.
    /// </param>
    public RemovableDriveManager(MessageWindow? messages = null)
    {
        _messages = messages;
    }

    /// <summary>
    ///     Gets the ejectable devices: one row per hot-pluggable device
    ///     (all its volumes together), one per removable-media volume.
    /// </summary>
    public ObservableCollection<RemovableDriveEntry> Drives { get; } = [];

    /// <summary>Gets whether at least one enumeration has completed since <see cref="Start" />.</summary>
    /// <remarks>
    ///     This is what separates "no removable storage" from "not looked yet". Both leave
    ///     <see cref="Drives" /> empty, and a consumer that publishes the list elsewhere -- Steam's
    ///     storage pages -- has to say "nothing here" when a card is pulled, but must not say it at
    ///     startup to someone who is holding a card the first scan has not reached.
    /// </remarks>
    public bool HasScanned { get; private set; }

    /// <summary>The storage read behind the latest list, or an empty one before the first read.</summary>
    /// <remarks>
    ///     Readable from any thread. Written on the refresh worker before its list reaches the UI thread, so
    ///     a consumer reacting to a list change sees the read that produced it and needs no walk of its own.
    /// </remarks>
    internal StorageInventory Inventory => Volatile.Read(ref _inventory);

    /// <summary>
    ///     Gets whether anything ejectable is present; the sheet shows
    ///     its eject pill only while this is true.
    /// </summary>
    public bool HasDrives
    {
        get;
        private set => SetFieldIfChanged(ref field, value, nameof(HasDrives));
    }

    /// <summary>
    ///     Gets the last thing that happened ("X is safe to remove", or why
    ///     an eject was refused), for the panel's status line. Empty when there is
    ///     nothing to report.
    /// </summary>
    public string StatusText
    {
        get;
        private set
        {
            if (!SetFieldIfChanged(ref field, value, nameof(StatusText)))
            {
                return;
            }

            Raise(nameof(HasStatus));
        }
    } = "";

    /// <summary>Gets whether a status line should be shown.</summary>
    public bool HasStatus => StatusText.Length > 0;

    /// <summary>
    ///     Runs just before a row's media is ejected, and again with the outcome.
    /// </summary>
    /// <remarks>
    ///     The session sets this to its library policy. Every surface that ejects comes through
    ///     <see cref="EjectAsync" />, so hanging the policy here is what makes an eject mean the same
    ///     thing whether it was pressed in the overlay or on Steam's storage page, without this
    ///     manager knowing what a Steam library is.
    /// </remarks>
    internal LibraryPolicy? EjectObserver { get; set; }

    /// <summary>The session's card manifest watcher, stood down for each eject. Set by the session.</summary>
    internal CardAcfWatcher? CardWatcher { get; set; }

    /// <summary>Stops the timers and volume notifications. Idempotent; bound values keep their last state.</summary>
    public void Dispose()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
        _settle?.Stop();
        _settle = null;
        if (_window is { } window)
        {
            window.VolumeChanged -= OnVolumeChanged;
            window.DeregisterVolumeNotifications();
            _window = null;
        }
    }

    /// <summary>
    ///     Performs a first refresh, follows volume arrival and removal, and starts the
    ///     slow fallback snapshot. UI-thread callers only. Idempotent.
    /// </summary>
    public void Start()
    {
        if (_timer is not null)
        {
            return;
        }

        QueueRefresh();
        if (_messages is { } window && window.RegisterVolumeNotifications())
        {
            _window = window;
            window.VolumeChanged += OnVolumeChanged;
        }

        // Media slipped into a reader that already has its volume raises no volume
        // notification, and neither does a disk with no volume at all.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>
    ///     Forces a full re-enumeration — bound to the panel's refresh
    ///     button and run when the panel opens.
    /// </summary>
    public void Refresh()
    {
        QueueRefresh();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        QueueRefresh();
    }

    private void OnVolumeChanged(bool _)
    {
        // The notification precedes the mount and its letter. Restarting the one-shot
        // collapses the burst one device produces into a single snapshot.
        if (_settle is null)
        {
            _settle = new DispatcherTimer { Interval = CardVolumeMonitor.SettleDelay };
            _settle.Tick += (_, _) =>
            {
                _settle?.Stop();
                QueueRefresh();
            };
        }

        _settle.Stop();
        _settle.Start();
    }

    /// <summary>Re-enumerates off the UI thread, at most one at a time.</summary>
    private void QueueRefresh()
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var inventory = StorageInventory.Read();
                Volatile.Write(ref _inventory, inventory);
                var devices = Project(inventory);
                Dispatcher.UIThread.Post(() => Apply(devices));
            }
            catch (Exception ex)
            {
                Log.Warn($"Eject: refresh failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        });
    }

    /// <summary>
    ///     Which eject path a disk's hotplug facts call for, or null when
    ///     the disk is internal fixed storage and must not be listed at all.
    /// </summary>
    /// <param name="deviceHotplug">The device itself is hot-pluggable.</param>
    /// <param name="mediaRemovable">The media can leave the device.</param>
    /// <returns>UsbDevice for a hot-pluggable disk, otherwise Media for removable media, otherwise null.</returns>
    internal static EjectKind? Classify(bool deviceHotplug, bool mediaRemovable)
    {
        return deviceHotplug ? EjectKind.UsbDevice
            : mediaRemovable ? EjectKind.Media
            : null;
    }

    /// <summary>
    ///     Classifies one disk number on a fresh handle: null for a
    ///     system/app disk or anything that does not answer as external
    ///     hot-pluggable/removable storage. Query access only — a read handle on
    ///     <c>\\.\PhysicalDriveN</c> needs elevation, so this must work unelevated.
    /// </summary>
    /// <param name="disk">The physical disk number.</param>
    /// <param name="systemDisks">The guarded disks from <see cref="ResolveSystemDisks" />.</param>
    /// <returns>The current external-storage eject kind, or null for a guarded, unreadable or internal disk.</returns>
    internal static EjectKind? ClassifyDisk(int disk, HashSet<int> systemDisks)
    {
        if (systemDisks.Contains(disk))
        {
            return null;
        }

        using var handle = NativeStorage.OpenDiskForQuery(disk);
        return !handle.IsInvalid
               && NativeStorage.TryGetHotplugInfo(handle, out var media, out var hotplug)
            ? Classify(hotplug, media)
            : null;
    }

    /// <summary>Formats a capacity for the row's status line.</summary>
    /// <param name="bytes">The size in bytes; nothing is shown for 0.</param>
    /// <returns>Invariant decimal MB/GB/TB text, or empty for nonpositive sizes.</returns>
    internal static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "";
        }

        // Decimal units, matching how storage is sold and labeled. Invariant:
        // the app publishes with InvariantGlobalization, and the tests must see
        // the same digits regardless of the machine locale.
        var invariant = CultureInfo.InvariantCulture;
        return bytes >= 1_000_000_000_000L
            ? (bytes / 1_000_000_000_000.0).ToString("0.#", invariant) + " TB"
            : bytes >= 1_000_000_000L
                ? (bytes / 1_000_000_000.0).ToString("0.#", invariant) + " GB"
                : Math.Max(1, bytes / 1_000_000L).ToString(invariant) + " MB";
    }

    /// <summary>Formats a device's drive letters ("E:" / "E:, F:").</summary>
    /// <param name="letters">The letters, in the order they were found.</param>
    /// <returns>Comma-separated letter/colon pairs, or empty for no letters.</returns>
    internal static string FormatLetters(IReadOnlyList<char> letters)
    {
        return string.Join(", ", letters.Select(l => $"{l}:"));
    }

    /// <summary>
    ///     Projects one storage read into the ejectable-device list: one row per hot-pluggable device, one
    ///     per removable-media volume. Touches no handle.
    /// </summary>
    /// <param name="inventory">The storage read to project.</param>
    /// <returns>The rows, ordered by disk number.</returns>
    internal static List<EjectableDevice> Project(StorageInventory inventory)
    {
        // Candidate volumes: mounted local disks. USB HDDs report Fixed, so the
        // type never filters; only network, optical and absent drives are left out.
        var volumes = inventory.Volumes
            .Select(volume => (volume.Letter, volume.Disk, Size: volume.CapacityBytes))
            .ToList();
        // A disk Windows mounts no partition from still has its interface and a row.
        foreach (var disk in inventory.ExternalDisks.Where(disk => disk.InterfacePath.Length > 0))
        {
            AddUnletteredDisk(volumes, disk.Number, disk.CapacityBytes);
        }

        var result = new List<EjectableDevice>();
        foreach (var group in volumes.GroupBy(v => v.Disk).OrderBy(g => g.Key))
        {
            // Internal storage and the guarded system and app disks are not external, whatever
            // their hotplug flags claim.
            if (inventory.FindDisk(group.Key) is not { } disk)
            {
                continue;
            }

            var letters = group.Select(v => v.Letter).Where(char.IsAsciiLetter).OrderBy(l => l).ToArray();
            var size = group.Sum(v => v.Size);
            if (disk.Kind == EjectKind.UsbDevice)
            {
                // One row per DEVICE: the PnP eject takes every partition at
                // once, and per-partition rows would invite a doomed second try.
                var id = disk.InstanceId.Length > 0 ? disk.InstanceId : $"disk:{group.Key}";
                result.Add(new EjectableDevice(
                        id, disk.Name, FormatLetters(letters), size, disk.Kind, disk.DevInst, letters.FirstOrDefault())
                    { VolumeLetters = letters });
            }
            else
            {
                // Media rows stay per-volume: a multi-slot reader ejects each
                // card on its own.
                result.AddRange(group.Select(volume => new EjectableDevice(
                    volume.Letter == '\0' ? $"media:{disk.InstanceId}:{group.Key}" : $"media:{volume.Letter}",
                    disk.Name, volume.Letter == '\0' ? "No Windows drive letter" : FormatLetters([volume.Letter]),
                    volume.Size, disk.Kind, disk.DevInst, volume.Letter)
                {
                    DiskPath = disk.InterfacePath,
                    VolumeLetters = char.IsAsciiLetter(volume.Letter) ? [volume.Letter] : []
                }));
            }
        }

        return result;
    }

    /// <summary>Adds one synthetic volumeless entry when a disk with known capacity has no mounted volume.</summary>
    /// <param name="volumes">Working projection list modified only when an entry is missing.</param>
    /// <param name="disk">Current physical disk number; negative values are ignored.</param>
    /// <param name="capacity">Whole-disk bytes; nonpositive values are ignored.</param>
    internal static void AddUnletteredDisk(List<(char Letter, int Disk, long Size)> volumes, int disk, long capacity)
    {
        if (disk >= 0 && capacity > 0 && volumes.All(volume => volume.Disk != disk))
        {
            volumes.Add(('\0', disk, capacity));
        }
    }

    /// <summary>
    ///     The disks the eject list must never contain: whatever Windows
    ///     itself and WSGM run from. Belt and braces — an internal disk already
    ///     fails the hotplug classification, but a USB-attached boot drive would
    ///     not. Shared with the Format flow's target list, which must never offer
    ///     these either.
    /// </summary>
    /// <returns>Successfully resolved Windows and application disk numbers; failed lookups do not invent an identity.</returns>
    internal static HashSet<int> ResolveSystemDisks()
    {
        var disks = new HashSet<int>();
        foreach (var root in new[]
                 {
                     Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
                     Path.GetPathRoot(AppContext.BaseDirectory)
                 })
        {
            if (root is not { Length: > 0 } || !char.IsAsciiLetter(root[0]))
            {
                continue;
            }

            using var volume = NativeStorage.OpenVolumeForQuery(char.ToUpperInvariant(root[0]));
            if (!volume.IsInvalid
                && NativeStorage.TryGetDeviceNumber(volume, out _, out var disk))
            {
                disks.Add(disk);
            }
        }

        return disks;
    }

    /// <summary>
    ///     Merges a fresh device list into the bound collection without
    ///     replacing surviving rows (gamepad-cursor discipline). Internal for the
    ///     reconcile tests; production callers reach it through the refresh path.
    /// </summary>
    /// <param name="fresh">The snapshot to merge.</param>
    internal void Apply(List<EjectableDevice> fresh)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var added = 0;
        foreach (var device in fresh)
        {
            seen.Add(device.Id);
            var row = FindDrive(device.Id);
            if (row is null)
            {
                row = new RemovableDriveEntry(device.Id, device.Kind);
                Drives.Add(row);
                added++;
                Log.Info($"Eject: found {device.Name} ({device.Letters}) "
                         + $"{(device.Kind == EjectKind.UsbDevice ? "usb-device" : "media")} "
                         + $"id={device.Id}");
            }

            row.Name = device.Name;
            row.Letters = device.Letters;
            row.VolumeLetters = device.VolumeLetters;
            row.SizeBytes = device.SizeBytes;
            row.SizeText = FormatSize(device.SizeBytes);
            row.DevInst = device.DevInst;
            row.VolumeLetter = device.VolumeLetter;
            row.DiskPath = device.DiskPath;
            if (!row.Ejected)
            {
                continue;
            }

            // Listed again after a successful eject = reinserted and mounted;
            // the row is back in ordinary service.
            row.Ejected = false;
            row.ResultText = "";
        }

        var removed = 0;
        for (var i = Drives.Count - 1; i >= 0; i--)
        {
            var row = Drives[i];
            // A row mid-eject is never removed: its outcome message is about to
            // land on it.
            if (row.Busy || seen.Contains(row.Id))
            {
                continue;
            }

            Drives.RemoveAt(i);
            removed++;
        }

        if (added > 0 || removed > 0)
        {
            Log.Info($"Eject: device list now {Drives.Count} row(s) "
                     + $"(+{added}/-{removed}).");
        }

        HasDrives = Drives.Count > 0;
        HasScanned = true;
    }

    private RemovableDriveEntry? FindDrive(string id)
    {
        return Drives.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
    }

    /// <summary>
    ///     Safely ejects one row's device or media, updating the row and
    ///     <see cref="StatusText" /> with the outcome.
    /// </summary>
    /// <param name="entry">The row to eject.</param>
    /// <returns>Completion after the eject attempt and observer cleanup, or immediately for a disabled row. Inspect the row and StatusText for the outcome.</returns>
    /// <remarks>
    ///     Up to three PnP-veto or lettered-volume-lock attempts are made, 500 ms apart. A lettered volume
    ///     is safe after successful lock and dismount even if mechanical eject is unsupported. Unlettered
    ///     media requires explicit eject success. Library reconciliation runs before and after the attempt;
    ///     ordinary failures update the row instead of escaping. No cancellation token is accepted.
    /// </remarks>
    public async Task EjectAsync(RemovableDriveEntry entry)
    {
        if (!entry.ActionEnabled)
        {
            return;
        }

        // Claim before awaiting the shared gate so a second press cannot queue the same row.
        entry.Busy = true;
        await _ejectGate.WaitAsync();
        var observer = EjectObserver;
        var succeeded = false;
        try
        {
            entry.ResultText = "";
            StatusText = $"Ejecting {entry.Name}...";
            // The ACF watcher holds directory handles on card volumes; a locked
            // volume with any other open handle vetoes the eject, so WSGM would
            // veto itself. Stand the watcher down until the eject has finished.
            using var cardWatch = CardWatcher?.Suspend();

            // Before the media goes, not after: ejecting first would leave Steam holding a library
            // on a volume that is gone, which its own UI renders as a disconnected drive until
            // something cleans up.
            if (observer is not null)
            {
                await observer.EjectingAsync(entry).ConfigureAwait(true);
            }

            var devInst = entry.DevInst;
            var letter = entry.VolumeLetter;
            var name = entry.Name;
            var diskPath = entry.DiskPath;
            var result = await Task.Run(() => entry.Kind == EjectKind.UsbDevice
                ? EjectDevice(devInst, name)
                : letter == '\0'
                    ? EjectUnletteredMedia(diskPath)
                    : EjectMediaVolume(letter, name));
            succeeded = result.Success;
            if (result.Success)
            {
                entry.Ejected = true;
                entry.ResultText = "Safe to remove";
                StatusText = $"{name} is safe to remove.";
            }
            else
            {
                entry.ResultText = result.Message;
                StatusText = result.Message;
            }

        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Eject: {entry.Name} failed unexpectedly: {ex.Message}");
            entry.ResultText = "Eject failed";
            StatusText = $"Could not eject {entry.Name}: {ex.Message}";
        }
        finally
        {
            if (observer is not null)
            {
                try
                {
                    await observer.EjectedAsync(entry, succeeded).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.Warn($"Eject library reconciliation failed: {ex.Message}");
                }
            }

            entry.Busy = false;
            _ejectGate.Release();
        }

        Refresh();
    }

    private static EjectResult EjectUnletteredMedia(string path)
    {
        using var query = NativeStorage.OpenVolumeForQueryPath(path);
        if (query.IsInvalid || !NativeStorage.TryGetDeviceNumber(query, out _, out var disk)
                            || ClassifyDisk(disk, ResolveSystemDisks()) != EjectKind.Media)
        {
            return new EjectResult(false, "The removable medium changed. Refresh and try again.");
        }

        // Keep every exposed volume locked until the medium has been ejected.
        var locks = new List<SafeFileHandle>();
        try
        {
            foreach (var volumePath in NativeStorage.ListVolumeInterfaces())
            {
                using var volume = NativeStorage.OpenVolumeForQueryPath(volumePath);
                if (volume.IsInvalid || !NativeStorage.TryGetDeviceNumber(volume, out _, out var volumeDisk)
                                     || volumeDisk != disk)
                {
                    continue;
                }

                var locked = NativeStorage.OpenDeviceForMediaEject(volumePath);
                locks.Add(locked);
                if (locked.IsInvalid || !NativeStorage.LockVolume(locked))
                {
                    return new EjectResult(false,
                        "The card is still in use. Close its applications before ejecting it.");
                }
            }

            if (locks.Any(locked => !NativeStorage.DismountVolume(locked)))
            {
                return new EjectResult(false, "Windows could not dismount the card. It has not been ejected.");
            }

            using var handle = NativeStorage.OpenDeviceForMediaEject(path);
            if (handle.IsInvalid || !NativeStorage.EjectMedia(handle))
            {
                return new EjectResult(false,
                    "Windows could not eject this medium. No safe-removal confirmation was received.");
            }

            return new EjectResult(true, "");
        }
        finally
        {
            foreach (var locked in locks)
            {
                locked.Dispose();
            }
        }
    }

    /// <summary>
    ///     The PnP device eject, with retries for transient holders.
    ///     Worker thread only.
    /// </summary>
    private static EjectResult EjectDevice(uint diskDevInst, string name)
    {
        if (diskDevInst == 0)
        {
            Log.Warn($"Eject: no devnode for {name}.");
            return new EjectResult(false, "Windows could not identify this device.");
        }

        var target = NativeStorage.FindEjectTarget(diskDevInst);
        Log.Info($"Eject: requesting device eject for {name} "
                 + $"(devnode {NativeStorage.GetDeviceInstanceId(target)}).");
        var vetoType = NativeStorage.PnpVetoType.TypeUnknown;
        var vetoName = "";
        for (var attempt = 1; attempt <= EjectAttempts; attempt++)
        {
            var code = NativeStorage.RequestDeviceEject(target, out vetoType, out vetoName);
            if (code == NativeStorage.CrSuccess)
            {
                Log.Info($"Eject: {name} ejected (attempt {attempt}).");
                return new EjectResult(true, "");
            }

            if (code != NativeStorage.CrRemoveVetoed)
            {
                Log.Warn($"Eject: {name} failed with CONFIGRET {code}.");
                return new EjectResult(false,
                    $"Windows could not remove this drive (error {code}).");
            }

            Log.Info($"Eject: {name} vetoed (attempt {attempt}, "
                     + $"type {(int)vetoType} {vetoType}, by '{vetoName}').");
            if (attempt < EjectAttempts)
            {
                Thread.Sleep(RetryDelay);
            }
        }

        return new EjectResult(false, DescribeVeto(vetoType, vetoName));
    }

    /// <summary>
    ///     The media-level eject for a built-in reader's card. Worker
    ///     thread only.
    /// </summary>
    private static EjectResult EjectMediaVolume(char letter, string name)
    {
        Log.Info($"Eject: dismounting media volume {letter}: ({name}).");
        using var volume = NativeStorage.OpenVolumeForEject(letter);
        if (volume.IsInvalid)
        {
            Log.Warn($"Eject: could not open {letter}: "
                     + $"(Win32 {NativeStorage.LastWin32Error()}).");
            return new EjectResult(false, $"Could not open drive {letter}:.");
        }

        // The lock is the open-files check: it fails while anything else holds a
        // handle on the volume.
        var locked = false;
        for (var attempt = 1; attempt <= EjectAttempts; attempt++)
        {
            if (NativeStorage.LockVolume(volume))
            {
                locked = true;
                break;
            }

            Log.Info($"Eject: volume {letter}: still in use "
                     + $"(attempt {attempt}, Win32 {NativeStorage.LastWin32Error()}).");
            if (attempt < EjectAttempts)
            {
                Thread.Sleep(RetryDelay);
            }
        }

        if (!locked)
        {
            return new EjectResult(false,
                "Still in use — a running game or an active Steam download may have "
                + "files open on this card. Close it and try again.");
        }

        if (!NativeStorage.DismountVolume(volume))
        {
            Log.Warn($"Eject: dismount of {letter}: failed "
                     + $"(Win32 {NativeStorage.LastWin32Error()}).");
            return new EjectResult(false, $"Could not dismount drive {letter}:.");
        }

        // Many readers have no motorized eject and fail this call; the lock and
        // dismount above are what makes the card safe to pull.
        if (!NativeStorage.EjectMedia(volume))
        {
            Log.Info($"Eject: media-eject call for {letter}: not supported "
                     + $"(Win32 {NativeStorage.LastWin32Error()}); dismount succeeded.");
        }

        Log.Info($"Eject: {letter}: dismounted and safe to remove.");
        return new EjectResult(true, "");
    }

    /// <summary>
    ///     Formats a PnP veto using its reported application or service name when available.
    /// </summary>
    /// <param name="vetoType">The veto reason Windows reported.</param>
    /// <param name="vetoName">The vetoing module/service/path, possibly empty.</param>
    /// <returns>A refusal message; generic open-handle guidance suggests possible holders without identifying one.</returns>
    internal static string DescribeVeto(NativeStorage.PnpVetoType vetoType, string vetoName)
    {
        return vetoType switch
        {
            NativeStorage.PnpVetoType.WindowsApp when vetoName.Length > 0 =>
                $"Still in use by {vetoName}. Close it and try again.",
            NativeStorage.PnpVetoType.WindowsApp
                or NativeStorage.PnpVetoType.OutstandingOpen
                or NativeStorage.PnpVetoType.PendingClose =>
                "Still in use — a running game or an active Steam download may have "
                + "files open on this drive. Close it and try again.",
            NativeStorage.PnpVetoType.WindowsService when vetoName.Length > 0 =>
                $"The Windows service '{vetoName}' is blocking removal.",
            NativeStorage.PnpVetoType.WindowsService =>
                "A Windows service is blocking removal.",
            NativeStorage.PnpVetoType.InsufficientRights =>
                "Windows denied the removal (insufficient rights).",
            _ => "Windows refused to remove this drive right now. Try again in a moment."
        };
    }

    /// <summary>One ejectable device as the background snapshot reports it.</summary>
    /// <param name="Id">The row identity (device instance path, or "media:X").</param>
    /// <param name="Name">The device display name.</param>
    /// <param name="Letters">The volume letters, formatted.</param>
    /// <param name="SizeBytes">Total capacity across the listed volumes.</param>
    /// <param name="Kind">Which eject path applies.</param>
    /// <param name="DevInst">The disk devnode (PnP eject rows).</param>
    /// <param name="VolumeLetter">The letter to lock (media rows).</param>
    internal sealed record EjectableDevice(
        string Id,
        string Name,
        string Letters,
        long SizeBytes,
        EjectKind Kind,
        uint DevInst,
        char VolumeLetter)
    {
        /// <summary>Current disk interface path for media without a drive letter; empty when unavailable.</summary>
        internal string DiskPath { get; init; } = "";
        /// <summary>Drive letters included in this eject action, used by library reconciliation.</summary>
        internal IReadOnlyList<char> VolumeLetters { get; init; } = [];
    }

    private readonly record struct EjectResult(bool Success, string Message);
}
