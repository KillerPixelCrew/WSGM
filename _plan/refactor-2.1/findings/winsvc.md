# Windows services (winsvc) findings

Scope: the Windows-facing managers inside `src/WSGM` (Shell, Core, Interop): radio, audio, removable storage and SD format, display-off mute, display modes, RTSS shared memory and discovery, Windows power policy, console tools, other-manager takeover, standby and keep-awake, plus their tests and their overlap with `external/windows-device-control` (WDC). Baseline is `master` 1329813f with WDC `main` 2f07485. Line numbers were checked against that baseline where this file gives them; the review's own numbers are sometimes off, so anchor every edit by symbol.

Inputs folded in: the winsvc review (WINSVC-001 to 046), its adversarial verification (corrections, two refutations, missed findings WINSVC-V-001 to V-007), the completeness critic (CRIT-001 and CRIT-003 sit in winsvc batches and are written up here; conflicts 9, 10, 12, 13, 14, 15, 25 and 27 shape the solutions) and plan v2, which wins wherever it simplified a recommendation.

Counts: 52 findings written up (0 critical, 0 high, 11 medium, 32 low, 9 nit; WINSVC-C-001 added by the solution check) and 4 ids in the refuted or no-change list (WINSVC-016 moved there by the solution check). The review's plan-claim checks C1 to C15 are not findings; their substance lives in the findings below (C7 and C8 in WINSVC-017 and V-004, C3 in 028, C4 in 022). Plan v2 overruled the review's C5: WDC keeps one 90 s wall-clock pairing deadline (B065).

Plan v2 batches for this area: B016, B017, B090, B094, B095, B096, B097, B098, B099, B100, B101 (winsvc domain), with consumer edits riding in B063, B064, B065, B070 (WDC), B091 (device), B114 and B115 (session). B094 waits on B071; B099 waits on B066. No batch here waits on a maintainer decision any more: B064's D4 is decided (guidance edits approved, each diff shown with its batch and applied). The maintainer's DECISIONS.md (2026-10-03) is binding here; it changed WINSVC-010 and CRIT-003 (D9: no post-write readback on any host path), WINSVC-013 (newer config loads best effort), WINSVC-024 (D1 decided) and the wording of WINSVC-004, 016, 032, 033 and V-005. No finding here was security hardening, so none moved to the no-change list.

## Medium

### WINSVC-001: RTSS sensor source is sampled by two threads without synchronization

- **Severity:** medium
- **Where:** `src/WSGM/Core/RtssOsd.cs:874-1080` (`RtssOsdMetricsSource`, mutable fields at 881-893, `Sample` at 916), `RtssOsd.cs:1463-1466` (`RtssOsdRenderer.SampleSensors`), `RtssOsd.cs:1543` (render loop), `RtssOsd.cs:539-670` (`LhmSensorReader`, `_gate ??= Mutex.OpenExisting` at 584, `Close` at 662); `src/WSGM/Shell/AutoTdpService.cs` (the `Func<RtssOsdMetrics>` it calls), wired in `src/WSGM/Shell/ShellSession.cs` near 516.
- **Problem:** the OSD render loop (pool thread, 10 Hz) and AutoTDP's control loop both call `Sample()`. Both read and write `_cached`, `_cachedAtTicks`, `_lastIdle`, `_lastBusyBase`, `_hasCpuSample`, `_batteryWatts*` and `_providerAttemptTicks`, and both drive the same `LhmSensorReader`. Interleaved `GetSystemTimes` deltas give a bogus CPU load, which AutoTDP uses to classify stalls and count answered steps. Two racing opens can create two `Mutex` objects (one leaks), and `Close()` on one thread can dispose the view and mutex while the other reads.
- **Best solution:** give `RtssOsdMetricsSource` one `private readonly Lock _sampleLock = new();` and hold it for the whole body of `Sample()`, including the cache check, and in `Dispose()` around `_lhm.Dispose()`. The cached path then returns under the lock too, which costs nothing at 1 Hz and 10 Hz. Do not add a refresh thread or a volatile publication scheme: one lock around existing code is the smallest change that makes the shared cache and the LHM reader single-threaded. `LhmSensorReader` needs no lock of its own because its only caller is now serialized.
- **Tests:** none new. `RtssOsdMetricsSource` and `LhmSensorReader` have no injection seam (both construct their native readers directly; `AutoTdpTraceRecorder` builds its own source), and tests/WSGM.Tests/AGENTS.md forbids adding production seams only for a test, so the lock is verified by review. Existing RTSS tests stay green: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~RtssOsd"`. Worst case today, besides a bad CPU delta: one thread's `Close()` nulls `_gate` while another holds the LHM mutex, so that thread never releases it and the provider blocks until the pool thread dies.
- **Plan v2:** B094.
- **Related:** C14; WINSVC-008 (same renderer), WINSVC-031 (RTSS memory).

### WINSVC-003: Display-off mute restores whichever endpoint is default now, not the one it muted

- **Severity:** medium
- **Where:** `src/WSGM/Shell/DisplayOffMuteService.cs:389-465` (`Mute`, `Restore`), `:533-573` (`TryReadMuted`, `SetMuted`); WDC `external/windows-device-control/src/WindowsDeviceControl/CoreAudio.cs:107-195` (`GetVolume` and `SetMuted` address only the default render endpoint).
- **Problem:** the screen goes dark during a download and WSGM mutes the speakers. A Bluetooth headset connects and becomes default. On wake `Restore` reads the headset as not muted, concludes the user unmuted, and drops its claim. The speakers stay muted and nothing in WSGM unmutes them, a never-strand breach for audio. `Restore` is also gated on a successful read (`TryReadMuted` failing keeps the claim but never writes), which contradicts the no-readback-gate rule.
- **Best solution:** record the endpoint id at mute time and restore that endpoint by id, without a gating read.
  1. WDC (child commit first, inside B099): add `CoreAudio.GetVolume(string endpointId, out int percentage, out int muted)` and `CoreAudio.SetMuted(string endpointId, bool muted)`, built on the same `Enumerator().GetDevice(endpointId, ...)` pattern `GetDeviceFormat` already uses. B066 does not add them, so B099 carries this small library edit and its WDC tests.
  2. Expose both through the `IAudioEndpoints` port from WINSVC-020.
  3. `Mute()`: read the default render endpoint id and its mute state (this read decides whether WSGM claims anything, which is policy, not a gate on a write). If already muted, leave it. Otherwise `SetMuted(id, true)`; on success store `_mutedEndpointId = id` and set `_mutedByUs`.
  3a. The default render endpoint id comes from `CoreAudio.ListEndpoints(AudioDirection.Render, ...)` (the row with `IsDefault`); WDC has no separate default-id read and none is added.
  4. `Restore()`: call `SetMuted(_mutedEndpointId, false)` directly, with no read before or after it (D9: the write either dispatched or failed to dispatch). If the user already unmuted, the write is a harmless no-op. On success clear the claim and the id. On a failure to dispatch (endpoint absent, HRESULT error) keep the claim; the existing 2 s recovery tick already re-attempts the write, which waits on no readback, so no new mechanism is added. A muted Bluetooth endpoint that disconnected is then unmuted when it returns, which is the wanted outcome; switch the failure line in `SetMuted` from `Log.Warn` to `Log.Change` keyed on the endpoint so an absent endpoint does not write a line every 2 s. Delete the read-then-decide branch.
- **Tests:** in `tests/WSGM.Tests/Shell/DisplayMuteTests.cs`, over a fake `IAudioEndpoints`: the default changes while muted and restore unmutes the original id, not the new default; a failed restore write keeps the claim and the next tick retries; a user unmute during dark ends with the endpoint unmuted and the claim dropped. WDC: per-endpoint mute round trip in `CoreAudioTests` where the existing harness allows. Filters: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~DisplayMute|FullyQualifiedName~AudioManager|FullyQualifiedName~AudioProfile"` and the WDC `CoreAudioTests` filter on net8 and net10.
- **Plan v2:** B099 (after B066).
- **Related:** WINSVC-010 (readback gate), WINSVC-020 (port), WINSVC-026 (same service); U01-026 context.

### WINSVC-004: Storage is enumerated five ways with three identities

- **Severity:** medium
- **Where:** `src/WSGM/Shell/RemovableDriveManager.cs:294-399` (`ReadSnapshot`, row id = instance path or `media:X`), `src/WSGM/Shell/SdFormatManager.cs:202-247` (`ReadTargets`, instance id or `disk:N`), `src/WSGM/Shell/CardVolumeMonitor.cs:530-562` (`ScanCardLibraryPaths`), `src/WSGM/Shell/CardAcfWatcher.cs:144-195` (`Reconcile` over `DriveInfo`), `src/WSGM/Shell/SteamStorageBridge.cs:318, 620` (WDC `WindowsStorage.DescribeVolumes`, joined by disk number); `RemovableDriveManager.ResolveSystemDisks` runs on every scan except the eject list's cached copy.
- **Problem:** five readers of the same disks and volumes, each with its own identity, are joined after the fact by disk number or display text. They can disagree within one pass, the Steam bridge cannot otherwise tell that two rows describe the same card, and every consumer pays its own native enumeration (including system-disk resolution) per refresh.
- **Best solution:** add `src/WSGM/Shell/StorageInventory.cs`: one reader over WSGM `NativeStorage` that produces an immutable snapshot record per disk (disk number, device instance id, bus type, size, hotplug and removable class, system-disk flag) with its volumes (letter or `\0`, volume GUID path, size, label, ready). The system-disk set is cached inside the inventory the way the eject list caches it today. `RemovableDriveManager`, `SdFormatManager` (`ReadTargets`), `CardVolumeMonitor` and `SteamStorageBridge` each project from one snapshot; delete their own walks. `CardAcfWatcher` keeps its `DriveInfo.GetDrives()` walk: it watches every ready drive with a `SteamLibrary` marker, including fixed and network drives (`CardAcfWatcher.cs:144-169`), a scope the maintainer decided to keep (DECISIONS.md: keep watching every ready drive, fix the comment), and a disk inventory has no network drives, so projecting it would silently narrow what is watched. `SteamStorageBridge` stops calling WDC `WindowsStorage`; WDC keeps that API for library users (no deletion because unused). The UI-thread snapshot the bridge gets in B084 now projects from the inventory, and no revision cache is added (critic conflict 15). Row identities stay exactly as today so selection and Steam ids do not change.
- **Tests:** one fixture inventory (fake `NativeStorage` query port) drives the eject list, format targets and Steam projection and they agree on ids, letters and sizes; an unlettered media row yields no library paths; a card swap mid-pass is still abandoned by the card monitor's re-read. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~RemovableDrive|FullyQualifiedName~SteamStorageBridge|FullyQualifiedName~CardLibrary|FullyQualifiedName~CardName|FullyQualifiedName~SdFormat"`.
- **Plan v2:** B096 (after B095 and B084).
- **Related:** U01-040, U01-048 (WDC side), STEAMHOST-007/008 (B084), critic conflict 15; WINSVC-005, V-002, WINSVC-016 (no change; the ACF watcher keeps its own walk).

### WINSVC-005: Card and Steam paths are parsed back out of the display string `Letters`

- **Severity:** medium (verifier kept it; the wrong-letter trim is non-destructive)
- **Where:** `src/WSGM/Shell/RemovableDriveManager.cs:392` (an unlettered media row gets `Letters = "No Windows drive letter"`), `src/WSGM/Shell/LibraryPolicy.cs:124-130` (`LibraryPathsOn`), `src/WSGM/Shell/SteamStorageBridge.cs:243` (`TrimAllAsync`), `:392-398` (`BlockDevice`), `:628-637` (`SplitLetters`); `src/WSGM/Shell/RemovableDriveEntries.cs:73-85` (`Letters`).
- **Problem:** `SplitLetters` turns "No Windows drive letter" into the path `No Windows drive letter\`. `TrimAllAsync` then takes `path[0]` = `N` and runs an elevated `Optimize-Volume -DriveLetter N -ReTrim` on whatever volume N is. `LibraryPathsOn` builds a relative `No Windows drive letter\SteamLibrary` and the eject observer looks for it in the current directory. ReTrim erases nothing, so this is user-visible noise rather than data loss.
- **Best solution:** carry letters as data. Add `IReadOnlyList<char> VolumeLetters` to `EjectableDevice` and `RemovableDriveEntry` (internal setter, set wherever `Letters` is set today, empty for an unlettered row). `Letters` stays the display string, unchanged in content. `LibraryPolicy.LibraryPathsOn` and `SteamStorageBridge` (`TrimAllAsync`, `BlockDevice`) build `$"{letter}:\\"` from `VolumeLetters`. Delete `SteamStorageBridge.SplitLetters`. This lands in B094 and does not wait for the inventory; B096 later fills `VolumeLetters` from the snapshot.
- **Tests:** in `RemovableDriveTests` or `SteamStorageBridgeTests` (existing fake drive lists from B084): an unlettered media row contributes no trim letter and no library path; a two-letter USB row yields both mount paths. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~RemovableDrive|FullyQualifiedName~SteamStorageBridge"`.
- **Plan v2:** B094.
- **Related:** WINSVC-004, WINSVC-042.

### WINSVC-006: The format run reads mutable row fields as its safety baseline

- **Severity:** medium (verifier: worse than stated; first item in B094)
- **Where:** `src/WSGM/Shell/SdFormatManager.cs:171-199` (`Refresh` checks `Busy` only before starting), `:291-334` (`Apply` rewrites `SizeBytes`, `BusType`, `PreferredLetter` on surviving rows), `:407-663` (`FormatAsync` reads `entry.*` across many awaits), `:680-800` (`CompareIdentity`, `ReadTargetIdentity`, `VerifyTarget` compare against `entry`).
- **Problem:** a `Refresh` (overlay, or `SteamStorageBridge` at 76 and 278) that started before `Busy` posts `Apply` mid-run. A same-reader card swap keeps the instance id and disk number, so `Apply` rewrites `SizeBytes` and `BusType` to the new card's values. Every later reverification then compares the new card against itself, reports Same, and the run formats the swapped card. `PreferredLetter` can also change under the run.
- **Best solution:** at the very top of `FormatAsync`, before any `await` (it is entered on the UI thread), copy the row into an immutable `private sealed record FormatRunTarget(string Id, int DiskNumber, string Name, long SizeBytes, string BusType, char PreferredLetter)` (use the existing field types). Pass that record, not the `FormatTargetEntry`, to `VerifyTarget`, `RemoveExistingLibrary`, `ReadTargetIdentity`, `LogReverification`, `CompareIdentity`, the diskpart script builders, `WaitForVolume`, `WaitForLetter`, `CreateSteamLibrary` and the compensation path. The row is used only for UI text. Nothing else changes: no Busy check in `Apply`, no new state.
- **Tests:** in B094 the change is verified by review and the compiler: every reverification helper takes `FormatRunTarget`, never `FormatTargetEntry`, so no helper can read a row field. A unit test over the pure `CompareIdentity` would not exercise the fix (it already takes plain values), and `FormatAsync` has no fakeable seam until B097. B097's `SdFormatRunTests` (WINSVC-007) adds the real regression: a row rewritten mid-run (new `SizeBytes`/`BusType`) still makes the next reverification compare against the run's snapshot and abort. Filter now: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SdFormat"`. Manual M01-35/36 on spare media only.
- **Plan v2:** B094.
- **Related:** Shell/AGENTS.md strict-match rule; WINSVC-007 (the split keeps this record), WINSVC-035.

### WINSVC-007: `SdFormatManager` is a 1,636-line static-heavy aggregate with untested destructive orchestration

- **Severity:** medium
- **Where:** `src/WSGM/Shell/SdFormatManager.cs` (enumeration 202-339, three-stage diskpart run 407-663, Steam library removal, restore and registration 814-1091 and 1258-1370, add-library 1383-1489, retrim 1149-1192, `LibraryTabManager.MutateConfigAsync` at 509, script spooling under `Log.Directory` at 1102); `tests/WSGM.Tests/Shell/SdFormatTests.cs`.
- **Problem:** every native, process, Steam and config dependency is static, so the destructive run cannot be tested with fakes as Shell/AGENTS.md requires. Today's tests cover only script strings, `CompareIdentity` and VDF parsing.
- **Best solution:** split by owner, keeping behaviour byte-identical:
  - `SdFormatTargets.cs`: `Targets`, `HasTargets`, `Refresh`, `ReadTargets` (over the B096 inventory), `Apply`, `FindTarget`, `DescribeTarget`, `DescribeBus`, the `FormatTarget` record.
  - `SdFormatRun.cs`: `Busy`, `StatusText`, `Finished`, `_formatGate`, `CardWatcher`, `FormatAsync`, `VerifyTarget`, `CompareIdentity`, `ReadTargetIdentity`, `LogReverification`, the identity records, `WaitForVolume`, `WaitForLetter`, `RunDiskpart`, `RetrimVolume`, `TrimAsync` (stays public for `SteamStorageBridge`), the label and script helpers and the timing constants. Delete the `ReverifiedStages` constant array; the stage order is the code order and is asserted by tests.
  - `SteamLibraryRegistration.cs`: `RemoveExistingLibrary`, `FindExistingMarker`, `LettersOnDisk`, `RestoreRemovedLibraryIfCardSurvived`, `CreateSteamLibrary`, `RegisterLibrary`, `WriteMarkerAndClientDll`, `BackupOnce`, `AddLibrary*`, `ResolveLibraryRoot`. `SteamLibraryFolders`/`SteamLibraryVdf` stay the library domain's owners; the marker read moves out of `SteamLibraryVdf` so the parser stays pure (LIBRARY-023).
  - Four narrow ports on `SdFormatRun`: `IDiskpart` (run a script, returns the B095 `ConsoleToolResult`), `IDiskIdentity` (read identity, wait for volume and letter), `ISteamLibraryRegistration`, `IClock` (the retry delays). Production adapters wrap today's statics.
  - One `SdFormatRun` per session: `OverlayController` stops creating its own (the lazy `FormatManager` fallback at `OverlayController.cs:246-258`; the session already passes `formats` at `ShellSession.cs:714`); the Settings overlay-test composition (`SettingsWindow.axaml.cs:259`) builds the one instance it uses and passes it. This batch owns that change; WINSVC-038 (B101) only covers `SystemStatus`.
  - Card retirement goes through the config store's transaction (B039) instead of `LibraryTabManager.MutateConfigAsync`.
  Keep every per-stage reverification point and the three format attempts exactly (documented device evidence).
- **Tests:** new `SdFormatRunTests` over fake ports: a reverification precedes each destructive stage; an identity change before `clean` restores the removed registration once; an identity change after `clean` does not compensate; an Unknown diskpart outcome gives the uncertain message (WINSVC-015); a letter-keep failure stops. Move the VDF tests into `SteamLibraryVdfTests` (WINSVC-039). Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SdFormat|FullyQualifiedName~SteamLibraryVdf"`. Manual M01-35/36 on spare media only.
- **Plan v2:** B097 (after B096; uses B095's runner and B039's config transaction).
- **Related:** LIBRARY-023; WINSVC-006, 015, 035, 036, 038, 039, 046.

### WINSVC-009: Uninjectable Windows power singletons and a global mutation gate

- **Severity:** medium (verifier narrowed the scope)
- **Where:** `src/WSGM/Core/PowerSchemes.cs:20-21` (`Windows`, `MutationGate`), `src/WSGM/Core/CpuBoost.cs:73`, `src/WSGM/Core/HybridCores.cs:80`, `src/WSGM/Core/WindowsPowerModes.cs:10`; gate users in `CpuBoost.cs:155`, `HybridCores.cs:217`, `WindowsPowerModes.cs:41`, `PowerTimeouts.cs:107`, `src/WSGM/Shell/DisplayTimeouts.cs:99, 157, 180, 224`, `src/WSGM/Overlay/OverlayController.cs:552-557, 795`, `src/WSGM/Overlay/PowerSchemeSelection.cs:79`, `src/WSGM/Shell/NativeQamPowerProfileService.cs:62`; `PowerTimeouts.cs:193` reads `PowerSchemes.Windows`; `src/WSGM/Shell/DeviceCoordinator.cs:134` passes `WindowsPowerModes.Windows`.
- **Problem:** the four `Windows` statics cannot be replaced in tests or composed by the session, and the public static `MutationGate` is locked from 12 sites in 9 files. A single lock over the machine-global active scheme is correct; making it a process global reachable by anyone is the defect. The other globals the review listed are handled elsewhere: `VolumeFeedback` (WINSVC-019, B099), `RadioManager._feedWork` (WINSVC-022, B063), `MessageWindow._instance`/`Create()` (WINSVC-017, B114). `HotkeyService._nextId` is not a defect.
- **Best solution:** the lock moves into the one `PowerSchemes` instance; no new lane type (critic conflict 13).
  - `PowerSchemes` gets `private readonly Lock _mutation = new();` and `internal void Mutate(Action write)` plus `internal T Mutate<T>(Func<T> write)` that run the delegate under it. `Select` uses it. `Lock` is reentrant, so callers that wrap `Select` plus their persist step in `Mutate` keep today's nesting.
  - `CpuBoost`, `HybridCores`, `WindowsPowerModes` and the instance `PowerTimeouts` (WINSVC-030) take the `PowerSchemes` instance in their constructors and wrap their write bodies in `schemes.Mutate(...)`. `DisplayTimeouts` takes `PowerTimeouts`.
  - Delete `PowerSchemes.Windows`, `PowerSchemes.MutationGate`, `CpuBoost.Windows`, `HybridCores.Windows`, `WindowsPowerModes.Windows`.
  - `ShellSession` constructs one `PowerSchemes` over `WindowsPowerSchemeApi` and the four dependents, and passes them to `OverlayController` (`PowerSchemeSelection`, `HybridCoreSelection`, the timeout fallback), `SteamUiSessionHost` (constructor arguments only, for the NativeQam power, CPU-boost and hybrid-core services), `DeviceCoordinator` (power modes) and `ApplicationPerformanceReconciler` (CPU boost; B091 consumes the instances). The overlay-test composition builds its own set over the real APIs as it effectively does today.
  - DEVICE-040: fold the small duplicated Windows power port pieces together in the same batch.
- **Tests:** existing power tests switch to constructing instances over fakes; add one test that two concurrent `Mutate` calls on one `PowerSchemes` never overlap. UiTests fakes (`VisualTests`, `DevicePageCaptureTests`, `HybridCoreViewTests`, `ControllerNavigationTests`, `OverlayLayoutTests`) pass instances. Filters: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~PowerScheme|FullyQualifiedName~CpuBoost|FullyQualifiedName~HybridCore|FullyQualifiedName~PowerTimeout|FullyQualifiedName~DisplayTimeout"` and `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~HybridCoreView|FullyQualifiedName~DevicePageCapture"`.
- **Plan v2:** B090 (after B017 and B088).
- **Related:** U05-LFB-005, DEVICE-040, critic conflict 13; WINSVC-010, 030.

### WINSVC-010: Windows power writes are gated on readback

- **Severity:** medium (verifier: a breach of the plan contract, ships on its own)
- **Where:** `src/WSGM/Core/PowerSchemes.cs:53-73` (`Select` throws on a GUID mismatch), `src/WSGM/Core/HybridCores.cs:215-241` (`Apply` throws on a mismatch), `src/WSGM/Core/WindowsPowerModes.cs:39-50` (`Apply` throws when `api.Read() != id`), `src/WSGM/Shell/DisplayTimeouts.cs:177-190` (`Select` refuses when the current value cannot be read), `src/WSGM/Overlay/OverlayController.cs:795-801` (fallback timeout write only when `PowerTimeouts.Read` succeeds), `src/WSGM/Shell/NativeQamPowerProfileService.cs:25, 50, 65, 87, 103-110, 147` (see below); `src/WSGM/Core/CpuBoost.cs:152-179` (already warn-only, but still reads back after the write).
- **Problem:** a readback mismatch turns a write Windows accepted into an exception, so the UI reports failure and callers ask the user to re-read; a failed read blocks a timeout write outright. Plan v2 and the maintainer rule say readback never gates a write, a control or success. Two more instances turned up while consolidating, not in any review: `NativeQamPowerProfileService` sets `_requiresRead = true` after every successful `Select` and refuses the next Steam QAM selection until a state read clears it ("Windows state must be refreshed before another selection"), the same latch CRIT-003 removes from hybrid cores; and it refuses to publish any profile list longer than 64 ("Windows returned more than 64 power profiles"), an arbitrary limit.
- **Best solution:** D9 (no readback machinery on any host path): every apply path writes, refreshes the active scheme where the setting needs it, and returns normally, so the caller publishes the written value as observed. No read follows a write, not even for a warning line; a write either dispatched or threw because it failed to dispatch.
  - `PowerSchemes.Select`: `SetActive(id)` and return. Delete the `ReadActive()` and the mismatch throw, and the doc comment's "the caller must re-read before another action".
  - `HybridCores.Apply`: keep the per-source `Read` that composes the write (it carries the heterogeneous-policy fields through; that read is data for the write, not a readback), write both sources, `RefreshActiveScheme`, return. Delete the confirming loop and its `InvalidOperationException` (and the `<exception>` doc line).
  - `WindowsPowerModes.Apply`: `Set(id)` and return. Delete the `api.Read() != id` throw.
  - `CpuBoost.Apply`: keep HC's pre-write read that skips a write when both sources already hold the mode (it decides whether to write, not whether the write worked), and delete the trailing `api.Read` pair and its `Log.Warn`, with the remark sentence describing it. Add `src/WSGM/Core/CpuBoost.cs` to B017's files.
  - `DisplayTimeouts.Select`: check `DisplayTimeoutPolicy.Allows(seconds, Minimum(kind))` and write; delete the `_read(kind) is null` refusal. `DisplayTimeouts.Cycle` keeps its read, because the next preset is computed from the current value.
  - `OverlayController` timeout fallback (no `DisplayTimeouts` owner): write without the prior read.
  - `NativeQamPowerProfileService`: delete the `_requiresRead` refusal and both `_requiresRead = true` assignments in `SetPowerProfileAsync`; validate the requested id against `_offeredIds` (the published option list), select, persist, report success. No cached active-id field is added: every publication reads `ReadActive()` as it does today because Windows or the user can switch the scheme elsewhere; that is the publication's ordinary state read, not a confirmation of the command, and nothing waits on it. `_requiresRead` stays only as the read path's "re-enumerate the list" flag (set initially and after a failed read); rename it `_listStale` so it no longer reads as a gate. Delete the 64-profile refusal. Add the file to B017's list.
  - Callers that caught the readback exception (`PowerSchemeSelection`, `HybridCoreSelection`, `NativeQamPowerProfileService`, `NativeQamHybridCoreService`, `DeviceCoordinator` power-mode path) keep their catch for a write that failed to dispatch, and nothing else reaches it; their existing strings stay as they are.
- **Tests:** in `PowerSchemesTests`, `HybridCoreTests`, `CpuBoostTests`, `DisplayTimeoutsTests` and a new or existing NativeQam power test: each apply makes no read after its write (the fake records the call order) and returns success, and the written value is what gets published; a write that throws still surfaces as a failure; `DisplayTimeouts.Select` writes when the read fails; two QAM selections in a row both apply; a 65-scheme fake lists all 65. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~PowerScheme|FullyQualifiedName~CpuBoost|FullyQualifiedName~HybridCore|FullyQualifiedName~DisplayTimeout|FullyQualifiedName~NativeQamHybridCore|FullyQualifiedName~NativeQamPowerProfile"`.
- **Plan v2:** B017 (no dependencies; independent of the B090 instance change). B017's file list is incomplete and has one wrong path: it names `src/WSGM/Core/DisplayTimeouts.cs`, but the file is `src/WSGM/Shell/DisplayTimeouts.cs`; it also needs `src/WSGM/Core/WindowsPowerModes.cs`, `src/WSGM/Core/CpuBoost.cs` (D9), `src/WSGM/Overlay/OverlayController.cs` (timeout fallback), `src/WSGM/Shell/NativeQamPowerProfileService.cs` and `src/WSGM/Overlay/PowerSchemeSelection.cs` (WINSVC-C-001), and its test filter should add `FullyQualifiedName~NativeQamPowerProfile`.
- **Related:** CRIT-003 (same batch), WINSVC-C-001 (same batch, overlay latch), WINSVC-003 (mute restore read), refactor-plan L111.

### WINSVC-V-001: CardVolumeMonitor drops a pass that arrives while another runs

- **Severity:** medium
- **Where:** `src/WSGM/Shell/CardVolumeMonitor.cs:240-305` (`RunPassAsync`, `_gate.WaitAsync(TimeSpan.Zero, ...)` at 251), `:465-512` (`RemoveDepartedCardsAsync`).
- **Problem:** a pass can take up to 60 s of CEF round trips. A card pulled or swapped meanwhile raises its notification and settle timer, but the resulting pass sees the gate held and simply returns, contrary to the comment ("A second card arriving mid-pass simply waits"). The running pass works from its stale scan, so the departure is never reconciled and Steam keeps the departed card's library until some later notification or a Steam restart.
- **Best solution:** wait on the gate as the comment says: `await _gate.WaitAsync(lifetimeToken).ConfigureAwait(false);`. Bursts are already collapsed by the settle timer, so at most a few passes queue, each re-scanning current state. Lifetime cancellation still ends a waiting pass, and `_activePasses` already tracks it for teardown. No new flag or reschedule.
- **Tests:** CardVolumeMonitor has no tests today; add `CardVolumeMonitorTests` once B096/B101 give it a fake inventory. In B094, test the gate behaviour with the smallest seam that already exists; if none does, defer the test to B101 and say so in the batch result. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~CardVolume"`.
- **Plan v2:** B094.
- **Related:** WINSVC-V-002 (same file), WINSVC-004.

### CRIT-001: Steam autostart takeover disables Steam autostart without a recorded original

- **Severity:** medium (critic; ledger U04B-LFA-003)
- **Where:** `src/WSGM/Core/SteamAutostartService.cs:178-201` (`RecordDisabled` catches and logs a failed `ConfigStore.Mutate`), `src/WSGM/Core/SteamAutostartTakeover.cs:75-100` (`Disable` records, then writes).
- **Problem:** when the config write fails, `RecordDisabled` swallows it and `Disable` goes on to disable the scheduled task or rewrite the startup approval with no recorded original, so uninstall can never restore Steam autostart. `OtherManagers.Record` already throws and is correct.
- **Best solution:** delete the `try/catch` in `RecordDisabled` so the exception propagates; the per-item `try` in `SteamAutostartTakeover.Disable` already turns it into a refused item before any write. No new state.
- **Tests:** in `SteamAutostartTests`, a record delegate that throws leaves the task enabled and the approval bytes untouched, and the item is reported as not disabled. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamAutostart"`.
- **Plan v2:** B016.
- **Related:** U04B-LFA-003, critic conflict 27 (WINSVC-B3 owns these files; config supplies only the strict read), WINSVC-013, 014.

### CRIT-003: Steam QAM hybrid-core selection is gated on a fresh read and latched after a failure

- **Severity:** medium (critic)
- **Where:** `src/WSGM/Shell/NativeQamHybridCoreService.cs:25-70` (`SetHybridCoresAsync`: `_requiresRead` refusal at 38, fresh `cores.Read()` at 46, latch at 63), `:72-105` (`ReadAsync` clears the latch).
- **Problem:** a selection is refused when a fresh `Read()` fails, and after any failed write every later selection is refused until a publication read succeeds. A new explicit user action is the allowed retry, so this gates a control on readback.
- **Best solution:** mirror `NativeQamPowerProfileService._offeredIds`: validate the requested mode against the option list last published by `ReadAsync` (store the offered modes in a field when publishing; a selection before the first publication is refused as "no longer offered"), call `cores.Apply(mode)` (which no longer reads back after its write, WINSVC-010 under D9), report success when it returns, and delete `_requiresRead` entirely, including the fresh `cores.Read()` in the command. No cached active-mode field: each publication reads Windows as today because the mode can change elsewhere; that is the publication's ordinary state read, not a confirmation of the command, and nothing waits on it. A write that failed to dispatch reports its message for that action only.
- **Tests:** a failed hybrid-core write does not refuse the next explicit selection; a mode absent from the published list is refused; the command calls no `cores.Read()` of its own (only `Apply`'s composing read reaches the fake) and makes no read after `Apply`, and reports success. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~NativeQamHybridCore|FullyQualifiedName~HybridCore"`.
- **Plan v2:** B017.
- **Related:** WINSVC-010 (same batch; the power-profile service carries the same latch), critic conflict 13.

## Low

### WINSVC-002: An eject that throws leaves the library unregistered and held out of Steam

- **Severity:** low (verifier lowered from medium: only reachable through an unexpected exception)
- **Where:** `src/WSGM/Shell/RemovableDriveManager.cs:540-585` (`EjectAsync`), `src/WSGM/Shell/LibraryPolicy.cs:73-118` (`EjectingAsync`, `EjectedAsync`).
- **Problem:** `EjectingAsync` unregisters the library and records the held-ejected intent. If anything between it and `EjectedAsync` throws, the outer catch never reports the outcome, the intent stays recorded, `CardVolumeMonitor` skips the still-mounted card and Steam loses the library until the media leaves. The native eject wrappers do not throw, so this needs an unexpected exception.
- **Best solution:** declare `var succeeded = false;` and wrap from `await observer.EjectingAsync(entry)` through the eject in `try { await observer.EjectingAsync(entry); ...; succeeded = result.Success; } finally { await observer.EjectedAsync(entry, succeeded); }`, deleting the existing trailing `EjectedAsync` call. The `try` must start before `EjectingAsync`, not after it: `EjectingAsync` records the intent per path (`NoteEjected`) before each unregister, so a throw half way through it would otherwise leave intents recorded too. `EjectedAsync(false)` only clears intents, which is harmless for paths never recorded, and an exception it throws lands in the existing outer catch. No new field.
- **Tests:** test through the real `LibraryPolicy` over temp paths (no fake observer interface, per the verifier and the tests rule). Make the eject throw with the smallest existing seam; if the native eject cannot be replaced without a new production seam, defer the test to B101 and say so. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~RemovableDrive"`.
- **Plan v2:** B094.
- **Related:** WINSVC-V-001.

### WINSVC-008: RTSS OSD renderer starts in its constructor and disposes under a running loop

- **Severity:** low (verifier lowered from medium)
- **Where:** `src/WSGM/Core/RtssOsd.cs:1415-1419` (constructor starts `RenderLoopAsync`), `:1425-1454` (`Dispose`: 2 s wait, then `_writer.Dispose()`), `src/WSGM/Core/RtssNativeAdapter.cs:30-37`.
- **Problem:** `Dispose` waits 2 s and then releases the OSD slot even if the loop is still inside `Sample()` (which can block on the LHM mutex or start a process). A loop mid-`TryWrite` while `Dispose` zeroes the slot can re-claim it and leave "WSGM" OSD text in RTSS after exit. The window is narrow because `_disposed` is set first and `TryUpdate` checks it.
- **Best solution:** the defect is one interleaving: `RtssOsdWriter.TryUpdate` passes its `_disposed` check and is inside `RtssOsdSlots.TryWrite` while `RtssOsdWriter.Dispose` zeroes the slot, so the write lands after the release. Fix it in the writer with one `private readonly Lock _sync = new();` held for the whole body of `TryUpdate` (from the `_disposed` check through the write) and of `Dispose`. A release then never interleaves with a write, and any write after it sees `_disposed` and returns false, whether or not the render loop has finished. The renderer keeps its constructor start, its 2 s bounded wait and its disposal order; no `Start()` split, no hand-off of the release to the loop, no new state. (The `RtssOsdMetricsSource.Dispose` taken right after is already safe under a running `Sample()` once WINSVC-001's lock lands, and a late `Task.Delay` on the cancelled token returns at once.) The lock is uncontended at 10 Hz and allocates nothing. Do not add `Volatile` around `_lastProbe` (verifier: a reference write is already safely published). This deviates from the B094 spec text ("renderer started by RtssNativeAdapter after construction ... then releases the slot"): starting the loop in the constructor is not a defect, and the writer lock is the smaller fix for the real race.
- **Tests:** none new: the writer opens the named RTSS mapping directly and has no region seam (only `RtssOsdSlots` takes `IRtssOsdRegion`), so the lock is verified by review; the existing slot tests (`ClaimsTheFirstFreeSlotAndSkipsRtssOwnSlot`, `ReleaseZeroesOnlyTheOwnedEntry` and the rest) stay green. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~RtssOsd"`.
- **Plan v2:** B094.
- **Related:** WINSVC-001, 031.

### WINSVC-012: Refresh-rate pairing carries production branches that exist only for tests

- **Severity:** low
- **Where:** `src/WSGM/Core/RefreshRatePairingService.cs:31-33, 63-78` (optional `readOperatingPoint`, `restoreTarget`, `readTargetRate`), `:133, 323-343` (`_readOperatingPoint is null` branches, `?? "test"` originals key), `:361-375, 399`; `tests/WSGM.Tests/Core/RefreshRatePairingServiceTests.cs:215-245` (harness).
- **Problem:** the null-operating-point paths and the `"test"` key exist only because the test constructor omits those delegates, against tests/WSGM.Tests/AGENTS.md ("Do not add production branches solely to make a test convenient").
- **Best solution:** make `readOperatingPoint`, `restoreTarget` and `readTargetRate` required constructor parameters. The harness supplies a fixed operating point (the existing `Point(path, width)` helper) that tests can change, a `restoreTarget` that records into the same fake apply list, and a `readTargetRate` that returns `Current`. Delete every `_readOperatingPoint is null` branch, the `_applyRate` fallback in the restore path and the `?? "test"` keys. The two original-mode owners, the operating-point revision and the retry loops stay (WINSVC-011 refuted).
- **Tests:** port every existing `RefreshRatePairingServiceTests` case onto the new harness unchanged in intent; add one case where the operating point changes mid-operation and the cached rates are not applied. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~FrameLimitPairing|FullyQualifiedName~RefreshRatePairing|FullyQualifiedName~DisplayResolutionService"`.
- **Plan v2:** B098 (after B069 and B094).
- **Related:** critic conflict 10 (config B7 dropped), WINSVC-011, 027.

### WINSVC-013: `OtherManagers.RestoreAll` reports success when config.json is unreadable

- **Severity:** low (the ledger row U04B-LFA-004 rates it medium)
- **Where:** `src/WSGM/Core/OtherManagers.cs:453-475` (`RestoreAll` uses the lenient `ConfigStore.Load()`; zero records return 0); callers `src/WSGM/Core/Installer.cs:68` (uninstall), `src/WSGM/Program.cs`.
- **Problem:** an unreadable config loads as defaults with no records, so uninstall reports that every other manager was restored when nothing was read at all; the originals stay disabled.
- **Best solution:** read through the strict outcome API from B039 (`ConfigStore.Read()`). `Loaded` proceeds as today; `Absent` returns 0 (nothing was ever recorded); `Corrupt` or `Unreadable` logs the outcome and returns 1 without writing. A config written by a newer WSGM is not a failure: per DECISIONS.md it loads best effort as today (what is understood is loaded, no read-only mode), so its understood other-manager records are restored like any `Loaded` document. The removal of restored records keeps using the writer transaction, which refuses only on a document it cannot read.
- **Tests:** in `OtherManagersTests`, a corrupt-config fake returns 1 and performs no write and no restore; an absent config returns 0; a config carrying a newer schema version and an unknown field still restores its recorded managers. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~OtherManagers"`.
- **Plan v2:** B095 (after B094 and B039).
- **Related:** U04B-LFA-004, critic conflict 27, WINSVC-014.

### WINSVC-014: `OtherManagers` is a static class with defaulted dependencies and an untracked start-up task

- **Severity:** low
- **Where:** `src/WSGM/Core/OtherManagers.cs:160-500` (`autostart ??= new AutostartSystem()` at 194-196 and 262-264, static `ConfigStore` reads at 372, 457, 465, 481, one `ConfigStore.Mutate` per record at 478-488); `src/WSGM/Shell/ShellSession.cs:398` (`_ = Task.Run(OtherManagers.ReapplyAtStart)`); callers in `Program.cs:325, 418, 613`, `Settings/SettingsViewModel.cs:251-252`, `Settings/SettingsViewModel.Save.cs:618-624`, `Settings/SettingsViewModel.System.cs:215-236`, `Core/Installer.cs:68`.
- **Problem:** the takeover cannot be tested without the real autostart, service and config stores, and the start-up re-apply runs fire-and-forget: it can run `sc.exe` or close windows while the session is stopping, and nothing joins it. Ignoring `SelfElevation.RunElevatedAction`'s result is harmless (verifier: `Apply` re-detects afterwards).
- **Best solution:** an instance `OtherManagerTakeover` with constructor-injected `IAutostartSystem`, `IServiceSystem` (existing), a process probe and the config store instance. `Detect`, `Disable`, `Apply`, `ReapplyAtStart`, `RunElevatedDisable`, `RestoreAll`, `Record` and `Restore` become instance methods; `OtherManager`, `DetectedManager`, `OtherManagerRecord`, `OtherManagersResult`, `Known`, `DescribeRecords`, `ExecutableName` and `Key` stay as data or pure statics. `ReapplyAtStart` takes a `CancellationToken` and checks it between items. `ShellSession` keeps the start-up task in a field and joins it only inside the shutdown deadline, cancelling first, so `CloseWindows` (8 s per process) and `sc.exe` (15 s each) never extend Normal or SessionEnd shutdown. Program, Settings, Installer and WSGM.Setup construct the instance where they call it today.
- **Tests:** `Disable` records before each change (existing test kept); `Apply` after elevation re-detects; a cancelled `ReapplyAtStart` stops between items. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~OtherManagers|FullyQualifiedName~Autostart"`.
- **Plan v2:** B095.
- **Related:** U05-LFB-005 pattern, CRIT-001, WINSVC-013, 015.

### WINSVC-015: Three console-tool run paths with different timeout semantics

- **Severity:** low
- **Where:** `src/WSGM/Core/ConsoleTool.cs:62-98` (`Run`, bool), `:113-227` (`RunUntilAsync`, `ConsoleToolRunOutcome`), `:237-309` (`RunCapturedAsync`, folds not-started, killed and failed into `-1`); callers `src/WSGM/Core/AutostartSystem.cs:136, 153` (153 blocks with `.GetAwaiter().GetResult()`), `src/WSGM/Core/OtherManagers.cs:128-140` (sc.exe), `src/WSGM/Core/UnelevatedLauncher.cs:239`, `src/WSGM/Shell/SdFormatManager.cs:1117, 1125, 1172` (icacls, diskpart, powershell).
- **Problem:** a diskpart killed on its 600 s deadline reads as a definite "partition failed" and the card-database retirement at `SdFormatManager.cs:507-515` is skipped, although `clean` may already have run. The message says "Windows could not rebuild the drive. Reinsert the card and try again." (verifier correction: it does not claim nothing was erased). `Run` likewise abandons a timed-out process and reports a definite failure (U04A-LFA-014).
- **Best solution:** one runner. Generalize `RunUntilAsync` into `internal static Task<ConsoleToolResult> RunAsync(string exe, string arguments, DateTimeOffset deadline, bool captureOutput, CancellationToken cancellationToken)` returning `record ConsoleToolResult(ConsoleToolRunOutcome Outcome, int? ExitCode, string Output)` (keep the existing four-value enum: `NotStarted`, `Succeeded`, `Failed`, `Unknown`) and keep the injected-process overload for tests. Delete `Run` and `RunCapturedAsync`. Callers: sc.exe and schtasks treat `Unknown` as not confirmed and report the item as failed without retrying; `IAutostartSystem` and `AutostartSystem` stay synchronous (`Disable`, `Restore` and the scanner are synchronous and already run off the UI thread or in one-shot elevated modes), and `AutostartSystem` keeps its `.GetAwaiter().GetResult()` wait on the new runner, which never captures a context, so no async ripples through the interface; `UnelevatedLauncher` uses the new name. The SD format run (B097) treats a diskpart `Unknown` on the clean/partition script as "clean may have run": it calls the existing `RestoreRemovedLibraryIfCardSurvived` (which re-reads the marker and no-ops when the erase happened), retires the card-database entry only when `FindExistingMarker` no longer finds a marker, and finishes with "The card may have been erased. Reinsert it and check before using it." This re-reads state instead of retrying, and an intact card keeps its database entry and registration. D9 does not remove it: `Unknown` is the outcome of a process killed at its deadline, not a readback of a written value, nothing is re-armed or retried, and the marker check only decides which bookkeeping to undo after a destructive run.
- **Tests:** in `ConsoleToolTests` through the fake process: deadline expiry yields `Unknown` with the tree killed; not started yields `NotStarted` with no exit code; captured output survives a failure. In B097's `SdFormatRunTests`: a diskpart `Unknown` with the marker still on the card restores the registration and keeps the database entry; with no marker it retires the entry; both show the uncertain message. (This refines B097's spec, which says the retirement always runs.) Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~ConsoleTool|FullyQualifiedName~OtherManagers|FullyQualifiedName~UnelevatedLauncher|FullyQualifiedName~Autostart"`.
- **Plan v2:** B095 (runner); the diskpart mapping lands in B097.
- **Related:** U04A-LFA-014, WINSVC-007.

### WINSVC-017: Storage watchers create the process message window themselves

- **Severity:** low
- **Where:** `src/WSGM/Shell/CardAcfWatcher.cs:90`, `src/WSGM/Shell/RemovableDriveManager.cs:156`, `src/WSGM/Shell/ShellSession.SteamUi.cs:583-584` (`CardVolumeMonitor.StartNew(MessageWindow.Create(), ...)`); `src/WSGM/Interop/MessageWindow.cs` (`Create()`).
- **Problem:** the public get-or-create `MessageWindow.Create()` lets any holder own and destroy the one process window (U05-LFB-014).
- **Best solution:** owner-only construction as SESSION-B3 defines it: the composition root creates the one `MessageWindow` on the UI thread, disposes it last, and passes it by constructor to `CardAcfWatcher`, `CardVolumeMonitor`, `RemovableDriveManager` and every other consumer; delete `Create()`. Keep the existing counted `Register*/Deregister*` pairs as they are; no `IDisposable` claim objects and no HWND user data (critic conflict 14). `DisplayChangeWindow` gets the same owner-only construction in the same batch.
- **Tests:** existing `MessageWindow` and storage tests updated to receive a window; a consumer disposed before the window deregisters cleanly. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Tray|FullyQualifiedName~MessageWindow|FullyQualifiedName~WindowFinder|FullyQualifiedName~RemovableDrive|FullyQualifiedName~CardAcf"`.
- **Plan v2:** B114 (session domain).
- **Related:** U05-LFB-014, SESSION-043, critic conflicts 14 and 25 (WINSVC-B10 no longer touches HotkeyService or OverlayController activation), WINSVC-V-004, WINSVC-045.

### WINSVC-018: `AudioManager.Refresh` writes UI-owned fields from a pool thread

- **Severity:** low
- **Where:** `src/WSGM/Shell/AudioManager.cs:389-412` (`Refresh`, `QueueRefresh`), `src/WSGM/Shell/AudioProfileService.cs:69` (`_refresh = audio.Refresh`, invoked in `finally` blocks after `ConfigureAwait(false)` in `ApplyAsync`, `SetPlaybackFormatAsync`, `SetSpatialFormatAsync`).
- **Problem:** `Refresh` then writes `_stickyError`, `_refreshPending` and `_refreshPendingEndpoints` off the UI thread; a concurrent `RunPendingRefresh` on the UI thread can lose the pending flag, so an endpoint change after a profile apply may not be published.
- **Best solution:** make `Refresh` the UI-thread entry it should be: first line `if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(Refresh); return; }`. No other change.
- **Tests:** none new in B094: WSGM.Tests has no dispatcher harness and `AudioManager` starts a `DispatcherTimer` in `Start`, so the post is verified by review and the existing `AudioManagerTests` stay green. Once B099 gives `AudioManager` a fake `IAudioEndpoints` (WINSVC-020), a refresh requested from a pool thread can be asserted there if a headless dispatcher is available in that suite; otherwise leave it to review. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~AudioManager"`.
- **Plan v2:** B094.
- **Related:** WINSVC-020.

### WINSVC-019: Volume buttons run COM and WaveOut on the UI thread; the feedback player is a never-disposed static

- **Severity:** low
- **Where:** `src/WSGM/Shell/VolumeButtonService.cs` (`OnShellHook` calls `CoreAudio.ApplyCommand` and `VolumeFeedback.Play` synchronously), `src/WSGM/Shell/VolumeFeedback.cs` (static class, `_player` never disposed); consumers `AudioManager.cs` near 377, 556, 732, 807.
- **Problem:** a COM round trip and a WaveOut call per key press run on the Avalonia UI thread, against AudioManager's own "nothing slow on the UI thread" rule, and the WaveOut handle lives until process exit.
- **Best solution:** do not use `CoalescingVolumeWrite`: it keeps only the latest absolute percentage, while a button press is a relative step (up, down, mute toggle), so coalescing would drop presses. Instead `VolumeButtonService` keeps one serial chain, `private Task _buttonWork = Task.CompletedTask;`, and `OnShellHook` only appends: `_buttonWork = _buttonWork.ContinueWith(_ => Apply(command), TaskScheduler.Default);`. `Apply` runs `ApplyCommand` through `IAudioEndpoints` (WINSVC-020) and `feedback.Play()` off the UI thread, in press order, then posts `_audio.NoteExternalVolume(...)` and the indicator show/hide to `Dispatcher.UIThread` (those touch UI-owned state). Every press is applied exactly once, as Explorer does. Replace the static `VolumeFeedback` (`src/WSGM/Shell/VolumeFeedback.cs`) with an instance `VolumeFeedbackPlayer` in the same folder (same `Initialize`, `Reinitialize`, `Play`, pacing gates and player) created by the session, passed to `AudioManager` and `VolumeButtonService`, and disposed at shutdown after both.
- **Tests:** over a fake `IAudioEndpoints`: three quick presses apply three commands in order (none coalesced) and none runs on the calling thread; the feedback player is disposed at shutdown. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~AudioManager|FullyQualifiedName~VolumeAppCommands|FullyQualifiedName~VolumeOsd"`.
- **Plan v2:** B099. Its file list names `src/WSGM/Core/VolumeFeedback.cs`; the file is `src/WSGM/Shell/VolumeFeedback.cs`.
- **Related:** U01-026 (WDC WaveOut ownership, B066), WINSVC-009, 020.

### WINSVC-020: Six audio consumers reach Core Audio six different ways

- **Severity:** low
- **Where:** statics in `src/WSGM/Shell/AudioManager.cs`, `DisplayOffMuteService.cs`, `VolumeButtonService.cs`; an injected delegate in `src/WSGM/Shell/RadioManager.cs:95-106`; a direct call from a view model in `src/WSGM/Settings/AudioProfileEditor.cs:41-59`; `IAudioProfileOperations` in `src/WSGM/Shell/AudioProfileService.cs`.
- **Problem:** no single seam for tests, and a Settings view model acquires native audio state directly, against the "no native acquisition in views" rule.
- **Best solution:** rename and extend `IAudioProfileOperations` into one internal `IAudioEndpoints` port (list endpoints, default id, volume and mute by default and by endpoint id, device format, spatial format, volume and endpoint watches) with one production adapter over WDC `CoreAudio`'s static facade (critic conflict 9: WDC stays static with disposable registrations). The session builds one adapter and passes it to `AudioManager`, `AudioProfileService`, `DisplayOffMuteService`, `VolumeButtonService` (through `AudioManager`) and `RadioManager` (Bluetooth audio connect). `AudioProfileEditor` reads through `AudioProfileService` instead of `CoreAudio`. NativeQam audio services take the same instances by constructor.
- **Tests:** `AudioManager` selection latest-wins with a fake port; `AudioProfileEditor` populates from a fake service. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~AudioManager|FullyQualifiedName~AudioProfile|FullyQualifiedName~DisplayMute|FullyQualifiedName~BluetoothAction"`.
- **Plan v2:** B099 (after B066).
- **Related:** WDC-V-005 (B066 name fallback), WINSVC-003, 018, 019.

### WINSVC-021: Pairing token written on the WinRT thread; Rescan can leave the scanning flag stuck

- **Severity:** low
- **Where:** `src/WSGM/Shell/RadioManager.cs:1340-1357` (`OnPairingRequested` writes `_pairingToken` on the callback thread), read on the UI thread at 292 and 1298; `:446-461` (`Rescan` sets `BluetoothScanning = true`), `:478-483` (`StopAndRestartBluetoothWatch` returns early when feeds are not started).
- **Problem:** `_pairingToken` is a `uint`, so the write cannot tear, but it is UI-owned state written off the UI thread: a cancel on the UI thread can answer a request whose UI post has not run yet, and the ownership rule ("marshal UI state through the dispatcher", src/WSGM/AGENTS.md) is broken; and `Rescan` with the feeds stopped sets `BluetoothScanning` with nothing to ever clear it, so the spinner stays.
- **Best solution:** assign `_pairingToken = request.Token` inside the existing UI post that publishes the request, and clear it there too. In `Rescan`, guard the Bluetooth block with `BluetoothPower == RadioPower.On && _feedsStarted` (with B063 this becomes "a Bluetooth registration exists"), so the flag is set only when a restart is really queued.
- **Tests:** in `BluetoothActionTests` or `RadioManagerTests`: a pairing request posted from another thread is visible to a UI-thread cancel; `Rescan` with feeds stopped leaves `BluetoothScanning` false. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~BluetoothDeviceCatalog|FullyQualifiedName~RadioManager|FullyQualifiedName~BluetoothAction"`.
- **Plan v2:** B065.
- **Related:** WINSVC-022, 044, V-005.

### WINSVC-022: RadioManager compensates for WDC's process-wide feeds with a static queue and generations

- **Severity:** low
- **Where:** `src/WSGM/Shell/RadioManager.cs:41` (`_feedWork` static), `:478-518` (`QueueFeedWork`), `:675-713` (`_bluetoothWatchGeneration`).
- **Problem:** the static queue and generation counters exist only because the Settings preview builds a second RadioManager beside the session's and both drive WDC's single process-wide watcher; a second manager stopping its feed stops the session's.
- **Best solution:** consume WDC's per-call disposable watch registrations from B063 (`Start*Watch` returns an owned `IDisposable`): each RadioManager holds its own registrations, starting a feed creates one and disposing it is the stop. Delete `_feedWork`, `QueueFeedWork` and `_bluetoothWatchGeneration`. Do not dispose a Wi-Fi registration from inside its own callback (B063 documents this).
- **Tests:** start and stop scanning creates and disposes one registration each; a second manager does not stop the first manager's feed. Filter: the B063 WSGM filter, `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~RadioManager|FullyQualifiedName~RadioEntry|FullyQualifiedName~BluetoothAction|FullyQualifiedName~NativeQamNetwork|FullyQualifiedName~NativeQamBluetooth"`, plus WDC `WatchRegistrationTests` on net8 and net10.
- **Plan v2:** B063 (WDC batch carrying the consumer).
- **Related:** U01-001, U01-007, U01-040, A01-F003, A01-F005, WDC-014, critic conflict 9, WINSVC-038.

### WINSVC-023: NativeQam network service polls Wi-Fi status itself

- **Severity:** low
- **Where:** `src/WSGM/Shell/NativeQamNetworkService.cs:105-132` (polls `WindowsRadio.GetWifiStatus()` at 120 and merges rows by SSID string), `src/WSGM/Shell/RadioManager.cs:578-595` (publishes `ConnectedSsid`, `WifiSignal`, `WifiConnected` every 2 s).
- **Problem:** two pollers of the same native state, which can disagree, and a second place keyed by display SSID.
- **Best solution:** `NativeQamNetworkService` projects from the session's `RadioManager` (connected network, signal, rows keyed by `WifiNetworkKey` from B064) and stops calling `WindowsRadio` directly. Strings stay identical through the B064 mapping table.
- **Tests:** NativeQam network projection over a RadioManager with fake radio services publishes the same state as today. Filter: the B064 WSGM filter above (includes `NativeQamNetwork`).
- **Plan v2:** B064 (decided: D4 approved; the guidance diff is shown with the batch and applied).
- **Related:** WDC-022, WDC-V-002, C6 (Wi-Fi key consumers).

### WINSVC-024: KeepAwakeService loop is untracked and Dispose does not join it

- **Severity:** low
- **Where:** `src/WSGM/Shell/KeepAwakeService.cs:105-112` (`Dispose`), `:138-148` (`StartNew`, `_ = Task.Run(service.RunAsync)`), `:235-260` (`RunAsync`), `:316` (static `SteamDownloadActivity.QueryAsync`).
- **Problem:** `Dispose` cancels and disposes the CTS and the wake locks while a poll may still be in flight; the poll can then touch a disposed lock (swallowed by `WakeLock`) or a disposed token source. The ambient static CEF query makes the loop untestable.
- **Best solution:** a poll that finishes after `Dispose` is already harmless: `WakeLock.Acquire` catches `ObjectDisposedException` (`src/WSGM/Core/WakeLock.cs:42`), so a late poll holds nothing, the loop's `Task.Delay` on the cancelled token returns at once, and the session unsubscribes `DownloadActivityChanged` before `Dispose` (`ShellSession.Shutdown.cs:742`). So no join, `StopAsync`, deadline or `_loop` field is added (D1 is decided as safety-first ordered steps under one deadline, which B140 implements; B101 still does not depend on it, and a join would add a step to that deadline for no defect). Two changes only: (1) inject the download query as a constructor `Func<CancellationToken, Task<SteamDownloadOverview?>>` (production passes `SteamDownloadActivity.QueryAsync`), so the loop is testable; (2) `Dispose` stops disposing `_cts` (it has no timer and no linked sources, so `Cancel()` is all it needs), which removes the disposed-token-source access the review flagged. This deviates from the B101 spec wording "loop joined on dispose".
- **Tests:** in `KeepAwakeTests`, with a fake query that blocks until cancelled and then reports an active download: after `Dispose` the loop ends without throwing and the download hold reports not held. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~KeepAwake"`.
- **Plan v2:** B101.
- **Related:** D1 decided (ordered steps under one deadline, B140), not needed here.

### WINSVC-025: ModernStandbyGuard calls statics and disposes its lifetime under a running suspend

- **Severity:** low
- **Where:** `src/WSGM/Shell/ModernStandbyGuard.cs:40-70` (constructor, `Dispose` cancels and disposes `_lifetime`), `:82-83` (borrows `DisplayMuteDecider.IsDisplayOff/MayReportDark`), `:114-120` (`ModernStandby.ReadStandbyTiming`, `WasLastResumeUnattended`, `LastInput.Age`), `:166` (`WindowsPower.SuspendAsync(false, _lifetime.Token)`).
- **Problem:** untestable statics, a suspend that may still be running when its token source is disposed, and display-state interpretation borrowed from the mute feature.
- **Best solution:** inject a small standby port into the constructor: `Func<TimeSpan?> sinceWake`, `Func<bool> lastResumeUnattended`, `Func<TimeSpan> lastInputAge`, `Func<CancellationToken, Task> suspend` (production passes the WDC and `LastInput` statics; W02_02's internal port is not reachable from WSGM). For the lifetime: `Dispose` keeps `_lifetime.Cancel()` and deletes `_lifetime.Dispose()`. The source has no timer and no linked sources, so cancelling is all it needs, and a suspend still running then never touches a disposed source; no task field or continuation is added. Move `IsDisplayOff` and `MayReportDark` out of `DisplayMuteDecider` into a small static `DisplayPowerSignal` beside the MessageWindow display-state event; both the mute service and the guard call it.
- **Tests:** new `ModernStandbyGuardTests` over the fake port: an unattended resume past the threshold suspends once; user input within the window does not; `Dispose` during a pending suspend does not throw. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~ModernStandby|FullyQualifiedName~DisplayMute"`.
- **Plan v2:** B101.
- **Related:** B002 (W02_02 port semantics), WINSVC-026.

### WINSVC-026: DisplayOffMuteService adds a ProcessExit handler per instance and never removes it

- **Severity:** low
- **Where:** `src/WSGM/Shell/DisplayOffMuteService.cs:165-173` (constructor subscribes a lambda), `:176-196` (`Dispose` restores but leaves the handler).
- **Problem:** the handler roots every instance for the process lifetime and runs `Restore` off the UI thread against UI-owned fields, even after the ordered shutdown already restored.
- **Best solution:** store the handler in a field (`private readonly EventHandler _onProcessExit;`), subscribe it in the constructor as today, and unsubscribe it first thing in `Dispose`. It stays only as the fallback for an exit that skips session cleanup; B006 makes every normal exit run the cleanup, which calls `Dispose` and restores on the UI thread.
- **Tests:** none in B094: the service takes a live `MessageWindow` and calls `CoreAudio` statics, so WSGM.Tests cannot construct it without a native window, and no internal subscription count exists (do not add one). Verified by review in B094; once B099 constructs the service over the fake `IAudioEndpoints` (WINSVC-003/020) and a window the tests can supply, add the weak-reference check there (after `Dispose` and a full GC the instance is collected). Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~DisplayMute"`.
- **Plan v2:** B094.
- **Related:** B006 (exit cleanup), WINSVC-003.

### WINSVC-027: EDID is parsed twice and `DisplayProfiles` is a stale name

- **Severity:** low
- **Where:** `src/WSGM/Core/EdidModes.cs` (`ReadAdvertisedRefreshRates`, registry EDID located by string surgery in `DisplayProfiles.ReadPrimaryMonitorInstanceId`), WDC `DisplayEdid.cs:31-44` (`Parse`/`ReadModes` over the WinRT descriptor, used by `SettingsViewModel.cs:263`), `src/WSGM/Core/DisplayProfiles.cs` (only mode helpers remain; WDC 2f07485 removed the profile API).
- **Problem:** two EDID parsers with different sources, and a class name that no longer describes its content.
- **Best solution:** conditional, as plan v2 states. If WDC exposes advertised refresh rates for a `DisplayTargetIdentity` after B069, delete `EdidModes` and `ReadPrimaryMonitorInstanceId`, call the WDC API from the pairing service's production delegate, and rename `DisplayProfiles` to `DisplayModeHelpers` (internal static, same members). If WDC does not, change nothing but the stale `DisplayProfiles` class comment. Do not add the API to WDC for this.
- **Tests:** existing `RefreshRatePairingServiceTests` and `DisplayResolutionServiceTests` stay green. Filter: as WINSVC-012.
- **Plan v2:** B098.
- **Related:** CONFIG-028, CONFIG-029, critic conflict 10, WINSVC-012.

### WINSVC-028: DisplayArrivalWaiter duplicates the WDC display waits

- **Severity:** low
- **Where:** `src/WSGM/Shell/DisplayArrivalWaiter.cs`, `src/WSGM/Shell/ShellDisplaySignals.cs:36-38` (one pending `Task.Delay` per hint), WDC `DisplayTopology.WaitForPresentAsync`/`WaitForAvailableAsync`.
- **Problem:** two waiters for the same condition; WDC's own waits are not what WSGM uses, and the hint signal leaves a delay task per hint.
- **Best solution:** move the WSGM settle waiter into WDC as `WaitForTargetsAsync` with caller-supplied settle interval and backstop (WSGM passes today's 500 ms and 5 s) and an injected change hint (WSGM keeps `IDisplayChangeSignal`, since only WSGM owns the top-level window). Delete WDC's `WaitForPresentAsync`/`WaitForAvailableAsync` and the WSGM `DisplayArrivalWaiter`; move its tests to WDC `DisplayWaitTests`.
- **Tests:** settle needs two equal fingerprints, `Win32Exception` counts as not settled, backstop used when there is no target, cancellation ends, an empty target list returns at once. Filters: WDC `DisplayWaitTests` on net8 and net10; `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~GameModeEntry"`.
- **Plan v2:** B070.
- **Related:** U01-029, U01-040, U05-LFB-030, A01-F006, C3.

### WINSVC-029: Bluetooth container normalization overlaps WDC

- **Severity:** low
- **Where:** `src/WSGM/Shell/BluetoothDeviceCatalog.cs`, `tests/WSGM.Tests/Shell/BluetoothDeviceCatalogTests.cs`.
- **Problem:** WSGM normalizes raw WDC watcher output into logical devices, partly duplicating what WDC should publish. The review proposed moving the whole catalog to WDC; the verifier flagged the conflict with plan L75 and the critic resolved it (conflict 12).
- **Best solution:** WDC gains one container normalizer (container id grouping of endpoints) in B065; `BluetoothDeviceCatalog` stays in WSGM with its product policy (row naming, sweep census, audio pairing kinds) and calls the WDC normalizer instead of its own grouping code. Nothing else moves.
- **Tests:** `BluetoothDeviceCatalogTests` keep their expectations over the WDC-normalized input. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~BluetoothDeviceCatalog|FullyQualifiedName~RadioManager"`, plus the WDC `WindowsRadioTests|PairingTests` filter on net8 and net10.
- **Plan v2:** B065.
- **Related:** WDC-005, U01-040, critic conflict 12.

### WINSVC-030: Windows power policy has no single mutation owner

- **Severity:** low
- **Where:** `src/WSGM/Core/PowerSchemes.cs`, `CpuBoost.cs`, `HybridCores.cs`, `WindowsPowerModes.cs` (with `IPowerSchemeApi`, `ICpuBoostApi`, `IHybridCoreApi`, `IPowerModeApi`), static `src/WSGM/Core/PowerTimeouts.cs:28-133` (direct WDC `WindowsPower.Read/WriteSetting`), `src/WSGM/Shell/DisplayTimeouts.cs`, `src/WSGM/Interop/WindowsPowerSchemeApi.cs`.
- **Problem:** the four small ports are fine and UI tests fake them; what is missing is one owner of the mutation lock, and `PowerTimeouts` is a static that bypasses every port.
- **Best solution:** the `PowerSchemes` instance is the owner (WINSVC-009). Make `PowerTimeouts` an instance class over `IPowerSchemeApi` extended with `ReadSetting`/`WriteSetting` (implemented in `WindowsPowerSchemeApi` over WDC `WindowsPower`), taking the `PowerSchemes` instance for the lock and the active scheme. Keep the four existing ports; no `PowerPolicyLane` type.
- **Tests:** `PowerTimeouts` read and write over a fake `IPowerSchemeApi`; `DisplayTimeoutsTests` construct the instance. Filter: as WINSVC-009.
- **Plan v2:** B090.
- **Related:** DEVICE-040, critic conflict 13, WINSVC-009, 010.

### WINSVC-031: RTSS frametime read truncates at 1,024 entries

- **Severity:** low (plan v2 reduced it to the cap only)
- **Where:** `src/WSGM/Core/RtssFrametimeReader.cs:114` (`MaximumEntries = 1024`), `:195` (`count = Math.Min(arraySize, MaximumEntries)`), the capacity check in the loop at about 202; `src/WSGM/Core/RtssOsd.cs:74-92` and `RtssFrametimeReader.cs:41-60, 255-256` (two mappings of `RTSSSharedMemoryV2`).
- **Problem:** the entry cap silently drops applications past entry 1,024 although the per-entry capacity check is the real bound (no-arbitrary-limits rule). The duplicate mapping and the per-sample allocations at 1 Hz are not defects worth new mechanism (verifier: the path is not high-rate).
- **Best solution:** delete `MaximumEntries`; iterate `for (long index = 0; index < arraySize; index++)` and keep the existing `entry + entrySize > capacity` break as the only bound. Keep the frametime reader's own read-only mapping, independent of the OSD writer's read-write open, so AutoTDP keeps samples wherever write access is denied. No shared mapping owner, no reusable buffers, no allocation assertion.
- **Tests:** in `RtssFrametimeReaderTests`, a fake region with 1,100 valid entries yields 1,100 samples; an `arraySize` larger than the region stops at capacity. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Rtss|FullyQualifiedName~AutoTdpService"`.
- **Plan v2:** B100 (after B005 and B094).
- **Related:** C14, WINSVC-001, 032.

### WINSVC-032: RTSS discovery caps and `MainModule`

- **Severity:** low (verifier corrected the bitness claim)
- **Where:** `src/WSGM/Core/RtssDiscovery.cs:383` (file over 32 MiB treated as absent), `:469-470, 544` (more than 4,096 export names returns no exports, so the install reads as incompatible), `:563-583` (export names cut at 128 bytes), `:441` (`process.MainModule?.FileName`); `src/WSGM/Interop/NativeShellProcess.cs:65-80` (`TryGetImagePath`).
- **Problem:** the caps refuse or truncate valid content. `MainModule` needs `PROCESS_QUERY_INFORMATION | PROCESS_VM_READ`, so an unelevated WSGM cannot inspect an elevated RTSS and reports it as not running (WSGM is x64, so bitness is not the issue; integrity is). With RTSS now started with WSGM and kept alive (DECISIONS.md, USER-001 in B005), that false "not running" would also make the keep-alive launch RTSS again while it runs, so the fix is functional, not hardening.
- **Best solution:** delete the 32 MiB check (`file.Exists` stays); delete `MaxExportNames` and keep `namesOffset + nameCount * 4L > stream.Length` as the bound; delete `MaxExportNameBytes` and read each name until NUL or end of stream (a non-printable byte still discards the name as today). Read the process path with the existing `NativeShellProcess.TryGetImagePath((uint)process.Id)` (limited query right) and keep `process.StartTime` in the same `try` (it needs only the limited right).
- **Tests:** in `RtssDiscoveryTests`: a synthetic PE with 5,000 exports and a 200-byte name lists both; a process whose image path comes from the limited query (fake path provider if the existing seam allows) is reported running. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Rtss"`.
- **Plan v2:** B100.
- **Related:** CRIT-004 (same `MainModule` class, session B112), WINSVC-031.

### WINSVC-033: Authenticode online revocation can stall the first RTSS probe offline

- **Severity:** low (informational)
- **Where:** `src/WSGM/Interop/NativeAuthenticode.cs:41-62` (`WTD_REVOKE_WHOLECHAIN`), `src/WSGM/Core/RtssDiscovery.cs:388-417` (cache by length and mtime), `src/WSGM/Core/PerformanceService.cs:447` (a command probes inline).
- **Problem:** offline, the first verification of an RTSS file can wait for the CRL timeout. The cache limits this to once per file, but if that first probe happened inside a user command it would delay the command.
- **Best solution:** keep the revocation policy (its comment documents why). No code change: after B005 the performance service probes immediately at start (RTSS starts with WSGM and is kept alive, as the maintainer decided for USER-001), so the cache is warm before any command reaches the inline probe. B100 only confirms this ordering in the code and records it in the batch result; if B005's start probe is ever removed, the command path must reuse the last probe instead.
- **Tests:** none new; `PerformanceService` tests from B005 already assert the start probe. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~PerformanceService|FullyQualifiedName~Rtss"`.
- **Plan v2:** B100.
- **Related:** USER-001 (B005).

### WINSVC-035: The format run writes the library through a drive letter

- **Severity:** low
- **Where:** `src/WSGM/Shell/SdFormatManager.cs:1260-1288` (`CreateSteamLibrary`, `RegisterLibrary` write to `{letter}:\SteamLibrary` after `WaitForLetter` and a retrim), `src/WSGM/Interop/NativeStorage.cs:842` (`TryGetVolumeGuidPath`).
- **Problem:** Shell/AGENTS.md: resolve a letter to its volume GUID path once and never validate on a letter and then write to it. A letter reassigned between the wait and the write would put the marker and client DLL on another volume.
- **Best solution:** right after `WaitForLetter` succeeds, call `NativeStorage.TryGetVolumeGuidPath(letter, out var root)`, verify that volume maps to the run's disk number (`NativeStorage.OpenVolumeForQueryPath(root)` and `TryGetDeviceNumber` on that handle, the pair `EjectUnletteredMedia` already uses), stop with the existing card-changed message if it does not, and write the marker, client DLL and library folder through `root`. Only the Steam registration (VDF entry and the CEF add call) keeps the letter path, because that is what Steam stores.
- **Tests:** in `SdFormatRunTests` over the fake `IDiskIdentity`: a GUID path on another disk stops the run before any write; the registration receives the letter path while file writes go through the GUID root. Filter: as WINSVC-007.
- **Plan v2:** B097.
- **Related:** WINSVC-006, 007.

### WINSVC-036: Library removal blocks on async work inside `Task.Run`

- **Severity:** low
- **Where:** `src/WSGM/Shell/SdFormatManager.cs:904-905` (`RemoveLibraryByContentIdAsync(...).GetAwaiter().GetResult()`).
- **Problem:** sync-over-async on a pool thread; it ties up a worker for the CEF round trip and hides cancellation.
- **Best solution:** make the removal chain async end to end once `ISteamLibraryRegistration` exists: `RemoveExistingLibraryAsync` awaits the content-id removal, and `FormatAsync` awaits it directly instead of `Task.Run(() => RemoveExistingLibrary(...))`.
- **Tests:** the B097 fake registration completes asynchronously and the run observes its result. Filter: as WINSVC-007.
- **Plan v2:** B097.
- **Related:** WINSVC-007.

### WINSVC-037: DisplayScale writes into the live AppConfig from a mode transition

- **Severity:** low
- **Where:** `src/WSGM/Core/DisplayScale.cs` (`ApplyGameMode` capture block sets `config.SavedDisplayScaleEntries`), called from `src/WSGM/Shell/SessionModes.cs:232`.
- **Problem:** mutating the session's live config object bypasses the store; a reload replaces the instance and the captured recovery entries can be lost or written by an unrelated save (same pattern as U05-LFB-019).
- **Best solution:** `ApplyGameMode` returns the captured entries; `SessionModes` persists them through the config store's writer transaction (`Update`) and never assigns into the live `AppConfig`. The restore path reads them from the store.
- **Tests:** a game-mode entry with a fake store persists the captured scale entries once and leaves the live config object untouched. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SessionModes|FullyQualifiedName~GameModeEntryTransaction"`.
- **Plan v2:** B115 (session domain).
- **Related:** U05-LFB-019.

### WINSVC-038: Preview compositions build duplicate live managers

- **Severity:** low
- **Where:** `src/WSGM/Shell/SystemStatus.cs:46-55` (creates `AudioManager`, `RadioManager`, `RemovableDriveManager` when none are passed), `src/WSGM/Overlay/OverlayController.cs:246-258` (creates a second `SdFormatManager` with its own format gate), callers `OverlayController.cs:535, 1019`.
- **Problem:** hidden fallback construction: a sheet without session instances enumerates and watches the machine with its own managers, and a second format manager means a second format gate.
- **Best solution:** make the `SystemStatus` constructor parameters required and delete the `_owns*` flags. The composition that has no session (overlay-test, Settings preview) constructs the managers once at its own root, owns and disposes them, and passes them in; with a session, the session's instances are passed as today. The `OverlayController` half (its lazy `SdFormatManager` fallback) is done in B097 by WINSVC-007, which B101 depends on; this finding only checks that it landed. What each surface shows stays identical. B101's file list names `OverlayController.cs (preview SdFormatManager)`; it needs `src/WSGM/Settings/SettingsWindow.axaml.cs` (the preview root that now owns the managers) instead.
- **Tests:** UiTests compositions pass fakes; a `SystemStatus` built over passed managers disposes none of them. Filters: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SystemStatus"` and the overlay UiTests filter the batch touches.
- **Plan v2:** B101.
- **Related:** WINSVC-007, 022.

### WINSVC-039: Tests assert copied predicates and constants instead of the owners

- **Severity:** low
- **Where:** `tests/WSGM.Tests/Shell/SdFormatTests.cs:190-200` (pins `ReverifiedStages`), `:350-648` (VDF tests in the wrong class), `tests/WSGM.Tests/Shell/DisplayMuteTests.cs:9` (`DownloadCompletionRestoreDelay_IsTenSeconds`), `tests/WSGM.Tests/Shell/RadioManagerTests.cs:91-185` and `AudioManagerTests.cs:28-37` (test WDC `WindowsRadio`, `WifiProfile`, `CoreAudio`).
- **Problem:** constant pins and WDC tests in the WSGM suite give no owner coverage; no test drives RadioManager scanning, AudioManager refresh, DisplayOffMuteService, the format run, the card monitor, the ACF watcher, the eject path, OtherManagers Apply/RestoreAll, KeepAwake's loop or the standby guard.
- **Best solution:** delete the `ReverifiedStages` and ten-second constant pins (B097's run tests replace the first); move the VDF tests into `SteamLibraryVdfTests`; move the WDC-only tests into the WDC suite (U01-041) where equivalents are missing, else delete them; add owner tests for the card monitor and ACF watcher over fake inventories, and for OtherManagers Apply, in B101. The other owners get their tests in the batches listed under their own findings.
- **Tests:** the moved and new classes. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~KeepAwake|FullyQualifiedName~ModernStandby|FullyQualifiedName~CardVolume|FullyQualifiedName~CardAcf|FullyQualifiedName~OtherManagers|FullyQualifiedName~SteamLibraryVdf"`.
- **Plan v2:** B101 (after B097 and B099).
- **Related:** U01-041, U05-LFB-024.

### WINSVC-V-002: CardVolumeMonitor polls every 3 s while Steam or CEF is unavailable

- **Severity:** low
- **Where:** `src/WSGM/Shell/CardVolumeMonitor.cs:271-283` (`!_enabled() || !Steam.IsRunning` calls `Schedule()`), `:331-341` (`!SteamUiReadiness.IsReady` calls `Schedule()`); kicks at `src/WSGM/Shell/ShellSession.cs:1170` (Steam restart, game mode only) and `CardVolumeMonitor.Kick`; `src/WSGM/Shell/SteamUiReadiness.cs:93-180` (`Observe`, `WhenReadyAsync`, private `NextReadyAsync`).
- **Problem:** with the CEF master switch off, Steam not running, or Big Picture not visible (including a whole desktop session with Steam open), every 3 s settle a pass re-runs system-disk resolution, the volume walk and marker reads, for as long as the condition holds. These volume handle opens are not covered by `CardWatcher.Suspend()` during an eject.
- **Best solution:** remove both self-reschedules; the waiting branches only log once (existing `_waitingForSteamUi`) and return. Kick from the events that end the wait: in the `_monitor.SteamStarted` handler, call `_cardVolumes?.Kick("Steam started")` before the game-mode early return so desktop sessions get it too; when the waiting branch first sets `_waitingForSteamUi`, arm one continuation on the next transport ready edge (make `SteamUiReadiness.NextReadyAsync` internal and call it with the monitor's lifetime token) that calls `Kick("Steam UI ready")`. The ready edge already covers master-switch enable (desktop opens on the switch) and Big Picture readiness in game mode. Known delta to check manually: on the desktop, opening Big Picture raises no edge, so a pending reconcile then runs at the next volume notification, Steam restart or game-mode entry instead of within 3 s.
- **Tests:** with a fake inventory and readiness: no pass runs while waiting; a ready edge or a Steam-start kick runs exactly one pass. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~CardVolume"`.
- **Plan v2:** B101. Its file list lacks `src/WSGM/Shell/ShellSession.cs` (the `SteamStarted` kick) and `src/WSGM/Shell/SteamUiReadiness.cs` (`NextReadyAsync` visibility).
- **Related:** WINSVC-V-001, WINSVC-004.

### WINSVC-V-003: EffectivePowerModeNotification invokes the managed callback unguarded

- **Severity:** low
- **Where:** `src/WSGM/Interop/EffectivePowerModeNotification.cs:88-98` (`[UnmanagedCallersOnly] OnChanged` looks up and invokes `changed` synchronously on the powrprof thread), consumer `src/WSGM/Shell/DeviceCoordinator.cs:160` (`RequestPowerAssignmentReconcile`).
- **Problem:** any exception from the callback or code it reaches crosses an `UnmanagedCallersOnly` boundary and fail-fasts the process. This is the real unguarded native callback in this area; the window procedures only post (see WINSVC-045).
- **Best solution:** move the body of `OnChanged` into `internal static void Dispatch(nint context)` (the `[UnmanagedCallersOnly]` entry only calls it) and wrap the lookup and invoke there in `try { ... } catch (Exception ex) { Log.Warn(...); }`, returning normally. No per-registration flag: `Callbacks` maps an id to a bare `Action`, there is no registration record to hang one on, and effective power mode changes are rare enough that one warning per failure is not noise. No posting lane, no new owner.
- **Tests:** none new: `Callbacks` is private and filled only by the registration path, which calls powrprof, and a test-only insertion seam is not allowed (tests/WSGM.Tests/AGENTS.md), so the guard is verified by review. B091's filter stays green. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~AutoTdp|FullyQualifiedName~DevicePower|FullyQualifiedName~ApplicationPerformance"` (B091's filter).
- **Plan v2:** B091 (device domain).
- **Related:** U05-LFB-013, DEVICE-017, WINSVC-045.

### WINSVC-V-004: Late shutdown disposes UI-owned managers off the UI thread and after the message window

- **Severity:** low (plausible: whether Avalonia asserts on `DispatcherTimer.Stop` off-thread was not verified)
- **Where:** `src/WSGM/Shell/ShellSession.Shutdown.cs:475-496` (`_audio`, `_radios`), `:586-608` (`_steamStorage`, `_drives`), which run after `ConfigureAwait(false)` awaits on a pool thread; `:264` and `:652-757` (`DisposeUiOwnedSessionResources` destroys the MessageWindow at 751-755, before those disposals).
- **Problem:** `AudioManager.Dispose` stops a `DispatcherTimer` and touches UI-owned fields, `RadioManager.Dispose` raises bound properties, `RemovableDriveManager.Dispose` stops two dispatcher timers, all from a pool thread. `RemovableDriveManager.Dispose` also deregisters volume notifications on a window that is already destroyed (benign today because `MessageWindow.Dispose` zeroes the handle, but the order is wrong; ledger PV11-013, review C8).
- **Best solution:** two moves, no new mechanism and no reordering of the Steam UI teardown:
  1. Take the "message window" `CleanupUiResource` out of `DisposeUiOwnedSessionResources` and run it as the last shutdown step, in its own `Dispatcher.UIThread.InvokeAsync` after `_drives` is disposed. Unsubscribing the session's own MessageWindow handlers stays where it is.
  2. Dispose `_audio`, `_radios`, `_steamStorage` and `_drives` at their current positions through `await Dispatcher.UIThread.InvokeAsync(() => CleanupUiResource(failures, "...", ...))`, keeping the bridge-before-drives order.
  When B140 replaces shutdown with the ordered step list (D1 decided: safety-first ordered steps under one deadline), it keeps this order: providers on the UI thread, the MessageWindow last. The card monitor, ACF watcher, display mute, standby guard and keep-awake already dispose on the UI thread inside `DisposeUiOwnedSessionResources` before the window, so B094's spec mention of "card monitors" needs no change beyond step 1.
- **Tests:** a shutdown-order test, if `ShellSession` shutdown tests exist, asserting the window is disposed after the drive manager; otherwise record the manual check (tray Exit with a card inserted, no warning in `wsgm.log`). Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~RemovableDrive|FullyQualifiedName~AudioManager|FullyQualifiedName~RadioManager"`.
- **Plan v2:** B094.
- **Related:** U05-LFB-014, PV11-013, C8, WINSVC-017.

### WINSVC-C-001: The overlay power-profile picker locks itself after a failed selection

- **Severity:** low (solution check; the overlay twin of CRIT-003)
- **Where:** `src/WSGM/Overlay/PowerSchemeSelection.cs:23` (`CanSelect` requires `ActiveId is not null`), `:115-121` (the catch sets `ActiveId = null` and "Refresh to read Windows state before trying again"); for contrast `src/WSGM/Overlay/HybridCoreSelection.cs:95-103` reports the failure and stays selectable.
- **Problem:** any exception from a selection, including today's readback mismatch from `PowerSchemes.Select` (WINSVC-010), nulls `ActiveId`, which disables every choice until the user presses Refresh. After WINSVC-010 `Select` no longer reads back at all (D9), but a write that fails to dispatch still latches the control, which is the same gate CRIT-003 removes from the QAM hybrid-core row: a new explicit selection is itself the user action the rule allows.
- **Best solution:** in the catch, null `ActiveId` only when the failed run was a read (`requested is null`), where nothing is known; a failed selection keeps the `Schemes` and `ActiveId` from the last read and sets `Status` to the failure message alone, dropping the "Refresh to read Windows state before trying again" suffix for that path. Nothing else changes; the read path, the refresh button and the visuals stay as they are.
- **Tests:** if `PowerSchemeSelection` can be built over a fake `IPowerSchemeApi` (it already takes a `PowerSchemes` instance), a selection whose write throws leaves `CanSelect` true and the next selection runs; a failed read still clears `ActiveId`. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~PowerScheme"`.
- **Plan v2:** B017 (add `src/WSGM/Overlay/PowerSchemeSelection.cs` to its files).
- **Related:** WINSVC-010, CRIT-003.

## Nit

### WINSVC-040: Dead native declarations

- **Severity:** nit
- **Where:** `src/WSGM/Interop/NativeMethods.cs`: `MbOk`, `MbIconError` (about 8-9), `HidUsageMouse` (41), `WmMouseActivate`, `MaNoActivate` (72-75), `MessageBoxW` (267), `GetCursorPos` (367), `GetCurrentProcess` (502), `CursorPoint` (757).
- **Problem:** declarations with no caller in WSGM (WSGM.Setup has its own `MessageBoxW`).
- **Best solution:** delete them after a caller sweep that excludes `.claude/`, `bin/` and `obj/`.
- **Tests:** build only (`dotnet build src\WSGM\WSGM.csproj -c Release`).
- **Plan v2:** B094.
- **Related:** WINSVC-041.

### WINSVC-041: Duplicate GUID declarations

- **Severity:** nit
- **Where:** `GUID_DEVINTERFACE_VOLUME` in `src/WSGM/Interop/NativeMethods.cs:254-264` and `src/WSGM/Interop/NativeStorage.cs:126-127`; `GUID_ACDC_POWER_SOURCE` in `src/WSGM/Interop/MessageWindow.cs:51`.
- **Problem:** the same constant declared twice, and a power GUID living apart from the others.
- **Best solution:** volume GUID only: keep `NativeMethods.GuidDevInterfaceVolume` (already internal, used by `MessageWindow.cs:408`) and have `NativeStorage` use it, deleting the private `NativeStorage.VolumeInterfaceGuid` (`NativeStorage.cs:126-127`). The power GUID is not this batch's: B071 (WDC-011, its file list already includes `MessageWindow.cs`) switches WSGM to the library GUIDs, and B094 runs after B071, so the "until then" interim move is dropped.
- **Tests:** build only.
- **Plan v2:** B094 (volume GUID); the power GUID is resolved by B071.
- **Related:** WDC-011, WINSVC-040.

### WINSVC-042: `SizeText` and `ResultText` setters never raise their own names

- **Severity:** nit
- **Where:** `src/WSGM/Shell/RemovableDriveEntries.cs:96-100` (`SizeText`), `:143-147` (`ResultText`).
- **Problem:** both setters call `SetFieldIfChanged(ref field, value, nameof(StatusLine))`, so a binding to either property never updates.
- **Best solution:** `if (SetFieldIfChanged(ref field, value, nameof(SizeText))) { Raise(nameof(StatusLine)); }`, and the same for `ResultText`, matching the `Letters` setter.
- **Tests:** setting each raises its own name and `StatusLine`. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~RemovableDrive"`.
- **Plan v2:** B094.
- **Related:** WINSVC-005.

### WINSVC-043: Public static types in the application assembly

- **Severity:** nit
- **Where:** `src/WSGM/Core/ImageHeader.cs`, `DisplayProfiles.cs`, `PowerTimeouts.cs`, `DisplayScale.cs`, `LockScreenSettings.cs`.
- **Problem:** `public static` where nothing outside the assembly (tests use `InternalsVisibleTo`) needs them.
- **Best solution:** make them `internal`; `PowerTimeouts` is already an instance by then (B090), and `DisplayProfiles` may already be renamed (B098). Confirm with a build of WSGM, WSGM.Setup and the test projects.
- **Tests:** build only.
- **Plan v2:** B101.
- **Related:** WINSVC-027, 030.

### WINSVC-044: Bluetooth device matching is quadratic and logs per row

- **Severity:** nit
- **Where:** `src/WSGM/Shell/RadioManager.cs:755-805` (`ApplyDeviceChange`).
- **Problem:** O(n^2) LINQ matching plus a `Log.Change` key string per device per watcher event. Not high-rate, so only a nit.
- **Best solution:** one pass that builds a dictionary of current rows by id, then applies the change set, with one summary `Log.Change` per event instead of one per row.
- **Tests:** existing radio tests stay green; a change set adds, updates and removes the right rows. Filter: as WINSVC-021.
- **Plan v2:** B065.
- **Related:** WINSVC-021.

### WINSVC-046: Stale SdFormatManager class comment

- **Severity:** nit
- **Where:** `src/WSGM/Shell/SdFormatManager.cs` class comment ("THREE separate diskpart runs", cites `Shell\AGENTS.md` for device evidence); `src/WSGM/Shell/CardAcfWatcher.cs:12-26` class comment ("every mounted card", carried over from WINSVC-016).
- **Problem:** the guide now says "three destructive stages, not a fixed count of diskpart processes"; format can run three times.
- **Best solution:** reword the comment to the stage model (or move it to `SdFormatRun` if B097 has landed). In the same batch, fix the `CardAcfWatcher` class comment ("watches every mounted card's `SteamLibrary\steamapps`") to say it watches every ready drive carrying a Steam library marker, which is what `Reconcile` does (`CardAcfWatcher.cs:144-169`); this was the only remaining part of WINSVC-016.
- **Tests:** none.
- **Plan v2:** B101.
- **Related:** WINSVC-007.

### WINSVC-V-005: Bluetooth pairing PIN written to wsgm.log

- **Severity:** nit
- **Where:** `src/WSGM/Shell/RadioManager.cs:1356-1357` (`pin '{request.Pin}'`).
- **Problem:** the passkey has no diagnostic value in the log testers send. This is log hygiene, not security hardening: the passkey is single-use and useless after the ceremony, so nothing here rests on the dropped security items.
- **Best solution:** log the ceremony kind and device name only; drop the PIN from the message.
- **Tests:** none beyond the B065 filter.
- **Plan v2:** B065.
- **Related:** WINSVC-021.

### WINSVC-V-006: LHM sensor XML is cut at 4 MiB

- **Severity:** nit
- **Where:** `src/WSGM/Core/RtssOsd.cs:601` (`Math.Min(_view.Capacity, 4 * 1024 * 1024)`).
- **Problem:** a publication with no NUL inside the first 4 MiB parses as null and every sensor silently disappears.
- **Best solution:** read to the view capacity. The only remaining bound is the `int` range of the read length, which is a type check, not a content cap.
- **Tests:** in `RtssOsdTests`, a fake view larger than 4 MiB with the NUL past 4 MiB parses. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Rtss"`.
- **Plan v2:** B100.
- **Related:** WINSVC-031, 032.

### WINSVC-V-007: Fixed 8 KiB drive-layout buffer

- **Severity:** nit
- **Where:** `src/WSGM/Interop/NativeStorage.cs:641-656` (`TryGetPartitionTypes`, `stackalloc byte[8192]`).
- **Problem:** `IOCTL_DISK_GET_DRIVE_LAYOUT_EX` fails with an insufficient-buffer error above about 56 partitions, and the Steam Deck partition hint is then lost (display only).
- **Best solution:** keep the 8 KiB first attempt; on `ERROR_INSUFFICIENT_BUFFER` (122) or `ERROR_MORE_DATA` (234) retry with a heap buffer of twice the size until the call succeeds or fails with another error. Not a hot path.
- **Tests:** `ReadDriveLayout` is span-based and already size-agnostic, so a decode of a synthetic 64-partition layout guards the decoder only; the growth path calls the IOCTL and is verified by review. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SdFormat|FullyQualifiedName~RemovableDrive"`.
- **Plan v2:** B094.
- **Related:** WINSVC-004.

## Refuted or no-change

- **WINSVC-011** (two original display mode owners): refuted. The split is a recorded design (`DisplayResolutionService` documents why one shared original would let the second restore undo the first), and the operating-point revision and retry loops prevent applying a stale accepted-rate list, which the code calls a black screen. A single full-mode owner would also revert a user-owned manual refresh rate and change `FrameLimitOnly` behaviour. Only WINSVC-012 remains (B098).
- **WINSVC-034** (LockScreenSettings no-snapshot restore): refuted. Writing the secure default (sign-in required on wake) when no snapshot exists is deliberate; a no-op would strand a user without sign-in on wake whenever the snapshot is lost. B071 keeps today's writes and only skips `CreateSubKey` when the policy key is absent.
- **WINSVC-016** (CardAcfWatcher suspension not idempotent): no change. The only two `Suspend()` callers hold it in `using` scopes (`RemovableDriveManager.cs:538`, `SdFormatManager.cs:449`), so a double `Dispose` cannot happen, and the maintainer rule forbids a new guard without a concrete defect. The watch scope stays as it is (decided: keep watching every ready drive and fix the comment, DECISIONS.md); the stale class comment is fixed with WINSVC-046 in B101, and WINSVC-004 keeps the watcher's own `DriveInfo` walk. B094 drops this item.
- **WINSVC-045** (WndProc exception guard): no change. Both window procedures only compare messages and post handlers, so a subscriber exception never crosses the native boundary; the real unguarded callback is `EffectivePowerModeNotification` (WINSVC-V-003, B091). The citation for `DisplayChangeWindow` was also wrong (its WndProc is lines 82-96 of a 97-line file).
