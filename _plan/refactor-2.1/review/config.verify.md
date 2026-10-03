# Adversarial verification: config domain review (config.md)

Verifier: Claude, read-only. Baseline `master` 1329813f. I re-read the following in full: ConfigStore.cs, ShellSession.Config.cs,
ProfileService.cs, ProfileFanOut.cs, Log.cs, AudioProfileService.cs, GameModeReturnRecovery.cs, DisplayProfiles.cs,
ArtworkStateStore.cs, QuickAccessFolds.cs, AtomicFile.cs, BootManifestWriter.cs and ProfileEdits.cs (1-290). I read the
relevant ranges of Program.cs, ShellSession{,.Shutdown,.Modes,.Actions}.cs, SettingsViewModel{,.Save,.System}.cs,
Installer.cs, OtherManagers.cs, SteamAutostartService.cs, ShellRegistration.cs, RegistryValueSnapshot.cs,
ThemeInstaller.cs, ThemeLoader.cs, ThemeService.cs, AnimationService.cs, OverlayController.cs, PluginSettingsCoordinator.cs,
HidHideOwnership.cs, the WDC CoreAudio.Watch.cs and DisplayLayouts.cs, and the tests ConfigurationTests, LogLevelTests,
ProfileFanOutTests and PerformanceBuilders.

## Refuted

- **CONFIG-027** (late audio-watch callback hits a disposed semaphore). WDC `EndpointCallback.Raise` wraps every
  callback in `try { _onEvent?.Invoke(change); } catch { }` (external/windows-device-control/src/WindowsDeviceControl/CoreAudio.Watch.cs:328-339),
  and `Dispose` detaches the delegate first (236-237). A late `arrived.Release()` on a disposed `SemaphoreSlim` is
  therefore swallowed on the native thread. Nothing crashes and nothing changes behaviour. Not a defect. Drop it from B5
  and from the W01 dependency note.
- **CONFIG-042** (double normalization in SettingsViewModel). This is deliberate and commented: "Normalize so an
  injected bare AppConfig gets the same non-null nested sections ... the load path guarantees"
  (SettingsViewModel.cs:70-72). `FromLoadedConfig` and the tests inject configs that never went through Load. Normalize
  is idempotent apart from minting a tab id for a blank id, which a loaded config no longer has. No defect.

## Corrected

- **CONFIG-002**: confirmed mechanism, but the severity is **low**, not medium. A stale publication needs the posted
  T1 apply to run before the T2 write's watcher event bumps `_configReloadGeneration`
  (ShellSession.Config.cs:108-112, 168). Watcher delivery takes milliseconds, so the window is narrow. The T2 event
  always follows and re-applies the newer store after the 500 ms debounce, so the condition heals itself. The fix
  (serialize `ReloadAsync` on `_writeGate`, re-read) is still right.
- **CONFIG-004**: confirmed, with one mechanism correction. WSGM's own replace cannot cause the sharing violation the
  finding names: `Load` takes the same mutex as every writer (ConfigStore.cs:124, 1161) and degrades only after the 2 s
  timeout. A real transient comes from a non-WSGM opener (AV, an editor) or from a degraded read. The consumer list also
  cites SettingsWindow.axaml.cs:138, which is a comment explaining that the window avoids a load. The real
  lenient-load consumers on the Settings side are SettingsViewModel.cs:18, SettingsViewModel.System.cs:187/219/228 and
  SettingsWindowServices.cs:25. Add these unlisted lenient consumers too: Program.cs:596 (ExportSetupAnswers),
  OtherManagers.cs:372, SteamAutostartService.cs:92, LaunchWrapperStore.cs:20 and ShellRegistration.cs:115. The last
  one is deliberately fail-open; see Batch problem 5.
- **CONFIG-006**: the list is still incomplete. `DevicePowerCustomValues.WindowsMode` (`DevicePowerMode`,
  Core/DevicePowerPresetReference.cs:28) sits inside `Profiles.Global/Games[].Values.AcPowerPreset/BatteryPowerPreset.CustomValues`
  and `RepairProfileValuesJson` never repairs it (ConfigStore.cs:310-323). An unknown name there quarantines the whole
  file. The path is Core/Library/GameLibraryConfig.cs:51/60, not GameLibraryConfig.cs.
- **CONFIG-008**: the severity is **low**. Every copy is of the same unchanged file until the user edits it, and Mutate
  refuses to overwrite a corrupt config.json (ConfigStore.cs:147-151). The original therefore stays in place, and
  pruning removes only identical copies. Real loss needs repeated hand edits of a broken file. The dedupe-by-content
  fix is fine.
- **CONFIG-011**: the counts are wrong. `Log.Directory` has 27 code sites in 19 files outside Log.cs, plus one doc
  reference at SteamInputShim.cs:91. The cited list itself names 19 files; it is the summary "22 sites in 17 files"
  that is off. SettingsViewModel.System.cs has four sites (271, 279, 280, 292) and UnelevatedLauncher.cs has two.
- **CONFIG-012**: the problem is confirmed but the recommendation is unsafe; see Batch problem 4. Keep the reader-side
  mutex. Remove only the nesting: one writer `Transaction` scope replaces `AcquireLock` + `LoadForMutation` + `Save` +
  `Mutate`. That alone deletes the depth counter, out-of-order rules and degraded-scope nesting check.
- **CONFIG-019**: the severity is **low**. The learning write goes through the atomic replace and serialized mutex, so
  a write during shutdown is a valid write of a learned executable name. No recovery state is at risk. Tracking it in
  the B3 admission close is still right.
- **CONFIG-022**: downgrade to **nit**, and correct C3. The fan-curve path already uses `ProfileEdits`
  (`ProfileEdits.RemoveFanCurveReferences`, SettingsViewModel.Save.cs:455-460). The Global controller target is one
  field merged onto the fresh strict load under the lock (406-422), which is exactly the semantics a ProfileEdits call
  would have. No behaviour is wrong.
- **CONFIG-024**: one sub-claim does not hold. "WaitAsync abandons running work that ... can still mutate config"
  overstates it, because the abandoned work checks `cancellationToken.ThrowIfCancellationRequested()` before its only
  `Mutate` (GameModeReturnRecovery.cs:91-101). It mutates only when the token fires after line 91. The rest is accurate:
  the abandoned work keeps the static gate and can still run `DisplayLayouts.Apply`, which cannot be cancelled once
  started (70), after the caller has moved on.
- **CONFIG-028**: only half of it holds. The second `DisplayLayouts.Observe()` (DisplayProfiles.cs:35-37) is a
  stability check: the primary must be the same target before and after `ReadPrimaryMode`. It is not waste. The stale
  "display-profile path above" doc (189-192) and the misnomer stand.
- **CONFIG-032**: the `Log.Observe` half is refuted. `TaskFaults.ObserveFaults` only swallows faults
  (TaskFaults.cs:14-19). `Log.Observe` logs them as Warn or Error with the operation name (Log.cs:171-196) at 14 call
  sites, for example "Device cycle config apply" at ShellSession.Config.cs:222. They are not duplicates. The
  `SetMinimumLevel` wrapper nit stands. See Batch problem 7.
- **CONFIG-037**: confirmed for artwork.json, but the severity is **low**. These are user preferences, not recovery
  state. For quick-access-folds.json a reset loses only which sections are open; a full five-state `StateFile` with a
  write-refusal latch is more mechanism than that file needs (simplify rule). Apply the refusal to artwork.json and
  library-import.json. For folds, just do not write after a failed read.
- **CONFIG-038**: the consequence is worse than stated. Raise it to **medium**; see CONFIG-V-002. `Recover` throws
  `InvalidDataException`, which `ThemeLoader.Load` does not catch (ThemeLoader.cs:66-73 catches only
  IOException/UnauthorizedAccessException/JsonException). The exception escapes `ThemeService.Start`
  (ShellSession.cs:736) and fails session startup.
- **CONFIG-040**: SettingsWindow.axaml.cs:138 is a comment, not config IO. ShellSession.Modes.cs:824 writes the session
  field `_pendingReturnLayout`, not the shared AppConfig. The real instances are ShellSession.Actions.cs:114-115, which
  mutates `_config` from a worker, and the UI-thread loads at SettingsViewModel.cs:18,
  SettingsViewModel.System.cs:219 and SettingsWindowServices.cs:25. Each of those can wait up to the 2 s mutex timeout
  on the UI thread.
- **Plan check C17**: today's code does not guess a registry kind. An undefined `RegistryValueKind` number already
  loads, and is bounded at the only point of use by `normalizeKind` (String/ExpandString for Shell, DWord/QWord for
  StartupToGamingHome; ShellRegistration.cs:25-26, 44-45; RegistryValueSnapshot.cs:108). Making it `Corrupt` (B1 step
  7) is a new failure mode; see Batch problem 2.
- **Plan check C3**: the evidence at SettingsViewModel.Save.cs:455-462 is a `ProfileEdits` call, not a direct write.

## Confirmed (ids only)

CONFIG-001, CONFIG-003, CONFIG-005, CONFIG-007, CONFIG-009, CONFIG-010, CONFIG-013, CONFIG-014, CONFIG-015, CONFIG-016,
CONFIG-017, CONFIG-018, CONFIG-020, CONFIG-021, CONFIG-023, CONFIG-025, CONFIG-026, CONFIG-029, CONFIG-030, CONFIG-031,
CONFIG-033, CONFIG-034, CONFIG-035, CONFIG-036, CONFIG-039, CONFIG-041, CONFIG-043, CONFIG-044, CONFIG-045, CONFIG-046.
Plan claims C1, C2, C4-C16, C18-C28 confirmed as the reviewer stated, apart from the C3 and C17 corrections above.

## Missed findings

**CONFIG-V-001 (low): the strict config path's exception contract is wrong, and callers' catch filters miss it.**
ConfigStore.cs:1193 documents `InvalidDataException` for an unparseable file. `LoadForMutation` -> `DeserializeConfig`
actually throws `JsonException`, from `JsonNode.Parse` or the retry deserialize (ConfigStore.cs:176-231), or
`InvalidOperationException` when the root is an array. Callers that filter on that contract let the exception escape:
`ThemeService.ChangeConfig` (ThemeService.cs:749-751) and `AnimationService` (AnimationService.cs:981-982) catch
IOException/UnauthorizedAccess/InvalidOperation/Timeout but not `JsonException`. They go through `CommitWsgmSetting`
-> `ConfigStore.Mutate` (ShellSession.cs:734, 740, 755). With a corrupt config.json, a theme, sound or animation change
from Steam throws out of the command handler instead of returning `Refuse`. Recommendation: the B2b
`ConfigUnavailableException` must be the single strict-path failure type, covering parse, unreadable, schema and mutex
timeout. Update these filters in the same batch.

**CONFIG-V-002 (medium): an invalid theme update journal makes every shell session start fail.** `ThemeInstaller.Recover`
throws `InvalidDataException` for a journal over 128 KiB, with more than 256 names, or with an invalid id
(ThemeInstaller.cs:189-201). `ThemeLoader.Load` catches only IOException/UnauthorizedAccessException/JsonException
(ThemeLoader.cs:66-73). `InvalidDataException` derives from `SystemException`, not `IOException`. The exception
escapes `ThemeService.Start` (ThemeService.cs:778) and `StartSessionServices` (ShellSession.cs:736), faults
`StartAsync` and reaches `ObserveSessionStartupAsync`, which calls `desktop.Shutdown(1)` (App.axaml.cs:91-95). The
marker survives, so this repeats at every start. The install itself leaves such a marker: after commit `Unpack` calls
`Recover` (159), which throws for more than 256 names. The catch at 163-167 calls `Recover` again, so that throw
replaces the `ThemeStoreException`. The trigger is rare, since theme zips normally have one root folder, but the
effect is a WSGM shell that cannot start. Recommendation: remove the caps (CONFIG-038), use a source-generated context
for the journal, and add `InvalidDataException` to the loader's catch so a bad journal degrades to "theme recovery
pending" as designed. Add ThemeLoader.cs and a startup test to B5.

**CONFIG-V-003 (low): Quick Access pin saves can persist out of order.** `OverlayController` updates the in-memory pins
and then starts an unordered `Task.Run(() => ConfigStore.Mutate(config => config.QuickAccessPins = [.. snapshot]))`
for each change (OverlayController.cs:977-996). The tasks race for the mutex, so two quick changes can persist the
older full snapshot last. The shell's own watcher reload then pushes that stale list back into the overlay
(ShellSession.Config.cs:152 -> OverlayController.cs:410), and the user's last pin change visibly reverts.
`CommonPluginOverlaySource.MutatePinsAsync` is safe because it applies a delta to the fresh load. Recommendation:
persist pins as a delta inside the mutation, as widget pins already do, or serialize the saves through one queue. No
new state is needed.

**CONFIG-V-004 (low, verify once): the config and log-rotation mutexes are created without an ACL in a mixed-elevation
process set.** `new Mutex(false, @"Local\WSGM.Config")` (ConfigStore.cs:1368) and `Local\WSGM.LogRotate` (Log.cs:268)
use the creator's default DACL. The shell may run elevated (`ElevationPolicy`, BootManifestWriter.cs:30), as do the
elevated one-shots. While an elevated process holds the object, an unelevated WSGM process can get
`UnauthorizedAccessException` on open. ConfigStore then throws for writes (1391-1397), so the save fails, and degrades
reads to lock-free. The window is short because handles live only for one scope. Recommendation: when
`UserDataContext` takes over mutex naming, create the mutex with a `MutexAcl` that grants the interactive user's SID.
Confirm once with an elevated holder and an unelevated opener before changing anything.

**CONFIG-V-005 (nit): ArtworkStateStore can throw NullReferenceException outside its try on explicit JSON nulls.**
`link.ProviderId.Length`, `link.GameId.Length` and `filter.Tab.Length` run after the try/catch (ArtworkStateStore.cs:100-110).
`"providerId": null` deserializes to null and throws out of `FindGame`/`SaveGame`. Fold this into the CONFIG-037 /
StateFile change by using null-tolerant shape checks.

**CONFIG-V-006 (nit): a late watcher event recreates the debounce timer after shutdown.** `Debounce` does
`_configDebounce ??= new Timer(...)` with no disposed check (ShellSession.Config.cs:164-172). Shutdown disposes the
timer before the watcher (ShellSession.Shutdown.cs:657-664), so an event in between recreates a timer that is never
disposed. That timer then runs one `ConfigStore.Load`, which may also quarantine a copy. The apply itself is blocked by
`_disposed`. Recommendation: check `_disposed` in `Debounce`, or dispose the watcher first. This falls out of the H02
reloader if it owns both.

**CONFIG-V-007 (nit): ProfileFanOut's constructor doc is wrong.** "Subscribes to the service and applies its current
snapshot once" (ProfileFanOut.cs:36) is false. The constructor only subscribes, and the caller queues the first
snapshot (ShellSession.Performance.cs:361). Fix the doc, or move the initial queue into the owner, in B4.

**CONFIG-V-008 (low): the plan's EDID-identity migration has no config batch.** refactor-plan.md (Specific migrations)
requires "Fixing EDID decoding does not erase saved monitor paths ... existing EDID fields are preserved". The
`DisplayTargetIdentity` values it protects live in config.json: `GameModeLaunch.KnownDisplays[].Target`, every
`DisplayLayout.Outputs[].Target`, `WaitForDisplay` and `GameModeLaunchRecovery.PendingReturnLayout`
(GameModeLaunchConfiguration.cs:52, 132-147, 176). The config review does not mention it, and neither B3 nor B7 has a
fixture for it. Recommendation: B7, or the W02 batch that changes decoding, carries a config fixture. It loads a 2.0
config with stored targets and asserts identity equality and layout matching after the decoder change.

## Batch problems

1. **B1, generic enum repair breaks [Flags] values.** `LaunchWrapperMode` is `[Flags]` (LaunchWrapperCommand.cs:11-29),
   with unnamed combinations such as `InputLease | InputLeaseInject` = 6. Today the name repair uses `Enum.TryParse`,
   which accepts comma lists, and no `Definite` is applied to it. A metadata walker using `Enum.IsDefined` would reset
   a valid combined mode to `None` and silently drop a game's wrapper behaviour. The walker must accept a flags value
   whose bits are all defined, and B1 needs a fixture for one.
2. **B1 step 7 adds a failure mode.** An undefined recovery-enum number makes today's file load and is bounded at use
   (C17 correction). Under B1 it would make the whole config Corrupt: Settings saves, profile writes and recovery
   clears all refuse. That is more mechanism without a defect. Keep the use-time normalization for numbers. Unknown
   names already quarantine today, so leave that behaviour. Drop step 7, or limit it to `SteamAutostartRecord` after
   checking how its restore uses Kind/Scope.
3. **B1, the tolerant converter must preserve the wire format of SDK enums.** CapabilityValueKind, DisplayKey and the
   others carry type-level `[JsonConverter(typeof(JsonStringEnumConverter<T>))]` (WSGM.Device.Sdk/Capabilities/*).
   Options-level converters on the context take precedence over type attributes, so the new converter also writes
   cached plugin declarations. Add byte-identical round-trip fixtures for a cached `PluginSettingsManifest` and a
   profile `CapabilityValue`.
4. **B2b, lock-free share-delete reads remove a guarantee in use today.**
   - (a) The Settings transaction writes config.json, promotes splash images, can write config.json a second time
     (`RestoreSlotsThatFailedToPromote`) and writes boot.json, all inside the lock (SettingsViewModel.Save.cs:530-561).
     Today the shell's reload `Load` waits on that lock, which ShellSession.Config.cs:100-103 states explicitly, so it
     never sees the intermediate document. Lock-free reads would expose it and apply it.
   - (b) WSGM's own readers would overlap writers' `File.Move(temp, path, overwrite)` (AtomicFile.cs:73). Whether a
     rename over an open FILE_SHARE_DELETE target succeeds depends on the rename semantics. Under the no-retry rule,
     any failure surfaces as a failed save.

   Keep the reader mutex and delete only the nesting (CONFIG-012 correction). This also keeps B2b smaller.
5. **B2b step 6, "recovery readers report Unreadable as failure", must not reach fail-open paths.**
   `ShellRegistration.Uninstall` deliberately falls back to defaults so that WSGM's Shell value is deleted
   (`RegistryValueSnapshot.Restore` -> `DeleteValue` when nothing was captured, RegistryValueSnapshot.cs:103-113;
   ShellRegistration.cs:104-131). It runs from restore-shell, unregister-shell and setup. Restore-shell's boot.json
   disarm (Program.cs:259-268) and `DisarmCrashLoop` (Program.cs:454-470) are fail-open in the same way. If any of
   these refuse on Unreadable, the user is stranded in the WSGM shell, against CLAUDE.md "Explorer recovery ... never
   depends on ... a valid user configuration". B2b must list fail-open and fail-closed callers separately. Fail-open:
   shell unregistration and the boot disarm. Fail-closed: Steam autostart, other managers, UAC, lock-on-wake and
   display-scale restore, which report failure.
6. **B5, "RestoreAsync never clears", silently drops clearing at call sites that rely on it.** Today
   `RestorePendingAsync` clears the record whenever Explorer is up. The Game Mode entry path relies on that
   (GameModeEntryTransaction.cs:143). For a non-Custom launch without `GameAudio`, `PersistPendingReturnAsync` never
   runs (186-190), so nothing else clears the record. Restore-shell (Program.cs:290) and panic (Program.cs:769) rely on
   it too. B5 must add `ClearIfUnchanged` at each of these and test it. Otherwise a stale record re-applies an old
   desktop layout and audio at later starts.
7. **B6, the mapping `Log.Observe` -> `TaskFaults.ObserveFaults` drops failure logging** at 14 call sites (CONFIG-032
   correction). Keep `Log.Observe`, or move it with its logging intact.
8. **B5 file list.** It needs ThemeLoader.cs (CONFIG-V-002), and its "theme journal round trip with more than 256
   names" test should also assert that session start survives an invalid journal.
9. **B2b and B4 both edit `MutateProfilesAsync` / ShellSession.Config.cs.** B2b step 3 fixes the adapter and B4 creates
   `ConfigProfileStore` from it. Give the adapter to exactly one batch, B4 after B2b, so two batches do not rewrite the
   same lines.
10. **B2a depends on B1 without need.** B2a is pure plumbing (context, instance store, Log root) and builds without the
    B1 repair rewrite. Ordering B2a first reduces B1's consumer churn. Not blocking.
11. **B2b and the `ConfigUnavailableException` consumers.** B2b's file list must include ThemeService.cs,
    AnimationService.cs and SoundPackService (CONFIG-V-001), because their catch filters decide whether a refused
    strict write surfaces as `Refuse` or as an escaped exception.
12. **Missing migration coverage.** No batch carries the EDID-identity config fixture (CONFIG-V-008).
