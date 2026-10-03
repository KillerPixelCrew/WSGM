# WINSVC adversarial verification

Verifier for `_plan/refactor-2.1/review/winsvc.md`. Baseline: parent `master` 1329813f, WDC 2f07485. Read-only; nothing
was built or run. Every medium finding, every claim marked partially/stale/inaccurate/over-engineered, every
batch, and a sample of lows were checked against the current source and their callers.

## Refuted

**WINSVC-011 (medium, "two original display mode owners") - refuted.** No failure scenario is shown, and the
split is a recorded decision. `DisplayResolutionService` documents why it is separate
(Core/DisplayResolutionService.cs L11-16: "Sharing one captured original would let whichever restored second
put back a mode the other had already replaced"). Resolution apply keeps the current Hz
(DisplayProfiles.cs L220-229). Refresh pairing restores per target. The back-to-back shutdown restores
(ShellSession.Shutdown.cs L413-450) compose correctly. The "revision counters and retry loops" the finding
wants deleted are what keep a cached accepted-rate list from being applied to a display whose operating
point changed mid-operation. The class comment and `TryApplyManual` call that hazard "a black screen"
(RefreshRatePairingService.cs L157-160, L172-176, L276-283, L387-421). Keep WINSVC-012 (delete the test-only
`"test"` key and null-operating-point branches by giving the harness an operating-point fake). Drop the merge.

**WINSVC-034 (low, LockScreenSettings no-snapshot restore) - refuted as a defect, recommendation harmful.**
The mechanics are right: `RecoverySnapshot` without a capture yields `PolicyExisted: true`, empty schemes
(LockScreenSettings.cs L95-106), and WDC `Restore` then writes `1/1` into every scheme
(WindowsWakeSecurity.cs L104-117). That is the deliberate fallback to Windows' secure default, sign-in
required on wake. The proposed "no-op, nothing to restore" leaves a user permanently without sign-in on
wake whenever the snapshot is lost (corrupt or reset config, a manual edit). That is exactly the
never-strand case. At most, skip `CreateSubKey` when the key is absent so no empty policy key is left. Keep
the scheme default write. Remove this item from B1.

## Corrected

- **WINSVC-002 (medium -> low).** The behaviour is real but only reachable through an unexpected exception.
  `EjectDevice`, `EjectMediaVolume` and `EjectUnletteredMedia` are non-throwing native wrappers that return
  `EjectResult` (RemovableDriveManager.cs L587-735). `EjectingAsync` already catches unregister failures
  (LibraryPolicy.cs L86-95). The fix stays a `try/finally` around the observer pair.
- **WINSVC-005 (medium, keep; scope correction).** Confirmed. The wrong-letter `Optimize-Volume -ReTrim` is
  non-destructive (ReTrim erases nothing), and `LibraryPathsOn` reads a relative path that will not exist.
  The bug is visible to users but not dangerous. Move the small letters-as-data fix (rows carry
  `IReadOnlyList<char>`, delete `SplitLetters`) into B1. It does not need the 1,300-line StorageInventory
  (B4).
- **WINSVC-008 (medium -> low).** A re-claimed slot needs the loop to be mid-`TryWrite` on another thread
  exactly while `Dispose` releases the slot, after the 2 s join has already expired. `_disposed` is set
  first and `TryUpdate` checks it (RtssOsd.cs L302-307, L1425-1454). The `Volatile` publication of
  `_lastProbe` (RtssNativeAdapter.cs L30, L37, L71) adds nothing: a reference write is atomic and published
  safely under the .NET memory model. Drop that step (simplify). Explicit Start, then await-then-release,
  remains a reasonable low fix.
- **WINSVC-009 (medium, narrow it).** The uninjectable `Windows` static singletons (PowerSchemes.cs L20,
  CpuBoost.cs L73, HybridCores.cs L80, WindowsPowerModes.cs L10) are the plan-relevant defect. A single lock
  over the machine-global active power scheme is not a defect. The site count is 12 `lock` sites in 9 files.
  Simplest shape: the lock moves into the one injected `PowerSchemes` instance, and the other owners take
  that instance. No new `PowerPolicyLane` type. `HotkeyService._nextId` (a static id counter) is not a
  defect.
- **WINSVC-014.** Ignoring `SelfElevation.RunElevatedAction`'s result is harmless: `Apply` re-detects
  afterwards and derives disabled and failed from what is still active (OtherManagers.cs L340-359). The rest
  stands.
- **WINSVC-015.** The partition-failure text does not tell the user "nothing was erased". It says "Windows
  could not rebuild the drive. Reinsert the card and try again." (SdFormatManager.cs L485-494). The
  substantive point stands: a killed `diskpart` folds into `-1` like a definite failure, and the card-database
  retirement at L507-515 is skipped although `clean` may have run.
- **WINSVC-032 (bitness claim wrong).** WSGM is x64, so `Process.MainModule` on the x86 RTSS process works.
  The real failure mode is integrity: an unelevated WSGM cannot open an elevated RTSS. Access-denied is
  caught per process (RtssDiscovery.cs L441-454), which yields `NotRunning`. Shell WSGM normally runs
  elevated (docs/elevation.md), so this stays low. The caps part is confirmed.
- **WINSVC-045 / C7 (wrong citation and overstated defect).** DisplayChangeWindow.cs has 97 lines; its
  WndProc is L82-96, not L711-724. Both window procedures only compare messages and `Dispatcher.Post` the
  handlers (MessageWindow.cs L531-627, DisplayChangeWindow.cs L82-96), so a subscriber exception never
  crosses the native boundary. A guard there is cosmetic. The unguarded native callback that matters is
  `EffectivePowerModeNotification.OnChanged`, which invokes the managed delegate synchronously (see
  WINSVC-V-003). C7's conclusion (owner-only construction, no HWND user-data machinery) still holds.
- **C8 (stale ordering confirmed; consequence benign; not NEW).** The window is destroyed at
  ShellSession.Shutdown.cs L264 -> L752, before `_drives.Dispose` at L600. `MessageWindow.Dispose` zeroes
  `_volumeNotify` (MessageWindow.cs L439-454), so the later `DeregisterVolumeNotifications` is a no-op
  (L429-437). Nothing fails today. The ordering is already provisional ledger row PV11-013, so the claim
  that the ledger has "zero rows" for this domain is inaccurate.
- **C12.** `audit/A01/` also holds per-file-coverage.json, source-assessment.json, unit-accounting.json and
  related files. They are structural inventories (declarations, hashes) with no findings for this domain,
  so the substance holds.
- **C3 / C4 / WINSVC-029 (plan conflict not flagged).** refactor-plan.md L75 lists `BluetoothDeviceCatalog`
  among "useful current owners" to preserve. L127 says to move it into WDC and retire the WSGM copy. B8
  deletes it. The maintainer has to pick one; the review should not silently assume L127.

## Confirmed (ids only)

WINSVC-001, WINSVC-003, WINSVC-004, WINSVC-006, WINSVC-007, WINSVC-010, WINSVC-012, WINSVC-013, WINSVC-016,
WINSVC-018, WINSVC-019, WINSVC-021, WINSVC-026, WINSVC-040, WINSVC-042, C1, C2, C11, C13

Notes: WINSVC-006 is worse than stated. A same-reader card swap keeps the device instance id and disk
number, so a late `Apply` rewrites `SizeBytes`/`BusType` to the *new* card. `CompareIdentity` then reports
Same, and the run would format the swapped card. Window: a `Refresh` (overlay, or SteamStorageBridge L76 and
L278) that started before `Busy` and lands during the run. Keep it medium and first in B1.
WINSVC-010 is a breach of the plan contract ("Readback never gates supported writes or controls",
refactor-plan L111). It is independent of the lane and should ship on its own.

## Missed findings

**WINSVC-V-001 (medium, bug) CardVolumeMonitor drops a pass that arrives while another runs.**
Shell/CardVolumeMonitor.cs:251. `_gate.WaitAsync(TimeSpan.Zero, ...)` returns false and the pass simply
ends. Nothing reschedules it, although the comment at L248-249 says "A second card arriving mid-pass simply
waits". A pass can take up to 60 s of CEF round trips (L61, L298-300). A card pulled or swapped during that
time raises its notification and settle timer (L171-191), but the resulting pass is dropped. The running
pass works from its stale `present` scan, so `RemoveDepartedCardsAsync` (L465-512) never sees the removal.
`StillInTheReader` catches only cards that needed an action. Steam keeps the departed card's library until
some later notification or Steam restart. Recommendation: no new state. Wait on the gate (the comment's
intent), or have the running pass call `Schedule()` once in its `finally` when a notification arrived
meanwhile. Waiting is the simpler option.

**WINSVC-V-002 (low, efficiency/simplify) CardVolumeMonitor polls every 3 s while Steam or CEF is
unavailable.** Shell/CardVolumeMonitor.cs:271-283 and 331-341. When `_enabled()` (the CEF master switch) is
false, Steam is not running, or the Big Picture window is not ready, the pass calls `Schedule()` again. Each
pass re-runs `ResolveSystemDisks`, `MountedVolumes` and marker reads every 3 s for as long as the condition
holds, for example all session with the master switch off. These volume-handle opens are not covered by
`CardWatcher.Suspend()` during an eject. Recommendation: remove the self-reschedule. Kick from the events
that end the wait: Steam start (already wired only in Game Mode at ShellSession.cs L1170), master-switch
enable, and Steam UI readiness. That is removing mechanism, not adding it.

**WINSVC-V-003 (low, native boundary) EffectivePowerModeNotification runs the managed callback inside
`[UnmanagedCallersOnly]` without a guard.** Interop/EffectivePowerModeNotification.cs:88-98. `OnChanged`
looks up and invokes `changed` synchronously on the powrprof callback thread. Any exception from
`DeviceCoordinator.RequestPowerAssignmentReconcile` (Shell/DeviceCoordinator.cs L160), or from code it
reaches later, fail-fasts the process. This is the actual unguarded instance of U05-LFB-013 in this
domain; the window procedures only post (see the WINSVC-045 correction). Recommendation: a try/catch around
the invoke that logs once, or post to the existing lane. Owner: device/session domain, file in Interop.

**WINSVC-V-004 (low, plausible, threading) Late shutdown disposes UI-owned managers off the UI thread.**
Shell/ShellSession.Shutdown.cs:475, 488, 600 (and `_steamStorage` at L590). These run after
`await ... ConfigureAwait(false)` (for example L399), so they execute on a pool thread.
`AudioManager.Dispose` stops a `DispatcherTimer` and touches UI-owned fields (AudioManager.cs L230-246).
`RadioManager.Dispose` runs `StopScanning`/`UpdateScanning`, which raise bound properties.
`RemovableDriveManager.Dispose` stops two `DispatcherTimer`s (RemovableDriveManager.cs L123-141). Their
peers are marshalled explicitly through `DisposeUiOwnedSessionResources` (L652). Whether Avalonia 12's
timer `Stop` asserts thread access was not verified. Recommendation: move these disposals into the
UI-thread cleanup, ahead of the message window. That also fixes the C8 ordering in one small change, with
no new mechanism.

**WINSVC-V-005 (nit, privacy) Bluetooth pairing PIN written to wsgm.log.** Shell/RadioManager.cs:1356-1357
logs `pin '{request.Pin}'`. Testers share wsgm.log. The passkey is short-lived, but it has no diagnostic
value. Recommendation: log the ceremony kind only.

**WINSVC-V-006 (nit, rule: no arbitrary limits) LHM sensor XML is cut at 4 MiB.** Core/RtssOsd.cs:601
reads `Math.Min(_view.Capacity, 4 MiB)`. A publication without a NUL in that range parses as null, and every
sensor silently disappears. Recommendation: use the view capacity, which is the real bound.

**WINSVC-V-007 (nit, rule: no arbitrary limits) Fixed 8 KiB drive-layout buffer.**
Interop/NativeStorage.cs:641-656. `IOCTL_DISK_GET_DRIVE_LAYOUT_EX` fails with insufficient buffer above
about 56 partitions. The Steam Deck Linux hint is then lost, which is display-only. Recommendation: grow
the buffer on `ERROR_INSUFFICIENT_BUFFER`, or accept as no-change.

## Batch problems

- **B1.** (a) It contains the refuted WINSVC-034 no-op restore, which would strand users without sign-in
  on wake; drop it. (b) The `Volatile _lastProbe` step is unneeded mechanism. (c) The eject test plans a
  "fake observer", but `EjectObserver` is the concrete `internal sealed class LibraryPolicy`
  (LibraryPolicy.cs L52). A fake needs a new observer interface plus an eject seam, which is test-only
  production mechanism (tests AGENTS rule). Test through the real LibraryPolicy over temp paths or defer to
  B4. (d) B1 should absorb the small user-visible fixes now stranded in big batches: WINSVC-005
  letters-as-data, the WINSVC-010 HC-style writes, V-001, and the V-004 shutdown move, which also fixes C8.
- **B2.** It couples a small rule-driven fix (WINSVC-010) with about 900 lines of lane plumbing through
  SteamUiSessionHost, OverlayController and the UI tests. The new `PowerPolicyLane` type adds mechanism
  with no defect behind it. Put the lock in the injected `PowerSchemes` instance instead.
- **B3.** "The session tracks the start-up `ReapplyAtStart` task" without cancellation would let shutdown
  wait on `CloseWindows` (8 s per process, OtherManagers.cs L573-583) and on `sc.exe` (15 s each). The join
  must sit inside the B3 shutdown budget of planning-corrections, with cancellation checked between items.
  Otherwise it lengthens Normal and SessionEnd shutdown.
- **B4.** Correct direction, but 1,300 lines touching library and steamhost ownership in one batch.
  WINSVC-005 must not wait for it (see B1).
- **B6. Reject as written.** (a) It deletes the operating-point revision guard (WINSVC-011 refutation).
  (b) "Restore once with one full-mode write" changes behaviour. A manual refresh rate is user-owned and
  never restored today (RefreshRatePairingService.cs L152-155), but a captured full mode (w, h, Hz) would
  revert it whenever a resolution was also changed. (c) `SetStrategy(FrameLimitOnly)` restores refresh
  only; under one owner it would also restore resolution. That is a workflow change against the
  "UI/workflows identical" requirement. Replace B6 with WINSVC-012 (test fakes, delete test-only branches)
  plus moving `DisplayProfiles` and `EdidModes` under W02 if wanted.
- **B9.** (a) The frametime reader opens `RTSSSharedMemoryV2` read-only (RtssFrametimeReader.cs L255-256),
  the OSD writer read-write (RtssOsd.cs L338-339). A merged mapping owner must keep the read-only open
  independent of the write open. Otherwise, wherever write access is denied, AutoTDP loses its frametime
  samples, which is behaviour lost silently. (b) Reusable buffers plus a
  `GC.GetAllocatedBytesForCurrentThread` assertion on a 1 Hz path add mechanism without a defect. The
  reviewer's own C14 says the path is not high-rate. Keep only the cap removals.
- **B10.** The planned test "WndProc dispatch helper with a throwing subscriber does not propagate" tests
  something that already holds (handlers are posted). Converting the counted Register/Deregister pairs
  (MessageWindow.cs L396-437) into `IDisposable` claims adds mechanism. Owner-only construction plus the
  V-004/C8 order fix resolves U05-LFB-014. Put the real guard on EffectivePowerModeNotification (V-003)
  instead.
- **B5, B7, B8, B11.** No blocking problem found. B5's dependency chain B3 -> B4 -> B5 is right. B7's
  interim is no worse than today. B8 needs the BluetoothDeviceCatalog conflict above resolved first.
