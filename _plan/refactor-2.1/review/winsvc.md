# WINSVC review: Windows services inside WSGM

Reviewer domain: radio, audio, storage/SD, display, RTSS, power policy and miscellaneous managers in `src/WSGM`
(Shell, Core, Interop), their tests, and where they duplicate or wrap `external/windows-device-control` (WDC).
Baseline: parent `master` 1329813f, WDC `main` 2f074857. Read-only review; nothing was built or run.

Files read in full: Shell/RadioManager.cs, RadioEntries.cs (head), AudioManager.cs, AudioProfileService.cs,
RemovableDriveManager.cs, RemovableDriveEntries.cs, SdFormatManager.cs, SdFormatEntries.cs, CardVolumeMonitor.cs,
CardAcfWatcher.cs, DisplayOffMuteService.cs, DisplayArrivalWaiter.cs, BluetoothDeviceCatalog.cs, ShellDisplaySignals.cs,
DisplayLayoutDiagnostics.cs, SystemStatus.cs, KeepAwakeService.cs, ModernStandbyGuard.cs, VolumeFeedback.cs,
VolumeButtonService.cs, VolumeOsdVisibility.cs, DisplayTimeouts.cs (to line 190); Core/RefreshRatePairingService.cs,
OtherManagers.cs, RtssOsd.cs, RtssDiscovery.cs, RtssNativeAdapter.cs, RtssModels.cs, RtssLauncher.cs,
RtssFrametimeReader.cs, ConsoleTool.cs, ImageHeader.cs, DisplayProfiles.cs, DisplayResolutionService.cs, PowerSchemes.cs,
CpuBoost.cs (apply path), HybridCores.cs, PowerTimeouts.cs, WindowsPowerModes.cs (apply), WakeLock.cs,
LockScreenSettings.cs, DisplayScale.cs (capture path); Interop/RtssProfileApi.cs, NativeStorage.cs, MessageWindow.cs,
DisplayChangeWindow.cs, NativePathIdentity.cs, NativeAuthenticode.cs, NativeMethods.cs (declaration inventory and
dead-symbol sweep), WindowsHybridCoreApi.cs and the three sibling power adapters; WDC WindowsStorage.cs,
WindowsWakeSecurity.cs and the public surface of CoreAudio/WindowsRadio/DisplayEdid/DisplayModes. Tests: RadioManagerTests,
AudioManagerTests, SdFormatTests, plus the test-name inventory of RemovableDriveTests, DisplayMuteTests,
RefreshRatePairingServiceTests, OtherManagersTests, SystemStatusTests, RtssDiscoveryTests, BluetoothActionTests,
SteamStorageBridgeTests, ConsoleToolTests; UiTests PreviewAudioPanel and the power-selection fakes.
Lightly swept (statics, fire-and-forget, readback gates; no line-by-line read): ForegroundWindowWatcher, WindowIcons,
HotkeyService, SoundPackService, VariableRefreshCapabilities, VolumeIndicator, AudioPlaybackChoices, UacSettings,
WakeLockStatus, ModernStandbyDiagnostics, WindowFinder, RegistryValueSnapshot, UpdateChecker, UpdateExitWatcher,
KeyboardService. UpdateMonitor/StartupAppWatcher are already covered by U05-LFB-012/024.

Prior coverage: this domain is audit units U07B-INS and U11C-PRF. Both are listed as "unstarted Claude part; required new
source closure" in `audit-coverage.md` lines 21 and 30, and their closure outputs (`audit/A01/U07B-INS.md`,
`audit/A01/U11C-PRF.md`) do not exist. The disposition ledger has zero rows for RtssOsd, RtssDiscovery, RadioManager,
AudioManager, SdFormatManager, CardVolumeMonitor, DisplayOffMuteService, RefreshRatePairingService, OtherManagers or
NativeStorage. Unless a row cites a ledger id, every finding below is NEW.

## 1. Plan claims check

| # | Claim (source) | Verdict | Evidence and correction |
| --- | --- | --- | --- |
| C1 | "`RadioManager`, `AudioManager`, display arrival, Bluetooth identity and CCD interop overlap reusable library mechanisms." (refactor-plan.md L47) | partially | RadioManager compensates for WDC's process-wide feeds with a static queue and generation counters (RadioManager.cs L41, L485-518, L675, L707). BluetoothDeviceCatalog normalizes WDC watcher output (BluetoothDeviceCatalog.cs). DisplayArrivalWaiter duplicates WDC `DisplayTopology.WaitForPresentAsync` (U01-029). The main app has **no** CCD interop copy: the only QueryDisplayConfig copies outside WDC are `WSGM.Plugin.IntelGpu/Display/DisplayIdentity.cs` and `WSGM.DeviceLab/Wizard/LabSystemDump.Display.cs`. The claim also misses three real main-app overlaps: storage enumeration (`NativeStorage.MountedVolumes`/`TryGetDeviceNumber` against WDC `WindowsStorage.DescribeVolumes`/`DiskNumberFor`), EDID parsing (`Core/EdidModes.cs` against WDC `DisplayEdid.Parse`), and the per-feature power adapters over `WindowsPower`. Correction: list these, and assign the CCD duplicate to the GPU/Lab domains. |
| C2 | WDC 0.2.0 consumers: "Main `Shell/RadioManager`, `AudioManager`, native QAM audio/network/Bluetooth/brightness/display/power services, `Core/Display*`, `Power*`, `HybridCores`, `CpuBoost`, wake/lock recovery ..." (api-integration.md L7) | partially (incomplete manifest) | Missing WDC consumers in this domain: DisplayOffMuteService (CoreAudio.GetVolume/SetMuted L538, L559); VolumeButtonService (CoreAudio.ApplyCommand); VolumeFeedback (WaveOutFeedback); AudioProfileService (11 CoreAudio calls); Settings/AudioProfileEditor.cs L41-59 (direct CoreAudio from a view model); Overlay/RadioPanel.axaml.cs L283-302 (PairingKind); RadioEntries.cs (WifiSecurity); SteamStorageBridge.cs L318, L620 (WindowsStorage); SystemStatus.cs and RtssOsd.cs L1043 (WindowsPower.TryGetStatus); WakeLock.cs (WindowsPowerRequest); ModernStandbyGuard.cs (ModernStandby, WindowsPower.SuspendAsync); MessageWindow.cs L276-287, L356, L369 (WindowsPower notification registration); ShellDisplaySignals.cs, DisplayLayoutDiagnostics.cs and DisplayArrivalWaiter.cs (DisplayLayouts, DisplayTargetIdentity); LockScreenSettings.cs (WindowsWakeSecurity); PowerTimeouts.cs (WindowsPower.Read/WriteSetting); RefreshRatePairingService.cs L45-51 (DisplayModes); NativeQamNetworkService.cs L120 (WindowsRadio.GetWifiStatus); tests RadioManagerTests (WifiProfile/WindowsRadio) and AudioManagerTests (CoreAudio). I01 has to adapt all of these. |
| C3 | "Move `DisplayArrivalWaiter` mechanism and Bluetooth catalog normalization into WDC, retiring duplicate WSGM implementations." (L127) | accurate, refine | Both are confirmed duplicates. Refinement: the moved waiter replaces WDC's own `WaitForPresentAsync`/`WaitForAvailableAsync`; do not keep both. The waiter's change-hint stays an injected `IDisplayChangeSignal`, because only WSGM owns the hidden top-level window (DisplayChangeWindow.cs). |
| C4 | "Create per-instance `RadioPowerService`, `WifiService`, `BluetoothService`, `AudioService` and `DisplayService` ... multiple consumers subscribe to the same service instead of replacing a process feed." (L121) | accurate (as a target) | Current WSGM evidence: the static `_feedWork` exists only because the Settings preview builds a second RadioManager beside the session's (RadioManager.cs L29-41; SystemStatus.cs L47-55). Once subscriptions exist, WSGM deletes `_feedWork`, `QueueFeedWork` and `_bluetoothWatchGeneration`. This is a removal and must be in the W01 consumer task. |
| C5 | "Pairing is an attempt object with caller token, one overall 90 s active deadline ..." (L125) | over-engineered | WSGM already cancels a pairing explicitly (RadioManager.CancelPairing L1289-1304, Dispose L290-298). A new 90 s constant is an invented limit with no defect that needs it beyond U01-019's worst case. Correction: caller token plus WinRT's own ceremony completion; no new deadline. |
| C6 | "Wi-Fi identity is an immutable raw-byte `WifiNetworkKey` ..." (L123) | accurate, consumer list missing | WSGM keys rows by display SSID with Ordinal comparison: `WifiNetworkEntry(string ssid)` (RadioEntries.cs), `FindNetwork` (RadioManager.cs L945-948), `ConnectAsync(ssid)`/`ForgetAsync(ssid)` (L1024, L1118), RadioPanel, NativeQamNetworkService L105-132 (merges by `Ssid`). All of them must move to the key. Steam's network surface only reads state and starts or stops scans, so the key need not cross the bridge. |
| C7 | "Native windows use HWND user data/owned callback registrations rather than replaceable `_instance`; one MessageWindow owner hands out disposable subscriptions." (L115) | partially (over-engineered) | The defects are real: U05-LFB-013 (no try/catch in WndProc, MessageWindow.cs L530-627, DisplayChangeWindow.cs L711-724) and U05-LFB-014 (any holder's Dispose destroys the window). HWND user data plus GCHandle lifetime fixes neither. There is one window per process, so a static `_instance` set only by the single owner is correct. Correction: the session constructs the windows and passes them in. Delete the public get-or-create `Create()`. Wrap the WndProc body in try/catch. Only the three reference-counted registrations (volume, display state, shell hook) return an `IDisposable` claim. Plain events stay events. |
| C8 | Shutdown order "... dispose feature consumers before audio/radio/storage native providers; destroy MessageWindow after its last subscription." (L91) | stale vs current code (target valid) | Today the MessageWindow is destroyed inside `DisposeUiOwnedSessionResources` (ShellSession.Shutdown.cs L264 -> L752). That runs **before** `_audio` (L475), `_radios` (L488) and `_drives` (L600) are disposed, so `RemovableDriveManager.Dispose` deregisters volume notifications on a destroyed window (RemovableDriveManager.cs L136-141). This confirms U05-LFB-014. The target text is right; the migration must move the provider disposals ahead of the window. |
| C9 | U04B-LFA-004: `RestoreAll` (other managers) reports success when config.json is unreadable | accurate | OtherManagers.cs L457 uses the lenient `ConfigStore.Load()`. Zero records return 0 at L460-461. |
| C10 | U04A-LFA-014: `ConsoleTool.Run` abandons a timed-out process and reports a definite failure | accurate, and wider | ConsoleTool.cs L62-98. The sc.exe service writes in OtherManagers.cs L127-141 and AutostartSystem.cs L136 use it. `RunCapturedAsync` (L237-309) also folds not-started, timeout-killed and exit codes into `-1`, which diskpart relies on (see WINSVC-015). |
| C11 | U05-LFB-024 lists ShellDisplaySignals and MessageWindow dispatch as untested | accurate | No test references ShellDisplayChangeSignal or the MessageWindow WndProc. |
| C12 | "A01/A02 close the 42 unstarted parts ... before their writers begin" (L165) and audit-coverage closure outputs for U07B-INS/U11C-PRF | inaccurate (stale) | `audit/A01/` holds only final.md, findings.json and inventory JSON. `findings.json` has 6 entries and none touches audio, RTSS, storage, SD format or OtherManagers. Correction: this review is the closure input for U07B-INS and U11C-PRF. |
| C13 | Plan source diagnosis lists no RTSS, storage/SD, power-policy or display-off-mute owner work (sections "Remaining first-party domains" and the target architecture table) | inaccurate by omission | These areas hold 5 of this domain's medium findings: WINSVC-001, 002, 005, 006 and 011. The plan must name owners for them (section 4). |
| C14 | "No locks span native/network I/O in high-rate paths" / "High-rate samples ... no per-sample allocation" (L115) | accurate as rule; current code partially violates | The AutoTDP frametime path runs at 1 Hz (AutoTdpService.cs L67 `Window = 1 s`). It allocates a List, a byte[260], an AccessorRegion, records and strings per sample (RtssFrametimeReader.cs L74-178). That is not truly high-rate, so it is a nit (WINSVC-031). The RTSS OSD renderer at 10 Hz rebuilds only on change, which is acceptable. |
| C15 | W02_01 / W02_02 batches (WDC EDID bit, power-action dispatch seam) | out of domain, consistent | Both touch only WDC files. W02_02's internal `IWindowsPowerActionApi` is the seam ModernStandbyGuard should reuse (WINSVC-025) once WDC exposes it as instance-owned. Today it is `internal`, so WSGM still needs its own suspend port. |

## 2. Findings

Severity scale: critical / high / medium / low / nit. "Ledger" cites an existing id; otherwise NEW.

### Medium

**WINSVC-001 (medium, concurrency) RTSS sensor source is shared by two threads with no synchronization.**
RtssOsd.cs L874-1080 (`RtssOsdMetricsSource`), L1463-1466 (`SampleSensors`), L1543 (render loop); AutoTdpService.cs
L74/L112 (`Func<RtssOsdMetrics>`), wired at ShellSession.cs L516. Two threads call `Sample()` at once: the render loop
(Task.Run, 10 Hz) and AutoTDP's control loop. Both mutate `_cached`, `_cachedAtTicks`, `_lastIdle`, `_lastBusyBase` and
`_hasCpuSample`, and both drive `LhmSensorReader` (`_gate ??= Mutex.OpenExisting` can create two Mutex objects; `Close()`
can dispose the view and mutex while the other thread reads). Failure: interleaved `GetSystemTimes` deltas produce bogus
CPU load. AutoTDP uses that load to classify stalls and count answered steps. Handles also leak when two opens race.
NEW. Recommendation: one `Lock` around the sampling body, or make the 1 s cached sample the only shared state, published
through `Volatile` with a single-writer refresh. No new thread.

**WINSVC-002 (medium, bug) An eject that throws leaves the library unregistered and held out of Steam.**
RemovableDriveManager.cs L543-581, LibraryPolicy.cs L73-118. `EjectingAsync` unregisters the library and records the
held-ejected intent. If the `Task.Run` eject (or anything after `EjectingAsync`) throws, the `catch` at L576 never calls
`EjectedAsync(entry, false)`. The intent stays recorded, `CardVolumeMonitor` skips the still-mounted card
(CardVolumeMonitor.cs L360) and Steam loses the library until the media physically leaves. NEW. Recommendation: call
`EjectedAsync(entry, false)` in the failure path (try/finally around the observer pair). No new state.

**WINSVC-003 (medium, never-strand) Display-off mute restores whichever endpoint is the default now, not the one it muted.**
DisplayOffMuteService.cs L389-465, L533-573; WDC `CoreAudio.SetMuted(bool)` addresses the default endpoint only. Scenario:
the screen is dark, a download is running, and the speakers are muted. A Bluetooth headset then connects and becomes the
default. On wake, `Restore` reads the headset as not muted (L445-452) and drops the claim. The speakers stay muted and
nothing in WSGM unmutes them. Restore is also gated on a successful read (L436-443). NEW. Recommendation: record the
muted endpoint id at mute time and restore that endpoint by id (needs per-endpoint mute in the W01 AudioService). The
existing 2 s recovery tick already retries; add no new mechanism.

**WINSVC-004 (medium, duplication/architecture) Storage is enumerated five ways with three identities.**
RemovableDriveManager.ReadSnapshot L294-399 (row id = device instance path or `media:X`); SdFormatManager.ReadTargets
L202-247 (instance id or `disk:N`); CardVolumeMonitor.ScanCardLibraryPaths L530-562; CardAcfWatcher.Reconcile L144-195
(`DriveInfo`, every ready drive); SteamStorageBridge L318 and L620 (WDC `WindowsStorage.DescribeVolumes`, joined by disk
number "because nothing else can say those describe the same card", L314-317). `ResolveSystemDisks` runs on every scan
except the eject list's cached copy (L296). WDC `WindowsStorage` duplicates `NativeStorage.MountedVolumes`/
`TryGetDeviceNumber`. NEW (U01-040/U01-048 cover only the WDC side). Recommendation: one `StorageInventory` snapshot (disk
number, instance id, bus, size, hotplug class, volumes with letter and volume GUID path) built by one reader. The eject
list, format targets, card monitor and Steam storage bridge all project from it.

**WINSVC-005 (medium, bug) Card and Steam paths are parsed back out of the display string `RemovableDriveEntry.Letters`.**
LibraryPolicy.cs L124-130, SteamStorageBridge.cs L243, L392, L628-637; RemovableDriveManager.cs L392 sets an unlettered
media row's `Letters` to "No Windows drive letter". For that row `SplitLetters` yields `No Windows drive letter\`.
`TrimAllAsync` then takes `path[0]` = 'N' and runs an elevated `Optimize-Volume -DriveLetter N` on whatever volume N is
(SteamStorageBridge.cs L239-245). `LibraryPathsOn` reads a relative `No Windows drive letter\SteamLibrary` against the
current directory. NEW. Recommendation: rows carry `IReadOnlyList<char> VolumeLetters` as data, and the display string is
derived from it. Delete `SplitLetters`.

**WINSVC-006 (medium, bug) The format run reads mutable row fields as its safety baseline.**
SdFormatManager.cs L171-199 (`Refresh` checks `Busy` only before starting), L291-334 (`Apply` does not check `Busy`),
L407-663 (FormatAsync reads `entry.SizeBytes`, `entry.BusType`, `entry.PreferredLetter` across many awaits). A refresh
started just before the format posts `Apply` mid-run. That rewrites the identity baseline used by `ReadTargetIdentity`
(L752-775) and the letter to keep (L465, L581), defeating the AGENTS "strict match" rule. NEW. Recommendation: snapshot an
immutable `FormatTarget` record at the start of `FormatAsync` and use only that.

**WINSVC-007 (medium, architecture/testability) `SdFormatManager` is a 1,636-line static-heavy aggregate with untested
destructive orchestration.** It holds enumeration (L202-339), the three-stage diskpart run (L407-663), Steam library
removal, restore and registration over CEF and VDF (L814-1091, L1258-1370), add-library (L1383-1489), retrim
(L1149-1192), config mutation through static `LibraryTabManager.MutateConfigAsync` (L509) and script spooling under
`Log.Directory` (L1102). Every native, process, Steam and config dependency is static. Shell/AGENTS.md requires
"Test orchestration with fakes and temporary paths", yet SdFormatTests covers only script strings, `CompareIdentity` and
`SteamLibraryVdf`. NEW. Recommendation: see target design section 4 (split into targets, run and library registration
with four ports).

**WINSVC-008 (medium, lifecycle) RTSS OSD renderer lifetime.**
RtssOsd.cs L1415-1419 starts the render loop inside the constructor, through RtssNativeAdapter's constructor at
RtssNativeAdapter.cs L37. `Dispose` (L1425-1454) waits 2 s, then disposes `_writer` and `_shutdown` even if the loop is
still inside `Sample()`, which can block 200 ms on the LHM mutex or start a process. A loop iteration after
`_writer.Dispose()` calls `TryUpdate`. `_disposed` was set first, so it returns false. The risk is the reverse order: the
loop holds `_writer` mid-`TryWrite` while Dispose zeroes the slot, and the late write re-claims the slot, leaving "WSGM"
OSD text in RTSS after exit. `_lastProbe` is read from other threads without `Volatile` (`ProfileExists` L64-76,
renderer callback L37). NEW. Recommendation: start the loop in an explicit Start; Dispose cancels, awaits the loop, then
releases the slot once; publish `_lastProbe` through `Volatile.Write`.

**WINSVC-009 (medium, global state) Process globals in this domain.**
`RadioManager._feedWork` static (L41); `VolumeFeedback` static player, never disposed (VolumeFeedback.cs whole file);
`PowerSchemes.MutationGate` static `object` locked from 9 call sites across Core, Shell, Overlay and NativeQam
(PowerSchemes.cs L21; OverlayController.cs L795; NativeQamPowerProfileService.cs L62; Overlay/PowerSchemeSelection.cs
L79; DisplayTimeouts.cs L99/157/180/224; PowerTimeouts.cs L107; CpuBoost.cs L155; HybridCores.cs L217;
WindowsPowerModes.cs L41); static `Windows` singletons on PowerSchemes, CpuBoost, HybridCores and WindowsPowerModes;
`MessageWindow._instance`/`DisplayChangeWindow._instance` handed out by `Create()` to any caller; `HotkeyService._nextId`
static. Ledger: U05-LFB-005/014 (partially). Recommendation: the session composes one instance of each, and the power
lane is a single injected object (section 4).

**WINSVC-010 (medium, rule: no hard readback) Windows power writes gated on readback.**
PowerSchemes.Select L53-73 throws when the active GUID reads back different. HybridCores.Apply L215-241 throws on a
readback mismatch. WindowsPowerModes.Apply L39-50 throws when `api.Read() != id`. DisplayTimeouts.Select L177-190 refuses
the write when the current value cannot be read. DisplayOffMuteService.Restore L436-443 is covered in WINSVC-003.
CpuBoost.Apply L152-179 already follows HC (write, refresh, read for a warning only). NEW. Recommendation: make every one
behave like CpuBoost. Write, refresh the active scheme, read back only for the log line, and publish the written value as
observed. `DisplayTimeouts.Cycle` keeps its read, because the next preset is computed from the current value.

**WINSVC-011 (medium, ownership/over-engineering) Two independent "original display mode" owners.**
RefreshRatePairingService.cs (423 lines: per-target originals, operating-point revisions, two-attempt retry loops at
L127-157 and L388-421) and DisplayResolutionService.cs (one original with no target identity, L95-102). Shutdown restores
them back to back as two separate writes (ShellSession.Shutdown.cs L413-450). The pairing service is also driven from two
threads (ledger U05-LFB-019, pairing instance). WSGM-side analogue of U01-035 (display mutations have no shared
serialization). NEW for the WSGM owner. Recommendation: one `TransientDisplayMode` owner. It captures the full current
mode (target, width, height, Hz) on the first transient write, serializes all transient writes, and restores with one
mode write. FrameLimitPairing stays pure policy. Delete the revision counters and retry loops.

### Low

**WINSVC-012 (low, test-shaped production code)** RefreshRatePairingService.cs L133, L323-343, L361-363, L399: the
`_readOperatingPoint is null` branches and the `"test"` originals key exist only for the test constructor. This
contradicts tests/WSGM.Tests/AGENTS.md ("Do not add production branches solely to make a test convenient"). NEW.
Recommendation: tests provide an operating-point fake; delete the branches (folded into WINSVC-011).

**WINSVC-013 (low->medium per ledger, recovery)** OtherManagers.RestoreAll on unreadable config. Ledger U04B-LFA-004,
confirmed at L457. Recommendation: use the strict read and return 1 on unreadable config without writing.

**WINSVC-014 (low, ownership)** OtherManagers.cs is a static class. It defaults its dependencies with
`autostart ??= new AutostartSystem()` (L194-196, L262-264), reads the static `ConfigStore` (L372, L457, L465, L481), and
calls `ConfigStore.Mutate` once per record (L478-488). `Apply` ignores `SelfElevation.RunElevatedAction`'s result (L340).
`ReapplyAtStart` runs fire-and-forget from `ShellSession.cs L398` (`_ = Task.Run(...)`), untracked and not joined at
shutdown, so it can run sc.exe while the session is stopping. NEW. Recommendation: an instance `OtherManagerTakeover`
with injected autostart, service, process and config ports, and a tracked start-up task owned by the session.

**WINSVC-015 (low, uncertain side effects)** ConsoleTool.cs has three run paths with different timeout semantics: `Run`
(L62-98, ledger U04A-LFA-014), `RunUntilAsync` (tri-state, L113-227) and `RunCapturedAsync` (L237-309, `-1` for
everything). When diskpart is killed on its 600 s timeout, SdFormatManager reads that as "partition failed"
(SdFormatManager.cs L485-494) and tells the user nothing was erased, although `clean` may have run. Compensation is safe
only because `RestoreRemovedLibraryIfCardSurvived` re-reads the marker. AutostartSystem.cs L153-154 blocks on
`RunCapturedAsync(...).GetAwaiter().GetResult()`. Recommendation: one `RunAsync(exe, args, deadline, capture, ct)`
returning `{NotStarted, Succeeded, Failed(exit), Unknown}` plus output. Diskpart Unknown maps to "the card may have been
erased; reinsert and check".

**WINSVC-016 (low, behavior)** CardAcfWatcher.cs L144-169 watches every ready drive with a `SteamLibrary` marker. That
includes fixed and network drives (`DriveInfo.GetDrives` plus `IsReady`, which can block on a dead share). The class
comment says "every mounted card". `Suspension.Dispose` (L289-295) is not idempotent: a double dispose drives
`_suspensions` negative and the watcher never resumes. NEW. Recommendation: make the suspension idempotent and project
from StorageInventory. Whether to restrict watching to removable media is open question Q1.

**WINSVC-017 (low, ownership)** CardAcfWatcher.cs L90 and RemovableDriveManager.cs L156 call `MessageWindow.Create()`
themselves instead of receiving the session's window. Ledger U05-LFB-014. Recommendation: constructor injection.

**WINSVC-018 (low, threading)** AudioProfileService invokes `audio.Refresh` in `finally` blocks after
`ConfigureAwait(false)` (AudioProfileService.cs ApplyAsync, SetPlaybackFormatAsync, SetSpatialFormatAsync). AudioManager's
`Refresh` then writes the UI-owned `_stickyError` and `_refreshPending`/`_refreshPendingEndpoints` from a pool thread
(AudioManager.cs L389-412). A concurrent `RunPendingRefresh` on the UI thread can lose the pending flag. NEW.
Recommendation: `Refresh` posts to the UI thread; it is the one entry point that lacks this.

**WINSVC-019 (low, UI thread / ownership)** VolumeButtonService.OnShellHook runs `CoreAudio.ApplyCommand` (COM) and
`VolumeFeedback.Play` synchronously on the Avalonia UI thread. That contradicts AudioManager's own "nothing slow on the
UI thread" rule (AudioManager.cs L49-52). `VolumeFeedback._player` is never disposed at shutdown, so the WaveOut handle
lives until process exit (WDC side: U01-026). NEW. Recommendation: the audio owner runs button commands on its existing
write worker (`CoalescingVolumeWrite` pattern), and the feedback player becomes an instance owned and disposed by the
audio owner.

**WINSVC-020 (low, duplication/seams)** Six audio consumers reach CoreAudio through different paths. AudioManager,
DisplayOffMuteService and VolumeButtonService call statics. RadioManager has an injected delegate (L95, L99-106).
Settings/AudioProfileEditor.cs L41-59 queries CoreAudio directly from a view model. AudioProfileService has its own
`IAudioProfileOperations` port. NEW (the view-level call also violates the plan's "no ... native acquisition in views").
Recommendation: one internal `IAudioEndpoints` port (generalized from `IAudioProfileOperations`) over the W01 AudioService
instance, consumed by all six.

**WINSVC-021 (low, threading)** RadioManager.OnPairingRequested writes `_pairingToken` on the WinRT callback thread
(L1349) while the UI thread reads it (L292, L1298). `Rescan` sets `BluetoothScanning = true` even when the feeds are not
started (L450-460 with the early return at L480-483), so the flag can stay true. NEW. Recommendation: assign the token
inside the UI post; set `BluetoothScanning` only when the restart is queued.

**WINSVC-022 (low, WDC overlap)** RadioManager's `_feedWork`, `QueueFeedWork` and `_bluetoothWatchGeneration` (L41,
L478-518, L675-713) and BluetoothDeviceCatalog exist to compensate for WDC's process-wide feeds and raw endpoint events.
Ledger U01-001/U01-007/U01-040. Recommendation: delete them when the W01 subscriptions land (C4).

**WINSVC-023 (low, duplication)** NativeQamNetworkService.cs L118-121 polls `WindowsRadio.GetWifiStatus()` itself, while
RadioManager already publishes `ConnectedSsid`/`WifiSignal`/`WifiConnected` every 2 s (RadioManager.cs L578-595). NEW.
Recommendation: project from RadioManager.

**WINSVC-024 (low, lifecycle)** KeepAwakeService.StartNew starts an untracked `Task.Run(RunAsync)`. Dispose cancels and
disposes the CTS and wake locks without joining the loop (KeepAwakeService Dispose; StartNew). A poll in flight can try
to acquire a disposed request; WakeLock swallows the ObjectDisposedException. The download query goes through the
ambient static `SteamDownloadActivity`. NEW. Recommendation: Dispose awaits the loop; the CEF query becomes an injected
port (steamhost dependency).

**WINSVC-025 (low, testability)** ModernStandbyGuard calls the statics `ModernStandby.ReadStandbyTiming`,
`WasLastResumeUnattended`, `LastInput.Age` and `WindowsPower.SuspendAsync`, and disposes `_lifetime` while
`SuspendAsync` may still be running. It also borrows `DisplayMuteDecider.IsDisplayOff/MayReportDark` from the mute
feature. NEW. Recommendation: inject a standby-state and suspend port, and move the display-state interpretation next to
the MessageWindow display signal.

**WINSVC-026 (low, leak)** DisplayOffMuteService.cs L172 adds an `AppDomain.ProcessExit` handler per instance and never
removes it. The handler roots the instance and runs `Restore` off the UI thread against UI-owned fields. NEW.
Recommendation: the session's ordered shutdown already calls Dispose, which restores (L176-196). Keep ProcessExit only as
an unsubscribed-on-dispose fallback, or let the session own the exit path.

**WINSVC-027 (low, duplication)** EDID is parsed twice. `Core/EdidModes.ReadAdvertisedRefreshRates` reads the registry
EDID, located by string surgery on the device path (DisplayProfiles.cs `ReadPrimaryMonitorInstanceId`). WDC
`DisplayEdid.Parse/ReadModes` reads the WinRT descriptor (WDC DisplayEdid.cs L31-44; used by SettingsViewModel.cs L263).
The class name `DisplayProfiles` is stale: WDC commit 2f07485 removed the display profile API, and WSGM's class only holds
mode helpers. NEW. Recommendation: WDC exposes advertised refresh rates for a target; delete `EdidModes`; rename the
remaining helpers into the TransientDisplayMode owner.

**WINSVC-028 (low, WDC overlap)** DisplayArrivalWaiter duplicates the WDC waits. `ShellDisplayChangeSignal` leaves one
pending `Task.Delay` per hint (ShellDisplaySignals.cs L36-38). Ledger U01-029/U01-040, U05-LFB-030. Recommendation: per
C3.

**WINSVC-029 (low, WDC overlap)** BluetoothDeviceCatalog belongs in the WDC Bluetooth service. Ledger U01-040.

**WINSVC-030 (low, structure)** Windows power policy is spread over PowerSchemes, CpuBoost, HybridCores, WindowsPowerModes
(each with its own small adapter interface: IPowerSchemeApi, ICpuBoostApi, IHybridCoreApi, IPowerModeApi), the static
PowerTimeouts (direct WDC calls, L28-133), DisplayTimeouts and a global lock. The small ports are fine. What is missing
is one owner of the mutation lane. NEW. Recommendation: section 4. Keep the four ports, which UI tests already fake
(UiTests VisualTests L65, HybridCoreViewTests L24-102, DevicePageCaptureTests L76-91). Replace the static gate and
singletons with one injected `PowerPolicyLane`, and make PowerTimeouts an instance over a fifth port or over the scheme
port.

**WINSVC-031 (low/nit, duplication and allocation)** RtssFrametimeReader and RtssOsdSlots each open
`RTSSSharedMemoryV2`, each define the same signature constant and each parse the header (RtssFrametimeReader.cs L41-60;
RtssOsd.cs L74-92). `ReadLive` allocates per 1 Hz sample (L74-178). `MaximumEntries = 1024` (L58, L131) is redundant with
the capacity check at L141-144. NEW. Recommendation: one `RtssSharedMemory` owner with reusable buffers. Remove the
entry cap.

**WINSVC-032 (low, rule: no arbitrary limits)** RtssDiscovery.cs L383 treats an executable or DLL over 32 MiB as absent.
PeExportReader returns no exports above 4,096 names (L469, L544), which makes the install "incompatible". L470 and
L563-583 silently truncate export names at 128 bytes. `ReadProcesses` uses `process.MainModule` (L441), which fails
across bitness and elevation and makes a running RTSS look "not running". NEW. Recommendation: drop the caps (bounds
checks against the stream length already exist). Read the image path with `QueryFullProcessImageNameW` (already declared,
NativeMethods.cs L610), as the plan asks for Explorer.

**WINSVC-033 (low, latency)** `NativeAuthenticode.VerifyFile` uses `WTD_REVOKE_WHOLECHAIN` with online revocation inside
the RTSS probe (NativeAuthenticode.cs L41-62; RtssDiscovery.cs L405). Offline, the first probe can stall for the CRL
timeout. The cache by length and mtime (L388-417) limits it to once per file. NEW (informational). Recommendation: keep
the policy (the comment documents why), but run the probe off any command admission path. PerformanceService already
does this.

**WINSVC-034 (low, never-strand edge)** LockScreenSettings.ApplyDirect(false) without a captured snapshot builds
`RecoverySnapshot(PolicyExisted: true, -1, -1, ...)` (LockScreenSettings.cs RecoverySnapshot). WDC `Restore` then calls
`CreateSubKey` on the policy key, leaving an empty policy key that never existed, and writes 1/1 into every scheme
(WindowsWakeSecurity.cs Restore). NEW. Recommendation: with no snapshot, restore is a no-op that reports "nothing to
restore".

**WINSVC-035 (low, path rule)** SdFormatManager.CreateSteamLibrary/RegisterLibrary write to `{letter}:\SteamLibrary`
(L1260-1288) after WaitForLetter and a retrim, without resolving the letter to the volume GUID path. Shell/AGENTS.md says
"resolve it to a volume GUID path once ... never validate on a letter and then write to it". NEW. Recommendation: resolve
`TryGetVolumeGuidPath` right after WaitForLetter, verify it maps to the target disk, and write through it. Only Steam
registration keeps the letter path.

**WINSVC-036 (low, sync-over-async)** SdFormatManager.cs L904-905 blocks on
`RemoveLibraryByContentIdAsync(...).GetAwaiter().GetResult()` inside Task.Run. NEW. Recommendation: make the removal
path async once the registration port exists (WINSVC-007).

**WINSVC-037 (low, config ownership)** DisplayScale.ApplyGameMode writes `config.SavedDisplayScaleEntries = captured` into
the session's live AppConfig from the mode transition (DisplayScale.cs, capture block; called by SessionModes.cs L232).
Pattern of ledger U05-LFB-019. Recommendation: persist only through the store; config domain.

**WINSVC-038 (low, duplicate managers)** SystemStatus builds its own Radio, Audio and RemovableDrive managers when none
are supplied (SystemStatus.cs L47-55). These are the Settings and overlay-test previews, and they hit the live machine.
OverlayController creates a second SdFormatManager with its own format gate when the session supplies none
(OverlayController.cs L246-258). NEW. Recommendation: preview composition passes fakes or the session instances
(UI domain); there is never a second format gate.

**WINSVC-039 (low, test quality)** The tests mostly assert copied predicates or strings rather than the owners:
- SdFormatTests L190-200 pins the constant array `ReverifiedStages`. The test's own comment admits it cannot see
  placement.
- DisplayMuteTests L9 pins `DownloadCompletionRestoreDelay_IsTenSeconds`.
- RadioManagerTests covers only message formatting. L91-185 test WDC `WindowsRadio`/`WifiProfile` (ledger U01-041).
- AudioManagerTests L28-37 tests WDC `CoreAudio` (U01-041). The rest test `NormalizeVolume`, `Reconcile` and the
  tracker struct.
- SdFormatTests L350-648 are `SteamLibraryVdf` tests that belong in a `SteamLibraryVdfTests` class (tests AGENTS: "tests
  for one type share one class").
- No test drives `RadioManager` scanning, feeds or Wi-Fi commands (only BluetoothActionTests covers pairing and
  audio-connect), `AudioManager` refresh, selection or watch replacement, `DisplayOffMuteService` (only its pure
  decider), `SdFormatManager.FormatAsync`, `CardVolumeMonitor`, `CardAcfWatcher`, `RemovableDriveManager.EjectAsync`,
  `OtherManagers.Apply`/`RestoreAll`, `KeepAwakeService`'s loop or `ModernStandbyGuard`.

NEW except where cited. Recommendation: owner tests through the ports introduced below. Move the WDC tests to the WDC
suite. Delete the constant pins.

### Nit

**WINSVC-040 (nit, dead code)** NativeMethods.cs has no caller for `MessageBoxW` (L267), `MbOk`/`MbIconError` (L8-9),
`HidUsageMouse` (L41), `WmMouseActivate`/`MaNoActivate` (L72-75), `GetCurrentProcess` (L502), `GetCursorPos` (L367) or
`CursorPoint` (L757). WSGM.Setup has its own MessageBoxW. NEW. Recommendation: delete them.

**WINSVC-041 (nit, duplication)** GUID_DEVINTERFACE_VOLUME is declared twice (NativeMethods.cs L254-264 and
NativeStorage.cs L126-127). GUID_ACDC_POWER_SOURCE lives in MessageWindow.cs L51 instead of beside the other power GUIDs.
NEW. Recommendation: keep one declaration of each.

**WINSVC-042 (nit, notification)** RemovableDriveEntry `SizeText` and `ResultText` setters raise only `StatusLine`, never
their own property name (RemovableDriveEntries.cs L96-100, L143-147). A binding to either property never updates. NEW.

**WINSVC-043 (nit, visibility)** `ImageHeader`, `DisplayProfiles`, `PowerTimeouts`, `DisplayScale` and
`LockScreenSettings` are `public static` in an application assembly; `internal` suffices. NEW.

**WINSVC-044 (nit, cost)** RadioManager.ApplyDeviceChange (L755-805) does O(n^2) LINQ matching and builds a `Log.Change`
string per device per watcher event. Bluetooth events are not high-rate, so this is a nit. NEW.

**WINSVC-045 (nit, native boundary)** MessageWindow and DisplayChangeWindow WndProcs have no exception guard. Ledger
U05-LFB-013, confirmed (MessageWindow.cs L530-627, DisplayChangeWindow.cs L711-724).

**WINSVC-046 (nit, docs)** The SdFormatManager class comment cites `Shell\AGENTS.md` for device evidence and still says
"THREE separate diskpart runs". The AGENTS text now says "three destructive stages, not a fixed count of diskpart
processes" (format can run three times). NEW. Recommendation: align the comment.

Totals: 46 findings (11 medium, 28 low, 7 nit); 39 NEW, 7 confirming ledger rows (U04A-LFA-014, U04B-LFA-004,
U05-LFB-005/013/014/019/024/030, U01-001/007/026/029/040/041 as cited).

## 3. Plan refinements

Additions (the plan names none of these owners today):

1. **RTSS owner** (U11C-PRF scope). A single `RtssSharedMemory` read and OSD owner. The metrics source is synchronized
   (WINSVC-001). The renderer starts in an explicit Start and disposes in order (WINSVC-008). Remove the caps and
   MainModule (WINSVC-032). Allocation-free frametime read with reusable buffers (WINSVC-031). The toolkit's "explicit
   Valve-FPS suppression" publication is fed from this owner's level state through PerformanceOverlayBridge.
2. **Storage inventory and eject/format** (U07B-INS scope). One `StorageInventory` (WINSVC-004). Rows carry letters as
   data (WINSVC-005). The eject failure path reports to the observer (WINSVC-002). The format run snapshots an immutable
   target (WINSVC-006) and writes through the volume GUID path (WINSVC-035). The format manager is split with ports
   (WINSVC-007).
3. **Audio endpoint port.** One `IAudioEndpoints` for six consumers (WINSVC-020). Display-off mute restores by endpoint id
   (WINSVC-003, needs W01 per-endpoint mute). Volume buttons run off the UI thread (WINSVC-019). VolumeFeedback becomes
   an instance (WINSVC-009).
4. **Windows power policy lane.** One injected `PowerPolicyLane` replaces `PowerSchemes.MutationGate` and the static
   singletons (WINSVC-009, WINSVC-030). All four apply paths follow the CpuBoost/HC model (WINSVC-010).
5. **TransientDisplayMode owner.** It replaces the originals and revision loops of RefreshRatePairingService and
   DisplayResolutionService (WINSVC-011/012). It is also WSGM's single caller of WDC display mode writes, which gives the
   plan's "display mutations serialize through one injected service per host" a WSGM-side consumer.
6. **OtherManagers and ConsoleTool.** An instance takeover with strict restore and a tracked start-up re-check
   (WINSVC-013/014). One console runner with a tri-state outcome (WINSVC-015).
7. **Dead-code and duplicate cleanup** in Interop (WINSVC-040/041) and the test relocation/rewrites (WINSVC-039).
8. **Consumer manifest correction** for the WDC 0.2.0 group (C2) and the CCD reassignment (C1).

Changes:

- C7: replace "HWND user data/owned callback registrations" with "single owner constructs; static instance read by the
  WndProc; try/catch at the native boundary; reference-counted registrations return IDisposable claims". The defects
  need nothing more.
- C5: remove the invented 90 s pairing deadline; caller cancellation is enough.
- C3: the moved arrival waiter replaces WDC's own waits (no two waiters).
- Shutdown (L91, B3 table): add the concrete current-order fix. `_audio`, `_radios`, `_drives`, `_steamStorage`, card
  monitors and the display-mode restore run before `DisposeUiOwnedSessionResources` destroys the MessageWindow. Today
  they run after it (C8).

Mechanisms in the plan or code that over-engineer, and the simpler shape:

| Mechanism | Where | Simpler shape |
| --- | --- | --- |
| HWND user data + callback registration objects for MessageWindow/DisplayChangeWindow | plan L115 | One owner, static instance, try/catch WndProc, IDisposable only for the three counted registrations |
| 90 s pairing deadline | plan L125 | Caller token plus WinRT completion |
| Operating-point revision counter + two-attempt retry loops in refresh pairing | RefreshRatePairingService.cs L82-92, L127-157, L366-421 | One display-mode lane: read the mode once per operation under the lane, no revision |
| Test-only production branches (`"test"` key, null operating point) | RefreshRatePairingService.cs L133, L323-343, L361-363 | Fakes in tests |
| Static feed queue + watch generation in RadioManager | RadioManager.cs L41, L485-518 | Per-instance WDC subscription; disposing the subscription is the stop |
| Five storage enumerations joined by disk number and display text | WINSVC-004/005 | One inventory snapshot |
| Readback that throws after Windows power writes | WINSVC-010 | Write, refresh, log the readback, publish the written value |
| Arbitrary caps (32 MiB, 4,096 exports, 128-byte names, 1,024 RTSS entries) | WINSVC-031/032 | Stream and capacity bounds only |
| Three ConsoleTool run variants | WINSVC-015 | One runner, one outcome type |
| Two RTSS mappings with duplicated header parsing | WINSVC-031 | One mapping owner |

Not over-engineering, keep: SdFormatManager's per-stage re-verification and three format attempts (documented device
evidence in Shell/AGENTS.md); CardVolumeMonitor's re-read before acting; AudioManager's revision-based latest-wins
selection; ConsoleTool's kill-and-wait on deadline. Each answers a recorded defect.

## 4. Target design

Owners (all are instances constructed by the session composition root, inert until Start, registered in
`SessionLifetime`):

| Owner (new or kept) | Responsibility | Replaces / absorbs | Ports |
| --- | --- | --- | --- |
| `AudioManager` (kept) | Volume, endpoint and selection projection for UI and Steam | its own static CoreAudio calls; VolumeFeedback static | `IAudioEndpoints`, `IVolumeFeedback` |
| `IAudioEndpoints` (new internal port, Shell) | List/default/volume/mute (incl. by endpoint id)/format/spatial/watch | `IAudioProfileOperations` (renamed + extended) and every static CoreAudio call in WSGM | adapter over W01 `AudioService` |
| `VolumeFeedbackPlayer` (new instance, Shell) | Owns the WaveOut feedback player; Play/Reopen/Dispose | `VolumeFeedback` static class | WDC WaveOutFeedback |
| `DisplayOffMuteService` (kept) | Mute policy; restores the endpoint id it muted | ProcessExit handler | `IAudioEndpoints`, `ILastInput`, display signal |
| `VolumeButtonService` (kept) | Shell-hook routing; commands on the audio write worker | UI-thread COM call | `AudioManager` |
| `RadioManager` (kept, shrunk) | Wi-Fi/Bluetooth projection keyed by `WifiNetworkKey`/logical device | `_feedWork`, generations, BluetoothDeviceCatalog | W01 RadioPower/Wifi/Bluetooth services (owned registrations) |
| `StorageInventory` (new, Shell) | One snapshot of disks, volumes, letters, GUID paths, hotplug class, bus, size | ReadSnapshot/ReadTargets/ScanCardLibraryPaths/CardAcf drive walk; WDC WindowsStorage use in SteamStorageBridge | `IStorageQueries` over NativeStorage |
| `RemovableDriveManager` (kept) | Eject list projection and eject actions | its own enumeration | `StorageInventory`, `IEjectOperations` |
| `SdFormatTargets` (new, from SdFormatManager) | Format target projection | SdFormatManager enumeration + Apply | `StorageInventory` |
| `SdFormatRun` (new, from SdFormatManager) | Three-stage destructive run with per-stage reverification, immutable target, GUID-path writes | FormatAsync, VerifyTarget, ReadTargetIdentity, WaitFor*, RunDiskpart, RetrimVolume | `IDiskpart`, `IDiskIdentity`, `ISteamLibraryRegistration`, `IClock` |
| `SteamLibraryRegistration` (new adapter; library domain owns policy) | Remove/restore/add a card library live (CEF) or closed (VDF) | RemoveExistingLibrary, RestoreRemovedLibraryIfCardSurvived, RegisterLibrary, CreateSteamLibrary, AddLibrary, WriteMarkerAndClientDll, BackupOnce | SteamLibraryFolders/SteamLibraryVdf |
| `CardVolumeMonitor`, `CardAcfWatcher` (kept) | Card reconcile and manifest watching | own drive walks, `MessageWindow.Create()` | `StorageInventory`, injected MessageWindow claim |
| `PowerPolicyLane` (new tiny class, Core) | The single mutation lock for scheme, boost, cores, mode, timeouts | `PowerSchemes.MutationGate` | none |
| `PowerSchemes`/`CpuBoost`/`HybridCores`/`WindowsPowerModes` (kept, de-static) | Feature policy over their existing ports, all HC-style write | static `Windows` singletons, readback throws | existing 4 ports + lane |
| `PowerTimeouts` (static -> instance) | Timeout read/write | static class | `IPowerSchemeApi` extended with Read/WriteSetting, lane |
| `TransientDisplayMode` (new, Core) | Accepted modes, transient refresh/resolution writes, single original, restore | RefreshRatePairingService state + DisplayResolutionService + DisplayProfiles mode helpers + EdidModes | `IDisplayModes` over WDC DisplayModes/DisplayEdid |
| `RtssSharedMemory` (new, Core) | One RTSSSharedMemoryV2 mapping: frametime read + OSD slot write | RtssFrametimeReader mapping and parse, RtssOsdWriter mapping | `IRtssRegion` (existing) |
| `RtssOsdRenderer` (kept) | Level/custom/power status render loop; explicit Start; ordered Dispose | ctor-started loop | `RtssSharedMemory`, `RtssOsdMetricsSource` |
| `RtssOsdMetricsSource` (kept, synchronized) | 1 s cached sensor sample shared by OSD and AutoTDP | unsynchronized mutable fields | LHM reader, kernel counters |
| `OtherManagerTakeover` (new instance, from OtherManagers static) | Detect/Disable/Apply/Restore/Reapply | static OtherManagers methods | `IAutostartSystem`, `IServiceSystem`, `IProcessProbe`, config store |
| `ConsoleTool` (kept, one runner) | Run hidden console tools with a deadline | `Run`, `RunCapturedAsync` | `IConsoleToolProcess` (existing) |

Old symbol -> new owner, dissolved and split files:

`Shell/SdFormatManager.cs` (dissolved):

| Old symbol | New owner |
| --- | --- |
| `DefaultLabel`, `SanitizeLabel`, `BuildDiskpart*Script`, `ReverifiedStages` (const array deleted; stage list becomes the run's ordered steps) | `SdFormatRun` (static pure helpers kept) |
| `VolumeWaitMs`, `FormatAttempts`, `FormatRetryDelayMs`, `LetterProbeAttempts`, `CardChangedMidRunMessage` | `SdFormatRun` |
| `Targets`, `HasTargets`, `Refresh`, `ReadTargets`, `Apply`, `FindTarget`, `DescribeTarget`, `DescribeBus`, `FormatTarget` record | `SdFormatTargets` |
| `Busy`, `NotBusy`, `StatusText`, `HasStatus`, `Finished`, `Finish`, `_formatGate`, `CardWatcher` | `SdFormatRun` (single instance per session; overlay and Steam storage share it) |
| `FormatAsync`, `VerifyTarget`, `CompareIdentity`, `ReadTargetIdentity`, `LogReverification`, `TargetIdentity`, `TargetIdentitySnapshot`, `WaitForVolume`, `WaitForLetter`, `RunDiskpart`, `RetrimVolume`, `TrimAsync` | `SdFormatRun` (`TrimAsync` stays a public entry for SteamStorageBridge) |
| `RemoveExistingLibrary`, `FindExistingMarker`, `LettersOnDisk`, `FullPathOrNull`, `RestoreRemovedLibraryIfCardSurvived`, `CreateSteamLibrary`, `RegisterLibrary`, `WriteMarkerAndClientDll`, `BackupOnce`, `LibraryRemoval` | `SteamLibraryRegistration` |
| `AddLibraryAsync`, `ResolveLibraryRoot`, `AddLibrary` | `SteamLibraryRegistration` (entry kept on the run object for its Busy gate) |
| Card retirement `LibraryTabManager.MutateConfigAsync` (L509) | config-store transaction port (config domain) |

`Shell/VolumeFeedback.cs` (dissolved): `Initialize`/`Reinitialize`/`Play`/`Open`, the gates and `_player` move to the
`VolumeFeedbackPlayer` instance. Its consumers are AudioManager L377, L556, L732, L807 and VolumeButtonService.

`Core/RefreshRatePairingService.cs` and `Core/DisplayResolutionService.cs` (merged into `TransientDisplayMode`):

| Old symbol | New owner |
| --- | --- |
| `SetStrategy`, `FrameLimitOptions`, `FrameLimitRange`, `SelectRefreshHz`, `ApplyForCap`, `TryApplyManual`, `AcceptedRates` | `TransientDisplayMode` (policy calls into `FrameLimitPairing`) |
| `Restore` (both), `CaptureOriginal` (both), `_originals`, `_original` | `TransientDisplayMode.Restore` with one original `DisplayModeSnapshot` |
| `OperatingPointRevision`, `RefreshOperatingPoint`, `Snapshot`, retry loops, `"test"` key | deleted |
| `Options`, `Apply(DisplayResolution)` | `TransientDisplayMode.ResolutionOptions`/`ApplyResolution` |

Consumers that change: ShellSession.cs L161, L588-592, Shutdown L413-450, ShellSession.Performance.cs L35, L89 and the
pairing callbacks, NativeQamResolutionService, PerformanceOverlayBridge, Overlay performance surfaces, and the tests
RefreshRatePairingServiceTests and any DisplayResolutionService tests.

`Core/DisplayProfiles.cs` and `Core/EdidModes.cs` (dissolved): the accepted-mode enumeration, transient apply,
`ReadPrimaryOperatingPoint` and `TryRestoreRefreshRate` move into `TransientDisplayMode`'s `IDisplayModes` adapter.
`ReadAdvertisedRefreshRates` becomes a WDC call per target (W02). `ReadPrimaryMonitorInstanceId` and `EdidModes` are
deleted. `DisplayResolution` (record struct) stays in Core. Consumers that change: NativeQamResolutionService, the
ShellSession performance wiring and their tests. SettingsViewModel's display editor calls WDC DisplayModes/DisplayEdid
directly and does not change.

`Core/RtssOsd.cs` and `Core/RtssFrametimeReader.cs` (split by owner, not by partial):

| Old symbol | New owner |
| --- | --- |
| `IRtssOsdRegion`, `IRtssRegion` | merged into one `IRtssRegion` (read + write + busy) |
| `RtssOsdSlots` (pure) | kept, file `RtssOsdSlots.cs` |
| `RtssOsdWriter`, RtssFrametimeReader `TryOpen`/`Close`/`AccessorRegion`, `MapName`, `Signature` | `RtssSharedMemory` |
| `RtssFrametimeReader.Parse` (pure) | kept; writes into a caller-owned reusable list |
| `RtssOsdMetrics`, `RtssOsdPowerStatus`, `RtssOsdCustomSettings` | `RtssModels.cs` |
| `LhmSensorReader`, `RtssLhmSensors`, `RtssOsdMetricsSource` | `RtssSensors.cs` (synchronized) |
| `RtssOsdContent` (pure) | kept |
| `RtssOsdRenderer` | kept; Start/Dispose ordering |

`Core/OtherManagers.cs`: `OtherManager`, `DetectedManager`, `OtherManagerRecord`, `OtherManagersResult` and `Known` stay
as data. `IServiceSystem`/`ServiceSystem` stay. `Detect`, `Disable`, `Apply`, `ReapplyAtStart`, `RunElevatedDisable`,
`RestoreAll`, `Record` and `Restore` become instance methods on `OtherManagerTakeover`. `DescribeRecords`,
`ExecutableName` and `Key` stay static and pure. Consumers: Program.cs (the `--disable-other-managers` one-shot and the
uninstall restore), ShellSession.cs L398, Settings (SettingsViewModel.System/Save), WSGM.Setup UI ProfilePage,
Installer.cs, and OtherManagersTests.

`Core/PowerSchemes.cs`: `MutationGate` and `Windows` are deleted and replaced by an injected `PowerPolicyLane`.
Consumers that change: CpuBoost.cs L73/L155, HybridCores.cs L80/L217, WindowsPowerModes.cs L10/L41, PowerTimeouts.cs
L107/L193, DisplayTimeouts.cs L99/157/180/224, OverlayController.cs L552-557/L795, NativeQamPowerProfileService.cs L62,
Overlay/PowerSchemeSelection.cs L79, SteamUiSessionHost.cs L78/L109, ShellSession.cs L213, UiTests (VisualTests,
DevicePageCaptureTests, ControllerNavigationTests, HybridCoreViewTests, OverlayLayoutTests) and WSGM.Tests power tests.

`Core/ConsoleTool.cs`: `Run` and `RunCapturedAsync` are deleted in favor of `RunAsync(exe, args, deadline, captureOutput,
ct) -> ConsoleToolResult(Outcome, ExitCode?, Output)`. `RunUntilAsync` becomes the implementation. Consumers:
AutostartSystem.cs L136/L153, OtherManagers ServiceSystem L127-141, SdFormatRun (diskpart, icacls, powershell),
UnelevatedLauncher.cs L239, and ConsoleToolTests.

`Interop/NativeMethods.cs`: the dead symbols in WINSVC-040 are deleted and the volume GUID is deduplicated. The file
otherwise stays as is. Splitting a declarations file buys nothing.

Public API changes: none in WSGM projects (these types are app-internal; WINSVC-043 lowers visibility). Library-side,
WDC W01 needs per-endpoint mute/volume (`AudioService.SetMuted(endpointId, bool)`, `GetVolume(endpointId)`), Bluetooth
logical-device publication (the catalog moved), and a watch subscription that returns `IDisposable`. W02 needs advertised
refresh rates per `DisplayTargetIdentity`, the moved arrival waiter with an injected hint, and optionally device-type and
drive-type on `StorageVolume` if StorageInventory consumes WDC rather than NativeStorage. Recommendation: StorageInventory
uses WSGM NativeStorage, and WDC `WindowsStorage` stays as a library API with no WSGM caller, per the "no deletion
because unused" rule.

## 5. Implementation batches

Every batch builds green on its own, runs only its filtered tests, and leaves UI and workflows unchanged.

**WINSVC-B1 - Correctness fixes, no structural change (~450 lines).**
Files: RemovableDriveManager.cs, RemovableDriveEntries.cs, SdFormatManager.cs (target snapshot only), RtssOsd.cs
(metrics lock, renderer Start/Dispose order), RtssNativeAdapter.cs (Volatile `_lastProbe`, Start call), CardAcfWatcher.cs
(idempotent suspension), RadioManager.cs (token on the UI post, Rescan flag), AudioManager.cs (`Refresh` posts to the UI
thread), DisplayOffMuteService.cs (ProcessExit unsubscribe), LockScreenSettings.cs (no-snapshot restore is a no-op),
NativeMethods.cs/NativeStorage.cs (dead code, GUID).
Steps:
1. Wrap the observer pair in EjectAsync so `EjectedAsync(false)` runs on every failure path.
2. Raise their own names from the `SizeText`/`ResultText` setters.
3. Have `FormatAsync` copy an immutable target record and use only it.
4. Guard `RtssOsdMetricsSource.Sample` with a `Lock`; start the renderer from `RtssNativeAdapter` after construction;
   Dispose cancels, awaits the loop with its existing 2 s bound, then releases the slot.
5. Apply the remaining one-line fixes listed above.
Dependencies: none.
Tests: `RemovableDriveTests` (eject throw -> observer told false, through a fake observer; needs an eject port, so add
the minimal `Func<...>` seam already used by RadioManager's internal ctor); `RtssOsdTests` (concurrent `Sample` from two
threads over a fake LHM/kernel source gives a coherent cache); `SdFormatTests` (refresh during run does not change the
baseline, via an extracted pure snapshot helper); `LockScreenSettings` no-op restore test.
Filter: `FullyQualifiedName~RemovableDrive|FullyQualifiedName~RtssOsd|FullyQualifiedName~SdFormat|FullyQualifiedName~LockScreen|FullyQualifiedName~RadioManager|FullyQualifiedName~AudioManager`.

**WINSVC-B2 - Power policy lane and HC-style writes (~900 lines).**
Files: PowerSchemes.cs, CpuBoost.cs, HybridCores.cs, WindowsPowerModes.cs, PowerTimeouts.cs, DisplayTimeouts.cs,
WindowsPowerSchemeApi.cs (adds Read/WriteSetting), OverlayController.cs, PowerSchemeSelection.cs,
NativeQamPowerProfileService.cs, SteamUiSessionHost.cs (constructor arguments only), ShellSession.cs (composition), and
the UiTests fakes.
Steps:
1. Add `PowerPolicyLane` and delete `MutationGate` and the four `Windows` statics.
2. Make PowerTimeouts an instance.
3. Make Select/Apply write, refresh, log the readback and return the written value.
4. Let `DisplayTimeouts.Select` write without a prior read.
Dependencies: steamhost domain (SteamUiSessionHost constructor) and UI domain (OverlayController) are touched only to pass
the instances through.
Tests: readback mismatch publishes the written value and logs; the lane serializes concurrent applies (fakes).
Filter: `FullyQualifiedName~PowerScheme|FullyQualifiedName~CpuBoost|FullyQualifiedName~HybridCore|FullyQualifiedName~PowerTimeout|FullyQualifiedName~DisplayTimeout`
plus UiTests `FullyQualifiedName~HybridCoreView|FullyQualifiedName~DevicePageCapture`.

**WINSVC-B3 - Console runner and other-managers takeover (~800 lines).**
Files: ConsoleTool.cs, AutostartSystem.cs (call sites), UnelevatedLauncher.cs (call site), OtherManagers.cs, Program.cs,
ShellSession.cs, Settings call sites, WSGM.Setup ProfilePage, Installer.cs.
Steps:
1. Add one `RunAsync` with a `ConsoleToolResult` and delete `Run`/`RunCapturedAsync`.
2. Make `OtherManagerTakeover` an instance with injected ports.
3. `RestoreAll` uses the strict read and returns 1 on unreadable config without writing (U04B-LFA-004).
4. The session tracks the start-up `ReapplyAtStart` task.
Dependencies: config domain for the strict-read API. Today's `ConfigStore.LoadForMutation` is sufficient; switch to the
instance store when it lands.
Tests: timeout yields Unknown with the tree killed, through a fake process; RestoreAll on a corrupt-config fake returns 1
and does not write; Disable records before each change (existing test kept); Apply after elevation re-detects.
Filter: `FullyQualifiedName~ConsoleTool|FullyQualifiedName~OtherManagers|FullyQualifiedName~UnelevatedLauncher|FullyQualifiedName~Autostart`.

**WINSVC-B4 - StorageInventory (~1,300 lines).**
Files: new Shell/StorageInventory.cs; RemovableDriveManager.cs, RemovableDriveEntries.cs (letters as data),
CardVolumeMonitor.cs, CardAcfWatcher.cs, SteamStorageBridge.cs, LibraryPolicy.cs (`LibraryPathsOn` uses data),
SdFormatManager.cs (ReadTargets uses the inventory), NativeStorage.cs (query port).
Steps:
1. Build one reader producing the snapshot.
2. Make each consumer a projection of it.
3. Delete `SplitLetters` and the duplicate walks.
4. The eject list keeps its cached system disks inside the inventory.
Dependencies: library domain (LibraryPolicy) and steamhost domain (SteamStorageBridge) must accept the data-letter change
in the same batch.
Tests: an unlettered media row produces no paths, so trim selects nothing; the eject, format and Steam projections agree
on one fixture inventory; a card swap mid-pass is still abandoned (CardVolumeMonitor re-read).
Filter: `FullyQualifiedName~RemovableDrive|FullyQualifiedName~SteamStorageBridge|FullyQualifiedName~CardLibrary|FullyQualifiedName~CardName|FullyQualifiedName~SdFormat`.

**WINSVC-B5 - SdFormatManager split with ports (~1,500 lines).**
Files: SdFormatManager.cs -> SdFormatTargets.cs, SdFormatRun.cs and SteamLibraryRegistration.cs; SdFormatEntries.cs;
OverlayController.cs (no second instance; overlay-test composes a fake run); SteamStorageBridge.cs; ShellSession.cs;
tests split into SdFormatRunTests and SteamLibraryVdfTests.
Steps:
1. Move the symbols per the section 4 table.
2. Add the ports `IDiskpart`, `IDiskIdentity`, `ISteamLibraryRegistration` and `IClock`.
3. Write through the volume GUID path (WINSVC-035).
4. Make the removal async (WINSVC-036).
5. Map the diskpart Unknown outcome to the "may have been erased" message.
Dependencies: B3 (runner outcome); B4 (inventory); library domain (`SteamLibraryFolders`/`SteamLibraryVdf` stay their
owner); config domain (card retirement transaction).
Tests: a fake run asserts a reverification before each destructive stage (this replaces the constant pin); a changed
identity before clean restores the removed registration once; a changed identity after clean does not compensate; an
unknown diskpart outcome yields the uncertain message; a letter-keep failure stops.
Filter: `FullyQualifiedName~SdFormat|FullyQualifiedName~SteamLibraryVdf`.

**WINSVC-B6 - TransientDisplayMode (~900 lines).**
Files: new Core/TransientDisplayMode.cs; delete RefreshRatePairingService.cs, DisplayResolutionService.cs,
DisplayProfiles.cs (moved helpers) and EdidModes.cs (only when W02 exposes advertised rates; until then the EDID helper
moves into the adapter unchanged). Also ShellSession composition/performance/shutdown, NativeQamResolutionService and
PerformanceOverlayBridge call sites.
Steps:
1. Capture one original mode under the lane.
2. Serialize transient writes.
3. Restore once at shutdown, replacing the two restores.
4. Delete the revisions and test branches.
Dependencies: W02 (DisplayService/advertised rates) is optional; session domain (shutdown step rename).
Tests: resolution-then-refresh changes restore the original full mode in one write; a failed restore retains the original;
a manual refresh under a cap is refused (ported test); a display change mid-operation leaves the cache unpublished, now
expressed as "mode read under the lane".
Filter: `FullyQualifiedName~TransientDisplayMode|FullyQualifiedName~FrameLimitPairing`.

**WINSVC-B7 - Audio endpoint port and feedback instance (~1,200 lines).**
Files: AudioProfileService.cs (port rename/extend), AudioManager.cs, DisplayOffMuteService.cs, VolumeButtonService.cs,
VolumeFeedback.cs -> VolumeFeedbackPlayer.cs, RadioManager.cs (Bluetooth audio through the port),
Settings/AudioProfileEditor.cs (reads through AudioProfileService), ShellSession composition and shutdown, and the
NativeQam audio services' constructor arguments.
Steps:
1. One `IAudioEndpoints` adapter.
2. Consumers take the port.
3. Volume buttons run on the audio write worker.
4. The mute owner records the endpoint id. Use the W01 API when it exists; until then the adapter mutes the default
   endpoint and records its id, and restore refuses only when the id is not the current default. That interim is not
   worse than today.
Dependencies: W01 (per-endpoint mute, instance AudioService) for the final id-based restore; UI domain for the
AudioProfileEditor change.
Tests: a default change while muted restores the original endpoint (fake); a failed read keeps the claim and the tick
retries; a volume-button command does not run on the dispatcher thread; the feedback player is disposed at shutdown;
AudioManager selection latest-wins with a fake port.
Filter: `FullyQualifiedName~AudioManager|FullyQualifiedName~AudioProfile|FullyQualifiedName~DisplayMute|FullyQualifiedName~VolumeAppCommands|FullyQualifiedName~VolumeOsd|FullyQualifiedName~BluetoothAction`.

**WINSVC-B8 - Radio over WDC instance services (~1,300 lines).**
Files: RadioManager.cs, RadioEntries.cs, BluetoothDeviceCatalog.cs (deleted, moved to WDC), NativeQamNetworkService.cs
(project from RadioManager), NativeQamBluetoothService.cs, Overlay/RadioPanel.axaml.cs, SystemStatus.cs, ShellSession.cs,
and the RadioManager/BluetoothDeviceCatalog/BluetoothAction tests.
Steps:
1. Inject the RadioPower/Wifi/Bluetooth services.
2. Use owned subscriptions.
3. Delete `_feedWork` and the generations.
4. Key rows by `WifiNetworkKey`.
5. Pairing uses the attempt object with a caller token (no 90 s constant).
Dependencies: W01 published (I01). Steamhost and UI domains take the consumer edits in this batch.
Tests: start/stop scanning creates and disposes one subscription each; a second manager (preview) does not stop the
session's feed; a failed listing keeps rows; a key-identical rename is not duplicated; Forget is called with the key.
Move WifiProfile/WindowsRadio tests to the WDC suite.
Filter: `FullyQualifiedName~RadioManager|FullyQualifiedName~RadioEntry|FullyQualifiedName~BluetoothAction|FullyQualifiedName~NativeQamNetwork|FullyQualifiedName~NativeQamBluetooth`.

**WINSVC-B9 - RTSS shared memory owner and discovery caps (~1,000 lines).**
Files: RtssFrametimeReader.cs, RtssOsd.cs (split per table), RtssModels.cs, RtssDiscovery.cs (caps, process image path),
RtssNativeAdapter.cs, AutoTdpService.cs (sample buffer consumption only).
Dependencies: the performance/AutoTDP domain agrees on the `ReadLive` buffer contract.
Tests: parse into a reused list allocates nothing (allocation assertion via `GC.GetAllocatedBytesForCurrentThread`
around a fake region); a large export table is accepted; an RTSS process path comes from the image-name query fake; OSD
slot claim, update and release over one region.
Filter: `FullyQualifiedName~Rtss|FullyQualifiedName~AutoTdpService`.

**WINSVC-B10 - Message/display window ownership (~700 lines).**
Files: MessageWindow.cs, DisplayChangeWindow.cs, RemovableDriveManager.cs, CardAcfWatcher.cs, CardVolumeMonitor.cs,
HotkeyService.cs, OverlayController.cs L215, ShellSession.cs/.SteamUi.cs/.Shutdown.cs.
Steps:
1. Owner-only construction.
2. Try/catch in the WndProcs.
3. Reference-counted registrations return IDisposable claims.
4. Move provider disposal ahead of window destruction (C8).
Dependencies: session domain H02 (SessionLifetime ordering). Do this jointly or after H02's shutdown step table.
Tests: the WndProc dispatch helper with a throwing subscriber does not propagate; disposing a claim after the window is
gone is a no-op that is logged once; a second claim keeps the registration alive.
Filter: `FullyQualifiedName~MessageWindow|FullyQualifiedName~ShellDisplay|FullyQualifiedName~RemovableDrive|FullyQualifiedName~CardAcf`.

**WINSVC-B11 - Remaining owner tests and test hygiene (~1,100 lines, tests only plus small ports).**
Scope: KeepAwakeService (loop joined on dispose, with a fake download port; needs the steamhost CEF query port),
ModernStandbyGuard (standby/suspend port; reuse W02_02's semantics), CardVolumeMonitor/CardAcfWatcher over fake
inventories, OtherManagers Apply. Delete the constant pins (WINSVC-039) and move the misplaced VDF tests.
Dependencies: B4, B10; steamhost domain for the download-activity port.
Filter: `FullyQualifiedName~KeepAwake|FullyQualifiedName~ModernStandby|FullyQualifiedName~CardVolume|FullyQualifiedName~CardAcf`.

Order: B1 -> B2 -> B3 -> B4 -> B5 -> B6 -> B7 -> (I01: W01/W02 published) B8 -> B9 -> B10 (with H02) -> B11. B2, B3, B6 and
B9 are independent of each other after B1 and may be reordered. B7's final id restore and B8 wait for W01.

## 6. Risks and open questions

Risks:
- B4 and B5 change the code around destructive disk operations. They must keep every reverification point and the
  documented three-attempt format exactly, and M01-35/M01-36 must be re-run on spare media only.
- B7's interim (before W01) cannot restore a non-default endpoint. Ship B7's final form only with W01, or accept the
  interim that never unmutes the wrong device.
- B8 changes the Wi-Fi row identity. A network with the same display name but different bytes becomes two rows. That is
  correct per U01-016, and it is a visible change, though only for colliding SSIDs.

Open questions (maintainer decision):
- Q1: CardAcfWatcher watches the `SteamLibrary` marker on every ready local drive, including fixed internal disks and
  shares. Keep that tab-freshness behavior (default, preserves workflows), or restrict it to removable cards as its
  comment says?
