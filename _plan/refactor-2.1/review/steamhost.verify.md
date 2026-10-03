# Adversarial verification: steamhost.md

Verifier: Claude Opus 5.5, read-only, baseline `1329813f`. Every critical/high/medium finding was re-read against the
cited code and its callers, plus the toolkit pieces it depends on (`SteamUiPatchManager` switch/sync semantics,
`SteamUiModuleRuntime.PublishLoopAsync`, `SteamStartupMovie`, `SteamUiInjectedAsset`). Low/nit findings were spot-checked
and plan claims C1-C27 sampled (C9, C10, C13, C21, C24, C26 checked directly).

## Refuted

None. Every critical/high/medium finding describes real code behaviour. Several need their severity, mechanism or fix
corrected (below).

## Corrected

- **STEAMHOST-001 (high, should be medium).** The ordering defect is real (`ShellSession.Shutdown.cs:117-191` runs before
  `:355-370`), and so is the untokened `_cefMasterGate.WaitAsync()` at `:355`. But no hardware hazard follows. A QAM command
  that lands after `DeviceCoordinator.ShutdownAsync` reaches disposed capabilities or gates (`DeviceCoordinator.cs:805-862`).
  It fails with a disposed-object error inside the runtime and is contained there. It writes nothing. The gate wait is
  bounded by CEF evaluation timeouts. The fix is also stated too broadly. "Order host disposal before device/GPU disposal"
  must not be chosen: `Shutdown.cs:108-111` deliberately runs safety-critical device cleanup (AutoTDP restore, controller
  release, the HidHide cloak off) before any CEF teardown. Only a synchronous admission close may move to T0. That close is
  the existing no-CEF prefix of `DisableAsync` (`SteamUiSessionHost.cs:839-851`: clear the switch flags, then
  `CancelAllInflightRequests`). The runtime's publish/request gates read those flags (`:343-346`), so it adds no mechanism.
  Patch retraction keeps its place after device cleanup.
- **STEAMHOST-002 (medium).** Confirmed: `SetPatchSwitch`/`SetGlobalSwitch` are last-writer-wins
  (`SteamUiPatchManager.cs:346-429`), and the loop's else-branch (`SteamUiSessionHost.cs:952-960`) can overwrite an
  `Apply(true)`. The finding understates the scope in two ways:
  - The loop also overwrites the **global** switch through `SetGlobalEnabledAsync(false)`.
  - `DisableAsync` has the same overwrite pattern. Its final `SetPatchStates(false,false)` and
    `SetGlobalEnabledAsync(false)` (`:861-862`) can land after an ungated `Apply*`. Ungated callers include
    `GameModeEntered` (`ShellSession.cs:1149-1150`) and the reload (`ShellSession.Config.cs:125-140`). The host's equality
    guards (`:591`, `:668`, ...) then swallow the restore's re-apply.

  The fix must derive the patch **and** global switches from one snapshot in every pass, including `DisableAsync`, or the
  race only moves.
- **STEAMHOST-006 (medium latent, should be low).** Confirmed: `ReadModules()` runs once in the constructor
  (`SteamUiSessionHost.cs:261-262`), after common-plugin startup is awaited (`ShellSession.cs:303-316`). A plugin enabled
  later through Settings or Steam's settings page never gets its modules. The recommended fix ("dynamic") adds mechanism
  behind a latent defect, because no in-repo plugin implements `IPluginSteamUi`:
  - a toolkit module-set register/unregister API;
  - patch unregistration in `SteamUiPatchManager`, which today only registers;
  - a mutable bridge command allowlist, since `SteamUiBridgeHost` takes `modules.AllowedCommands` at construction
    (`:321-326`).

  Under the simplify rule the default should be "plugin Steam UI modules are read at session start; a plugin enabled later
  contributes modules from the next session", documented. Dynamic registration only if the maintainer chooses it.
- **STEAMHOST-008 (medium).** The I/O claim is confirmed: every round calls `DescribeVolumes` and reads
  `libraryfolders.vdf` per device and each card marker. The runtime reads sequentially, and a throw quarantines the module
  (`SteamUiModuleRuntime.cs:290-335`). The fix as written would silently drop updates. A projection cached only on
  `OnDrivesChanged` goes stale on events that do not change the drive collection:
  - format-target refresh (`_formats.Targets`);
  - library registration through `AddLibraryAsync`;
  - card rename and marker writes;
  - trim start/stop (`_trimRunning`);
  - `libraryfolders.vdf` edits by Steam.

  Either invalidate on all of those, or use the simpler fix the simplify rule prefers: make the projection cheap and safe.
  Describe only the volumes of the ejectable/formattable letters, skip network drives (the stall source), and keep the
  per-round read. Add a revision only if measurement still shows a cost.
- **STEAMHOST-021 (low).** "`NativeQamTdpState` only exists to be mapped by `ToRange`" is wrong. The RTSS OSD reads
  `ObservedWatts`/`DesiredWatts` from it (`ShellSession.Performance.cs:183-190`). Deleting it is fine only if the
  replacement device projection keeps both fields (see V-008).
- **STEAMHOST-029 (low).** The divergence claim is only half right. `DeleteAsync` applies the override only when `saved`
  (`AnimationService.cs:419-427`). `SetBootAsync` and `SetBootVolumeAsync` apply regardless (`:794-797`, `:486-489`).
  `ChangeConfigLocked` always keeps the unsaved value in memory (`:988-990`). The unified rule still stands.
- **STEAMHOST-031 (low, should be medium for the journal part).** The 256-name journal cap is worse than "recovery
  permanently pending". `Recover` throws `InvalidDataException`, which `ThemeLoader.Load` does not catch, and that escapes
  into session startup. Detail in V-001.
- **STEAMHOST-041 (medium, should be low).** Confirmed that the tests construct the real `RemovableDriveManager` and
  `SdFormatManager`, and that `SteamStorageBridge`'s constructor calls `_formats.Refresh()`. That starts read-only
  `NativeStorage.ListDiskInterfaces()`/IOCTL queries (`SdFormatManager.cs:171-221`). It violates the test guidance but
  mutates nothing, and the dispatcher post never runs under xUnit.
- **STEAMHOST-047 (low).** "The QAM rows can be re-applied transiently before that retraction runs" is not shown. On master
  off, `ApplyHostSteamUi(false)` re-runs `SetPatchStates(..., _enabled)` with components still enabled, but they are
  already applied, so nothing is re-applied. The real defect is the ungated overwrite race now folded into STEAMHOST-002.
- **STEAMHOST-035 (nit).** `BigArt`/`Last` are read by tests (`LibraryBadgesTests.cs:111-117`, `HomeCarouselTests.cs:51-53`),
  not by production. `HomeCarouselBackend` has no equality guard; only `LibraryBadgeBackend` does.

## Confirmed (ids only)

STEAMHOST-003, 004, 005, 007, 009, 010, 011, 012, 013, 014, 015, 016, 017, 018, 019, 020, 022, 023, 024, 025, 026, 027,
028, 030, 032, 033, 034, 036, 037, 038, 039, 040, 042, 043, 044, 045, 046; plan claims C1, C5, C7, C9, C13, C21, C22,
C24, C26.

## Missed findings

### STEAMHOST-V-001 (medium): an invalid theme update journal stops WSGM's session from starting, and keeps stopping it

- **Where:** `Core/Themes/ThemeInstaller.cs:181-208` (`Recover`), `Core/Themes/ThemeLoader.cs:64-74`,
  `Shell/ThemeService.cs:764-778`, `Shell/ShellSession.cs:730-736`.
- **Problem:** `Recover` throws `InvalidDataException` when the journal:
  - is over 128 KiB;
  - deserializes to null;
  - has a bad id;
  - names more than 256 entries (the STEAMHOST-031 cap);
  - is inconsistent.

  `InvalidDataException` derives from `SystemException`, not `IOException`, and `ThemeLoader.Load` catches only
  `IOException`, `UnauthorizedAccessException` and `JsonException`. The exception therefore escapes `ThemeService.Start()`,
  which runs synchronously on the UI thread inside `StartSessionServices` (`ShellSession.cs:736`). With no per-service
  isolation (U05-LFB-003), session startup fails. The marker file persists, so every later start fails the same way.

  The install path has a second escape. Inside `Unpack`'s catch, `Recover(root)` (`ThemeInstaller.cs:164-167`) can throw
  the same unwrapped exception. That escapes `ThemeService.StartWorkAsync`'s filter (`ThemeService.cs:949-960`), so `_busy`
  stays true and every theme operation answers "Another theme operation is still running" until restart.
- **Recommendation:**
  - Treat any recovery failure in `Load` as the existing "Theme update recovery remains pending" load error. Catch
    `InvalidDataException` too, with no catch-all.
  - Wrap the inner `Recover` call in `Unpack`'s catch.
  - Drop the 256-name cap and keep the 128 KiB byte bound (STEAMHOST-031).
  - Add a fixture test: an over-limit or garbage journal leaves `Load` usable with one load error.

### STEAMHOST-V-002 (low): content work slots can stick busy forever

- **Where:** `Shell/ThemeService.cs:926-983`, `Shell/AnimationService.cs:1007-1050`.
- **Problem:** both `StartWorkAsync` bodies catch a closed list of exception types and clear `_busy` only after that list.
  Any other exception (V-001's `InvalidDataException`, or an unexpected `ArgumentException` from a path) escapes the
  `Task.Run`, goes unobserved, and leaves `_busy == true`. The page then refuses all further work for the session.
- **Recommendation:** fold into STEAMHOST-029's shared work slot. Clear `busy` in a `finally`, and report any exception text
  as the slot's error. This is not a retry, only correct bookkeeping.

### STEAMHOST-V-003 (low): arbitrary 768 KiB size cap on the generated Steam UI asset

- **Where:** `eng/build-steam-assets.mjs:128-134`, `:259-264`.
- **Problem:** `maximumAssetBytes` is self-described as "a sanity bound ... not a limit anything downstream imposes", and it
  has already been raised twice (256 KiB, then 512 KiB, then 768 KiB). The asset is 645,950 bytes today, 84% of the cap.
  B7 (host publication data) and B9 (download sort moved into TS) add JS. This violates the no-arbitrary-limits rule and
  will fail the gate on a correct change.
- **Recommendation:** delete the size bound. Keep the non-empty, UTF-8 and no-BOM checks and the single-reviewed-file
  check.

### STEAMHOST-V-004 (low): 64 MB cap on importing the user's own boot movie

- **Where:** `Core/Animations/AnimationLibrary.cs:179-182` (reusing `AnimationRepoClient.MaximumMovieBytes`,
  `AnimationRepoClient.cs:29`).
- **Problem:** a local `.webm` over 64 MB is refused with "larger than the 64 MB safety limit". The import is a
  `File.Copy`, with no buffering and no third-party input, so the cap protects nothing and drops user content. The
  repository download uses the same constant through `BoundedHttp.CopyAsync` (`AnimationRepoClient.cs:167`), which streams
  too.
- **Recommendation:** remove the local import cap. For the download, leave the bound as a maintainer decision: it is a
  byte bound on third-party input, consistent with the ThemeStoreClient blob bound the reviewer accepted. Do not let the
  local path share it.

### STEAMHOST-V-005 (low-medium): Steam's startup-movie set-aside is not retried when Steam's settings store is still loading

- **Where:** `Shell/AnimationService.cs:855-887`; toolkit `Client/SteamStartupMovie.cs` (`ReadChoice` answers
  `ok:false, "Steam has not loaded its settings yet."`).
- **Problem:** the attempt runs at the transport's ready edge (Big Picture window visible). Steam's stores can lag that
  edge, which is why `LibraryTabManager.LibraryReadyProbe` waits up to 60 s for them. A reachable refusal returns `true`
  ("done") from `ReconcileSteamChoiceAsync`, because only `!Reachable` returns false. The set-aside is then not attempted
  again until the next Steam start or choice change. The user's Steam Startup Movie choice keeps overriding WSGM's movie,
  and the Animations page shows the error.
- **Recommendation:** no new retry machinery. Make the toolkit script wait for `settingsStore.clientSettings` in-script with
  a budget, exactly as `LibraryReadyProbe` waits for its stores (toolkit T02). Pair it with STEAMHOST-016.

### STEAMHOST-V-006 (low): the storage bridge reads UI-owned collections off the UI thread

- **Where:** `Shell/SteamStorageBridge.cs:168`, `:241`, `:298-299`, `:600`. Writers: `SdFormatManager.Refresh` posts
  `Apply` to the dispatcher (`SdFormatManager.cs:187`), and `RemovableDriveManager` reconciles on its own cadence.
- **Problem:** `_drives.Drives` and `_formats.Targets` are observable collections mutated on the UI thread and enumerated
  from the publication loop and command threads. An enumeration that overlaps a mutation can throw. On the publication path
  that quarantines the storage module for the session (`SteamUiModuleRuntime.FailModule`).
- **Recommendation:** fix together with STEAMHOST-007. The bridge projects an immutable snapshot on the UI thread when
  either collection changes, and both `ReadState` and command resolution read the last snapshot. That is one owner thread
  and no lock.

### STEAMHOST-V-007 (low): host construction subscribes session-owned events before its throwable steps

- **Where:** `Shell/SteamUiSessionHost.cs:285`, `:308-313`, `:317` (subscriptions), then `:321-326` (asset load and hash
  check, which can throw) and `:340` (runtime assigned).
- **Problem:** if construction throws after `:317`, the session-owned `NativeQamBrightnessService` keeps a
  `QueueStatePublication` handler on a half-built host whose `_runtime` is null. The network service is also already
  subscribed to the radio manager. Today the throw ends the session (U05-LFB-003), so this is latent. B3 step 6
  ("contain a host construction failure") would turn it into a live `NullReferenceException` on every brightness change.
- **Recommendation:** B3 builds the asset, bridge and runtime before any external subscription, or the composition root
  subscribes after construction succeeds.

### STEAMHOST-V-008 (low): the slider's ceiling fallback reaches the OSD as reported wattage

- **Where:** `Shell/NativeQamSemanticServices.cs:902-905` (`observed = valid(observed) ?? desired ?? maximum`);
  `Shell/ShellSession.Performance.cs:183-190` (`tdp.ObservedWatts ?? tdp.DesiredWatts`).
- **Problem:** `observed` is never null while the projection is available. On a device without readback (the Ally family)
  and with no desired value, the RTSS OSD shows the descriptor maximum as the current TDP. That value is fabricated, not
  observed and not written. It is not a readback gate, so the rule is not violated, but it is a wrong display.
- **Recommendation:** keep the ceiling fallback only in the slider mapping (`ToRange`). The projection record handed to the
  OSD keeps raw observed/desired. This lands naturally in B4 when `NativeQamTdpState` moves.

### STEAMHOST-V-009 (low): own heuristic of a 200 W ceiling on power-limit descriptors

- **Where:** `Shell/NativeQamSemanticServices.cs:891-892` (`maximum > 200` makes the descriptor incompatible).
- **Problem:** a device package's descriptor is the authority for its range, and HC mirrors the device's own TDP limits.
  A package publishing a maximum above 200 W loses the TDP slider and, through `Current`, the AutoTDP switch, with an
  "incompatible" status. This is an unexplained plausibility cap, not a shape check.
- **Recommendation:** keep the shape checks (integer, watt, writable, min < max, step ≥ 1) and drop the 200 W constant.

### STEAMHOST-V-010 (nit): Game Mode entry applies the Steam surfaces twice, once outside the master gate

- **Where:** `Shell/ShellSession.cs:1142-1154`.
- **Problem:** `ReleaseSteamUiBigPictureHold()` starts the gated restore, which applies everything. The handler then applies
  `ApplyNetworkIndicator` and `ApplySteamUiSurfacePreferences` again synchronously, outside `_cefMasterGate`. That ungated
  second path is one of the overwrite sources in the corrected STEAMHOST-002.
- **Recommendation:** removed by B2/B5. The coordinator applies config once under its gate.

## Batch problems

1. **B5 does not build green on its own.** It deletes the static `SteamUiReadiness`, but:
   - `LibraryTabManager.cs:224` (a B6 file) calls `SteamUiReadiness.RunWhenReadyAsync`;
   - `AnimationService.cs:1069` crefs it, which gives CS1574 under `GenerateDocumentationFile`, and Release builds must be
     warning-free.

   Neither file is in B5's list. Either B5 also edits both (passing the coordinator's `RunWhenReadyAsync` into the static
   tab boot sync as a delegate), or B6's instance conversion moves ahead of B5 with readiness injected as a delegate. No
   static forwarder (no compatibility layers). The same applies to the `KickTabBootSync` → `LibraryTabService.RequestBootSync`
   mapping, whose target only exists in B6.
2. **B5 step 3 offers an unsafe option.** "Order host disposal before device/GPU retirement" would put CEF retraction
   round trips ahead of the safety-critical device cleanup (`Shutdown.cs:108-111`) inside one deadline, which risks the
   never-strand rule (controller release and the HidHide cloak off). Only the synchronous admission close
   (STEAMHOST-001 correction) moves to T0. Disposal and retraction stay after device cleanup.
3. **B2 can silently drop edge side effects.** Collapsing the eight `Apply*` into a stored snapshot plus a queued pass must
   keep the synchronous on-edge effects:
   - `Apply(false)`: `CancelAllInflight` and `ReleasePerformanceObservation`. If deferred to the pass, rows keep answering
     commands after the switch is off.
   - `ApplyScreensaverTimeouts(false)`: `ForgetSteam`.
   - `ApplyNetworkIndicator(false)` while QAM is off: `PostStopScanning`.
   - `ApplyHostSteamUi`: the sound integration status text.
   - `ApplyHomeCarousel` with only the preference changed: publication without retraction.
   - `ApplyGlyphs`: the delivery decision and its log line.

   B2's test list covers the carousel case only. Add the others, plus "commands are refused immediately after
   Apply(QAM off)".
4. **B1 is not "no API change".**
   - Making `PerformanceServiceNativeQamAdapter.Profiles` required and injecting drive/format ports into
     `SteamStorageBridge` change constructor and init APIs and their test call sites (`SteamUiSessionHostTests.cs:219-220`,
     `SteamStorageBridgeTests.cs:39`).
   - Step 1 needs the STEAMHOST-008 correction, so cached projections do not go stale.
   - Step 2 needs the STEAMHOST-002 correction: the global switch and `DisableAsync` too.
5. **B3 step 6** needs V-007, or containment creates a crash path.
6. **B7 step 3** (dynamic plugin module registration) adds toolkit mechanism (module-set mutation, patch unregistration, a
   mutable bridge allowlist) for a latent defect. Per the simplify rule, make it conditional on the maintainer's answer to
   open question 2 and default to read-at-start.
7. **B8** may move `ThemeService.Start` off the UI thread. `AnimationService.Start` must stay synchronous before Steam
   starts (`ShellSession.cs:748-750`: "Started before Steam so a shuffle on start is what Steam reads"). The STEAMHOST-009
   fix (write outside the lock) must not defer the start-time override write. B8 should also carry V-001 and V-002 (same
   files) and V-004.
8. **B4** must keep observed/desired watts on the replacement projection for the OSD (STEAMHOST-021 correction), and
   should carry V-008 and V-009.
9. **B9 / B7** add JS to the single asset. Land V-003 (remove the asset size cap) first, or the gate fails on correct
   changes.
