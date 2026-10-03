# SteamUiToolkit C# review: adversarial verification (toolkitcs.verify)

Verifies `_plan/refactor-2.1/review/toolkitcs.md` against toolkit `main` `388dd1b` (the same head the reviewer used) and parent
`master` `1329813f`. Read-only: no build, no tests, no live Steam. I re-read every file behind a critical, high or
medium finding, plus the bridge, manager, transport, connection, discovery, runtime, module set, builder, extension
host, client scripts, startup movie, collections, install folders, running apps, library data, current page,
storage, file picker, gate, page and QAM row patches. I traced the consumers in `src/WSGM`, `src/WSGM.Plugin.Sdk` and
`tests/WSGM.Tests`.

General note on evidence: the source line citations mostly hold, but many **test-file** citations point past the end
of the file. The suite is 6,866 lines in 41 files, not the "~7.7k in 43" the review states. Examples:
`SteamStorageTests.cs:445-454` (file has 154 lines), `SteamPowerMenuTests.cs:216-231` (54),
`SteamGatePatchContractTests.cs:171-203` (54), `SteamPagePatchTests.cs:649-656` (56), `SteamPageTests.cs:680-717`
(127), `SteamWindowSurfaceTests.cs:505-556` (255), `SteamQuickAccessRowPatchTests.cs:327-350` (189),
`SteamPanelFoldsTests.cs:683-738` (75), `SteamStartupMovieTests.cs:583-600` (57). A few source citations are wrong
too: `SteamStartupMovie.cs:294-296,336-338` (165 lines), `SteamSharedContext.cs:245-250` (18 lines) and
`SteamBrightnessSurface.cs:459-469` (121 lines). Where the substance holds, the finding is listed under Corrected with
the right location.

---

## Refuted

- **TOOLKITCS-017** (low; checked because it backs plan claim C23). The review says that when an oversized binding
  is forwarded as `{}`, nothing logs a line naming the refused request, and it recommends logging the drop with the
  method name at the connection. The connection already does exactly that: `SteamUiCdpConnection.cs:500-503` calls
  `DropMalformed($"{method} parameters exceeded the byte limit")`, which warns (`:418-424`). The only gap is that the
  warning is throttled to three per connection. The recommendation is already implemented, so B2's "oversized
  binding drop logged (017)" step has nothing to do.

## Corrected

- **TOOLKITCS-015** (high → **low**). The code path is real. When `returned==0` and exactly one entry appears, the
  script adopts it and rewrites its fields (`Client/SteamApps.cs:358-374`). The trigger is narrow, though. Steam's
  `AddShortcut` normally returns the id, and any non-zero return that differs from the gained entry is refused by
  `(returned&&gained[0]!==returned)` (`:364`). So the overwrite needs two things together: a client build whose
  `AddShortcut` returns nothing, and an add from outside WSGM inside the same 100 ms polling window. WSGM's own adds
  are serialized by `SteamApps.Writes` (`:393`). The recommended guard is still right; the severity is not.
- **TOOLKITCS-004** (medium → **low**; wrong location). The readiness check is `Surfaces/SteamSharedContext.cs:12-17`,
  not `:245-250`. Two things reduce the impact. Every successful evaluation on the channel restores `Ready`
  (`PersistentSteamUiTransport.cs:203-208`), and patch probes and publications run continuously. Probes and client
  scripts also wrap their bodies in `try/catch` and return `{error}` (`SteamUiProbeJs.Close`, `SteamClientScript.Read`),
  so `exceptionDetails` answers are rare. The defect holds: an answered JS error sets `Incompatible` and does not reset
  `ConsecutiveTimeouts` (`:227-237`). Its window is short, however.
- **TOOLKITCS-012** (medium, scope narrowed). The bridge's teardown is already bounded: `SteamUiBridge.cs:313`
  allows 2 s for removal and `:337` allows 1 s for the pump. The transport is bounded too:
  `SteamUiCdpConnection.DisposeAsync` waits 1 s, and the event pumps get another 1 s
  (`PersistentSteamUiTransport.cs:323-332`). The real gaps are these:
  - `SteamUiModuleRuntime.DisposeAsync` waits without any bound for the publication loop, including a
    non-cancellable `publication.Read()`, and for every in-flight request task (`SteamUiModuleRuntime.cs:92-113`).
  - The manager removes patches one at a time, up to 8 s each, after a scheduler wait of up to 30 s
    (`SteamUiPatchManager.cs:272-300`). With ~35 registered patches and a hung renderer that is minutes. When the
    30 s wait gives up, it also leaves every patch injected (`:277-282`).

  The fix belongs in the runtime and the manager only. Adding `ShutdownAsync(CancellationToken)` to the bridge and
  the transport as well is extra API the defect does not need.
- **TOOLKITCS-016** (medium; wrong locations for the startup-movie part). The substance holds:
  - `set()` writes three settings in sequence (`Client/SteamStartupMovie.cs:65-67`). If a later write throws, the
    reply is `{ok:false,err}` without `choice` (`:107-109`), and `Parse` returns `Choice=null` (`:142-146`). The
    caller then loses the original choice after `startup_movie_id` has already been cleared.
  - Collections: `Client/SteamCollections.cs:109-110` creates and saves the collection, and a later throw drops its
    id.
  - Install folders: `Client/SteamInstallFolders.cs:205-209` swallows purge and label failures.

  Plan claim C28 carries the same wrong line numbers.
- **TOOLKITCS-055** (medium, scope wider than stated). The picker symptoms are real: `listPlaces` calls
  `DriveInfo.IsReady` synchronously, `ListFolder` cannot be cancelled once started, raw `ex.Message` is returned,
  `\\.\` passes, and Downloads is computed as `profile\Downloads` (`Surfaces/SteamFilePickerSurface.cs:63-103,
  109-161, 207-221`). The blocking, however, is a property of the module runtime, not of the picker. Any handler with
  a synchronous prefix stalls the bridge's only request pump; see TOOLKITCS-V-002.
- **TOOLKITCS-064** (medium; locations wrong). The substance holds. For example, the
  `Assert.DoesNotMatch(..., liveRoute)` against a test-local literal is at `SteamPageTests.cs:65`, not `:710-716`, and
  the storage probe test is substring-only at `SteamStorageTests.cs:29-36`. Most of the other test-file line ranges
  cited in 064, 065 and 069 do not exist (see the general note). Re-derive them before B7.
- **TOOLKITCS-032** (low, inaccurate premise). `ISteamUiPatch.Version` is not "`1` everywhere": WSGM's
  `SteamDownloadSortPatch.Version` returns `SteamDownloadSort.ScriptVersion` = 5 (`src/WSGM/Core/SteamDownloadSort.cs:30,
  304`). The value is only logged as `v5`, and the script embeds its own `dlSortVersion` (`:250`), so removing the
  member is still safe. The rationale needs correcting, and B4 must edit that WSGM file.
- **TOOLKITCS-013** (low). "No disposed check" is inaccurate: `SetRuntimeBindingAsync` → `LeaseAsync` →
  `SubscribeAsync` throws `ObjectDisposedException` (`PersistentSteamUiTransport.cs:143`).
- **Plan claim C3** ("Only `ISteamUiModule`/`SteamPage` cross the boundary"). This is incomplete. See
  TOOLKITCS-V-001: the toolkit assembly is host-owned for plugins, so everything reachable from `ISteamUiModule` is a
  runtime type-identity contract.
- **Refinement R4 / plan claim C21.** The review drops quarantine Reset because "WSGM recreates the host on
  recreation". That premise is false. `SteamUiSessionHost` is built once per `ShellSession`
  (`src/WSGM/Shell/ShellSession.cs:1042`) and disposed only at shutdown (`ShellSession.Shutdown.cs:356-369`). A single
  exception from a publication `Read()` therefore removes the surface for the whole WSGM session
  (`SteamUiModuleRuntime.cs:324-331` → `FailModule`). Dropping Reset keeps today's behaviour, which is acceptable
  under the simplify rule, but the stated justification must change to "same as today; reset happens on WSGM restart".
- **Refinement R8 / plan claim C34.** "The TypeScript picker renders in pages with the existing 'More' control" is
  false. `SteamUiAssets/Source/file-picker.ts` (206 lines) has no paging and no "More" control, and nothing in the
  assets defines one. Renderer paging would be new UI, which conflicts with the rule that the UI stays identical,
  unless the maintainer accepts it. The rest of R8 (dropping spool, worker caps, Busy/Capacity outcomes, clock
  injection and the policy type) is consistent with the simplify and no-arbitrary-limits rules, and it correctly
  stays an open question against the binding correction in `planning-corrections.md:13-23`.
- **API inventory (§4).** All 229 public types are counted correctly, but the "decision per public type" is not
  per type for roughly 150 surface types, which are lumped into "R". The inventory also does not mark the
  plugin-contract closure (V-001), so `ISteamUiPatch`, `SteamUiCommandResult`, `SteamUiModuleBuilder`,
  `SteamUiBridgeRequest`, `SteamGatePatch` and `SteamPagePatch`/`SteamPageProbe` show as ordinary R* changes.

## Confirmed

TOOLKITCS-001, TOOLKITCS-002, TOOLKITCS-003, TOOLKITCS-014, TOOLKITCS-018, TOOLKITCS-019, TOOLKITCS-020,
TOOLKITCS-025, TOOLKITCS-027, TOOLKITCS-036, TOOLKITCS-041, TOOLKITCS-044, TOOLKITCS-066, TOOLKITCS-007,
TOOLKITCS-026, TOOLKITCS-031, TOOLKITCS-033, TOOLKITCS-034, TOOLKITCS-037, TOOLKITCS-046, TOOLKITCS-047; plan claims
C1, C2, C4, C5, C6, C8, C9, C10, C13, C14 (R3), C15, C16, C17, C18, C19, C20, C22 (R6), C24, C25, C26, C27 (R7),
C29, C30 (R9), C31, C32, C33, C35, C36, C37 (R1, fold T01_01), C38, C39, C40, C41.

Notes on confirmed items:

- **001.** The TypeScript gate performs the identical invocation (`SteamUiAssets/Source/gates/storage.ts:196-207`),
  so fixing the C# probe alone removes nothing. The B2 dependency on the TS domain is mandatory, not optional.
- **036.** When the QAM counts move off the bridge, the rows that draw TDP controls must probe `tdpAvailability`,
  `tdpComponent` and `profileProjection` themselves. The default row probe checks only `performanceActions` plus
  common counts (`Surfaces/SteamQuickAccessRowPatch.cs:26-33,72-80`).
- **019.** The real defect is the frozen set. The observer is published at `Client/SteamRunningApps.cs:63`, before
  `RegisterForAppLifetimeNotifications` (`:64`), and later reads skip installation (`:59`). "Generation restarts at 1"
  is documented, intended behaviour (`:15-17`).

## Missed findings

### TOOLKITCS-V-001: medium. Toolkit public types reachable from `ISteamUiModule` are an unversioned plugin ABI

- **Where:** `src/WSGM/Shell/PluginPackageLoader.cs:172-176` makes `typeof(ISteamUiModule).Assembly` HostOwned, so
  every plugin load context shares the host's SteamUiToolkit. `src/WSGM.Plugin.Sdk/PluginSteamUi.cs:51,59` exposes
  `IReadOnlyList<ISteamUiModule> SteamUiModules` and `SteamPage`. The loader comment (`:200-205`) names the manifest
  `apiVersion` (`PluginApi.Version`, `PluginManifestReader.cs:87-89`) as the only compatibility gate.
- **Defect:** The closure reachable from a plugin module is part of the MIT plugin contract. It includes
  `ISteamUiPatch`, `SteamUiPatchContext`, `SteamUiPatchBounds`, `SteamUiPatchProbeResult`,
  `SteamUiPatchOperationResult`, `SteamUiStatePublication`, `SteamUiCommandHandler`/`Delegate`/`Result`,
  `SteamUiBridgeRequest`, `SteamUiModuleBuilder`, `SteamUiPayloadReader`, `SteamUiPayload`, `SteamUiPatchEvaluation`,
  `SteamUiProbeJs`, `SteamGatePatch`, `SteamPagePatch`/`SteamPageProbe`, `SteamUiBridgeIdentity`,
  `SteamUiModuleResolver` and `SteamCef.JsString`. Batches B3 to B5 change or remove members on several of these
  (Version/ResourceKey/Bounds, `Unique`, the gate constructor, `SteamPageProbe.Tokens`, `SteamUiCommandResult`
  validation). An out-of-tree plugin compiled against 0.1.0 would then fail with `MissingMethodException` or
  `TypeLoadException` at load or first call, instead of being refused cleanly by the manifest gate. The plan's
  "advance toolkit to 0.2.0" does nothing here: the assembly version is ignored by design (`:200-205`).
- **Recommendation:** Mark this closure as "plugin contract" in the inventory. Batch the contract-breaking member
  changes so they land in one parent commit (B4 + B5 contract parts) and bump `PluginApi.Version` in that same commit.
  Keep Plugin.Sdk docs in sync. No compatibility facade: the maintainer rule forbids one, and the version bump is
  the honest refusal path.

### TOOLKITCS-V-002: medium. Command handlers run synchronously on the bridge's single request pump, which blocks cancels

- **Where:** `SteamUiBridge.cs:776-827` invokes `RequestReceived` inline from one pump. `SteamUiModuleRuntime.cs:168`
  calls `RespondAsync(request)` inline, and that method runs the handler synchronously up to its first `await`
  (`:202-210`). `cancel` messages travel through the same pump (`:162-165` via `SteamUiBridge.cs:815-819`).
- **Defect:** A handler with a synchronous prefix stalls every subsequent request and every `cancel` for the
  duration, so cancellation of a running handler cannot be delivered. The concrete instance is
  `SteamFilePickerSurface.cs:207-210`: `Task.FromResult(ListPlaces())` blocks on `DriveInfo.IsReady` for a
  disconnected network drive. WSGM handlers with synchronous prefixes, such as hardware or COM calls before their
  first await, have the same effect. The bridge comment at `:773-775` shows the hazard was known for the
  constructing thread but not for the pump itself.
- **Recommendation:** In `OnRequestReceived`, start `RespondAsync` off the pump (`Task.Run(() => RespondAsync(request))`,
  tracked in `_requestTasks` as today). This is one line and needs no new state, which satisfies the simplify rule.
  TOOLKITCS-055's picker fix then needs no separate worker plumbing for `listPlaces` beyond its own cancellation.

### TOOLKITCS-V-003: low. Install-folder parsers report "nothing changed" after the script ran

- **Where:** `Client/SteamInstallFolders.cs:248-251` (`jsonValue is null` → `Unavailable`) and the matching
  `catch (JsonException)` → `Unavailable` in `InterpretAdd`, `InterpretRemove` and `InterpretLabel`. The enum
  documents `Unavailable` as "the debug channel could not be reached, so no live add happened" (`:20-21,41-42,62-63`).
- **Defect:** A reachable reply that is null or malformed comes from a script that already ran `AddInstallFolder` or
  `RemoveInstallFolder`, but it is reported as "no live add happened". Callers may then retry or ignore an add that
  did happen. TOOLKITCS-003 covers the transport layer only; these parsers re-collapse the distinction.
- **Recommendation:** In B3, map reachable null or unparseable replies to the new `Unknown` outcome, not `NotSent`,
  in every client parser. Add one parser-table test per status enum.

### TOOLKITCS-V-004: low. Arbitrary caps the TOOLKITCS-031 inventory missed

- **Where:**
  - The extension and patch identifier length cap of 96 (`SteamUiExtensionHost.cs:286`).
  - Command error text truncated to 2,048 characters before delivery to the page (`SteamUiBridge.cs:1000-1003`). That
    text is content the page shows, and chunked delivery already exists.
  - The patch fingerprint truncated to 512 (`SteamUiPatchManager.cs:1093`).
- **Recommendation:** Remove the identifier cap, since a type and character-set check is enough. Explicitly decide
  in R12 that diagnostic bounding applies only to log lines (log-safety boundary). Text delivered to the page as a
  command answer is not truncated.

## Batch problems

1. **B4 does not build on its own.** Its parent edits omit the WSGM implementers of the changed `ISteamUiPatch`
   members: `src/WSGM/Core/SteamDownloadSort.cs:297-316` (Version 5, ResourceKey, Bounds) and
   `src/WSGM/Core/SteamInputGlyphStylePatch.cs:22-55`. They also omit the `SteamGatePatch` constructor callers whose
   `resourceKey` argument is removed: `src/WSGM/Shell/SteamChordResetSurface.cs:37` and
   `SteamControllerCapsSurface.cs:57`. §4 lists these files, but the B4 Files and Dependencies sections do not.
2. **B5 does not build on its own.** The namespace unification removes `SteamUiToolkit.Surfaces`, but only
   `ShellSession.Actions.cs` is listed. `src/WSGM/Shell/ShellSession.cs:10` and `src/WSGM/Shell/SteamUiSessionHost.cs:7`
   also contain `using SteamUiToolkit.Surfaces;` and fail with CS0246 once the namespace is gone.
3. **B3, B4 and B5 break the plugin ABI without a `PluginApi.Version` bump** (V-001). The bump belongs in the parent
   commit of the first contract-breaking batch. B4 and B5's contract changes should land together so plugins see one
   bump.
4. **B2 step "builder argument validation (045)" makes refusals quarantine modules.** It makes
   `new SteamUiCommandResult(false, null)` throw. WSGM builds failure results from nullable details in many places:
   `NativeQamAudioFormatService.cs:60,77` (`result.Detail`), `NativeQamBluetoothService.cs:67,121,212`
   (`_radios.StatusText`) and `NativeQamSemanticServices.cs:1549` (`status.Detail`). With the change, a refusal that
   happens to lack a detail throws inside the handler. `RespondAsync` catches it and calls `FailModule`
   (`SteamUiModuleRuntime.cs:217-221`), which quarantines the whole module for the session. `default(SteamUiCommandResult)`
   also bypasses any constructor check. Replace this with a non-throwing normalisation in the runtime, filling a
   fixed "no reason reported" text that the log line already uses (`:234`). Keep builder `ArgumentNullException`
   checks for null delegates only.
5. **B4 risks a deadlock with R5 and TOOLKITCS-030.** R5 makes WSGM's post-pass reconcile run in a `Synchronized`
   handler raised by the manager's own loop. That handler calls `SetGlobalEnabledAsync`
   (`SteamUiSessionHost.cs:944-950`). TOOLKITCS-030 makes an awaited switch "wait for the next completed pass". A
   handler awaiting the next pass while running inside the loop task that would execute it never completes. Either
   raise `Synchronized` asynchronously after the loop iteration, or keep the awaited-switch path running its own pass
   under the scheduler gate, as it does today. State which in the batch.
6. **B6 / R8 introduces visible UI.** Renderer paging adds a control that does not exist today (see Corrected R8).
   This is a maintainer decision under the rule that UI stays identical, so it must not be presented as preserving
   existing chrome.
7. **B2's 017 step is void** (017 is refuted). Drop it from the step list.
8. **B1 leaves the event rename unscheduled.** B1 deletes the generation notification lane but keeps
   `NotificationReceived` "for now", and no later batch performs the `BindingCalled` rename §4 promises. Either
   assign it to B3 with the other `ISteamUiTransport` changes or drop the rename.
9. **B1 changes test expectations it does not list.** The `127.0.0.1`-only change requires updating
   `SteamCefTests.LoopbackWebSocketUrlsOnTheDebugPortAreAccepted`, whose `localhost` row would fail. The
   answered-error-keeps-`Ready` change requires updating `PersistentSteamUiTransportTests` around `FailFirstEvaluation`
   (`:180-208`). Both files are in B1's list, but the steps should name the expectation changes so a reviewer does not
   read them as regressions.
10. **B2's storage-probe step cannot land before the TS gate change.** A probe that resolves without invoking
    exports while the gate still invokes them (`gates/storage.ts:196-207`) can diverge: the probe reports compatible,
    the gate fails to install, and the surface sits Degraded. B2 and the TS storage change must land in the same
    child commit.
