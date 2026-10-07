# Storage identity and event-driven availability for Issue 200

## What identity survives media removal and a changed mount point?

### Takeaway

Bind a managed shortcut to an existing logical source id plus the expected local volume GUID and a
volume-relative backing path. Use current drive letters only as resolved presentation/compatibility
paths; never discover replacement content by filename, label, disk number, or serial alone. This is
a recommendation grounded in the
[issue's explicit identity and no-remap requirements](https://github.com/KillerPixelCrew/WSGM/issues/200).

### Cited Findings

- Windows volume GUID paths identify volumes independently of letters; one volume may have multiple
  GUID paths. They are assigned at initial installation and formatting. Labels can collide and
  letters change.
  [Microsoft: Naming a Volume](https://learn.microsoft.com/en-us/windows/win32/fileio/naming-a-volume).
- Mount Manager retains names in the host's `HKLM\SYSTEM\MountedDevices` database after a volume
  goes offline and reconstructs links on return/reboot using the underlying volume unique id. This
  supports same-installation reinsertion; it does not establish a portable cross-machine GUID
  guarantee.
  [Microsoft: Mount Manager requests](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/supporting-mount-manager-requests-in-a-storage-class-driver).
- `GetVolumeInformationW` returns a filesystem serial assigned at formatting, not the manufacturer's
  physical device serial. Its output is a DWORD; Microsoft does not promise global uniqueness.
  [Microsoft: GetVolumeInformationW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getvolumeinformationw).
- Physical disk numbers are only stable until removal/restart, so the current inventory's join key
  cannot be durable shortcut identity.
  [Microsoft: IOCTL_STORAGE_GET_DEVICE_NUMBER](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-ioctl_storage_get_device_number);
  [StorageInventory.cs:10](D:/Coding/WSGM/src/WSGM/Shell/StorageInventory.cs:10).
- `GetVolumePathNameW` finds the actual volume root even through mounted folders/junctions.
  `GetVolumeNameForVolumeMountPointW` resolves that root to a GUID path;
  `GetVolumePathNamesForVolumeNameW` returns all current letters/mounted folders.
  [Microsoft: GetVolumePathNameW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getvolumepathnamew),
  [GUID resolution](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getvolumenameforvolumemountpointw),
  [current mount paths](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getvolumepathnamesforvolumenamew).
- WSGM already resolves a letter to a volume GUID to avoid acting on a different card after letter
  reuse. That helper currently accepts only a char; its volume-information wrapper discards serial
  output. Both are reusable starting points, with general root-path resolution needed for directory
  mounts. [NativeStorage.cs:832](D:/Coding/WSGM/src/WSGM/Interop/NativeStorage.cs:832),
  [NativeStorage.cs:881](D:/Coding/WSGM/src/WSGM/Interop/NativeStorage.cs:881).
- Configured shortcut folders already have a never-reused `folder:` id. Imported records already
  contain source/key/appid and exact written command fields, but those fields do not constitute a
  separate backing-storage binding.
  [GameLibraryConfig.cs:16](D:/Coding/WSGM/src/WSGM/Core/Library/GameLibraryConfig.cs:16),
  [ImportPlan.cs:39](D:/Coding/WSGM/src/WSGM/Core/Library/ImportPlan.cs:39).

### Inferences

- Recommended source binding: `SourceId`, friendly `Name`, source kind, `BindingKind` (local-volume
  or configured-network), `ExpectedVolumeGuidPath`, optional observed filesystem serial/filesystem
  name as additional mismatch evidence, `SourceRootRelativePath`, and last known mount path for
  diagnostics. Recommended title binding: source id, stable source title key, Steam shortcut id,
  source type, and authoritative backing file/directory path relative to that volume. Keep existing
  exact command fields intact for ownership/conflict checks. A ROM's backing file is its ROM, not
  merely the installed emulator. [Issue 200](https://github.com/KillerPixelCrew/WSGM/issues/200),
  [current record](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/Library/ImportPlan.cs#L39).
- Combined-scope requirement from the parent's latest user clarification: the full ROM importer is
  the authoritative producer now. Give every configured importer profile/source root its own stable
  source id, allow several profiles to share one physical volume binding, and attach per-ROM
  relative paths to those profiles. A parsed multi-file title needs the required backing-path set
  (for example its manifest plus required track/content files), with explicit required/optional
  distinction. The emulator route is a separate required input and may live on another volume. Index
  each required input by its own resolved volume/source so either dependency can invalidate the
  title without pretending the emulator's disk is the ROM's disk. These are recommended interface
  requirements for the authorized combined importer/availability scope, extending the
  [issue's authoritative ROM-path and source metadata requirements](https://github.com/KillerPixelCrew/WSGM/issues/200).
- With the combined Emulator Manager research scope, persist a stable emulator id independently of
  the ROM volume/source reference; resolve the installed emulator/version through its manager.
  Reasons should distinguish `RomMediaUnavailable`, `ContentMissing`, `EmulatorUnavailable`, and
  `DependencyMissing` while retaining shared identity/access errors. Uninstalling/updating/moving an
  emulator never deletes or rebinds its ROM library; only the launchable projection changes. This is
  a recommended shared interface for
  [availability](https://github.com/KillerPixelCrew/WSGM/issues/200) and
  [Emulator Manager](https://github.com/KillerPixelCrew/WSGM/issues/203), not an inspected
  emulator-install implementation.
- Resolve imported absolute paths while their expected source is present, with
  mounted-folder/junction handling. Normalize/reject escaping `..` or rooted relative paths; record
  the actual endpoint volume rather than assume every file under a source text prefix stays on its
  outer drive. File opens/checks then use the expected GUID root and relative path. Multiple current
  mount paths for one volume are aliases, not multiple libraries.
  [Microsoft path resolution](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getvolumepathnamew),
  [current mount paths](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getvolumepathnamesforvolumenamew).
- A serial match cannot authorize rebinding. A finite format-generated serial can collide and a
  cloned filesystem can preserve serial/content. A changed GUID, reformatted card, unexpected serial
  when both are known, conflicting candidates, or duplicate logical marker means
  unavailable/identity mismatch pending explicit reassociation. Never search another card for the
  same basename. Existing Steam content ids may enrich a source's library badge/diagnostics but must
  not turn a mismatched physical binding into an automatic alias.
  [Microsoft serial contract](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getvolumeinformationw),
  [Issue 200](https://github.com/KillerPixelCrew/WSGM/issues/200).
- Migration: preserve all imported titles. Bind old authoritative backing paths only when the
  corresponding configured source is online and verified. If offline, retain the old path as a
  pending hint and publish `Unknown`/needs binding, not a fabricated volume id. A future Windows
  reinstall, move to another PC, clone replacement or lost Mount Manager registration needs an
  explicit source reassociation flow. A media-side logical id could help suggest a candidate later;
  writing a new marker is unnecessary for the basic feature and does not solve cloning ambiguity.
  [Mount Manager host persistence](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/supporting-mount-manager-requests-in-a-storage-class-driver),
  [Issue 200 metadata requirements](https://github.com/KillerPixelCrew/WSGM/issues/200).

### Gaps

- These identifiers do not provide cryptographic or perfect physical-card identity against exact
  clones. No reviewed Microsoft API promises that a GUID is portable unchanged between machines or
  after formatting/reinstallation; report same-host persistence only. No card operations or identity
  probes were run.
- Path/GUID compatibility must be proved for the actual launcher/emulator. Translating back to a
  letter then letting another process reopen it has a letter-reassignment race. The launch interface
  should carry the resolved expected-volume path and generation; compatibility fallback must not
  silently choose another media file.

## Which existing events can update availability promptly?

### Takeaway

Reuse `MessageWindow` for PnP arrival/removal and resume, with an independent availability owner.
Arrival means attached, not ready. Pure drive-letter/mount-folder changes need a mount-name signal
or bounded metadata fallback; changing to a different PnP registration API alone does not cover that
gap. [MessageWindow.cs:201](D:/Coding/WSGM/src/WSGM/Interop/MessageWindow.cs:201),
[Microsoft mount-change notification](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/mountmgr/ni-mountmgr-ioctl_mountmgr_change_notify).

### Cited Findings

- `MessageWindow.RegisterVolumeNotifications` is reference-counted and explicitly registers
  `GUID_DEVINTERFACE_VOLUME`; `VolumeChanged(bool)` deliberately drops identity and arrives on the
  UI thread. Existing code requires settling because Windows may not have finished assigning mount
  paths. Resume signals already exist.
  [MessageWindow.cs:201](D:/Coding/WSGM/src/WSGM/Interop/MessageWindow.cs:201),
  [MessageWindow.cs:414](D:/Coding/WSGM/src/WSGM/Interop/MessageWindow.cs:414),
  [MessageWindow.cs:645](D:/Coding/WSGM/src/WSGM/Interop/MessageWindow.cs:645).
- The window is message-only. Microsoft says such windows receive no broadcasts. Generic
  `DBT_DEVTYP_VOLUME` broadcasts go to top-level windows and cannot themselves be registered through
  `RegisterDeviceNotification`; the current explicit device-interface registration is therefore
  materially different. [MessageWindow.cs:45](D:/Coding/WSGM/src/WSGM/Interop/MessageWindow.cs:45),
  [Microsoft: Message-only windows](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#message-only-windows),
  [RegisterDeviceNotification](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerdevicenotificationa).
- `CM_Register_Notification` is available from Windows 8, needs no HWND, and permits
  device-interface callbacks. It does not replay existing devices: register first, enumerate
  afterward, and tolerate duplicate arrival/enumeration sightings. Both native event APIs require
  fast callbacks with blocking I/O moved elsewhere.
  [CM_Register_Notification](https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_register_notification),
  [RegisterDeviceNotification](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerdevicenotificationa).
- `IOCTL_MOUNTMGR_CHANGE_NOTIFY` queues a request when the caller's counter is current and completes
  it when the persistent mount-name database changes, then returns its new counter. It is a relevant
  additional primitive for letter/folder-only changes.
  [Microsoft: mount-change notification](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/mountmgr/ni-mountmgr-ioctl_mountmgr_change_notify).
- A normal hidden top-level window receives default device/volume broadcasts without the
  HWND_MESSAGE limitation. The WMI `Win32_VolumeChangeEvent` contract explicitly mentions addition
  of a drive letter or mounted drive, maps events to WM_DEVICECHANGE/WM_SETTINGCHANGE, and excludes
  network drives. The reviewed native WM_DEVICECHANGE docs describe device/media events, but do not
  explicitly promise every mounted-folder name edit.
  [Default broadcasts](https://learn.microsoft.com/en-us/windows/win32/devio/detecting-media-insertion-or-removal),
  [Win32_VolumeChangeEvent](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-volumechangeevent),
  [WM_DEVICECHANGE](https://learn.microsoft.com/en-us/windows/win32/devio/wm-devicechange).
- `CardVolumeMonitor` already debounces for three seconds and performs a startup pass, but its scan
  is specifically external removable volumes with `SteamLibrary` markers; its job is registration
  policy, and Steam work waits for readiness. It is not a generic managed-content monitor.
  [CardVolumeMonitor.cs:53](D:/Coding/WSGM/src/WSGM/Shell/CardVolumeMonitor.cs:53),
  [CardVolumeMonitor.cs:147](D:/Coding/WSGM/src/WSGM/Shell/CardVolumeMonitor.cs:147),
  [CardVolumeMonitor.cs:560](D:/Coding/WSGM/src/WSGM/Shell/CardVolumeMonitor.cs:560).
- `WindowsStorage.DescribeVolumes` currently reads readiness, volume label, total/free bytes and
  disk number; network drives are intentionally excluded. WSGM `StorageInventory.Read` also opens
  every disk/volume and reads capacity/classification. Neither is a sufficiently minimal every-event
  identity-only source index.
  [WindowsStorage.cs:47](D:/Coding/WSGM/external/windows-device-control/src/WindowsDeviceControl/WindowsStorage.cs:47),
  [StorageInventory.cs:92](D:/Coding/WSGM/src/WSGM/Shell/StorageInventory.cs:92).

### Inferences

- Smallest owner: one managed-content availability manager owned by `ShellSession`, native
  identity/mount helpers in `NativeStorage`, pure status/metadata rules in Core. Subscribe to the
  existing volume event independently of CardVolumeMonitor's Steam policy or CEF master switch.
  Startup registers first and reconciles remembered sources even with Steam closed; Steam reconnect
  gets the latest snapshot rather than restarting detection. Use existing shutdown
  cancellation/disposal and resume entry points.
  [Core guidance](D:/Coding/WSGM/src/WSGM/Core/AGENTS.md:9),
  [ShellSession.cs:1002](D:/Coding/WSGM/src/WSGM/Shell/ShellSession.cs:1002),
  [ShellSession.Power.cs:85](D:/Coding/WSGM/src/WSGM/Shell/ShellSession.Power.cs:85).
- On a volume burst: one coalesced mount/topology refresh, then schedule only sources whose expected
  GUID/root mapping or readiness changed. Since the current event has no identity, compare against
  an in-memory table rather than run per-ROM checks on every event. Removal first invalidates old
  checks by generation; absence of the expected volume marks all its titles unavailable without
  opening each file. Arrival uses a short settle delay and a finite read-only retry budget while
  mounting finishes. Failure leaves unknown/not ready until the next event or low-frequency retry.
  [existing settle pattern](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Shell/CardVolumeMonitor.cs#L199),
  [Issue 200 event requirements](https://github.com/KillerPixelCrew/WSGM/issues/200).
- Add one owned asynchronous mount-name subscription for prompt pure mount-point changes; keep PnP
  signals for physical disappearance, since offline teardown need not change persistent names. The
  IOCTL is useful, but a small adapter needs handle/cancellation proof before adoption. If
  unavailable, slowly refresh mount metadata for configured GUIDs only, without checking their
  contents. Moving to `CM_Register_Notification` is optional if a windowless reusable WindowsStorage
  watch is desired; it is not required to start this feature.
  [mount database](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/supporting-mount-manager-requests-in-a-storage-class-driver),
  [change notification](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/mountmgr/ni-mountmgr-ioctl_mountmgr_change_notify).
- Comparing the two small options: a hidden top-level notification window needs one HWND lifetime,
  validated volume payload parsing and broadcast coalescing; it likely improves drive-letter event
  coverage, but that likelihood is not complete proof for folder mount edits. Keep the established
  HWND_MESSAGE owner and add one mount-manager handle with one pending asynchronous counter request
  that rearms after completion, cancelling/disposing it at shutdown. This is the smallest option
  whose documented event condition directly covers the persistent letter/folder-name requirement; no
  generic watcher framework is needed. On every completion take a complete mount-name snapshot, then
  diff only configured source bindings and perform content checks only for changed sources.
  Low-frequency mount-name reconciliation remains a recovery fallback. Treat prompt letter-only and
  folder-only updates as required acceptance cases, not deferred features.
  [broadcast contract](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerdevicenotificationa),
  [mount-manager change contract](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/mountmgr/ni-mountmgr-ioctl_mountmgr_change_notify).
- Coalesce resume notifications and rebuild identity/mount/readiness before publishing availability.
  Old cached `Available` is not current launch permission after resume or process start. Bounded
  retries here are harmless reads, not automatic hardware-write retries.
  [resume entry](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Shell/ShellSession.Power.cs#L85),
  [Issue 200 startup/resume scenarios](https://github.com/KillerPixelCrew/WSGM/issues/200).

### Gaps

- No fresh hardware evidence establishes the exact event sequence for this user's reader, iSCSI
  reconnects, explicit letter-only edits, or network remounts. Existing dated code comments are
  evidence of earlier scenarios, not a new pass.
- Direct mount-name IOCTL use, directory mount handling, event registration failures and zero-wakeup
  behavior need a bounded Windows validation scenario. Do not claim that an API name alone proves
  prompt mount-change delivery or power behavior.

## How do file deletion, network outages, large libraries and launch races work?

### Takeaway

Use one source-root watcher and one coalesced worker per reachable source, then check only affected
backing paths. Missing media is a source-level state; missing content on present media is a
title-level state. Launch always rechecks the exact bound target and handles the final open/launch
error. [Issue 200](https://github.com/KillerPixelCrew/WSGM/issues/200),
[Microsoft FileSystemWatcher](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher?view=net-10.0),
[Microsoft File.Exists caveats](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.exists?view=net-10.0).

### Cited Findings

- FileSystemWatcher events can duplicate, overflow and omit per-file events on folder moves; it is a
  change hint, not complete truth. Its Error event includes loss of a remote connection. Native
  directory watches do not report changes to the watched root itself and require enumeration after
  lost events.
  [FileSystemWatcher](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher?view=net-10.0),
  [Error event](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher.error?view=net-10.0),
  [ReadDirectoryChangesW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-readdirectorychangesw).
- WSGM already has watcher suspension for eject/format because directory handles can veto volume
  locking. A new watcher must not reopen handles during that suspension.
  [CardAcfWatcher.cs:18](D:/Coding/WSGM/src/WSGM/Shell/CardAcfWatcher.cs:18),
  [CardAcfWatcher.cs:108](D:/Coding/WSGM/src/WSGM/Shell/CardAcfWatcher.cs:108).
- `File.Exists` returns false for permission errors, invalid paths, failed disks and absence alike,
  and cannot prevent a check/use race.
  [Microsoft: File.Exists](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.exists?view=net-10.0).
- Network volume device broadcasts do not cover ordinary network commands/outages. SMB does not
  support the local volume-management APIs, although directory-change notifications are supported
  where the redirector/filesystem implements them.
  [DEV_BROADCAST_VOLUME](https://learn.microsoft.com/en-us/windows/win32/api/dbt/ns-dbt-dev_broadcast_volume),
  [GetVolumePathNamesForVolumeNameW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getvolumepathnamesforvolumenamew),
  [ReadDirectoryChangesW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-readdirectorychangesw).
- Access to a low-power disk can queue/fail I/O or wake it; Microsoft explicitly advises limiting
  device access for battery life. `GetDevicePowerState` reports working/low-power state but requires
  an owned device/object handle.
  [GetDevicePowerState](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getdevicepowerstate).
- Cancellation of synchronous I/O is a request; some operations continue toward completion, and
  cancellation does not wait. Therefore a task timeout cannot be presented as proof that a
  underlying blocked network check has stopped.
  [CancelSynchronousIo](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-cancelsynchronousio).

### Inferences

- Recommended status is `Unknown`/checking, `Available`, or `Unavailable`, with a reason such as
  `StorageAbsent`, `StorageNotReady`, `IdentityMismatch`, `ContentMissing`, `AccessDenied`, or
  `SourceUnreachable`. Persist last successful validation and last known state for diagnostics,
  while runtime generations govern whether results are current. Errors cannot prove deletion; no
  title removal follows an availability failure.
  [Issue 200 durable metadata](https://github.com/KillerPixelCrew/WSGM/issues/200),
  [File.Exists limitations](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.exists?view=net-10.0).
- Watch only configured available roots, with filename/directory-name filters and subtrees where
  required; never one watcher per ROM or a whole-drive recursive watcher. Map file events to indexed
  title paths, directory moves/deletes to the affected prefix. Overflow/root disappearance
  invalidates that source and schedules one bounded reconciliation; dispose/recreate watchers after
  source remount. Share the existing eject/format suspension discipline.
  [watcher limitations](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher?view=net-10.0),
  [native root limitation](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-readdirectorychangesw),
  [existing suspension](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Shell/CardAcfWatcher.cs#L108).
- A per-source worker holds dirty paths plus one full-reconcile flag; bursts merge, only one pass
  runs per source, global disk work stays bounded, and stale generation results are discarded. A
  source returning online validates its imported paths in bounded chunks or grouped directory
  listings, without hashing ROM content. Publish changed title states in batches. The exact chunk
  size/interval is tuning to measure, not a proven constant.
  [issue scale requirements](https://github.com/KillerPixelCrew/WSGM/issues/200),
  [Windows event loss behavior](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-readdirectorychangesw).
- Reserve low-frequency checks for watcherless/faulted configured sources and root recreation;
  absent sources get one root/reachability check, not one operation per title. Local dormant sources
  retain last-known/unknown state until event, manual recheck, foreground use or launch; avoid
  total/free-space/marker scans across all disks just to update shortcut status. A power query can
  suppress opportunistic validation when an already-available handle reports low power, but does not
  prove that acquiring a new handle is wake-free.
  [power contract](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getdevicepowerstate),
  [current broad inventory](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Shell/StorageInventory.cs#L97).
- Network sources use configured source id plus canonical UNC root and relative content path; mapped
  drive letters remain hints. Network-change signals can schedule a check, but periodic
  low-frequency backoff is necessary because storage events are incomplete. Keep network checks off
  UI and isolated from local sources, with one in-flight attempt per source. A UI deadline may
  return unknown promptly, while a still-blocked operation occupies its bounded slot and cannot
  spawn replacement tasks indefinitely.
  [network event limits](https://learn.microsoft.com/en-us/windows/win32/api/dbt/ns-dbt-dev_broadcast_volume),
  [I/O cancellation limits](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-cancelsynchronousio).
- Launch-time interface: resolve managed record by stable id, resolve expected volume/source,
  validate authoritative backing file/directory and required route inputs, then return current path
  or a typed unavailable reason with the source name. Even `Available` must pass this check; catch
  the final launch/open failure and refresh state because removal can occur afterward. User-facing
  recheck asks only for that source/title. A metadata check proves existence/readability, not that a
  ROM loads or a portable game's dependencies work.
  [Issue 200 launch handling](https://github.com/KillerPixelCrew/WSGM/issues/200),
  [check/use race](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.exists?view=net-10.0).

### Gaps

- Research baseline: WSGM `57a3dafdc7b4871eeb073d15ceb92dc5356abd6f`; WindowsDeviceControl
  `79dae8bceca9e987a495df02eb3e6b490fe0870a`, verified by local Git reads on 2026-10-06. Source and
  official API review only: no build, tests, deployment, Steam session, hardware access, or live
  power/I/O measurements.
- Required proof: inserted/absent-at-startup cards, removal/reinsertion while Steam runs, changed
  letter/folder mount, resume, wrong card with duplicate filename and serial, reformatted/clone
  media, content/root deletion while volume remains, denied access, watcher overflow, flaky UNC
  source, watcher release before eject, and a representative large library with counted file I/O and
  sleeping-drive observation. Actual launch/emulator use remains a separate runtime proof.
