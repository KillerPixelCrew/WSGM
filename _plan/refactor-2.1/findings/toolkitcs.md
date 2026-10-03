# SteamUiToolkit C# findings

Scope: the C# side of the SteamUiToolkit submodule, `external/steam-ui-toolkit/src/SteamUiToolkit/**` (not the `SteamUiAssets` TypeScript sources, which are the toolkitjs area) and its test project, plus the WSGM consumers each fix forces. Baseline: toolkit `main` at `388dd1b`, parent `master` at `1329813f`. Sources: `_plan/refactor-2.1/review/toolkitcs.md`, its adversarial verification `toolkitcs.verify.md`, the completeness critic `_critic.md` and plan v2 (`refactor-plan-v2.md`, `batches.json`). Where plan v2 changed a review recommendation, the plan v2 version is the one written here.

Path shorthand used below: `tk/` is `external/steam-ui-toolkit/src/SteamUiToolkit/`, `tkt/` is `external/steam-ui-toolkit/tests/SteamUiToolkit.Tests/`. Line numbers come from the review and many are wrong (the verifier found test citations past the end of their files and several wrong source lines). Anchor every edit by symbol name, not by line.

Counts (after verifier corrections and the maintainer decisions of 2026-10-03 in `DECISIONS.md`): 2 high, 17 medium, 40 low, 14 nit, and 2 in the no-change list (TOOLKITCS-017 refuted, TOOLKITCS-010 dropped as security theater). 73 findings are written up below, including the four the verifier found (TOOLKITCS-V-001 to V-004).

Plan v2 batches that implement this area, in execution order:

- **B048** transport and connection correctness (no public API change).
- **B049** probe safety, bridge parsing, module runtime and storage resolution (lands with the TypeScript storage gate change in the same child commit).
- **B052** Plugin API 4, which also covers the toolkit types reachable from `ISteamUiModule`.
- **B053** dispatch-aware outcomes, explicit `SteamClient`, no ambient session (breaking, with parent consumers in the same parent commit).
- **B054** patch contract, one manager loop, quarantine in the manager, bounded teardown (breaking, with parent implementers).
- **B056** public surface cleanup, version 0.2.0.
- **B057** host-supplied Quick Access presentation (only TOOLKITCS-047 here; the batch's unrelated D13 item is decided: the badge shows the library's name, supplied by the host).
- **B059** file picker.
- **B062** test quality.
- **B177** (docs pass) carries the C# public API inventory with the plugin-contract closure marked and the bridge trust-boundary note.

Every toolkit batch follows the publication rule: commit and push the child on `main`, then one parent commit with the gitlink plus the consumer edits. Toolkit batches also run `npm run prelude:claims` in the child and the parent `npm run steam-assets:build`, `steam-assets:check` and `steam-assets:claims`. The toolkit test command used below is `dotnet test external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj --filter "..."`, written as "toolkit filter". The parent command is `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "..."`, written as "parent filter".

Cross-domain resolutions from the critic that shape these solutions: shutdown is one safety-first step list under one deadline (conflict 1, D1), so toolkit teardown takes the caller's deadline token and there are no shutdown phases. steamhost's `DependsOn` and quarantine reset API are dropped (conflict 5). Dynamic module registration, which conflict 5 dropped, is decided the other way: plugin Steam UI modules register when the plugin becomes ready, so the toolkit gains module add and remove (TOOLKITCS-041, TOOLKITCS-044). Consumers map the client outcome `Unknown`, not `DispatchedUnknown` (conflict 5). Bridge host id and nonce are dropped (conflict 6), and every other security-hardening item is dropped by maintainer decision: TOOLKITCS-010 moves to no-change, and TOOLKITCS-009, TOOLKITCS-048, TOOLKITCS-055 and TOOLKITCS-061 keep only their functional parts. The picker renders the whole list as today (conflict 7). T01_01 is not landed and folds into B053 (conflict 20). Each library API batch carries its consumers in the same parent commit (conflict 21).

Where a finding below departs from its batch spec in `batches.json`, the finding wins, and it says why in its own section: B048 skips the transport task tracking (TOOLKITCS-012) and keeps accepting `localhost` (TOOLKITCS-009); B049 keeps in-order handler dispatch instead of `Task.Run` (TOOLKITCS-V-002) and needs the live provider-token read up front (TOOLKITCS-001); B053 drops the running-apps version stamp (TOOLKITCS-019); B054 also moves `performanceActions` into the QAM rows (TOOLKITCS-036) and adds module add and remove for plugin modules registered on readiness (TOOLKITCS-041); B056 keeps `SteamPageProbe.Tokens` as a literal and leaves the six page surfaces alone (TOOLKITCS-049), and `SteamUiText` has fifteen call sites, not three (TOOLKITCS-057); B057 drops the toolkit's `custom` check without a generic replacement (TOOLKITCS-047); B059 has no `TimedOut` or `Cancelled` picker outcome and no device-namespace refusal or fixed refusal strings (TOOLKITCS-055).

---

## High

### TOOLKITCS-001: Storage probe calls every function export of Steam's transport module

- **Severity:** high
- **Where:** `tk/Surfaces/SteamStorageSurface.cs:174-184` (the probe in `SteamStorageSurface.Patch`); the TypeScript twin `tk/SteamUiAssets/Source/gates/storage.ts:186-210` (`resolve` of the transport provider); `tkt/SteamStorageTests.cs` (substring-only probe test, around `:29-36`).
- **Problem:** The probe finds the module matching `['GetDefaultTransport','m_transport']`, loads its exports, then calls every function export with no arguments until one returns an object with `GetDefaultTransport`. It runs on every synchronization pass. The toolkit's own rule in `SteamUiProbeJs` ("constructing exports has restarted a machine and signed Steam out") and the parent CEF safety rule forbid exactly this. The gate in `storage.ts` does the same sweep at install, so fixing only the C# probe leaves the hazard.
- **Best solution:** Identify the provider by source text, never by calling candidates.
  - Probe (C#): keep `findUnique(['GetDefaultTransport','m_transport'])` and `service`. Replace the call loop with a read-only selection: among the module's function exports, keep those whose own source (`Function.prototype.toString.call(fn)`, which executes nothing) matches the provider's author tokens, and require exactly one. Report `{service, transportModule:1, provider:<count>}`; compatible means `service==1 && transportModule==1 && provider==1`. Drop `transportResolved`, `claimable` and `claimed` from the probe: the probe can no longer see the transport instance, and the claimability check moves to the gate's install, which already reports failure through its install result.
  - Gate (TS): use the same source-token selection (`sourceMatches` in the toolkitjs vocabulary), require exactly one match, and call only that one accessor, once, at install, to obtain the transport it wraps. That single call of a source-identified accessor is not a sweep. If the selection is not unique, install fails with `lastError` naming the count.
  - The tokens cannot come from `eng/check-storage.mjs`: its provider is a synthetic `OI: () => provider` whose source names nothing, and `GetDefaultTransport` alone is wrong (a class export that defines it matches and must never be called). Take them from the accessor's source as the maintainer reads it on the current client, by the transport module's literal id, as an attended step of B049 before the child commit (never during a Steam cold start; same rule as TOOLKITJS-018). Then make the fixture's accessor source carry those tokens and add a class export defining `GetDefaultTransport` that throws if constructed. Probe and gate share one token list in the same child commit.
  - The fingerprint changes with the meaning (for example `steam-storage-v2:unique-service+unique-provider`), because claimability is no longer part of the probe.
  - This beats "call only exports that look like getters" because any invocation of an unidentified export is the forbidden act. It also beats keeping the sweep in the gate alone, because the gate runs on every Big Picture document too.
- **Tests:** a NodeScript test in `tkt/SteamStorageTests.cs` that runs the real probe expression against a fake webpack runtime whose every export is a spy, asserting no export is invoked and that `provider` counts 0, 1 and 2 correctly. Extend `eng/check-storage.mjs` so the gate invokes only the source-matched export. Toolkit filter `FullyQualifiedName~SteamStorageTests`; child `npm run prelude:claims`. Attended: the maintainer confirms the gate installs and answers on the live client after deploy (not during Steam's cold start).
- **Plan v2:** B049 (with TOOLKITJS-018 in the same child commit; verify batch problem 10). The live token read is an attended precondition of B049, not of B061, which lands later.
- **Related:** U03A-SUTS-003, TOOLKITJS-018, plan claim C26, TOOLKITCS-066 (coverage).

### TOOLKITCS-036: Bridge refuses to install unless four Quick Access/TDP fingerprints match

- **Severity:** high
- **Where:** `tk/Surfaces/SteamUiBridgePatch.cs:24-46,79-82` (`StructuralFingerprint`, `ProbeExpression`, `ProbeAsync`); `tk/Surfaces/SteamQuickAccessRowPatch.cs:26-33,72-80` (`CommonRequiredCounts`, `_probeExpression`).
- **Problem:** Every gate lives inside the bridge (`SteamGatePatch.GateExpression`), yet the bridge probe requires `tdpAvailability`, `tdpComponent`, `performanceActions` and `profileProjection` to each match exactly once. A Steam build that moves one Quick Access module takes down host pages, library badges, the Home carousel, the Extensions tab and every other gate.
- **Best solution:**
  - Bridge probe asks only what the bridge needs: the preamble resolving webpack (a throw there already answers `{error}` through `SteamUiProbeJs.Close`) and `react:count(ReactTokens)`. Compatible means `IsOne(root,"react")`. New fingerprint `steam-ui-bridge-v1:webpack+react` (see TOOLKITCS-038 for the prefix).
  - Move the four counts into the Quick Access row probe as common required counts, so every QAM row keeps exactly today's precondition: `CommonRequiredCounts` gains `tdpAvailability`, `tdpComponent`, `profileProjection` and `performanceActions`. When a row's `primaryCountName` is already `performanceActions`, do not emit the key twice. The token lists move with them (`SteamUiProbeJs.TdpPresentationTokens`, `PerformanceActionTokens`, the two literal lists).
  - This beats picking a per-row subset because it keeps every QAM row's visible behaviour identical while freeing all non-QAM surfaces.
- **Tests:** in `tkt/SteamQuickAccessRowPatchTests.cs` and a bridge probe test: bridge compatible with only React and webpack present; a QAM row incompatible when `tdpComponent` is 0 or 2; a non-QAM gate (for example the page patch) still applies when the TDP counts are absent. Toolkit filter `FullyQualifiedName~SteamQuickAccessRowPatchTests|FullyQualifiedName~SteamUiBridge`.
- **Plan v2:** B054. The B054 spec names three counts for the rows (`tdpAvailability`, `tdpComponent`, `profileProjection`); `performanceActions` moves too, because today every row also waits on it through the bridge probe and dropping it would let a row whose primary module differs mount on a client where the performance-actions module is missing or ambiguous.
- **Related:** U02B-SUTC-003, plan claim C15; verify note on 036 (rows must probe the TDP counts themselves).

---

## Medium

### TOOLKITCS-002: A caller cancel or timeout during send can kill the shared CDP connection

- **Severity:** medium
- **Where:** `tk/SteamUiCdpConnection.cs:272-330` (`InvokeAsync`).
- **Problem:** `_wire.SendAsync(request, deadline.Token)` runs under a token linked to the caller. A caller cancel or the per-call timeout during the send can leave a partial frame (`ClientWebSocket.SendAsync` aborts the socket when its token fires), and the `catch` around the send cancels `_shutdown`, which kills the connection every other caller shares.
- **Best solution:** Keep the caller's deadline for waiting on `_outstanding`, `_sendGate` and the reply, but run the send itself under the connection's lifetime only, and let the caller stop waiting for it without cancelling it:
  - After `_sendGate` is taken, start one private `SendAndReleaseAsync(request)` that does `try { await _wire.SendAsync(request, _shutdown.Token); } catch { _shutdown.Cancel(); throw; } finally { _sendGate.Release(); }`, then `await sending.WaitAsync(deadline.Token)`. The gate is released when the frame is finished, not when the caller leaves, so the next sender never starts inside a half-written frame.
  - Sending under `_shutdown.Token` alone would be wrong: a renderer that stops reading its socket would hold the caller in `SendAsync` past its own timeout. `WaitAsync` keeps the caller's bound.
  - `_shutdown.Cancel()` now fires only for a real wire failure (a socket whose send failed is broken for everyone anyway). `completion.Task.WaitAsync(deadline.Token)` stays as the caller's reply wait. No new state.
- **Tests:** extend `tkt/Fakes/QueueWire.cs` so a send can block until released and can fault. Tests in `tkt/SteamUiCdpConnectionTests.cs`: a caller cancel while the send is blocked returns to the caller at once, leaves the connection alive, and a second request succeeds after the first frame is released; a faulting send ends the connection. Toolkit filter `FullyQualifiedName~SteamUiCdpConnectionTests`.
- **Plan v2:** B048.
- **Related:** U02A-SUTC-011, plan claim C8, TOOLKITCS-003 (dispatch phase depends on this send boundary).

### TOOLKITCS-003: Evaluation failures are not classified by whether the expression was sent

- **Severity:** medium
- **Where:** `tk/PersistentSteamUiTransport.cs:162-246` (`EvaluateAsync` catch blocks); `tk/SteamUiTransportModels.cs` (`SteamUiEvaluationResult`); `tk/SteamUiCdpConnection.cs` (`InvokeAsync`, `EvaluateAsync`); `tk/Client/SteamClientScript.cs:9-22` (`SteamClientWriteResult`).
- **Problem:** A caller cancel or timeout after the expression was sent returns `Reachable=false`, which `SteamClientWriteResult` documents as "changed nothing, may offer again". Local framing faults (non-text frame, 8 MiB response bound, notification queue overflow) come back as `Reachable=true` with an error, as if Steam answered. Writers cannot tell "never ran" from "may have run".
- **Best solution:**
  - Add `public enum SteamUiDispatch { NotSent, Closed, Unanswered, Answered }` and change `SteamUiEvaluationResult` to `(SteamUiDispatch Dispatch, string? Value, string? Error, SteamUiGenerations Generations)`; delete `Reachable` and the `Unavailable` factory.
  - The connection marks the send boundary at the moment the send starts, not when it completes: after TOOLKITCS-002 a started frame is always finished, so Steam may run it even if the caller stopped waiting. `InvokeAsync` sets a local `started` just before it starts `SendAndReleaseAsync`, and any exception after that point (cancel, timeout, connection loss, framing fault) is rethrown as `internal sealed class SteamUiUnansweredException : IOException` with the original as `InnerException`. Exceptions before it propagate unchanged. `SetRuntimeBindingAsync` unwraps it (`ExceptionDispatchInfo.Throw(ex.InnerException!)`) so its documented exceptions (TOOLKITCS-013) and the bridge's `when (ex is not OperationCanceledException)` filter keep working.
  - Mapping in `PersistentSteamUiTransport.EvaluateAsync`: transport closed by `SetEnabled(false)` is `Closed`; discovery, connect, gate waits and caller cancel before the send are `NotSent`; `SteamUiUnansweredException` is `Unanswered` (a timeout inside it still feeds `DropUnansweredConnection` as today); a reply (value or JavaScript exception, see TOOLKITCS-004) is `Answered`.
  - Client writes become `SteamClientWriteResult(SteamClientWriteOutcome Outcome, string? Error)` with `NotSent | Unknown | Rejected | Applied`: `Answered` with an error is `Rejected`, `Answered` with an ok reply is `Applied`, `Unanswered` is `Unknown`, `NotSent` and `Closed` are `NotSent`. `CefEvalResult` is deleted with the session (TOOLKITCS-014).
  - Four outcomes, not the Codex plan's six (plan v2 section 1).
  - D9 (no readback machinery outside the Claw) does not remove `Unknown`: it is the Steam script's missing answer, not a device readback, and nothing waits on it. There is no re-arm, unresolved entry or recovery entry behind it; the caller reports it once and moves on.
- **Tests:** a phase table in `tkt/PersistentSteamUiTransportTests.cs` driven through `ResponsiveWire`/`QueueWire`: not sent (discovery absent), closed, unanswered (cancel after send), answered error, answered value. The T01_01 tests move here and assert against `SteamUiDispatch`. Toolkit filter `FullyQualifiedName~PersistentSteamUiTransportTests|FullyQualifiedName~SteamClientTests`.
- **Plan v2:** B053.
- **Related:** U02B-SUTC-001, U02A-SUTC-008, U02A-SUTC-001, U02B-SUTC-007, A01-F004, plan claim C10, R2, T01_01 (folded), TOOLKITCS-V-003.

### TOOLKITCS-012: Toolkit teardown has unbounded waits WSGM's shutdown deadline cannot cut

- **Severity:** medium (verifier narrowed the scope)
- **Where:** `tk/SteamUiModuleRuntime.cs:82-118` (`DisposeAsync`); `tk/SteamUiPatchManager.cs:254-309` (`DisposeAsync`); `tk/PersistentSteamUiTransport.cs` (`StartReconnectLocked`, `DisposeDetachedConnectionAsync` fire-and-forget); WSGM `src/WSGM/Shell/ShellSession.Shutdown.cs:356-382`, `src/WSGM/Shell/SteamUiSessionHost.cs` (disposal).
- **Problem:** The runtime waits with no bound for the publication loop (including a non-cancellable `publication.Read()`) and every in-flight request task. The manager waits up to 30 s for the scheduler, then removes patches one by one at up to 8 s each, so a hung renderer with about 35 patches takes minutes, and a 30 s give-up leaves every patch injected. The bridge (2 s removal, 1 s pump) and transport (1 s connection, 1 s pumps) are already bounded; their retired-connection and reconnect tasks are untracked.
- **Best solution:**
  - Add `public Task ShutdownAsync(CancellationToken cancellationToken)` to `SteamUiModuleRuntime` and `SteamUiPatchManager` only; `DisposeAsync` calls `ShutdownAsync(CancellationToken.None)` so non-WSGM callers keep today's behaviour. No new method on the bridge or transport (verifier correction, plan v2).
  - Runtime: after unsubscribing and cancelling in-flight work, await the publication task and the request tasks with `.WaitAsync(cancellationToken)`. On cancellation, log one line naming how many requests and whether the publication round are still running, and return. They are retained, not freed underneath.
  - Manager: take `_schedulerGate` with a token linked to the caller and to the existing 30 s ceiling. Remove in removal order (TOOLKITCS-025) with each patch's timeout linked to the caller's token. When the caller's token fires, stop, mark the remaining patches `RemoveFailed` with "shutdown deadline reached before removal", and log their ids in one line.
  - Transport and bridge: no change. Their waits are already bounded (connection dispose 1 s, event pumps 1 s, bridge removal 2 s and pump 1 s). The untracked reconnect loop exits on `_shutdown`, and each `DisposeDetachedConnectionAsync` runs that same bounded connection dispose, so tracking them in a list would add state (and a list that needs pruning over a long session) without a defect to fix. B048's "(012 part)" is therefore not done (verifier: "the fix belongs in the runtime and the manager only").
  - WSGM: `SteamUiSessionHost` passes its shutdown deadline's token (from the D1 step list, `StopAsync(Deadline)`) to both `ShutdownAsync` calls. Command admission closes at T0 while patch retraction stays after device cleanup (plan v2 architecture).
- **Tests:** in `tkt/SteamUiPatchManagerTests.cs`, a `FakePatch` whose remove never completes: `ShutdownAsync` returns at the caller's deadline and the snapshot names the patch `RemoveFailed`. In a new `tkt/SteamUiModuleRuntimeTests.cs`, a publication `Read()` that never completes: `ShutdownAsync` returns at the deadline. Toolkit filter `FullyQualifiedName~SteamUiPatchManagerTests|FullyQualifiedName~SteamUiModule`; parent filter `FullyQualifiedName~SteamUiSessionHost`.
- **Plan v2:** B054 (runtime and manager); the B048 spec's "track reconnect and retirement tasks (012 part)" is dropped as above. WSGM's shutdown ordering is decided: D1, safety-first ordered steps under one deadline, with no percentage cutoffs or preliminary drain (B140).
- **Related:** U02A-SUTC-012, plan claim C9, critic conflict 1 (replaces toolkitcs A5 "B3 shutdown phases"), TOOLKITCS-025.

### TOOLKITCS-014: Process-global `SteamUiTransportSession` routes every client call

- **Severity:** medium (architecture)
- **Where:** `tk/SteamUiTransportSession.cs:44-226`; static client classes in `tk/Client/*.cs`; `tk/Client/SteamClientScript.cs:117-153` (two evaluation paths); `tk/SteamUiPatchManager.cs:1107` and `tk/Client/SteamRunningApps.cs:131` (`IsClosedReason`); WSGM consumers listed in review section 4 (19 files).
- **Problem:** Attach, enable and evaluate state is process-global, the write gate `SteamApps.Writes` is process-wide, and there are two inconsistent evaluation paths (the session path never throws on caller cancel, the explicit path rethrows). Tests and consumers depend on ambient state.
- **Best solution:**
  - Delete `SteamUiTransportSession.cs` and `CefEvalResult`. Make `PersistentSteamUiTransport.SetEnabled(bool, string?)` public and move `DisabledReason` to `PersistentSteamUiTransport.DefaultClosedReason`.
  - New `public sealed class SteamClient` in `tk/Client/SteamClient.cs`, constructed from one `ISteamUiTransport`. It owns one private write lane (`SemaphoreSlim _writes`) shared by apps, collections and install folders, and exposes sub-objects `Apps`, `Collections`, `InstallFolders`, `Library`, `Downloads`, `CurrentPage`, `StartupMovie`, `RunningApps`. The static classes become those sub-object types; record and enum types stay in their files; pure helpers (`SteamApps.NormalizeAppId`, `IsShortcutAppId`, `SteamInstallFolders.NormalizePath`, `SteamDownloadActivity.IsActive`) stay public static. The transport parameters on `SteamCollections.SyncAsync` and `SteamStartupMovie.*` go away.
  - `SteamClientScript.EvaluateAsync` keeps one path over the client's transport and never throws on caller cancel (it returns `NotSent` or `Unknown` per TOOLKITCS-003).
  - `IsClosedReason` users: running apps checks `result.Dispatch == SteamUiDispatch.Closed`. The manager's log level reads the role's transport snapshot health (`Idle` while closed, the manager always holds a subscription), which needs no new plumbing.
  - No `ISteamUiLog` constructor parameter (R10, see TOOLKITCS-043).
  - WSGM composes one `SteamClient` in `ShellSession` and passes it to every consumer in review section 4's "SteamClient instance" row, plus `LibraryFilter`, `LibraryTabManager`, `SteamLibraryTabs` (raw evaluation through the transport).
- **Tests:** the existing client tests move to instance calls against `FakeSteamUiTransport`; a write-lane test proving an install-folder add and a shortcut add never overlap. Toolkit filter `FullyQualifiedName~SteamClientTests|FullyQualifiedName~SteamLibraryReadTests|FullyQualifiedName~SteamStartupMovieTests|FullyQualifiedName~PersistentSteamUiTransportTests`; parent filter `FullyQualifiedName~RunningApplicationTarget|FullyQualifiedName~SteamDownloadSort|FullyQualifiedName~SteamUiSessionHost|FullyQualifiedName~GameLibrary|FullyQualifiedName~Animation|FullyQualifiedName~LaunchFix`.
- **Plan v2:** B053, with the parent consumer edits (library B5 mapping, LIBRARY-024, OVERLAY-010, STEAMHOST-016/V-005) in the same parent commit.
- **Related:** U02B-SUTC-004, U02B-SUTC-042, plan claims C1/C4, TOOLKITCS-018, TOOLKITCS-029.

### TOOLKITCS-016: Partial client writes lose the evidence of what they already changed

- **Severity:** medium (verifier corrected the startup-movie locations)
- **Where:** `tk/Client/SteamCollections.cs:109-126` (create then later steps); `tk/Client/SteamStartupMovie.cs` (`set()` writes three settings around `:65-67`, error reply `:107-109`, `Parse` `:142-146`); `tk/Client/SteamInstallFolders.cs` (`BuildAddExpression`, the swallowed purge and label `catch(e){}`, `InterpretAdd`).
- **Problem:** A collection created and saved before a later step throws loses its id, so the next sync creates a duplicate. The startup-movie set-aside clears `startup_movie_id` before a later write throws, and the error reply has no `choice`, so the caller loses Steam's original choice. Library add swallows purge and label failures and reports `Added`.
- **Best solution:** Every script reply after the first mutation carries the ownership evidence, success or not, and C# keeps it on partial outcomes.
  - Collections: declare `let col=null` before the script's `try` (today it is a `let` inside it, which the shared `catch` cannot see), and every failure reply after `NewUnsavedCollection(...).Save()` carries `id:col.id`, including the "Steam does not let this collection be changed" return and the shared catch (`{ok:false, err, id}`). `ParseSync` reads `id` regardless of `ok`, so `SteamCollectionSyncResult.Id` is set on failure and WSGM records ownership.
  - Startup movie: the set-aside script already reads `now` before writing; what is missing is that `now` lives inside the `try`, so the shared catch answers `{ok:false, err}` without it. Wrap the set-aside's `await set(...)` in its own `try`/`catch` that answers `{ok:false, err, choice:now}`. `Parse` reads `choice` regardless of `ok`, and `SteamStartupMovieResult` keeps it on that partial outcome. WSGM persists it from the partial reply (STEAMHOST-016). Restore failures keep today's reply without `choice`, so WSGM keeps the choice it already stored. Per D10, WSGM hands that stored choice back through `StartupMovie` restore at exit when no WSGM movie is chosen and at uninstall; the restore script needs no change for that, only the new callers (STEAMHOST).
  - Install folders: the add script records `purgeFailed` (count) and `labelError` instead of empty catches; `InterpretAdd` maps either to a new `SteamLibraryAddStatus.Partial` with a detail naming what did not happen.
  - No retries anywhere.
- **Tests:** NodeScript runs of the real scripts against fake `SteamClient` objects: collection create then throw returns the id; set-aside whose second write throws keeps `choice`; install-folder purge failure reports `Partial`. Toolkit filter `FullyQualifiedName~SteamClientTests|FullyQualifiedName~SteamStartupMovieTests`.
- **Plan v2:** B053.
- **Related:** U02B-SUTC-008, U02B-SUTC-010, U02B-SUTC-011, plan claim C28 (wrong lines), STEAMHOST-016.

### TOOLKITCS-018: Install-folder read-modify-write scripts are not serialized

- **Severity:** medium
- **Where:** `tk/Client/SteamInstallFolders.cs:130-194` (`AddAsync`, `RemoveAllAtPathAsync`, `SetLabelAsync`); `tk/Client/SteamApps.cs:176` (`Writes`).
- **Problem:** Concurrent adds at one path both read the folder list and both register, recreating the duplicate-library defect the class exists to prevent. Apps and collections share a write gate; install folders do not.
- **Best solution:** Covered by the `SteamClient` write lane of TOOLKITCS-014: `InstallFolders.AddAsync`, `RemoveAllAtPathAsync` and `SetLabelAsync` take `_writes` exactly as apps and collections do. No second lock type.
- **Tests:** two concurrent `AddAsync` calls on one `SteamClient` over a fake transport that records evaluation overlap; assert no overlap. Toolkit filter `FullyQualifiedName~SteamClientTests`.
- **Plan v2:** B053.
- **Related:** U02B-SUTC-009, TOOLKITCS-014.

### TOOLKITCS-019: Running-apps observer is published before it is registered

- **Severity:** medium
- **Where:** `tk/Client/SteamRunningApps.cs:58-75` (install), `:198-228` (cleanup).
- **Problem:** The observer object is assigned to `window` (`:63`) before `RegisterForAppLifetimeNotifications` runs (`:64`), and later reads skip installation when the object exists (`:59`). If registration throws, every later read returns a frozen set as healthy. Cleanup ignores an `ok:false` reply, and the observer carries no version stamp, so an older script's object is trusted. (The generation restarting at 1 is documented and intended, verifier note, so it is not changed.)
- **Best solution:** In the install script, register first and assign the observer to `window` only after registration returned a handle with `unregister`; if registration throws, nothing is published and the reply is the existing `{ok:false, err}`, so the next read installs again. Log one warning line when the cleanup reply is `ok:false`. Single-reader contract documented on `SteamClient.RunningApps` (WSGM has one reader, `RunningApplicationTarget`); no in-page reference counting (R9).
  - No version stamp. Once an observer is only ever published registered, one left behind by an earlier WSGM (a crash before cleanup) is a working observer of the same shape and is safe to reuse. If the observer's shape ever changes, the change renames `ObserverProperty`, which needs no runtime check. The B053 spec's "with a version stamp" is dropped for this reason (simplify rule).
- **Tests:** NodeScript: registration throws leaves no `window` observer and the next read retries install; an existing registered observer is reused without a second registration. Toolkit filter `FullyQualifiedName~SteamClientTests`; parent filter `FullyQualifiedName~RunningApplicationTarget`.
- **Plan v2:** B053.
- **Related:** U02B-SUTC-002, U02B-SUTC-015, U02B-SUTC-040, plan claim C30, R9.

### TOOLKITCS-020: Refusal and rejection log lines write plugin secrets to wsgm.log

- **Severity:** medium
- **Where:** `tk/SteamUiModuleRuntime.cs:224-236` (the "did nothing" line with `Payload: {request.Payload}`); `tk/SteamUiBridge.cs:740-755` (rejection line with a 200-character payload prefix); WSGM sources of secret payloads `src/WSGM/Shell/WsgmSteamSettingsService.cs:414`, `src/WSGM/Shell/CommonPluginSteamUiSource.cs:387`.
- **Problem:** Plugin `secret` settings travel as `configure`/`set` command payloads, a stale-revision refusal is ordinary, and testers paste wsgm.log. Secrets reach a file users share.
- **Best solution:** Add `internal static string SteamUiShared.DescribePayload(string? json)`: parse with `JsonDocument`, write the same structure with every string value replaced by `"…(<length>)"`, keep property names, numbers, booleans, nulls and nesting. Unparseable input becomes `<unparseable, N chars>`. Use it in both lines. The bridge line keeps its log-line bound (log-safety boundary per R12/V-004) after redaction. This keeps the diagnosis the comment at the runtime line asks for (which field was missing, of what kind) without values.
- **Tests:** a unit test for `DescribePayload` (nested object with a secret string, numbers and booleans kept, malformed input); a runtime test that a refused command's log line contains the property name and not the secret. Toolkit filter `FullyQualifiedName~SteamUiModule|FullyQualifiedName~SteamUiBridgeHostTests`.
- **Plan v2:** B049.
- **Related:** NEW (U02A-SUTC-062/046 cover size only), plan claim C13, R11.

### TOOLKITCS-025: Synchronization removes the bridge before the gates that live in it

- **Severity:** medium
- **Where:** `tk/SteamUiPatchManager.cs:495-525` (`SynchronizeAsync`, `BridgeFirst`), `:555-558` (removal inside `SynchronizePatchAsync`), `:288` (dispose uses the reverse); WSGM `src/WSGM/Shell/SteamUiSessionHost.cs:850-863` (two-pass disable).
- **Problem:** Every pass walks patches bridge first, both to apply and to remove. A global disable removes the bridge first, then every gate's removal fails (`RemoveFailed`). WSGM works around it with a two-pass disable.
- **Best solution:** Split each pass into two loops under the one scheduler gate: first, every patch that should be off (global off, its switch off, or faulted per TOOLKITCS-044) is removed in `RemovalOrder()` (gates by id, the bridge last); then every patch that should be on is synchronized in `ApplyOrder()` (bridge first, then by id). `BridgeFirst()` becomes these two private methods. Dispose and `ShutdownAsync` use `RemovalOrder()`. No dependency graph (R3). WSGM deletes the two-pass disable in B054.
- **Tests:** in `tkt/SteamUiPatchManagerTests.cs`: global disable with a bridge and a gate ends with both `Disabled` and no `RemoveFailed`; enabling applies the bridge before the gate. Toolkit filter `FullyQualifiedName~SteamUiPatchManagerTests`; parent filter `FullyQualifiedName~SteamUiSessionHost`.
- **Plan v2:** B054.
- **Related:** U02A-SUTC-002, U02A-SUTC-020, plan claim C14, R3, STEAMHOST-037, TOOLKITCS-012.

### TOOLKITCS-027: An apply that failed, threw or timed out is left in the page

- **Severity:** medium
- **Where:** `tk/SteamUiPatchManager.cs:702-714` (`!applied.Succeeded` branch), `:759-770` (catch blocks), `:740-755` (the verify-failure removal that already exists).
- **Problem:** When `ApplyAsync` fails, throws or times out after it started, the manager records `Degraded` or `Retrying` and leaves whatever the apply already injected. After a timeout the state is `Retrying`, but nothing schedules a retry.
- **Best solution:**
  - Track `applyStarted` (a local set just before `ApplyAsync`). Extract the existing "applied but did not verify; removing it" block into one private `RemoveAfterFailedApplyAsync(entry, generationEpoch, fingerprint, diagnostic)`, which runs `RemoveAsync` once under a fresh per-patch timeout (not the cancelled operation token) and sets `Degraded` or `RemoveFailed` as the verify path does today. Call it from the verify failure, the `!applied.Succeeded` branch and both catch blocks when `applyStarted`.
  - After a timed-out apply whose removal succeeded, call the existing `ScheduleSettleRetry(entry, generationEpoch)`, the same probe-first retry `AbsentTarget` already uses, so `Retrying` is truthful. That retry probes and verifies, so it is not a blind reapply (plan v2: "never blindly reapplied"). It is a UI patch, not a device write, so the no-retry rule for uncertain device writes does not apply.
- **Tests:** add a "mutate then fail" mode to `tkt/Fakes/FakePatch.cs` (marks itself injected, then fails or hangs) and assert removal ran for failure, throw and timeout, and that a timeout schedules one settle retry. Toolkit filter `FullyQualifiedName~SteamUiPatchManagerTests`.
- **Plan v2:** B054.
- **Related:** U02A-SUTC-003, plan claim C16, TOOLKITCS-028, TOOLKITCS-066.

### TOOLKITCS-041: Module set breaks on declaration order and misattributes failures

- **Severity:** medium
- **Where:** `tk/SteamUiModule.cs:170-230` (`SteamUiModuleSet` constructor, `_modulesByPatch`); `tk/SteamUiModuleRuntime.cs:440-471` (`FailModule`, `IsModuleFailed`).
- **Problem:** A command-only or publication-only module declared before the installer of the same patch id makes the installer's `_modulesByPatch.Add` throw `ArgumentException`, although the docs promise order independence and `InvalidOperationException`. Two publications for one patch id are accepted. A failure is attributed to whichever module registered the patch id first, not the module whose callback failed.
- **Best solution:** Delete `_modulesByPatch`. Keep three indexes: installers (`patchId -> module`, duplicates already refused), publications (`patchId -> (publication, module)`, a second publication for the same id throws `InvalidOperationException` naming both modules), and commands (`(patchId, command) -> (handler, module)`). `TryGetCommand` returns the owning module; the publication loop knows its module. `FailModule` takes the module itself, and quarantine is checked by the module that owns the callback being run. `AllowedCommands` is derived as today.
  - Dynamic registration (decided: plugin Steam UI modules register when the plugin becomes ready; this replaces the read-at-session-start rule of STEAMHOST-006 and critic conflict 5). The set gets `Add(ISteamUiModule)` and `Remove(string moduleId)`. Both build new immutable index snapshots and swap them in one reference write, so the bridge pump and the publication loop read without a lock; `Add` runs the same duplicate checks as construction and leaves the set unchanged when it throws. `SteamUiModuleRuntime` exposes `AddModule(module)` and `RemoveModuleAsync(moduleId, cancellationToken)`: add registers the module's patches with the manager, hands the new vocabulary to the bridge and queues a publication; remove cancels the module's in-flight requests, unregisters its patches (new `SteamUiPatchManager.UnregisterAsync(patchId, cancellationToken)`, which removes the patch from the page under the scheduler gate and then drops the entry) and shrinks the vocabulary. The bridge gets `SteamUiBridgeHost.SetAllowedCommands(vocabulary)`, which swaps `_allowedCommands` and the authorizer's copy and, when the bridge is ready, re-sends the bootstrap configuration. Today a reused bridge keeps its old `config.allowed` (`bridge.ts` returns early on reuse), so the page twin reads the allow map through one `let allowed` that the reuse path replaces before returning (toolkitjs, same child commit). No unregistration of built-in modules is needed; WSGM uses add and remove only for plugin modules (STEAMHOST-006, B086).
- **Tests:** in `tkt/SteamUiModuleTests.cs`: command-only module declared before its installer builds; duplicate publication throws `InvalidOperationException`; a throwing command handler quarantines its own module and not the installer's. Dynamic part: a module added after the runtime started has its command answered and its publication delivered; a duplicate patch id on `Add` throws and leaves the set unchanged; after `RemoveModuleAsync` its patch is removed from the page, its entry is gone from the manager snapshot and its command is refused with "No handler is registered for this command." (TOOLKITCS-042). A NodeScript test runs the bootstrap twice with the same generations and a changed `allowed` map: the second run reuses the bridge and a command allowed only by the new map is sent. Toolkit filter `FullyQualifiedName~SteamUiModule|FullyQualifiedName~SteamUiPatchManagerTests|FullyQualifiedName~SteamUiBridgeHostTests`.
- **Plan v2:** B049 (indexes); B054 (module add and remove, manager unregistration, bridge vocabulary swap), with the host side in B086.
- **Related:** U02A-SUTC-032, U02A-SUTC-033, plan claim C20, TOOLKITCS-044, STEAMHOST-006 (decision: register on readiness).

### TOOLKITCS-044: Quarantine is held by the runtime but retraction is mirrored by WSGM

- **Severity:** medium
- **Where:** `tk/SteamUiModuleRuntime.cs:42,440-471`; `tk/SteamUiPatchManager.cs` (switch handling); WSGM `src/WSGM/Shell/SteamUiSessionHost.cs:68,1460-1530` (`_failedPatchIds`, `OnModuleFailed`, `Quarantined`, `SetPatchStates`).
- **Problem:** Quarantine is permanent per runtime, but WSGM has to mirror `_failedPatchIds` so its own `SetPatchStates` does not remount a quarantined surface. The policy lives in two places and drifted once already (the comment above `OnModuleFailed`).
- **Best solution:**
  - `SteamUiModuleRuntime` takes the `SteamUiPatchManager` in its constructor. `FailModule` calls `manager.Fault(patchId, reason)` for each of the failing module's patches.
  - `SteamUiPatchManager.Fault(string patchId, string reason)` sets a sticky `Faulted` flag on the entry (for the manager's lifetime) and queues a synchronization; a patch is wanted only when `globalEnabled && entry.Enabled && !entry.Faulted`. The snapshot shows the reason.
  - No reset API (R4). Quarantine lives with the registered patch entry. For built-in modules that is the runtime instance, which in WSGM is the whole session (the host is built once per `ShellSession`), so the reset is a WSGM restart, the same as today. A plugin module registered on readiness (TOOLKITCS-041) loses its entries, fault included, when it is removed, so a plugin that restarts and registers again starts clean; that follows from unregistration and needs no reset call.
  - WSGM deletes `_failedPatchIds`, `_failedPatchGate`, `Quarantined` and the `SetPatchEnabled(..., false)` loop in `OnModuleFailed`; `OnModuleFailed` keeps only its log line.
- **Tests:** in `tkt/SteamUiPatchManagerTests.cs`: a faulted patch stays removed after `SetPatchEnabled(id, true)`; runtime test that a throwing publication faults the module's patches. Toolkit filter `FullyQualifiedName~SteamUiPatchManagerTests|FullyQualifiedName~SteamUiModule`; parent filter `FullyQualifiedName~SteamUiSessionHost`.
- **Plan v2:** B054.
- **Related:** U02A-SUTC-034, plan claims C21/C41, R4 (justification corrected by the verifier), STEAMHOST-004, critic conflict 5.

### TOOLKITCS-055: File picker blocks, cannot cancel and misses redirected Downloads

- **Severity:** medium (verifier: wider than stated, the blocking part is TOOLKITCS-V-002)
- **Where:** `tk/Surfaces/SteamFilePickerSurface.cs:63-103` (`ListPlaces`), `:109-161` (`ListFolder`), `:200-222` (handlers, `Task.FromResult(ListPlaces())`); `tk/Surfaces/SteamSurfaceJsonContext.cs`; `tk/SteamUiAssets/Source/file-picker.ts`.
- **Problem:** `listPlaces` calls `DriveInfo.IsReady` and free space synchronously (a disconnected network drive blocks). A listing cannot be cancelled once started. `\\?\` paths reach the page in their long form. Downloads is `profile\Downloads`, so a redirected Downloads folder is not offered.
- **Best solution:** Follow critic conflict 7 and plan v2 B059 (supersedes the Codex B2 picker and R8's "More" paging).
  - Dropped by maintainer decision (security theater, DECISIONS.md): replacing `ex.Message` with fixed refusal strings and refusing device namespaces (`\\.\`, `\\?\GLOBALROOT`, `\Device\`, `\??\`). The page keeps showing the exception's message, and a device path is listed or reported inaccessible like any other path. No paging (decided, as plan v2 already said).
  - C#: both handlers start their work with `Task.Run` (today only `listFolder` does; `listPlaces` runs `ListPlaces()` inline on the bridge pump, see TOOLKITCS-V-002) and await it with `.WaitAsync(cancellationToken)`. There is no C# timer: the page's existing bridge request timeout sends `cancel`, the runtime cancels the request token, `WaitAsync` throws, and the runtime's existing `OperationCanceledException` path returns without answering (the page has already rejected that request). A worker result that arrives later is dropped with its task. The picker never cancels on navigation; stale tickets are dropped on the page.
  - Outcomes the page can receive: `Listed | Empty | Inaccessible`; `Inaccessible` carries the exception's message as today. No `TimedOut` or `Cancelled` outcome: a cancelled request is never answered, so those values could not reach the page (the B059 spec lists them; they are dropped as dead).
  - Path rules: `\\?\X:\...` and `\\?\UNC\server\share\...` are normalized to ordinary paths so the page shows and returns the plain form. No namespace refusal list. Mapped drives, redirected known folders and reachable UNC keep working.
  - Downloads via `SHGetKnownFolderPath(FOLDERID_Downloads)` through a plain `LibraryImport`, falling back to `profile\Downloads` only when the call fails.
  - The sorted listing returns whole through the existing chunked delivery (no cap after TOOLKITCS-031).
  - JS (toolkitjs): render the whole list as today (no paging, no "More"), a ticket also guards `listPlaces`, an unmount effect settles `null` once, explicit selection or cancel settles once.
  - No spool, worker caps, `ProviderBusy`/`ProviderCapacity`, injected clock or `FilePickerPathPolicy` type. Library folders keep refusing UNC.
- **Tests:** new `tkt/SteamFilePickerTests.cs`: a 450-entry temp tree lists in order; hidden and system entries skipped; extension filter; `\\?\` and `\\?\UNC\` normalization; an unreadable folder answers `Inaccessible` with its message; a blocking enumerator seam (internal) plus a cancelled request token: the handler returns at the cancel, the bridge pump is not blocked, and no answer is sent when the worker finishes. `eng/check-file-picker.mjs` (new) for two directories then Back with a stale listing ignored, and cancel or unmount settling `null` once. Toolkit filter `FullyQualifiedName~SteamFilePicker`. Manual M01-31 as rewritten in plan v2 section 5.
- **Plan v2:** B059 (merged with TOOLKITJS-B8).
- **Related:** U03B-SUTS-004, plan claim C34, R8 (corrected), critic conflict 7, TOOLKITJS-021, TOOLKITJS-V-008, LIBRARY-035, TOOLKITCS-V-002.

### TOOLKITCS-064: Probe and client-script tests check text fragments, not behaviour

- **Severity:** medium (test quality; verifier: most cited test lines are wrong)
- **Where:** probe and client tests across `tkt/` (for example `SteamClientTests.cs`, `SteamStartupMovieTests.cs`, `SteamHomeCarouselTests.cs`, `SteamLibraryBadgeTests.cs`, `SteamNavigationPanelTests.cs`, `SteamPageTests.cs`, `SteamScreensaverTests.cs`, `SteamStorageTests.cs:29-36`, `SteamPowerMenuTests.cs`, `SteamExtensionsTabTests.cs`, `SteamGameContextMenuTests.cs`, `SteamThemeStyleTests.cs`); restated predicates in `SteamGatePatchContractTests.cs`, `SteamPagePatchTests.cs`; `Assert.DoesNotMatch` against a test-local literal at `SteamPageTests.cs:65`.
- **Problem:** Tests assert fragments of generated JavaScript or restate gate predicates as strings. A probe that throws, invokes exports or answers the wrong shape still passes. The `DoesNotMatch` case tests nothing in production.
- **Best solution:** Re-derive every anchor first (the cited ranges mostly do not exist). Then run each gate and surface probe through `tkt/Fakes/NodeScript.cs` against fixture models (claimed, unclaimed, ambiguous, absent, already-owned) and run verify and remove predicates against status objects, following the existing pattern in `SteamWindowSurfaceTests.cs`. Keep source-token assertions only as secondary structural guards. Delete the `DoesNotMatch`-on-a-literal test.
- **Tests:** this finding is the tests. Run the whole toolkit .NET suite once at the end of the batch; child `npm run prelude:claims`.
- **Plan v2:** B062 (merged with TOOLKITJS-B11).
- **Related:** U02B-SUTC-005, U02B-SUTC-006, U03B-SUTS-006, U03A-SUTS-018, U03B-SUTS-042, plan claim C36.

### TOOLKITCS-066: Several production paths have no test

- **Severity:** medium (coverage)
- **Where:** `tk/SteamUiModuleRuntime.cs:155-273` (command path); `tkt/Fakes/QueueWire.cs` (cannot close or fault); `tkt/Fakes/FakePatch.cs:67-81` (throws before mutating); `tk/Surfaces/SteamNativeSurfaceCommands.cs` (`ReplayAsync`); `tk/Surfaces/SteamFilePickerSurface.cs`; `tk/Surfaces/SteamSoundOverrideSurface.cs`; `SteamClient` writes and timeout mapping; the storage probe's invocation safety; the bridge bootstrap error path.
- **Problem:** The runtime's refusal, quarantine, cancel, duplicate-sequence and undelivered-response paths, CDP close/fault and cancel-during-send, apply-then-fail removal and ordered teardown, replay, the picker, sound overrides and client write mapping are untested.
- **Best solution:** Each behaviour batch adds the test for the path it changes (B048: `QueueWire` close, fault and blocked send; B049: runtime command matrix through `EmitBindingPayload`, storage probe Node test, bridge bootstrap error; B053: client write and timeout mapping; B054: `FakePatch` mutate-then-fail and ordered teardown; B059: picker). B062 fills what remains: `SteamNativeSurfaceCommands.ReplayAsync` against `FakeSteamUiTransport` (ready, not ready, stale generation) and `SteamSoundOverrideSurface` publication and command shapes.
- **Tests:** as listed; B062 runs the whole toolkit suite once.
- **Plan v2:** B062 (with parts landing in B048, B049, B053, B054, B059).
- **Related:** U02A-SUTC-007, U02A-SUTC-037, U02A-SUTC-038, U03A-SUTS-019, U03A-SUTS-020, U03B-SUTS-005, U02B-SUTC-004.

### TOOLKITCS-V-001: Toolkit types reachable from `ISteamUiModule` are an unversioned plugin ABI

- **Severity:** medium (found by the verifier)
- **Where:** `src/WSGM/Shell/PluginPackageLoader.cs:172-176` (toolkit assembly is host-owned for plugin load contexts), `:200-205` (manifest `apiVersion` is the only compatibility gate); `src/WSGM.Plugin.Sdk/PluginSteamUi.cs:51,59`; `src/WSGM.Plugin.Sdk/PluginManifestReader.cs:87-89`.
- **Problem:** Every plugin shares the host's SteamUiToolkit, so the closure reachable from `ISteamUiModule` is part of the plugin contract: `ISteamUiPatch`, `SteamUiPatchContext`, `SteamUiPatchBounds`, `SteamUiPatchProbeResult`, `SteamUiPatchOperationResult`, `SteamUiStatePublication`, `SteamUiCommandHandler`/`Delegate`/`Result`, `SteamUiBridgeRequest`, `SteamUiModuleBuilder`, `SteamUiPayloadReader`, `SteamUiPayload`, `SteamUiPatchEvaluation`, `SteamUiProbeJs`, `SteamGatePatch`, `SteamPagePatch`/`SteamPageProbe`, `SteamUiBridgeIdentity`, `SteamUiModuleResolver`, `SteamCef.JsString`. B053 to B056 change members on several. A plugin built against toolkit 0.1.0 would fail with `MissingMethodException` or `TypeLoadException` at load or first call instead of being refused by the manifest gate. The toolkit's own package version does not help; the loader ignores assembly versions by design.
- **Best solution:** Bump `PluginApi.Version` to 4 once, in B052, and state in the Plugin SDK README that API 4 includes this toolkit closure. All later contract-breaking toolkit batches (B053 to B056) stay inside unreleased API 4, so plugins see one bump and an older plugin is refused cleanly at manifest read. No compatibility facade. The C# public API inventory in the toolkit reference marks this closure as "plugin contract" (B177).
- **Tests:** in `tests\WSGM.Plugin.Sdk.Tests`, the existing manifest-version test updated to 4; parent filter `FullyQualifiedName~PluginHost|FullyQualifiedName~CommonPlugin`.
- **Plan v2:** B052 (bump), B177 (inventory marking).
- **Related:** plan claim C3 (incomplete), verify batch problem 3, SDK-018/019/020/030.

### TOOLKITCS-V-002: Command handlers run on the bridge's only request pump and block cancels

- **Severity:** medium (found by the verifier)
- **Where:** `tk/SteamUiBridge.cs:770-830` (`DispatchRequestsAsync` invokes `RequestReceived` inline); `tk/SteamUiModuleRuntime.cs:155-175` (`OnRequestReceived` calls `RespondAsync` inline), `:177-200` (`_inflight` registration inside `RespondAsync`).
- **Problem:** `RespondAsync` runs synchronously up to its handler's first `await`, on the bridge's single pump. A handler with a synchronous prefix (the picker's `Task.FromResult(ListPlaces())` on a disconnected network drive, or a WSGM hardware or COM call before its first await) stalls every later request and every `cancel`, so a running handler cannot be cancelled.
- **Best solution:** Keep the dispatch order and fix the blocking where it is; do not move handler starts off the pump.
  - Why not `Task.Run(() => RespondAsync(...))` (the B049 spec's wording): the page sends row commands without awaiting the previous one (`sendCommand` in `components.ts`), and the authorizer orders them only at receipt. Today each handler's synchronous prefix runs in arrival order on the pump; a `Task.Run` from the pump thread goes to that thread's local queue and pool workers can start two accepted writes for the same control in either order, so a slider could finish on an older value. That would be a new defect in device writes, to remove a stall nobody has observed outside the picker.
  - B049: `SteamFilePickerSurface`'s `listPlaces` handler starts `ListPlaces()` with `Task.Run` like `listFolder` already does (the only known synchronous blocker; the rest of the picker work stays in TOOLKITCS-055/B059).
  - B049: document on `SteamUiCommandHandler` and `SteamUiModuleBuilder.Command` that a handler is invoked on the bridge's request pump in arrival order and must return its task promptly; blocking reads go on `Task.Run` inside the handler, while writes keep their order through the backend's own serialization.
  - B049: check the parent's handlers (`src/WSGM/Shell/*Surface*.cs`, `NativeQam*.cs`, `Steam*Service*.cs` registered through `SteamUiModuleBuilder.Command` or `new SteamUiCommandHandler`) for blocking Win32, WMI, COM or file I/O before their first `await`. A blocking read is moved onto `Task.Run` inside that handler; a write path is left in order. List what was found in the commit message.
  - No runtime change, no new state.
- **Tests:** in `tkt/SteamUiModuleRuntimeTests.cs` (new): two requests for one command start their handlers in arrival order, and a `cancel` for a handler that is awaiting is processed. The blocking-enumerator case is tested with the picker in B059 (TOOLKITCS-055). Toolkit filter `FullyQualifiedName~SteamUiModule`.
- **Plan v2:** B049, with the B049 spec's `Task.Run` dispatch replaced as above. B059's "the handler is already off the bridge pump since B049" then refers to the picker handlers only.
- **Related:** TOOLKITCS-055, critic CRIT-006 (request tokens are disposed, not cancelled, on completion; unchanged here).

---

## Low

### TOOLKITCS-004: An answered JavaScript exception marks the shared channel Incompatible

- **Severity:** low (verifier lowered from medium; readiness check is at `tk/Surfaces/SteamSharedContext.cs:12-17`)
- **Where:** `tk/SteamUiCdpConnection.cs:235-260` (`EvaluateAsync` throws `InvalidDataException` for `exceptionDetails`); `tk/PersistentSteamUiTransport.cs:227-237` (catch sets `Incompatible`, keeps `ConsecutiveTimeouts`).
- **Problem:** One expression's exception sets the shared channel to `Incompatible` and does not reset the unanswered run. `SteamSharedContext.IsReadyAt` requires `Ready`, so Quick Access replay, game raise and side-menu snapshots return false until another evaluation succeeds. The window is short (any later success restores `Ready`, and probes catch their own errors), but the signal is wrong: an answered error proves the renderer is alive.
- **Best solution:** `SteamUiCdpConnection.EvaluateAsync` returns `(string? Value, string? Error)` for an answered result, with `exceptionDetails` as `Error`, and throws only for protocol or framing faults. The transport treats any answered result as alive: `SetHealth(Ready)`, `ResetTimeouts`, and returns the error in the result (`Reachable=true` in B048, `Dispatch=Answered` from B053). CDP `error` replies and framing faults keep today's handling until TOOLKITCS-003 reclassifies them.
- **Tests:** update `tkt/PersistentSteamUiTransportTests.cs` around `FailFirstEvaluation` (name the expectation change in the commit so it is not read as a regression): an answered exception leaves health `Ready` and clears a pending timeout run. Toolkit filter `FullyQualifiedName~PersistentSteamUiTransportTests`.
- **Plan v2:** B048.
- **Related:** NEW (U02A-SUTC-040 covers only the timeout counter), verify batch problem 9.

### TOOLKITCS-005: Every one-shot request starts a reconnect loop

- **Severity:** low
- **Where:** `tk/PersistentSteamUiTransport.cs:140-161` (`SubscribeAsync`), `:330-345` (`LeaseAsync`), `:750-770` (`ReleaseAsync`).
- **Problem:** `LeaseAsync` goes through `SubscribeAsync`, which starts a reconnect loop when none runs. The loop and the request then race on `ConnectGate`, and the lease release cancels the loop. One wasted task and a discovery race per one-shot call.
- **Best solution:** `LeaseAsync` takes a request-only subscription: increment `channel.Subscribers` under `channel.Sync` without calling `StartReconnectLocked`, and release through the same `ReleaseAsync` (zero subscribers still detaches and closes the connection). The request connects through `EnsureConnectedAsync` as it does today. Persistent subscribers keep starting the loop. Per-call connect for one-shots stays.
- **Tests:** count `FixtureDiscovery` calls in `tkt/PersistentSteamUiTransportTests.cs`: one `EvaluateAsync` with no subscriber causes one discovery and leaves no loop running. Toolkit filter `FullyQualifiedName~PersistentSteamUiTransportTests`.
- **Plan v2:** B048.
- **Related:** NEW (related U02A-SUTC-010), plan claim C5.

### TOOLKITCS-006: An absent target escalates the reconnect backoff like a failure

- **Severity:** low
- **Where:** `tk/PersistentSteamUiTransport.cs:15-21` (`RetryDelays`), `:420-486` (`ReconnectLoopAsync`), `:535-545` (absent target returns null).
- **Problem:** While Steam starts or Big Picture is not up yet (`requireMainWindow`), discovery finds no target and the loop escalates 1/4/16/30 s, so attachment can lag up to 30 s after the window appears.
- **Best solution:** In `ReconnectLoopAsync`, distinguish "no endpoint" (`EnsureConnectedAsync` returned null without throwing) from a connection failure (exception) or a dropped connection. For no endpoint, wait `RetryDelays[0]` and do not advance `attempt`. Failures and unstable connections escalate as today.
- **Tests:** there is no delay seam today (`RetryDelays` is a static array and `RetryDelay(int)` only maps an attempt). Add an optional `IReadOnlyList<TimeSpan>? retryDelays` parameter to the existing internal test constructor (production constructors pass today's 1/4/16/30 s), and have the test pass distinct millisecond steps: discovery returning null three times, then an endpoint, attaches after three first-step waits, while three thrown connection failures escalate. TOOLKITCS-068 reuses this parameter. Toolkit filter `FullyQualifiedName~PersistentSteamUiTransportTests`.
- **Plan v2:** B048.
- **Related:** U02A-SUTC-013.

### TOOLKITCS-007: Dead generation-notification lane and pump in the transport

- **Severity:** low (simplification)
- **Where:** `tk/PersistentSteamUiTransport.cs:57-70` (`_notificationEvents`), `:115-118` (its pump), `:886-889` (`RaiseNotificationReceived`), `:127-133` (`NotificationReceived` doc); consumer `tk/SteamUiBridge.cs:706-715`.
- **Problem:** The `_notificationEvents` lane delivers generation-method notifications through `NotificationReceived`, but the only consumer (the bridge) ignores everything except `Runtime.bindingCalled`, which travels on `_bindingEvents`. No WSGM consumer exists.
- **Best solution:** B048 deletes `_notificationEvents`, its pump, `RaiseNotificationReceived` and their disposal lines; generation methods only update generations and raise `GenerationChanged`. B053 renames `ISteamUiTransport.NotificationReceived` to `BindingCalled` (documented as binding calls only) and updates the bridge and the four WSGM transport fakes (`tests/WSGM.Tests/Shell/RunningApplicationTargetTests.cs`, `SteamDownloadSortPatchTests.cs`, `SteamUiSessionHostTests.cs` twice).
- **Tests:** existing bridge tests keep passing; one transport test that a `Page.frameNavigated` notification raises no event. Toolkit filter `FullyQualifiedName~PersistentSteamUiTransportTests|FullyQualifiedName~SteamUiBridgeHostTests`.
- **Plan v2:** B048 (lane), B053 (rename; verify batch problem 8).
- **Related:** NEW, plan claim C7.

### TOOLKITCS-008: A burst of one role's generation snapshots can evict another role's

- **Severity:** low
- **Where:** `tk/PersistentSteamUiTransport.cs:48-55` (`_generationEvents`, `DropOldest(64)`), `:891-894` (`RaiseGenerationChanged`); consumer `tk/SteamUiBridge.cs:831-856`.
- **Problem:** Both roles share one lossy queue. Many MainWindow navigations can evict a SharedJSContext snapshot; the bridge invalidates readiness only from this event and would keep delivering under stale generations until the next one.
- **Best solution:** Latest wins per role. Give `TargetChannel` a `PendingGeneration` slot. `RaiseGenerationChanged` stores the snapshot in the slot under `channel.Sync`; if the slot was empty it writes the role to a `Channel<SteamUiTargetRole>` bounded to the role count (it can never be full, because each role is queued at most once). The pump reads a role, takes and clears that slot, and raises `GenerationChanged`. Intermediate snapshots of one role are still skipped (as today), but a role's latest snapshot is never lost.
- **Tests:** raise 100 MainWindow snapshots and one SharedJSContext snapshot while the handler is blocked; after release, both roles' latest snapshots are delivered. Toolkit filter `FullyQualifiedName~PersistentSteamUiTransportTests`.
- **Plan v2:** B048.
- **Related:** NEW.

### TOOLKITCS-009: A non-loopback listener row can refuse Steam's own debug port

- **Severity:** low
- **Where:** `tk/SteamCef.cs:108-163` (`IsSteamPortOwner`); `tk/NativeTcp.cs:20-24,95-138`.
- **Problem:** Candidates are every listener on port 8080, loopback first and the rest in table order. A process bound to a specific non-loopback address on 8080 (for example a LAN service on `192.168.x.y:8080`) is never reached by WSGM's connect to 127.0.0.1, yet when its row comes before Steam's wildcard row it decides the verdict, and WSGM refuses to attach to a Steam that does own the port.
- **Best solution:** `IsSteamPortOwner` first filters candidates to `LocalAddress == NativeTcp.Loopback` or `0` (0.0.0.0), the only rows a connect to 127.0.0.1 can reach, then keeps today's loopback-first order and verdicts. The review's other half, refusing `localhost` in `IsAllowedDebuggerUrl` against a `::1` squatter, is dropped by maintainer decision (security theater, DECISIONS.md); `localhost` stays accepted.
- **Tests:** in `tkt/SteamCefTests.cs`, a listener table with an unrelated process on a specific LAN address listed before Steam's wildcard row answers "owned by steamwebhelper". Toolkit filter `FullyQualifiedName~SteamCefTests`.
- **Plan v2:** B048 (owner filter only; no `localhost` change).
- **Related:** U02A-SUTC-014, U02A-SUTC-015, plan claim C6, verify batch problem 9.

### TOOLKITCS-013: `SetRuntimeBindingAsync` throws where `EvaluateAsync` returns a result

- **Severity:** low (verifier: the disposed check already exists through `SubscribeAsync`)
- **Where:** `tk/SteamUiTransportModels.cs:146-151` (`ISteamUiTransport.SetRuntimeBindingAsync` doc); `tk/PersistentSteamUiTransport.cs:250-283`.
- **Problem:** The method throws `InvalidOperationException` when the transport is closed and `IOException` when the target is absent, while `EvaluateAsync` returns results. The contract is undocumented; its single caller (the bridge) catches.
- **Best solution:** Keep the throwing contract and document it on the interface: `ObjectDisposedException` after disposal, `InvalidOperationException` with the closed reason while disabled, `IOException` when the target is absent, `OperationCanceledException` on cancel or timeout. No disposed check is added (it exists). Done with the B053 interface edits.
- **Tests:** none beyond the existing bridge tests.
- **Plan v2:** B053.
- **Related:** U02A-SUTC-049.

### TOOLKITCS-015: AddShortcut without a returned id adopts any single new shortcut

- **Severity:** low (verifier lowered from high: needs a client build that returns no id plus an outside add inside the same 100 ms window)
- **Where:** `tk/Client/SteamApps.cs:358-374` (`AddShortcut` script, `gained` adoption).
- **Problem:** When `AddShortcut` returns 0 and exactly one shortcut appears in the 2 s poll, the script adopts it and overwrites its name, target, start directory and arguments. A non-zero mismatching return is already refused.
- **Best solution:** When `returned` is 0, adopt the gained entry only if its exe and app name equal the request (compare with the same normalization the script uses for the target, case-insensitive path compare); otherwise answer `{ok:false, err:"unconfirmed", adopted:false}` and write nothing. C# maps that to `Confirmed=false` with outcome `Unknown` (the add may have happened; WSGM's library maps `Unknown` to "may not have been created; scan again" and never retries).
- **Tests:** NodeScript run of the real script: no-id plus a matching entry adopts; no-id plus a foreign entry leaves it untouched and reports unconfirmed. Toolkit filter `FullyQualifiedName~SteamClientTests`.
- **Plan v2:** B053.
- **Related:** U02B-SUTC-012, plan claim C29, LIBRARY-V-005.

### TOOLKITCS-021: Lossy reads collapse unreachable, refused and absent into empty values

- **Severity:** low
- **Where:** `tk/Client/SteamLibraryData.cs:96-121,177-182,250-255`; `tk/Client/SteamApps.cs:526-547,584-624,840-860`; `tk/Client/SteamCurrentPage.cs:82-87`; WSGM `src/WSGM/Overlay/CardManagerView.cs:181`, `src/WSGM/Overlay/LibraryTabsView.cs:651,676,700`, `src/WSGM/Shell/SteamArtworkBrowserSource.cs:754`.
- **Problem:** `ListGamesAsync`, `ListCollectionsAsync`, `ListStoreTagsAsync`, `IsLoadedAsync` and the icon, account and logo reads return null, empty or 0 for every failure, so callers treat "Steam not reachable" as "no games".
- **Best solution:** Add `public readonly record struct SteamReadResult<T>(SteamUiDispatch Dispatch, T? Value, string? Error)` with `Succeeded => Dispatch == Answered && Error is null`. `Library.ReadGamesAsync` (already typed) stays; add typed `ReadCollectionsAsync`, `ReadStoreTagsAsync`; `Apps.ReadOfficialIconUrlAsync`, `ReadAccountIdAsync`, `ReadLogoPositionAsync` and `CurrentPage.GetAsync` return `SteamReadResult<T>`. Delete the lossy forms; `IsLoadedAsync` callers use `ReadGamesAsync` failure detail. The WSGM call sites switch to the typed reads and keep their current UI on failure (an empty list with the same visuals), but stop caching a failure as "no games".
- **Tests:** parser tables for each typed read (answered value, answered error, not sent). Toolkit filter `FullyQualifiedName~SteamLibraryReadTests|FullyQualifiedName~SteamClientTests`; parent filter `FullyQualifiedName~GameLibrary|FullyQualifiedName~Artwork`.
- **Plan v2:** B053.
- **Related:** U02B-SUTC-013, plan claim C11.

### TOOLKITCS-022: App ids are `long`, `int` or `uint` depending on the type

- **Severity:** low
- **Where:** `tk/Client/SteamLibraryData.cs:23,32,63-71` (`GamesExpression`, fallback `a.appid>=2147483648`); `tk/Client/SteamCurrentPage.cs:14,62`; `tk/Client/SteamDownloadActivity.cs:131`; WSGM `src/WSGM/Overlay/ArtworkView.cs`, `LaunchWrapperView.cs`, `OverlaySubView.cs` (`SteamLibraryApp.AppId`).
- **Problem:** `GamesExpression` returns raw signed ids from the page, so its fallback test for shortcut ids (`>=2147483648`) never matches; `SteamCurrentPage` drops negative ids; types disagree.
- **Best solution:** Normalize in the page scripts with `>>>0` at every place an app id leaves the page (games, current page, downloads), and use `uint` in every public record (`SteamLibraryApp`, `SteamCollectionInfo`, `SteamCurrentApp`, `SteamDownloadOverview`). The shortcut test becomes `IsShortcutAppId(uint)` on the C# side. Parent call sites change type only.
- **Tests:** NodeScript run of `GamesExpression` against a fixture with a negative (shortcut) appid returns the unsigned value and is classified as a shortcut. Toolkit filter `FullyQualifiedName~SteamLibraryReadTests`.
- **Plan v2:** B053.
- **Related:** U02B-SUTC-014, plan claim C12.

### TOOLKITCS-026: Per-resource semaphores and `ResourceKey` duplicate the scheduler gate

- **Severity:** low (simplification)
- **Where:** `tk/SteamUiPatchManager.cs:228-229,337,543-544,786` (`_resourceGates`); `ISteamUiPatch.ResourceKey` (`:178-179`) and every implementer; WSGM `src/WSGM/Core/SteamDownloadSort.cs`, `src/WSGM/Core/SteamInputGlyphStylePatch.cs`; `SteamGatePatch` constructor `resourceKey` callers `src/WSGM/Shell/SteamChordResetSurface.cs:37`, `SteamControllerCapsSurface.cs:57`; `tkt/SteamQuickAccessRowPatchTests.cs` (pins it).
- **Problem:** Every path that takes a resource gate already holds `_schedulerGate`, so the per-resource gates never contend. `ResourceKey` exists only to key them.
- **Best solution:** Delete `_resourceGates`, the waits and releases, `ISteamUiPatch.ResourceKey`, the `SteamGatePatch` constructor's `resourceKey` parameter, and every implementer's property (toolkit surfaces and the WSGM files above). Delete the test that pins the key.
- **Tests:** existing manager tests; compile the parent. Toolkit filter `FullyQualifiedName~SteamUiPatchManagerTests|FullyQualifiedName~SteamQuickAccessRowPatchTests`; `dotnet build WSGM.slnx -c Release -p:SkipNativeArtifacts=true`.
- **Plan v2:** B054 (verify batch problem 1: the WSGM implementers and gate callers are in the batch).
- **Related:** U02A-SUTC-054, plan claim C19, TOOLKITCS-V-001.

### TOOLKITCS-028: Kill switch, generation change and timeout share one diagnostic

- **Severity:** low
- **Where:** `tk/SteamUiPatchManager.cs:759-766` (catch `OperationCanceledException`).
- **Problem:** All three cancellation sources record "Patch operation timed out.", so a log cannot tell a user switching a feature off from a hung renderer.
- **Best solution:** In the catch, decide by what is observable: switch off (`!_globalEnabled` or `!entry.Enabled`, or `Faulted`) records "Patch operation cancelled by its switch."; a changed `entry.GenerationEpoch` records "Steam UI generation changed during the operation."; otherwise "Patch operation timed out after <phase timeout>." The state choices follow TOOLKITCS-027.
- **Tests:** three manager tests driving each source with a hanging `FakePatch`. Toolkit filter `FullyQualifiedName~SteamUiPatchManagerTests`.
- **Plan v2:** B054.
- **Related:** U02A-SUTC-018, plan claim C17.

### TOOLKITCS-029: Patch state logging runs under the entry lock and reads a static

- **Severity:** low
- **Where:** `tk/SteamUiPatchManager.cs:1081-1108` (`SetStateLocked`).
- **Problem:** `SteamUiLog.Change` runs while `entry.Sync` is held, and the warning level consults the static `SteamUiTransportSession.IsClosedReason`.
- **Best solution:** `SetStateLocked` builds the snapshot and returns the log line (key, text, warn flag) instead of writing it; callers write it after leaving the lock. The warn flag uses the role's transport snapshot (`Health == Idle` means the host closed the transport) instead of the static; B053 makes that switch when it deletes the session, B054 moves the write out of the lock. The full fingerprint stays in the snapshot and only the log line is bounded (TOOLKITCS-V-004).
- **Tests:** a manager test whose log sink, on each state line, calls `manager.GetSnapshots()` on another thread and requires it to finish within one second (`GetSnapshots` takes every entry lock, so it would time out if the line were written under one). Put it in the log-sink collection (TOOLKITCS-043). Toolkit filter `FullyQualifiedName~SteamUiPatchManagerTests`.
- **Plan v2:** B054 (static removal rides B053).
- **Related:** U02A-SUTC-055, NEW for the static coupling, TOOLKITCS-014.

### TOOLKITCS-030: `SetPatchEnabledAsync` runs a full pass over every patch

- **Severity:** low
- **Where:** `tk/SteamUiPatchManager.cs:374-387` (`SetPatchEnabledAsync`), `:355-372` (`SetGlobalEnabledAsync`); WSGM callers `src/WSGM/Shell/SteamUiSessionHost.cs:862,956` (global form); `tests/WSGM.Tests/Shell/SteamDownloadSortPatchTests.cs:22` (only caller of the per-patch form).
- **Problem:** The awaited per-patch switch synchronizes every patch. The review proposed "wait for the next completed pass" once the manager owns the only loop, but the verifier showed that deadlocks: WSGM's `Synchronized` handler calls `SetGlobalEnabledAsync` from inside the loop, and waiting for a pass the loop must still run never completes.
- **Best solution:** Keep the awaited forms running their own pass under `_schedulerGate`, as today, through the same pass method the loop uses (TOOLKITCS-025's two-loop pass). The `Synchronized` event does not exist today: B054 adds `public event EventHandler? Synchronized` to `SteamUiPatchManager` for STEAMHOST-004 (WSGM's `SynchronizeLoopAsync` and its reconciliation move into its handler). The manager's existing queued pass (`QueueSynchronization`) raises it after the pass has released `_schedulerGate`, through `Task.Run` (never inline in the pass task), so a handler that awaits a switch takes the gate normally; a throwing handler is logged and does not stop the loop. The full-pass cost is accepted: the per-patch form has no production caller.
- **Tests:** a manager test whose `Synchronized` handler awaits `SetGlobalEnabledAsync(false)` completes; parent filter `FullyQualifiedName~SteamUiSessionHost`.
- **Plan v2:** B054 (verify batch problem 5; R5 as amended).
- **Related:** U02A-SUTC-021, plan claim C18, R5, STEAMHOST-004.

### TOOLKITCS-031: Caps that refuse or drop valid content

- **Severity:** low (no-arbitrary-limits)
- **Where:** enforced: `SteamUiPatchBounds.MaximumExpressionCharacters` (`tk/SteamUiPatchManager.cs:56-92,155-159`; WSGM raises it to 2 MiB in `src/WSGM/Core/SteamInputGlyphStylePatch.cs:52-55`); `SteamUiBridgeHost.MaximumDeliveryCharacters` 32 M (`tk/SteamUiBridge.cs:219-225`); `SteamUiExtensionHost.MaximumScriptCharacters` (`tk/SteamUiExtensionHost.cs:36-41`); side-menu `8192`/`33`/`>32` (`tk/Surfaces/SteamSideMenuSnapshot.cs` `Parse` and `ReadExpression`); `windows.length>32` (`tk/Surfaces/SteamGameWindowActivation.cs:50`, `tk/Surfaces/SteamNativeSurfaceCommands.cs:93`). Documented only: "at most 8" targets (`SteamControllerTargetRow.cs:23`), "16" zones (`SteamDeviceControlsRow.cs:62`), "64" options (`SteamPowerProfileRow.cs`, `SteamHybridCoreRow.cs`, `SteamCpuBoostRow.cs`, `SteamResolutionRow.cs`), "256 of up to 160" folds (`SteamPanelFoldsSurface.cs:19`), theme 96/4 MiB/32/64 (`SteamThemeStyleSurface.cs:9-22`), screensaver "four" rows and 32-character ids (`SteamScreensaverSurface.cs`), 256-character routes (`SteamNavigationPanelSurface.cs:29-30`).
- **Problem:** These caps drop or refuse valid content (a large glyph stylesheet, a long listing, a 33rd overlay window) and none protects a transport: delivery is already chunked.
- **Best solution:** Delete every enforced cap above and the documented caps in XML docs and the toolkit `reference.md`. Keep only the D2 transport bounds: CDP 8 MiB response and 1 MiB notification reads, and request backpressure (32 outstanding CDP requests, 64 queued bridge requests), plus the 30 s operation ceiling. `SteamUiPatchBounds` itself goes in TOOLKITCS-032's contract change: `ISteamUiPatch.OperationTimeout` (default 8 s via `SteamUiPatch.DefaultTimeout`) replaces it, and `MaximumDiagnosticCharacters` becomes `SteamUiShared.MaximumDiagnosticLength` for log lines only. Do not adopt U02A-SUTC-061's manifest cap or U02B-SUTC-019's string bounds. The overlay activation identity cap is TOOLKITCS-054.
- **Tests:** side-menu parse of 40 windows; a glyph patch with a 3 MiB expression applies through `FakeSteamUiTransport`; an extension with a 300 KiB script is discovered. Toolkit filter `FullyQualifiedName~SteamWindowSurfaceTests|FullyQualifiedName~SteamUiPatchManagerTests|FullyQualifiedName~SteamUiExtensionHostTests`.
- **Plan v2:** B054 (expression cap with `SteamUiPatchBounds`), B056 (delivery cap, extension, side-menu and window caps, documented caps). Remaining bounds are decided: D2 accepts exactly the plan v2 list, whose toolkit entries are the CDP reads and request backpressure above; every other cap goes.
- **Related:** U03A-SUTS-009, U03B-SUTS-010, U03B-SUTS-012, U02B-SUTC-019, R12, TOOLKITCS-V-004, TOOLKITJS-V-008.

### TOOLKITCS-032: `Unique` and `Version` carry no information

- **Severity:** low (simplification; verifier corrected the premise)
- **Where:** `tk/SteamUiPatchManager.cs:110-120,638` (`SteamUiPatchProbeResult.Unique`, the check), every probe that sets `Compatible` and `Unique` equal (`SteamGatePatch.cs`, `SteamUiPatchEvaluation.cs:150-155`, `SteamOverlayActivationPatch.cs:80`, test fake); `ISteamUiPatch.Version` and `SteamUiPatchSnapshot.Version` (logged as `v1`); WSGM `src/WSGM/Core/SteamDownloadSort.cs:30,304` (`Version` returns `ScriptVersion` = 5).
- **Problem:** `Unique` is always equal to `Compatible`. `Version` is 1 everywhere except WSGM's download sort (5); it is only logged, and the download-sort script embeds its own `dlSortVersion`.
- **Best solution:** Delete `SteamUiPatchProbeResult.Unique` (the manager checks `Compatible` and a non-empty fingerprint), `ISteamUiPatch.Version`, `SteamUiPatchSnapshot.Version` and the `v{n}` in the log line. The fingerprint carries the revision. Delete the `Version` member from `SteamDownloadSortPatch` (its `ScriptVersion` constant stays for the script). Lands with the contract change under Plugin API 4 (TOOLKITCS-V-001).
- **Tests:** compile toolkit and parent; existing manager tests. Toolkit filter `FullyQualifiedName~SteamUiPatchManagerTests`; parent filter `FullyQualifiedName~SteamDownloadSort`.
- **Plan v2:** B054.
- **Related:** U03A-SUTS-022, NEW for `Unique`, verify batch problem 1.

### TOOLKITCS-033: A probe the page answered with an error is recorded as an absent target

- **Severity:** low
- **Where:** `tk/SteamUiPatchEvaluation.cs:62-65,130-138` (`EvaluateOutcomeAsync`, `EvaluateProbeAsync`); `tk/Surfaces/SteamGatePatch.cs:175-183` (duplicate parser, TOOLKITCS-035).
- **Problem:** `!result.Reachable || result.Value is null` treats a reachable evaluation with an error as "no target present", so the manager re-probes on the settle schedule and never records the page's error as an incompatibility.
- **Best solution:** In `EvaluateProbeAsync`, an answered result with an error returns `new SteamUiPatchProbeResult(TargetPresent: true, Compatible: false, null, error)` (Incompatible, with the page's error as the diagnostic). Only not-sent, closed or unanswered results are absent. In B049 the test is `Reachable && Error is not null`; B053 rewrites it to `Dispatch == Answered && Error is not null`.
- **Tests:** a probe whose evaluation answers an error records `Incompatible` with that diagnostic. Toolkit filter `FullyQualifiedName~SteamUiPatchManagerTests`.
- **Plan v2:** B049.
- **Related:** U02A-SUTC-022, U02B-SUTC-016.

### TOOLKITCS-034: JSON readers throw `InvalidOperationException` on wrong root or value kinds

- **Severity:** low
- **Where:** `tk/SteamUiPatchEvaluation.cs:67-95,183-198,229-256` (`EvaluateOutcomeAsync`, `IsOne`, `Flag` and siblings); `tk/SteamUiBridge.cs:719-727` (`OnNotificationReceived`: `TryGetProperty` on the root, `name.GetString()`).
- **Problem:** `TryGetProperty` on a non-object root and `GetString` on a non-string value throw `InvalidOperationException`, which handlers written for `JsonException` do not catch.
- **Best solution:** Check kinds before reading: every helper that reads a property returns false when `root.ValueKind != JsonValueKind.Object`; `EvaluateOutcomeAsync` treats a non-object root as a failure with the bounded raw value as diagnostic; the bridge requires an object root and `name.ValueKind == JsonValueKind.String` before `GetString`. No extra catch blocks.
- **Tests:** helpers return false for array, string and number roots; the bridge ignores a binding with an array root and a numeric `name`, followed by a valid sentinel request that is processed (also covers TOOLKITCS-067's async negative). Toolkit filter `FullyQualifiedName~SteamUiBridgeHostTests|FullyQualifiedName~SteamUiPatchManagerTests`.
- **Plan v2:** B049.
- **Related:** U02A-SUTC-023, U02A-SUTC-051, plan claim C24.

### TOOLKITCS-035: Two probe-result parsers, one keeping the raw unbounded page value

- **Severity:** low (duplication)
- **Where:** `tk/Surfaces/SteamGatePatch.cs:164-206` (`internal static ProbeAsync`) vs `tk/SteamUiPatchEvaluation.cs:115-161` (`EvaluateProbeAsync`); callers in `SteamGatePatch`, `SteamUiBridgePatch`.
- **Problem:** The gate copy duplicates the parser and stores the raw page value unbounded as its diagnostic.
- **Best solution:** Delete `SteamGatePatch.ProbeAsync`. Gates and the bridge patch call `SteamUiPatchEvaluation.EvaluateProbeAsync(context, SteamUiTargetRole.SharedJsContext, expression, compatible, fingerprint, "SharedJSContext is unavailable.", cancellationToken)`, which bounds its diagnostic and gets TOOLKITCS-033/034 for free.
- **Tests:** existing gate tests. Toolkit filter `FullyQualifiedName~SteamGatePatch|FullyQualifiedName~SteamStorageTests`.
- **Plan v2:** B049.
- **Related:** NEW.

### TOOLKITCS-037: Bootstrap page errors are unlogged and removal ignores its answer

- **Severity:** low
- **Where:** `tk/SteamUiBridge.cs:378-388` (`BootstrapAsync` returns false on any unreachable or errored result); `tk/Surfaces/SteamUiBridgePatch.cs:93` (fixed "Native-QAM bridge handshake failed."); `tk/SteamUiBridge.cs:690-698` (removal discards the evaluation result).
- **Problem:** A bootstrap the page answered with an error returns `false` with no line, and the manager logs only the fixed handshake text. Removal never checks the `{ok:true}` it asks for.
- **Best solution:** `BootstrapAsync` returns `(bool Ready, string? Error)` internally; the page's error (or "Steam UI target unavailable") becomes the apply diagnostic, so the manager's state line carries it. `SteamUiBridgePatch.ApplyAsync` uses that text instead of the fixed one. Removal parses the result with `SteamUiPatchEvaluation.IsSuccessful` and logs one warning with the page's answer when it is not `{ok:true}`; it does not retry.
- **Tests:** a fake transport answering the bootstrap with an error: the patch snapshot's diagnostic contains it; removal answered `{ok:false}` logs a warning. Toolkit filter `FullyQualifiedName~SteamUiBridgeHostTests`.
- **Plan v2:** B049.
- **Related:** U02A-SUTC-017 (removal), NEW (bootstrap), plan claim C25.

### TOOLKITCS-042: Disabled, quarantined and unregistered commands share one refusal text

- **Severity:** low
- **Where:** `tk/SteamUiModuleRuntime.cs:202-210` (`RespondAsync` refusal); `tk/SteamUiModule.cs:48-50` (`SteamUiCommandResult.Refused`); `tk/Surfaces/SteamPowerLimitSurface.cs:54-57` (`ISteamPowerLimitBackend` default reuses it for "mode selection unsupported").
- **Problem:** Three different causes, plus one unsupported-feature case, all answer "The requested semantic service is not active.", so a log or a row cannot say why.
- **Best solution:** One fixed reason per cause, chosen in `RespondAsync`: commands disabled keeps "The requested semantic service is not active." (the common case reads as today); quarantined is "This surface was turned off after an error."; no handler is "No handler is registered for this command."; the power-limit default returns "Mode selection is not supported on this device.". Expose them as static `SteamUiCommandResult` members next to `Refused`.
- **Tests:** runtime matrix test asserting each reason. Toolkit filter `FullyQualifiedName~SteamUiModule`.
- **Plan v2:** B049.
- **Related:** U02A-SUTC-035, U03A-SUTS-030.

### TOOLKITCS-043: The log sink is a non-volatile static and no test observes diagnostics

- **Severity:** low
- **Where:** `tk/SteamUiLog.cs:46-56` (`_sink`).
- **Problem:** `_sink` is written after construction without a memory barrier, so a reader on another thread can miss the host's sink; no test asserts log output.
- **Best solution:** Keep the single process sink (R10: write-only diagnostics, one per process; injecting `ISteamUiLog` into every owner is churn with no defect). Mark `_sink` `volatile` (or use `Volatile.Read/Write`). Tests that observe the sink go into one xUnit collection with `DisableParallelization = true` (B062).
- **Tests:** a log-sink collection with the redaction test (TOOLKITCS-020) and the manager log-level test.
- **Plan v2:** B049 (volatile), B062 (collection).
- **Related:** U02A-SUTC-006, plan claim C4 (the `ISteamUiLog` parameter is dropped), R10.

### TOOLKITCS-045: Command builders do not validate arguments; failures without a reason are accepted

- **Severity:** low
- **Where:** `tk/SteamUiModuleBuilder.cs:85-112` (`Command<T>`, `Command`); `tk/SteamUiModule.cs:33-37` (`SteamUiCommandResult`, "never null on failure").
- **Problem:** `Publication` validates its arguments but `Command` does not check `patchId` or `command`. `new SteamUiCommandResult(false, null)` and `default(SteamUiCommandResult)` violate the documented "never null on failure".
- **Best solution:** Plan v2 replaces the review's "throw in the constructor" (verify batch problem 4: WSGM builds refusals from nullable details, and a throw inside a handler would quarantine the module for the session). Builders add `ArgumentException.ThrowIfNullOrWhiteSpace(patchId)` and `(command)` plus the existing null checks on delegates and readers; nothing else throws. (The B049 spec says builders only null-check delegates; the two identity checks run once at module construction, never inside a handler, so they cannot quarantine anything, and `Publication` already checks its id the same way. No parent command is registered with a blank name.) The runtime normalizes: in `RespondAsync`, a failed outcome with `Error is null` (including `default`) becomes the fixed "no reason reported" text the log line already uses. Update the record's doc to say the runtime fills a missing reason.
- **Tests:** a handler returning `default(SteamUiCommandResult)` is answered with "no reason reported" and the module is not quarantined; builder null and blank argument checks. Toolkit filter `FullyQualifiedName~SteamUiModule`.
- **Plan v2:** B049.
- **Related:** U02A-SUTC-063, verify batch problem 4.

### TOOLKITCS-046: Internal module builders duplicate the public builder

- **Severity:** low (duplication)
- **Where:** `tk/Surfaces/SteamSurfaceModule.cs:112-195` (`SteamPayloadReader<T>`, `Declare<T>`, `Publication<T>`, `Command<T>`, `Command`, `TryReadValueWrite`, `Invalid`, `SteamSettingPersistence`) vs `tk/SteamUiModuleBuilder.cs:14-112`.
- **Problem:** Most surfaces use an internal copy of the public builder; `SteamSoundOverrideSurface` and `SteamThemeStyleSurface` already use the public one.
- **Best solution:** Delete `SteamSurfaceModule.cs` and move each symbol: `SteamPayloadReader<T>` to the existing public `SteamUiPayloadReader<T>`; `Declare<T>` to `SteamUiModuleBuilder.Module<T>(id, patchId, enabled, read, typeInfo, patches, commands)`; `Publication<T>`, `Command<T>`, `Command` to the existing builder methods; `TryReadValueWrite` to `SteamUiPayload.TryReadValueWrite` (internal); `Invalid` to `SteamUiCommandResult.Invalid(string)`; `SteamSettingPersistence` into `Surfaces/SteamFrameLimitRow.cs` with unchanged values. Update every surface. Enumerate the old file's declarations before deleting it and account for each (CLAUDE.md move rule).
- **Tests:** `tkt/SteamSurfaceModuleTests.cs` and every surface test still pass. Toolkit filter `FullyQualifiedName~SteamSurfaceModuleTests|FullyQualifiedName~SteamChoiceRowTests`.
- **Plan v2:** B056.
- **Related:** NEW (related U03A-SUTS-016).

### TOOLKITCS-047: Generic power-preset row refuses WSGM's magic `custom` id

- **Severity:** low
- **Where:** `tk/Surfaces/SteamPowerPresetRow.cs:68-93`; TS `components.ts` power-preset section; WSGM `src/WSGM/Shell/NativeQamPowerPresetService.cs:43-45`, `src/WSGM/Shell/DevicePowerAssignments.cs`.
- **Problem:** The library hard-codes WSGM's `"custom"` id and refuses it; the module id is fixed and the row lacks the `Serialize`/`id` parameters its siblings have.
- **Best solution:** Add `bool Selectable = true` to `SteamPowerProfileOption`. WSGM publishes its custom entry with `Selectable = false` (`NativeQamPowerPresetService.ReadAsync`). The row drops its `option != "custom"` special case and does not check selectability itself: the command handler is stateless, a generic check would need a state read per command, and WSGM's `DevicePowerAssignments.AssignAsync` already refuses any id that is not one of the device's presets ("This device power profile is no longer available."), which covers `custom`. The row gains `id` and `Serialize` parameters like its siblings. On the page, with TOOLKITJS-V-006 semantics: an unselectable option is listed only in a dropdown whose current value it is, is never sent, and the whole-state rejection is dropped, so the visible UI is unchanged.
- **Tests:** the row forwards any well-formed target to the backend (no `custom` literal left in the toolkit); parent: `AssignAsync` with `custom` is refused; `eng/check-power-profile.mjs` pins "listed only where current, never sent". Toolkit filter `FullyQualifiedName~SteamChoiceRowTests|FullyQualifiedName~SteamPerformanceTests`; parent filter `FullyQualifiedName~NativeQam`.
- **Plan v2:** B057 (moved out of B056 into the presentation contract). The batch's unrelated D13 item is decided: before the first publication the badge shows the library's name, supplied by the host, not "Internal"; the toolkit holds no product label.
- **Related:** U03A-SUTS-005, U03A-SUTS-016, plan claim C32, TOOLKITJS-V-006, DEVICE-025.

### TOOLKITCS-048: `primaryCountName` is interpolated raw into evaluated JavaScript

- **Severity:** low
- **Where:** `tk/Surfaces/SteamQuickAccessRowPatch.cs:50-81` (constructor, `_probeExpression`), `:164-166` (read back by name).
- **Problem:** The name becomes an object key in the probe expression without validation, so a mistyped value (a dash, a space) breaks the probe at runtime instead of at construction. The injection framing of the review is dropped by maintainer decision (security theater, DECISIONS.md): every value is compile-time code of the toolkit, WSGM or a plugin.
- **Best solution:** In the constructor, require `primaryCountName` to match `^[A-Za-z_$][A-Za-z0-9_$]*$` and throw `ArgumentException` otherwise (a type and character-set check, no length limit), so the mistake fails once at module construction with the row named.
- **Tests:** constructor theory with valid and invalid names. Toolkit filter `FullyQualifiedName~SteamQuickAccessRowPatchTests`.
- **Plan v2:** B049.
- **Related:** U03A-SUTS-021.

### TOOLKITCS-049: `SteamPageProbe.Tokens` is a raw JavaScript array literal

- **Severity:** low
- **Where:** `tk/Surfaces/SteamPagePatch.cs:10,69-71`; WSGM page surfaces `src/WSGM/Shell/SteamAnimationsSurface.cs`, `SteamArtworkBrowserSurface.cs`, `SteamGraphicsSurface.cs`, `SteamLibraryImportSurface.cs`, `SteamThemesSurface.cs`, `SteamWsgmSettingsSurface.cs`.
- **Problem:** `SteamPageProbe(Name, Tokens)` takes `Tokens` as a JavaScript array literal string, with no escaping and no duplicate-name check.
- **Best solution:** Keep `Tokens` as the JavaScript array literal. It has the same type as every public token constant in `SteamUiProbeJs` (`ReactTokens`, `NativeFieldTokens` and the rest are literal strings the QAM row probe also splices in), every value is compile-time code of the toolkit, WSGM or a plugin rather than untrusted input, and turning it into a list would change those public constants inside the plugin contract (TOOLKITCS-V-001) for no defect. What is missing is the construction check: `SteamPagePatch.Create` validates each probe name as an identifier (same rule as TOOLKITCS-048) and refuses duplicate names with `ArgumentException`, since a duplicate key silently overwrites a count in the probe's JSON. The six WSGM page surfaces use only the static `SteamPageProbe` members and need no edit.
- **Tests:** duplicate names throw; an invalid name throws; every static `SteamPageProbe` member passes. Toolkit filter `FullyQualifiedName~SteamPagePatchTests`; parent compile.
- **Plan v2:** B056 (its "SteamPageProbe tokens list" and "six SteamPagePatch users" are replaced by the construction check above).
- **Related:** U03B-SUTS-020, TOOLKITCS-V-001.

### TOOLKITCS-050: Command payload shapes are not checked exactly

- **Severity:** low
- **Where:** `tk/Surfaces/SteamBrightnessSurface.cs` (`setBrightness`), `tk/Surfaces/SteamNavigationPanelSurface.cs:184-190` and `tk/Surfaces/SteamExtensionsTabSurface.cs:195-201` (`activate`), `tk/Surfaces/SteamStorageSurface.cs:239-266` (`adopt`, `eject`, `format`; `TryReadId` caps at `int.MaxValue`), `tk/Surfaces/SteamFrameLimitRow.cs:156-162` (`setRefreshRate` requires a discarded `persistence`).
- **Problem:** Some readers accept extra or missing properties, storage ids are read as `int` though they are uint32, and one command requires a field it ignores.
- **Best solution:** Each reader checks the exact property set and kinds the page sends (type checks only, no new ranges): `setBrightness` and `activate` take exactly their documented properties; storage ids read as `uint`; `setRefreshRate` stops requiring `persistence` (the field itself is deleted from the page in TOOLKITJS-014).
- **Tests:** rejection theories per command (extra property, wrong kind, missing property, id above `int.MaxValue` accepted). Toolkit filter `FullyQualifiedName~SteamStorageTests|FullyQualifiedName~SteamNavigationPanelTests|FullyQualifiedName~SteamPerformanceTests`.
- **Plan v2:** B056.
- **Related:** U03A-SUTS-010, U03A-SUTS-026, TOOLKITJS-014.

### TOOLKITCS-051: `ulong.TryParse` without invariant culture

- **Severity:** low
- **Where:** `tk/Surfaces/SteamPerformanceSurface.cs:505`.
- **Problem:** Parsing uses the current culture.
- **Best solution:** `ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)`.
- **Tests:** none needed beyond the existing performance tests. Toolkit filter `FullyQualifiedName~SteamPerformanceTests`.
- **Plan v2:** B049.
- **Related:** U03A-SUTS-027.

### TOOLKITCS-053: `KeyboardOpen` defaults to "confirmed closed"

- **Severity:** low
- **Where:** `tk/Surfaces/SteamSideMenuSnapshot.cs:29-34` (`SteamWindowSideMenu`).
- **Problem:** `KeyboardOpen` defaults to `false` while `OverlayActive` defaults to `null`, so a construction that omits it claims the keyboard is closed.
- **Best solution:** Default `bool? KeyboardOpen = null`, matching the doc ("null when unavailable"). Check WSGM readers of `KeyboardOpen` treat `null` as unknown (they already must, since the parser yields null).
- **Tests:** the side-menu parse test asserts `null` when the page reports no keyboard. Toolkit filter `FullyQualifiedName~SteamWindowSurfaceTests`.
- **Plan v2:** B056.
- **Related:** U03B-SUTS-011.

### TOOLKITCS-054: Overlay activation map has a sticky overflow and a 32-identity cap

- **Severity:** low
- **Where:** `tk/Surfaces/SteamOverlayActivationPatch.cs:28-39` (`ApplyExpression`); reader in `tk/Surfaces/SteamSideMenuSnapshot.cs` (`!activation.overflow`).
- **Problem:** After 33 identities or one malformed callback, `overflow` is set and the map cleared for good, so every overlay reads unknown until the patch is reapplied.
- **Best solution:** Delete `overflow` and the `size>=32` check. A callback with invalid `pid` or `appid` is ignored (it cannot be attributed); one with a valid identity but a non-boolean `active` deletes that identity (unknown for that overlay only). The side-menu reader stops reading `overflow`. No pruning: entries are one per game process seen in this document's life, and the document is replaced on Steam restart.
- **Tests:** NodeScript of the apply expression with 40 identities and one malformed callback: other identities keep their state. Toolkit filter `FullyQualifiedName~SteamWindowSurfaceTests`.
- **Plan v2:** B049.
- **Related:** U03B-SUTS-010, TOOLKITCS-031.

### TOOLKITCS-061: Extension host lacks a reserved prefix and drops rejected manifests

- **Severity:** low
- **Where:** `tk/SteamUiExtensionHost.cs:189-200` (id rules), `:276-282` (rejection).
- **Problem:** An extension named `steam-ui` may claim `steam-ui.power-limit`, which collides with the toolkit's own patch id; every rejection discards the parsed manifest, contradicting "an extension that fails to load still has its patches removed".
- **Best solution:** Refuse extension ids and declared patch ids that start with the host-reserved prefixes (`steam-ui.` and the host's own, passed by the host) with a new `SteamUiExtensionRejection` reason `ReservedPrefix`. A rejection keeps the parsed manifest on `SteamUiExtensionRejection` when parsing succeeded, so a host can still remove its declared patches. The review's reparse-point containment check (refusing junctions and symlinks under the extension root, `:231-239`) is dropped by maintainer decision (security theater, DECISIONS.md); containment stays as today.
- **Tests:** in `tkt/SteamUiExtensionHostTests.cs` with a temp directory: reserved prefix refused; a rejected extension exposes its manifest. Toolkit filter `FullyQualifiedName~SteamUiExtensionHostTests`.
- **Plan v2:** B056 (without the reparse check).
- **Related:** U02A-SUTC-030, U02A-SUTC-031, U02A-SUTC-061 (its reparse part is dropped, and its manifest cap was never adopted), plan claim C27, R7.

### TOOLKITCS-062: Extension host docs promise a lifecycle nothing implements

- **Severity:** low (docs)
- **Where:** `tk/SteamUiExtensionHost.cs:9-27`.
- **Problem:** The summary promises "same patch lifecycle, same clean removal", but nothing turns an extension into a module and there are no production references.
- **Best solution:** Rewrite the docs to describe discovery and validation only: what `Discover` returns, the rejection reasons, and that composing an extension into modules is the host's job. No adapter (R7: no consumer).
- **Tests:** none.
- **Plan v2:** B056.
- **Related:** U02A-SUTC-029, plan claim C27, R7.

### TOOLKITCS-063: Test seams and the bridge authorizer are public without a consumer

- **Severity:** low
- **Where:** `tk/SteamUiEndpointDiscovery.cs:12-48` (`ISteamUiEndpointDiscovery`, `SteamUiEndpoint`), `tk/SteamUiCdpConnection.cs:15-48` (`ISteamUiCdpWire`, `ISteamUiCdpWireFactory`), `tk/PersistentSteamUiTransport.cs:98-102` (internal constructor), `tk/SteamUiBridge.cs` (`SteamUiBridgeAuthorizer`, `SteamUiBridgeAuthorizationResult`).
- **Problem:** Four types are public "for consumer testing" but the only constructor accepting them is internal; WSGM fakes `ISteamUiTransport` instead. The authorizer pair is public with no consumer.
- **Best solution:** Make all six `internal`. Tests reach them through the existing `InternalsVisibleTo("SteamUiToolkit.Tests")` (`tk/Properties/AssemblyInfo.cs`). Confirm none is in the plugin-contract closure (TOOLKITCS-V-001); none is.
- **Tests:** compile toolkit, tests and parent. Toolkit filter `FullyQualifiedName~SteamUiBridgeAuthorizerTests|FullyQualifiedName~SteamUiEndpointDiscoveryTests`.
- **Plan v2:** B056.
- **Related:** U02A-SUTC-009, NEW for the authorizer.

### TOOLKITCS-065: Getter-only tests restate constants

- **Severity:** low (test quality; verifier: many cited lines are wrong)
- **Where:** `tkt/SteamUiPatchManagerTests.cs` (`PatchBoundsPreservePublishedNamedArguments`), `tkt/SteamUiModuleTests.cs`, `tkt/SteamStorageTests.cs`, `tkt/SteamPageTests.cs`, `tkt/SteamThemeStyleTests.cs`, `tkt/SteamPanelFoldsTests.cs`, `tkt/SteamStartupMovieTests.cs`, `tkt/SteamScreensaverTests.cs` (restates `Commands`), `tkt/PersistentSteamUiTransportTests.cs` (constant table).
- **Problem:** These tests assert getters or constant tables and fail only when someone edits the constant on purpose.
- **Best solution:** Re-derive the anchors by symbol, delete the getter-only tests (the `PatchBounds` one disappears with `SteamUiPatchBounds` anyway), and rely on TOOLKITCS-069 for the command vocabulary.
- **Tests:** whole toolkit suite once.
- **Plan v2:** B062.
- **Related:** U02A-SUTC-066, NEW for the rest.

### TOOLKITCS-067: Async negative assertions and unsynchronized test statics

- **Severity:** low (test quality)
- **Where:** `tkt/SteamUiBridgeHostTests.cs:41-55` (asserts zero deliveries right after emitting to a pumped channel); `tkt/PersistentSteamUiTransportTests.cs:345-370` (mutates the process-wide session); `tkt/Fakes/FakeSteamUiTransport.cs:31,152` (`Generations` written across threads).
- **Problem:** An "absence" assertion checked before the pump ran passes vacuously; a test mutates global state without a non-parallel collection; a fake field races.
- **Best solution:** Negative assertions emit the malformed input followed by a valid sentinel and assert only the sentinel was delivered (done with TOOLKITCS-034 in B049). The session-mutating test disappears with the session (B053). `FakeSteamUiTransport.Generations` is read and written under the fake's existing lock (or `Volatile`).
- **Tests:** as described. Toolkit filter `FullyQualifiedName~SteamUiBridgeHostTests|FullyQualifiedName~PersistentSteamUiTransportTests`.
- **Plan v2:** B062 (sentinel pattern lands in B049, session test removal in B053).
- **Related:** U02A-SUTC-036, U02A-SUTC-065.

### TOOLKITCS-068: Tests wait on real time and fail with bare cancellations

- **Severity:** low (test quality)
- **Where:** `tkt/Fakes/TestJson.cs:16-23` (throws a bare `TaskCanceledException`); `tkt/PersistentSteamUiTransportTests.cs:210-242` (waits out the 1 s reconnect with a 10 s budget); `tkt/SteamUiPatchManagerTests.cs:131-150,309-330`.
- **Problem:** Slow tests and failure messages that do not say what was awaited.
- **Best solution:** `TestJson` wait helpers throw `TimeoutException` naming what they waited for. The manager's settle delay and the transport's retry delays become values passed through internal constructors (the transport's retry-delay parameter already lands in B048 with TOOLKITCS-006; the manager gets an internal constructor beside its public one, which forwards today's constants), and the tests pass a few milliseconds instead of waiting out real seconds. This beats injecting a `TimeProvider`, which would add a fake-clock package and timer plumbing for the same result. No production behaviour change.
- **Tests:** the affected tests run without real waits. Toolkit filter `FullyQualifiedName~PersistentSteamUiTransportTests|FullyQualifiedName~SteamUiPatchManagerTests`.
- **Plan v2:** B062.
- **Related:** U02A-SUTC-064.

### TOOLKITCS-069: The "full set" surface test lists 21 of about 31 surfaces by hand

- **Severity:** low (test quality)
- **Where:** `tkt/SteamSurfaceModuleTests.cs:13-96`; each surface's `Commands` property.
- **Problem:** The test misses about ten surfaces and compares hand-kept `Commands` lists against hand-kept expectations.
- **Best solution:** Derive each surface's `Commands` from the handlers it registers (the property returns the command names of its own handler table), and build the test's set from every public surface type found by reflection over the toolkit assembly, asserting every surface's module builds and its commands equal its handler names.
- **Tests:** the rewritten `SteamSurfaceModuleTests`. Toolkit filter `FullyQualifiedName~SteamSurfaceModuleTests`.
- **Plan v2:** B062.
- **Related:** U02B-SUTC-038, U03A-SUTS-016, TOOLKITCS-065.

### TOOLKITCS-V-003: Install-folder parsers report "no live add happened" after the script ran

- **Severity:** low (found by the verifier)
- **Where:** `tk/Client/SteamInstallFolders.cs` (`InterpretAdd`, `InterpretRemove`, `InterpretLabel`: `jsonValue is null` and `catch (JsonException)` map to `Unavailable`; the enum docs at `:20-21,41-42,62-63`).
- **Problem:** A reachable reply that is null or malformed comes from a script that already ran `AddInstallFolder` or `RemoveInstallFolder`, yet it is reported as `Unavailable` ("the debug channel could not be reached, so no live add happened"). A caller may treat an add that happened as one that did not.
- **Best solution:** Split `Unavailable` into `NotSent` and `Unknown` on all three status enums. The parsers take the evaluation's `SteamUiDispatch`: `NotSent`/`Closed` map to `NotSent`; `Unanswered`, and an answered null or unparseable reply, map to `Unknown`. Apply the same rule in every client parser (apps, collections, startup movie). Callers never retry `Unknown`.
- **Tests:** one parser-table test per status enum (dispatch phase times reply shape). Toolkit filter `FullyQualifiedName~SteamClientTests`.
- **Plan v2:** B053.
- **Related:** TOOLKITCS-003, TOOLKITCS-016.

### TOOLKITCS-V-004: More arbitrary caps the TOOLKITCS-031 inventory missed

- **Severity:** low (found by the verifier)
- **Where:** extension and patch identifier length cap of 96 (`tk/SteamUiExtensionHost.cs:286`); command error text truncated to 2,048 characters before delivery to the page (`tk/SteamUiBridge.cs:1000-1003`); patch fingerprint truncated to 512 in the snapshot (`tk/SteamUiPatchManager.cs:1093`, `SetStateLocked`).
- **Problem:** The identifier cap refuses valid ids; the page receives a cut-off refusal it shows to the user; the stored fingerprint is cut although only log lines need bounding.
- **Best solution:** Remove the identifier length cap and keep the character-set check. Send command error text to the page whole (delivery is chunked). Store the full fingerprint and diagnostic in `SteamUiPatchSnapshot`; bound only the text written to the log line with `SteamUiShared.MaximumDiagnosticLength` (log-safety boundary, R12).
- **Tests:** a 200-character extension id is accepted; a 5,000-character refusal reaches the page whole through `FakeSteamUiTransport`; the snapshot keeps a 1,000-character fingerprint. Toolkit filter `FullyQualifiedName~SteamUiExtensionHostTests|FullyQualifiedName~SteamUiBridgeHostTests|FullyQualifiedName~SteamUiPatchManagerTests`.
- **Plan v2:** B056.
- **Related:** TOOLKITCS-031, R12.

---

## Nit

### TOOLKITCS-011: Stale comments in the toolkit csproj

- **Severity:** nit
- **Where:** `tk/SteamUiToolkit.csproj:11-13` ("read through a stack buffer"; `NativeTcp.ListListeners` uses `AllocHGlobal` and is the only `unsafe` member), `:25-27` (pre-1.0 comment names "module id").
- **Problem:** The comments describe code that no longer exists.
- **Best solution:** Reword the `AllowUnsafeBlocks` comment to "`NativeTcp.ListListeners` decodes the variable-length listener table from unmanaged memory". Reword the pre-1.0 note without "module id" (probes resolve by source tokens). The version bump itself is TOOLKITCS-060/B056.
- **Tests:** none.
- **Plan v2:** B048.
- **Related:** U02A-SUTC-053.

### TOOLKITCS-023: Two client error-reply shapes

- **Severity:** nit
- **Where:** `tk/Client/SteamClientScript.cs:27-28` (`{ok:false,err}`) vs `tk/Client/SteamInstallFolders.cs:99-100` (`{ok:false,result,message}`).
- **Problem:** Two shapes for one concept, read by two refusal parsers.
- **Best solution:** One shape: `SteamClientScript.ErrorReply` becomes `{ok:false, err:String(e.message||e), result:(e&&e.result)}` with `result` optional. `SteamInstallFolders` builds its scripts with `SteamClientScript.Read` and reads `err` and `result` with one shared `RefusalOf`.
- **Tests:** the install-folder parser table (TOOLKITCS-V-003) covers both fields. Toolkit filter `FullyQualifiedName~SteamClientTests`.
- **Plan v2:** B053.
- **Related:** NEW.

### TOOLKITCS-024: Public client members document only a summary

- **Severity:** nit
- **Where:** `tk/Client/SteamApps.cs:66-67,496-591` (`SteamLogoPosition`, icon, account and logo APIs).
- **Problem:** Parameters and return values are undocumented.
- **Best solution:** While moving these onto `SteamClient.Apps` (TOOLKITCS-014), document parameters, the `SteamReadResult<T>` outcomes and units for `SteamLogoPosition`.
- **Tests:** none (the build treats CS1573/CS1591 as errors).
- **Plan v2:** B053.
- **Related:** U02B-SUTC-021.

### TOOLKITCS-038: WSGM vocabulary in library strings

- **Severity:** nit
- **Where:** `tk/SteamUiBridge.cs:42-45` (correlation prefix); `tk/Surfaces/SteamUiBridgePatch.cs:93,108` ("Native-QAM bridge"); `native-qam-*` and `qam-v1:` fingerprints across the row files.
- **Problem:** Product vocabulary in a generic library.
- **Best solution:** Correlation prefix `steam-ui:` in `SteamUiBridgeRequest.ToCorrelationId`; fingerprints `steam-ui-*`; diagnostics say "Steam UI bridge". These are log-only strings; WSGM has no parser of them (review grep), but grep `src` and `tests` again before the rename.
- **Tests:** update any test pinning the old strings. Toolkit filter `FullyQualifiedName~SteamQuickAccessRowPatchTests|FullyQualifiedName~SteamUiBridge`.
- **Plan v2:** B054.
- **Related:** U02A-SUTC-045, U02B-SUTC-031.

### TOOLKITCS-039: Bridge verify hard-codes schema version 1

- **Severity:** nit
- **Where:** `tk/Surfaces/SteamUiBridgePatch.cs:27-29` (`VerifyExpression`, `b.version===1`).
- **Problem:** A literal instead of the host's schema constant.
- **Best solution:** Build the expression from `SteamUiBridgeHost.SchemaVersion` (`"b.version===" + SchemaVersion`), as a static readonly string.
- **Tests:** none beyond existing verify tests.
- **Plan v2:** B054.
- **Related:** U02B-SUTC-035.

### TOOLKITCS-040: `SteamUiShared.Bound` overshoots by three and can split a surrogate pair

- **Severity:** nit
- **Where:** `tk/SteamUiShared.cs:22-27`.
- **Problem:** The bounded text is up to three characters longer than the maximum (the ellipsis is appended after the cut) and can end on a lone high surrogate.
- **Best solution:** Cut at `maximum - 1` and append a single `…`; if the last kept char is a high surrogate, cut one more. Used only for log lines and diagnostics.
- **Tests:** a unit test with an emoji at the boundary and an exact-length check.
- **Plan v2:** B049.
- **Related:** U02A-SUTC-044.

### TOOLKITCS-052: Screensaver id regex `$` matches before a trailing newline

- **Severity:** nit
- **Where:** `tk/Surfaces/SteamScreensaverSurface.cs:94`.
- **Problem:** `$` accepts `"id\n"`.
- **Best solution:** Use `\z` as the end anchor.
- **Tests:** a theory case with a trailing `\n` is refused. Toolkit filter `FullyQualifiedName~SteamScreensaverTests`.
- **Plan v2:** B049.
- **Related:** U03A-SUTS-025.

### TOOLKITCS-056: Part of the toolkit lives in a `SteamUiToolkit.Surfaces` namespace

- **Severity:** nit
- **Where:** `tk/Surfaces/SteamGameWindowActivation.cs:7`, `SteamOverlayActivationPatch.cs:5`, `SteamRouteNavigation.cs:7`, `SteamSideMenuSnapshot.cs:8`, `SteamNativeSurfaceCommands.cs:6`, `SteamSharedContext.cs:3`; WSGM `src/WSGM/Shell/ShellSession.cs:10`, `src/WSGM/Shell/SteamUiSessionHost.cs:7`, `src/WSGM/Shell/ShellSession.Actions.cs`.
- **Problem:** Six files use a second namespace for no reason.
- **Best solution:** Move them to `SteamUiToolkit` and delete the `using SteamUiToolkit.Surfaces;` lines in the three WSGM files and in tests (verify batch problem 2: the first two WSGM files were missing from the batch).
- **Tests:** compile toolkit, tests and parent.
- **Plan v2:** B056.
- **Related:** U03B-SUTS-029, U02B-SUTC-033, U03A-SUTS-024.

### TOOLKITCS-057: `SteamUiText` is a one-line public helper only WSGM uses

- **Severity:** nit
- **Where:** `tk/Surfaces/SteamUiText.cs:4-12`; WSGM `src/WSGM/Shell/NativeQamSemanticServices.cs` (fifteen `SteamUiText.Of` call sites in that one file; the B056 spec's "three call sites" is wrong). No toolkit code uses it.
- **Problem:** A WSGM convenience lives in the library's public API.
- **Best solution:** Delete `SteamUiText.cs`; the file holds several top-level classes, so add one `internal static string Text(string? value)` with the same body to the file's existing `internal static class NativeQamUi` and point every call site at it (STEAMHOST-044). Grep `src` and `tests` for `SteamUiText` before deleting so none is missed.
- **Tests:** parent filter `FullyQualifiedName~NativeQam`.
- **Plan v2:** B056.
- **Related:** U02B-SUTC-032, STEAMHOST-044.

### TOOLKITCS-058: Sound override record shape and naming

- **Severity:** nit
- **Where:** `tk/Surfaces/SteamSoundOverrideSurface.cs:10,25,43-49`.
- **Problem:** A mutable `string[]` in a record, a chunk label `sound-overrides-probe` that breaks the `steam_ui_*_probe_` pattern, and no `id` parameter.
- **Best solution:** `IReadOnlyList<string>` in `SteamSoundOverrideState`; chunk label `steam_ui_sound_overrides_probe_`; add the `id` parameter like sibling surfaces (default today's id).
- **Tests:** the sound-override coverage added in TOOLKITCS-066.
- **Plan v2:** B056.
- **Related:** U03A-SUTS-031, U03A-SUTS-029.

### TOOLKITCS-059: Contract docs read as readback gates

- **Severity:** nit
- **Where:** `tk/Surfaces/SteamCpuBoostRow.cs:30-33`, `SteamHybridCoreRow.cs:27-30`, `SteamPowerProfileRow.cs:28-31` ("Selects and verifies ... The verified outcome"); `tk/Surfaces/SteamBrightnessSurface.cs` ("confirmed readback", verifier: the file has 121 lines, anchor by text); `tk/Surfaces/SteamVariableRefreshRow.cs:11` ("What the device reports"); `tk/SteamUiModule.cs` (`SteamUiCommandResult.Payload` "including confirmed readback").
- **Problem:** The docs tell backend authors to verify by readback before reporting success, which the maintainer rule forbids.
- **Best solution:** Reword to D9: the backend dispatches the selection and reports success when the write was dispatched, publishing the written value as observed, or a failure when it could not be dispatched. No outcome waits on a readback and nothing is left "unresolved" or "uncertain". Only a package whose hardware reads back (today the MSI Claw) may refresh the shown value from it afterwards, and that never decides success.
- **Tests:** none.
- **Plan v2:** B056.
- **Related:** NEW, maintainer rule "no hard readback requirement", D9.

### TOOLKITCS-060: Stale documentation in code

- **Severity:** nit
- **Where:** `tk/Surfaces/SteamPageSurface.cs:53-56` (router back-stack fallback not implemented); `tk/Surfaces/SteamThemeStyleSurface.cs` vs reference (polling); `tk/Surfaces/SteamGatePatch.cs:39` and `tk/Surfaces/SteamUiProbeJs.cs:8-11` ("literal module ids"); `tk/SteamUiModule.cs:100-101` (order); `tk/Surfaces/SteamUiBridgePatch.cs:12-14` ("manager orders by stable patch id"); `tk/SteamCef.cs:19-23,33` (`EnsureRemoteDebuggingEnabled` return); `tk/Surfaces/SteamPowerMenuSurface.cs:73`; `tk/Surfaces/SteamSettingsRows.cs:86-87` (names WSGM); `tk/Surfaces/SteamSurfaceJsonContext.cs:10-11`; `tk/SteamUiExtensionHost.cs:51-53` ("ordered by id" vs directory sort); `tk/SteamUiToolkit.csproj` version.
- **Problem:** Comments and docs that describe behaviour the code does not have.
- **Best solution:** Fix each to match the code: describe the page surface without the unimplemented fallback; describe theme polling as the code does it; say probes resolve modules by source tokens; module order is independent (after TOOLKITCS-041); the manager applies the bridge first and removes it last (TOOLKITCS-025); `EnsureRemoteDebuggingEnabled` returns "true when enabled and the flag is present afterwards"; remove the WSGM name from `SteamSettingsRows`; `Discover` sorts its result by extension id with ordinal comparison (one `OrderBy`, which makes the "ordered by id" doc true and the result deterministic, since directory enumeration order is unspecified). Bump `<Version>` to `0.2.0`.
- **Tests:** none.
- **Plan v2:** B056.
- **Related:** U03B-SUTS-014, U03B-SUTS-013, U02B-SUTC-017, U03A-SUTS-023, U03A-SUTS-028, U02B-SUTC-034, U02A-SUTC-060, plan claims C35, C39.

### TOOLKITCS-070: Recording backend fake mislabels and hides calls

- **Severity:** nit (fakes)
- **Where:** `tkt/Fakes/RecordingBackend.cs:105-108,166-169,194-197,262-272`; `tkt/SteamPanelFoldsTests.cs` (shadows the shared fake).
- **Problem:** "boost" labels both CPU boost and boost power; one `ActivateAsync(string)` serves two backends; `CollapseAsync` is dead; results are always `Applied`; the folds test defines its own copy.
- **Best solution:** Distinct labels per backend method, separate `ActivateAsync` implementations per interface (explicit interface implementations), delete `CollapseAsync`, add a settable result per method (default `Applied`), and make the folds test use the shared fake.
- **Tests:** whole toolkit suite.
- **Plan v2:** B062.
- **Related:** U02B-SUTC-037, U03B-SUTS-045, NEW (dead method).

### TOOLKITCS-071: `NodeScript` needs `node` on PATH and loses output on timeout

- **Severity:** nit
- **Where:** `tkt/Fakes/NodeScript.cs:14,29-38`.
- **Problem:** Tests fail on machines where `node` is not on PATH, and a timeout discards stdout and stderr.
- **Best solution:** Resolve node from `NODE` (environment) first, then PATH, with a clear failure naming both; read stdout and stderr asynchronously from the start and include both in the timeout exception.
- **Tests:** whole toolkit suite.
- **Plan v2:** B062.
- **Related:** U02B-SUTC-041, U02B-SUTC-027.

---

## Refuted or no-change

- **TOOLKITCS-017** (refuted by the verifier): an oversized binding payload is already logged with its method name at the connection (`SteamUiCdpConnection.ProcessMessage` calls `DropMalformed($"{method} parameters exceeded the byte limit")`), so the recommended log line exists; the B049 step "oversized binding drop logged" is dropped.
- **TOOLKITCS-010** (`iphlpapi.dll` import in `tk/NativeTcp.cs` `GetExtendedTcpTable` without `DefaultDllImportSearchPaths(System32)`; was nit, B048, related U02A-SUTC-052): Dropped by maintainer decision (security theater, DECISIONS.md). DLL search-order hardening fixes no functional defect; the import stays as it is, and the B048 step is removed.
