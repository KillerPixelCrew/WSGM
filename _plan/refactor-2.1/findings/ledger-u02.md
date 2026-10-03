# SteamUiToolkit C# ledger findings (U02A-SUTC, U02B-SUTC)

Scope: the 109 ids of the first Claude audit's SteamUiToolkit C# units, U02A-SUTC-001 to 066 (transport, CDP
connection, bridge host, patch manager, modules, runtime, extension host, toolkit tests) and U02B-SUTC-001 to 043
(client layer, surfaces, docs, repo configuration, test fakes). Bodies come from
`claude-findings-raw.json` (all 109 present); the disposition rows only map them to the old Codex tasks T01, T02 and
T03. Every claim was checked against toolkit `main` 388dd1b and parent `master` 1329813f, the same heads the current
domain reviews used.

Path shorthand: `lib/` is `external/steam-ui-toolkit/src/SteamUiToolkit/`, `toolkit/` is `external/steam-ui-toolkit/`.
Every other path is relative to the repository root. Anchor edits by symbol; line numbers drift.

Counts (ledger severity): 13 medium, 47 low, 49 nit, no critical or high. 98 ids have a section below (12 medium,
44 low, 42 nit); 11 are refuted or no-change and are listed at the end, three of them (U02A-SUTC-004, 052, 061)
dropped by the maintainer's 2026-10-03 decisions (`DECISIONS.md`: security hardening is dropped). 81 sections are
short: a current domain finding (TOOLKITCS-*, TOOLKITJS-*, STEAMHOST-*) already covers the id and carries the
solution, so the section only names it and the batch. 17 sections were checked against the code here and carry the
full fix, because no domain finding owns them (mostly the ids plan v2 Appendix B parks in B178), because the domain
finding covers only half, or because a decision replaced the domain finding's fix (U02A-SUTC-044, D2).

The decisions in `DECISIONS.md` are binding here and override plan v2 section 4 and any domain finding that
disagrees.

Plan v2 batches that implement this area: B047 (toolkit check infrastructure and repo metadata), B048 (transport and
connection), B049 (probe safety, bridge parsing, module runtime), B050 (bridge and module resolution in the injected
script), B051 (gate comments), B053 (dispatch-aware outcomes, explicit `SteamClient`, no ambient session), B054
(patch contract, one manager loop, quarantine in the manager, bounded teardown), B056 (public surface cleanup,
version 0.2.0), B062 (test quality) and B177 (documentation pass). Every toolkit batch commits and pushes the child on
`main` first, then one parent commit records the gitlink with any consumer edits. Child C# check:
`dotnet build external\steam-ui-toolkit\SteamUiToolkit.slnx -c Release` and
`dotnet test external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj --filter ...`; child
script check: `npm run prelude:claims` in `external/steam-ui-toolkit`.

Recurring rejections, so no batch adds them back: any security hardening against same-user threats (a bridge host
id or per-bootstrap nonce, sender or integrity checks, DLL search-path pins, reparse-point containment; critic
conflict 6, TOOLKITCS refinement R6, `DECISIONS.md`), a `DependsOn` graph for patches (R3), a quarantine Reset API (R4, with the corrected
rationale "same as today; reset on WSGM restart"), an injected `ISteamUiLog` per component (R10), a second inbound
byte cap at the bridge (TOOLKITCS-017 refuted; the CDP 1 MiB notification read is the D2 bound), and byte caps on
extension manifests or `SteamUiPayload` strings (TOOLKITCS-031, no-arbitrary-limits). D2 is decided: only the plan
v2 list of byte bounds stays, so diagnostic truncation goes too (U02A-SUTC-044).

## Medium

### U02A-SUTC-001: One-shot session turns a JavaScript exception into success with a null value

- **Severity:** medium
- **Covered by:** TOOLKITCS-003 with refinement R2 and plan claim C37 (_plan/refactor-2.1/review/toolkitcs.md), confirmed by the
  verifier. Still present: `lib/SteamUiTransportSession.cs:208-211` maps `Reachable` to `CefEvalResult.Ok(Value)`.
  B053 deletes the session and `CefEvalResult`; an answered JavaScript error becomes `Answered` with `Error`, which
  client writes report as `Rejected`. T01_01 is superseded and its tests move into B053.
- **Plan v2:** B053 (Appendix B assigns it explicitly).
- **Related:** U02B-SUTC-007, U02A-SUTC-008, A01-F004.

### U02A-SUTC-002: Global disable removes the bridge before the gates that live in it

- **Severity:** medium
- **Covered by:** TOOLKITCS-025 and STEAMHOST-004 (confirmed). Every pass processes removals in reverse order with the
  bridge last, then applies with the bridge first; WSGM deletes its two-pass `DisableAsync` in
  `src/WSGM/Shell/SteamUiSessionHost.cs`.
- **Plan v2:** B054.
- **Related:** U02A-SUTC-020, U02A-SUTC-017, U02A-SUTC-037.

### U02A-SUTC-003: A failed, thrown or timed-out apply leaves a partial mutation with no removal

- **Severity:** medium
- **Covered by:** TOOLKITCS-027 (confirmed). An apply that began and then failed, threw or timed out runs one bounded
  `RemoveAsync` and is never blindly reapplied; a `Retrying` state reuses the existing `ScheduleSettleRetry`.
- **Plan v2:** B054.
- **Related:** U02A-SUTC-018, U02A-SUTC-037.

### U02A-SUTC-005: Module resolver pushes a new chunk and closure into Steam's webpack runtime on every call

- **Severity:** medium (ledger hypothesis; TOOLKITJS-017 confirmed it for the gates)
- **Where:** `lib/SteamUiAssets/Source/module-resolver.ts:3-12` (`webpackChunksteamui.push` with
  `steam_ui_${scope}_${Date.now()}`); C# callers `lib/SteamUiModuleResolver.cs:21` (`CreateExpression`) and
  `lib/Surfaces/SteamUiProbeJs.cs:115-119` (`Preamble`, one resolver per probe evaluation); gates
  `components.ts`, `page-gate.ts` and most gates; WSGM `src/WSGM/Core/SteamDownloadSort.cs` (`InstallExpression`).
- **Problem:** every resolver creation pushes a chunk entry whose callback closure stays referenced by Steam's chunk
  array and adds one `installedChunks` key. Gates create one per install attempt, and every C# probe creates one per
  evaluation; `SteamUiPatchManager.SynchronizeAsync` probes every enabled patch on every pass (`:606-611`), so the
  array grows for the whole Steam session. Two creations with the same scope in the same millisecond reuse a chunk id;
  webpack then skips the callback for an already installed id, `runtime` stays undefined and the resolver throws
  "Steam modules unavailable", a spurious probe failure. TOOLKITJS-017 fixes only the gate half (memoize one resolver
  per bridge in `bridge.ts`); the C# probe half is in no finding.
- **Best solution:** capture the webpack runtime once per document inside `createSteamUiModuleResolver` itself, so
  the bridge, every standalone C# probe and WSGM's two direct callers (`SteamDownloadSort`, `SteamLibraryTabs`)
  share it. The function first reads a window property (for example `__steamUiWebpackRuntime_v1`, defined with
  `Object.defineProperty` defaults: non-enumerable, non-writable, non-configurable). Only when it is absent does it
  push one chunk with the fixed id `steam_ui_runtime_v1` whose callback defines the property if it is still absent.
  When the runtime is loaded the callback runs synchronously inside the push; webpack skips the callback of any later
  push of the same id, so a repeat push during Steam's cold start (array present, runtime not yet loaded) is
  harmless and the first queued entry defines the property once the runtime processes it. A new document is a new
  window, so the cache cannot outlive its runtime. The function stays a single plain-JS function expression
  (TOOLKITJS-V-002, C# embeds it) and keeps no `failed` set across calls (TOOLKITJS-V-001). This replaces the
  "capture the webpack runtime once per bridge in bridge.ts" wording of the B050 spec: the capture moves one level
  down so probes get it too; `getWebpackRuntime` in `bridge.ts` may still memoize one resolver per bridge for its
  source `WeakMap` (TOOLKITJS-017). In the same child commit, update the two sentences of `toolkit/docs/reference.md`
  section 3 "Module resolution" that describe per-gate resolvers avoiding a chunk push and a resolver not repeating a
  failed factory call, and the `SteamUiModuleResolver` class remarks. This beats "a monotonic counter in the id"
  because it removes the per-call push instead of renaming it, and it beats per-gate caches because one rule covers
  probes, gates and WSGM's callers.
- **Tests:** a `check-startup.mjs` case whose webpack fixture models the JSONP rule (count `push` calls, run the
  callback only for an id not yet installed): 100 resolver creations, two of them with a stubbed identical
  `Date.now()`, push exactly once and all resolve; a fixture where the array exists before the runtime and is
  processed later resolves on the next call. An existing check that swaps the runtime object on one fixture window
  (rather than editing `runtime.m`) must build a fresh window per scenario, as a new document would. Run
  `npm run prelude:claims`, then
  `dotnet test external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj --filter "FullyQualifiedName~SteamCefTests|FullyQualifiedName~SteamWindowSurfaceTests|FullyQualifiedName~Probe"`.
  The B050 filter `FullyQualifiedName~ModuleResolver` matches no test today: the resolver resource test is
  `SteamCefTests.ResolverResourceEncodesScopeAndContainsSharedDiscoveryBoundary`, and the only C# test that executes
  a resolver in Node is in `SteamWindowSurfaceTests` (its fixture `push` calls the callback every time, which still
  works with the cache). Then `npm run steam-assets:build`, `steam-assets:check`, `steam-assets:claims` in the parent.
- **Plan v2:** B050 (owns `module-resolver.ts` and TOOLKITJS-017; its C# test filter should use the names above);
  WSGM's download-sort call moves to a TS fragment with STEAMHOST-018 in B139.
- **Related:** TOOLKITJS-017, TOOLKITJS-V-001, TOOLKITJS-V-002, STEAMHOST-018, U02A-SUTC-058.

### U02A-SUTC-006: SteamUiLog is a process-wide mutable sink with no isolation, and no toolkit test observes diagnostics

- **Severity:** medium
- **Covered by:** TOOLKITCS-043 with refinement R10: keep the single static sink (one per process, write-only
  diagnostics), make `_sink` volatile, and put sink-observing tests in one non-parallel xUnit collection. The
  ledger's per-component injection is rejected as churn without a defect.
- **Plan v2:** B049 (volatile sink); B062 (log-sink tests in one non-parallel collection).
- **Related:** U02A-SUTC-065, TOOLKITCS-067.

### U02A-SUTC-007: Toolkit suite never exercises the module runtime command path or the builder payload validation

- **Severity:** medium
- **Covered by:** TOOLKITCS-066 (confirmed). B049 adds the runtime command matrix (disabled, quarantined, cancel,
  duplicate sequence, undelivered response, invalid payload) through `FakeSteamUiTransport.EmitBindingPayload`; B062
  closes the coverage finding.
- **Plan v2:** B049 (tests), B062 (resolves TOOLKITCS-066).
- **Related:** U02A-SUTC-032, U02A-SUTC-034, U02A-SUTC-035.

### U02B-SUTC-001: Uncertain post-dispatch outcomes are reported as "unreachable, changed nothing"

- **Severity:** medium
- **Covered by:** TOOLKITCS-003 (confirmed) and TOOLKITCS-V-003 (parsers re-collapsing reachable null replies). The
  transport gains `SteamUiDispatch { NotSent, Closed, Unanswered, Answered }`; client writes report `NotSent`,
  `Unknown`, `Rejected` or `Applied`; a timeout or cancel after send is `Unknown`, which callers never retry (the
  library consumer stops the run and keeps the record, LIBRARY-004 part 2). D9 does not change this: it removes
  readback machinery for device writes, and `Unknown` here is a transport classification with no readback, re-arm or
  recovery entry behind it.
- **Plan v2:** B053.
- **Related:** U02B-SUTC-010, U02B-SUTC-011, A01-F004, LIBRARY-V-005.

### U02B-SUTC-002: A failed running-apps observer install leaves a stale observer that later reads report as healthy

- **Severity:** medium
- **Covered by:** TOOLKITCS-019 (confirmed; the frozen set is the real defect). The observer is assigned to `window`
  only after `RegisterForAppLifetimeNotifications` returned a handle; a throw publishes nothing, so the next read
  installs again. No version stamp: TOOLKITCS-019 drops the B053 spec's "with a version stamp" because an observer
  that is only ever published registered is safe to reuse, and a shape change renames the property instead.
- **Plan v2:** B053.
- **Related:** U02B-SUTC-015, U02B-SUTC-040.

### U02B-SUTC-003: The generic bridge patch requires Quick Access performance/TDP fingerprints

- **Severity:** medium
- **Covered by:** TOOLKITCS-036 (confirmed). The bridge probe keeps only the webpack preamble and `react` (new
  fingerprint `steam-ui-bridge-v1:webpack+react`); the QAM row patches probe `tdpAvailability`, `tdpComponent`,
  `profileProjection` and `performanceActions` themselves (TOOLKITCS-036 adds the fourth count to the three the B054
  spec names, so no row mounts where it could not before).
- **Plan v2:** B054.
- **Related:** U02B-SUTC-031, U02A-SUTC-020.

### U02B-SUTC-004: The client layer is static and bound to the ambient SteamUiTransportSession

- **Severity:** medium
- **Covered by:** TOOLKITCS-014 (confirmed): delete `SteamUiTransportSession`; one `SteamClient` instance per
  transport with one write lane for apps, collections and install folders, composed once in `ShellSession`.
- **Plan v2:** B053.
- **Related:** U02B-SUTC-009, U02B-SUTC-042, U02A-SUTC-006.

### U02B-SUTC-005: Injected client scripts are only substring-asserted, never executed

- **Severity:** medium
- **Covered by:** TOOLKITCS-064 (verifier corrected its test-line anchors): run the client expressions through
  `NodeScript` against page models. B053's own tests already execute the AddShortcut, collection, startup-movie,
  install-folder and running-apps scripts it changes.
- **Plan v2:** B062 (B053 for the scripts it edits).
- **Related:** U02B-SUTC-006, U03B-SUTS-006.

### U02B-SUTC-006: Gate contract tests restate JavaScript predicate strings instead of evaluating them

- **Severity:** medium
- **Covered by:** TOOLKITCS-064: `SteamGatePatchContractTests` is rewritten to evaluate each claiming gate's probe in
  Node against unclaimed and claimed fixtures and the verify/remove predicates against status objects.
- **Plan v2:** B062.
- **Related:** U02B-SUTC-005, U02B-SUTC-039.

## Low

### U02A-SUTC-008: Local framing/discovery/queue errors are reported as Reachable=true / Incompatible

- **Severity:** low
- **Covered by:** TOOLKITCS-003: discovery and connect errors map to `NotSent`, post-send failures to `Unanswered`;
  local limits are no longer an answered incompatibility.
- **Plan v2:** B053.
- **Related:** U02A-SUTC-001, U02A-SUTC-040.

### U02A-SUTC-009: Public extension seams are unusable outside the assembly

- **Severity:** low
- **Covered by:** TOOLKITCS-063: internalize `ISteamUiCdpWire`, `ISteamUiCdpWireFactory`,
  `ISteamUiEndpointDiscovery`, `SteamUiEndpoint` and the unused authorizer types (tests keep InternalsVisibleTo).
- **Plan v2:** B056.
- **Related:** U02A-SUTC-010.

### U02A-SUTC-010: One-shot evaluations connect and tear down a websocket per call

- **Severity:** low
- **Covered by:** TOOLKITCS-005 and plan claim C5: per-call connect stays (the disposition's "truthful lifetime
  contract"), but a request lease no longer starts a reconnect loop. The README sentence "It connects on first use
  and reconnects by itself" (`toolkit/README.md:190`) is true only while a subscription is held; B177 rewrites it to
  say that persistence requires a retained subscription and one-shot calls connect per call.
- **Plan v2:** B048 (behaviour), B177 (README wording).
- **Related:** U02A-SUTC-009.

### U02A-SUTC-011: Caller cancellation during a send tears down the shared connection

- **Severity:** low
- **Covered by:** TOOLKITCS-002 (confirmed): send under the connection's lifetime token; caller cancellation and the
  per-call timeout only end the wait.
- **Plan v2:** B048.
- **Related:** U02A-SUTC-038.

### U02A-SUTC-012: Fire-and-forget disposals have no owner

- **Severity:** low
- **Covered by:** TOOLKITCS-012, scope narrowed by the verifier: transport and bridge teardown are already bounded
  (connection dispose 1 s, pumps 1 s, bridge removal 2 s), the reconnect loop exits on `_shutdown`, and each
  `DisposeDetachedConnectionAsync` runs that same bounded dispose, so B048's "track reconnect and retirement tasks
  (012 part)" is dropped (tracking them would add a list that needs pruning, with no defect). B054 bounds the module
  runtime publication loop, its request tasks and the manager's removal across patches. No new `ShutdownAsync` on
  transport or bridge.
- **Plan v2:** B054 (runtime and manager bound). B048 does nothing for this id.
- **Related:** U02A-SUTC-041.

### U02A-SUTC-013: Absent target backs off up to 30 s

- **Severity:** low
- **Covered by:** TOOLKITCS-006: an absent target retries on the first delay; only connection failures escalate the
  1/4/16/30 s backoff.
- **Plan v2:** B048.
- **Related:** U02A-SUTC-019.

### U02A-SUTC-014: Port-owner check is IPv4-only

- **Severity:** low
- **Covered by:** TOOLKITCS-009 and plan claim C6: connect to `127.0.0.1` socket URLs only, so the IPv4 owner table
  is the complete check (update the `localhost` row in `SteamCefTests`).
- **Plan v2:** B048.
- **Related:** U02A-SUTC-015.

### U02A-SUTC-015: A non-127.0.0.1 specific-address row can decide the owner verdict

- **Severity:** low
- **Covered by:** TOOLKITCS-009: `SteamCef.IsSteamPortOwner` considers only loopback and `0.0.0.0` rows. The critic
  listed this id as unreferenced, but TOOLKITCS-009 cites it (as "U02A-014/015") and is still valid at
  `lib/SteamCef.cs:121-156`. Test: a `192.168.x` squatter row plus a Steam wildcard row.
- **Plan v2:** B048.
- **Related:** U02A-SUTC-014.

### U02A-SUTC-017: Bridge removal ignores the evaluation result

- **Severity:** low
- **Covered by:** TOOLKITCS-037 (confirmed): removal checks `{ok:true}`, and a bootstrap the page answered with an
  error carries that error into the apply diagnostic.
- **Plan v2:** B049.
- **Related:** U02A-SUTC-002, U02A-SUTC-056.

### U02A-SUTC-018: Kill-switch or generation cancellation is reported as "Patch operation timed out."

- **Severity:** low
- **Covered by:** TOOLKITCS-028: check which source cancelled (phase timeout, kill switch, generation change) and
  report each with its own text.
- **Plan v2:** B054.
- **Related:** U02A-SUTC-003.

### U02A-SUTC-019: Patch manager does not schedule its own synchronization on generation change

- **Severity:** low
- **Covered by:** TOOLKITCS refinement R5 (plan claim C18, accurate): the manager's existing coalescing queue becomes
  the only loop, generation events queue it, and it raises `Synchronized` after each pass; WSGM deletes
  `SynchronizeLoopAsync`. Still present: `SteamUiPatchManager.OnGenerationChanged` (`lib/SteamUiPatchManager.cs:977-1016`)
  only invalidates entries. Implementation detail for B054: call `QueueSynchronization()` once at the end of the
  handler when any entry was invalidated; raise `Synchronized` asynchronously after the iteration, never inline
  (toolkitcs.verify batch problem 5). The critic listed this id as unreferenced; R5 is its owner.
- **Plan v2:** B054.
- **Related:** U02A-SUTC-021, STEAMHOST-004, STEAMHOST-037, TOOLKITCS-030.

### U02A-SUTC-020: Hard-coded "bridge first" instead of a dependency model; docs claim id order

- **Severity:** low
- **Covered by:** TOOLKITCS-025 with refinement R3 (no `DependsOn` graph: bridge first on apply, last on every
  removal path, one rule in one place) and TOOLKITCS-060 for the stale order docs (`lib/SteamUiModule.cs:100-101`,
  `lib/Surfaces/SteamUiBridgePatch.cs:12-14`).
- **Plan v2:** B054 (ordering), B056 (docs).
- **Related:** U02A-SUTC-002.

### U02A-SUTC-021: SetPatchEnabledAsync runs a full synchronization pass

- **Severity:** low
- **Covered by:** TOOLKITCS-030, as amended by toolkitcs.verify batch problem 5: the awaited switch keeps running its
  own pass under the scheduler gate, which avoids the deadlock with the `Synchronized` handler. The pass stays
  whole on purpose: the bridge-last removal order needs the full set, and per-entry synchronization would bring back
  the dependency logic R3 drops. B054 records this as the resolution.
- **Plan v2:** B054.
- **Related:** U02A-SUTC-019.

### U02A-SUTC-022: A probe that answered with an error is classified as AbsentTarget

- **Severity:** low
- **Covered by:** TOOLKITCS-033 (confirmed): `Reachable && Error` is `Incompatible` with the diagnostic.
- **Plan v2:** B049.
- **Related:** U02B-SUTC-016.

### U02A-SUTC-023: Non-object JSON results throw InvalidOperationException in evaluation helpers

- **Severity:** low
- **Covered by:** TOOLKITCS-034 (confirmed): check `ValueKind == Object` (and string kinds) before reading members in
  `SteamUiPatchEvaluation` and `SteamUiBridge`.
- **Plan v2:** B049.
- **Related:** U02A-SUTC-051.

### U02A-SUTC-025: Ownership release assumes a v1 snapshot and treats a non-restoring accessor release as success

- **Severity:** low
- **Covered by:** TOOLKITJS-020 (confirmed): `releaseAccessor` refuses when no original is stored and `releaseValue`
  checks `isPropertySnapshot`; no snapshot versioning.
- **Plan v2:** B050.
- **Related:** U02A-SUTC-026.

### U02A-SUTC-027: Reference fault list for the CDP connection is stale

- **Severity:** low
- **Where:** `toolkit/docs/reference.md:271-273` against `lib/SteamUiCdpConnection.cs:97-126` (wire receive),
  `:332-389` (`ReadLoopAsync`), `:413-509` (`DropMalformed`, `ProcessMessage`).
- **Problem:** confirmed. The reference says a non-object message, an invalid id, an `error` member, a reply with
  neither `result` nor `error`, a notification without a method and oversized parameters end the connection. The
  code drops and logs non-JSON, non-object, invalid-id and method-less frames (at most three lines per connection),
  forwards oversized notification parameters as `{}` after a logged drop, and fails only the one request for an
  `error` reply or a reply lacking `result`. Only a full notification queue, a non-text frame or a response above
  8 MiB end the connection. A consumer reading the reference expects reconnects (and generation bumps) that never
  happen.
- **Best solution:** rewrite that paragraph from the code, in three sentences: what ends the connection (queue of
  256 full, non-text frame, response over 8 MiB, a failed send, socket close or read error), what fails one request
  (`error` member,
  reply without `result`), and what is dropped with a throttled warning (not JSON, not an object, invalid id,
  notification without a method, parameters over 1 MiB forwarded as `{}`, orphan responses). Write it after B048
  lands so it describes the final send and close behaviour.
- **Tests:** none (documentation); `npm run prelude:claims` in the child as B177's check.
- **Plan v2:** B177 (child `docs/reference.md`).
- **Related:** U02A-SUTC-039 (no-change), U02A-SUTC-057.

### U02A-SUTC-028: Reference bridge caps (16 KiB / 1 MiB) are stale; no inbound cap at the bridge

- **Severity:** low
- **Where:** `toolkit/docs/reference.md:469-470`, `:540`, `:547-550`, `:722`; code `lib/SteamUiBridge.cs:91-152`
  (authorizer, no payload size rule), `:225` (`MaximumDeliveryCharacters = 32 MiB`), `:246` (request channel
  `2 * MaximumPendingRequests` = 64); `lib/SteamUiCdpConnection.cs:157`, `:494-503` (1 MiB notification bound).
- **Problem:** confirmed. The authorizer table lists a `payload exceeded its limit` rejection at 16 KiB that no longer
  exists and omits `payload is missing`; `:547` says accepted payloads are at most 16 KiB; the constants row at `:722`
  gives a 16 KiB inbound cap and a 1 MiB delivery cap while the code has no bridge inbound cap and a 32 MiB delivery
  cap. Consumers and reviewers work from wrong limits. The ledger's second direction, adding an inbound bridge cap,
  is rejected: the CDP read bound of 1 MiB per notification is the D2-listed boundary, and TOOLKITCS-017 (refuted)
  shows the drop is already logged.
- **Best solution:** correct the reference to the code as it stands after B049 and B056: no bridge-level inbound cap,
  with a pointer to the connection's 1 MiB notification bound; the authorizer table matches `Authorize` exactly
  (schema, type, allowlist, positive sequence and generation, payload missing, stale generation, cancel, replays);
  the constants row lists schema 1, operation timeout 5 s, 32 pending requests and a 64-slot request channel, with
  no delivery cap once B056 deletes `MaximumDeliveryCharacters` (TOOLKITCS-031, TOOLKITJS-V-008; chunked delivery
  already exists); the rejection log line describes the redacted payload shape from B049 (TOOLKITCS-020) instead of
  "the first 200 characters". Also delete the sentence "The injected `request()` refuses a payload past the inbound
  cap itself" (`:478-479`): `bridge.ts` `request` has no size check, and an oversized payload is dropped by the
  connection's 1 MiB bound, so the page's request ends on its own timeout. The same pass drops the "Patch bounds
  default" row (B054 deletes `SteamUiPatchBounds`) and the extension "script cap" (B056) from section 13, and the
  section 8 "Identity and configuration" paragraph loses "`MaximumDeliveryCharacters = 32 MiB` as a guard on one
  delivery" and the "A state past the guard is not delivered" and "An answer past it" sentences. The cap is the only
  producer of the `refused` envelope (`lib/SteamUiBridge.cs:534`); describe `subscribeRefusal` exactly as B056 leaves
  it (removed with its producer, or kept), not from this note.
- **Tests:** none (documentation).
- **Plan v2:** B177; it depends on B049 and B056 for the final wording. Decided: D2 keeps the CDP 8 MiB response and
  1 MiB notification reads, and the diagnostic cap goes (U02A-SUTC-044), so the reference lists no diagnostic bound.
- **Related:** U02B-SUTC-018 (same edit), U02B-SUTC-019, U02A-SUTC-057, TOOLKITCS-031.

### U02A-SUTC-029: Extension host is an unused, unintegrated public subsystem whose docs over-promise

- **Severity:** low
- **Covered by:** TOOLKITCS-062 with refinement R7: keep discovery and validation, drop the promised module adapter
  (no consumer), and rewrite the docs to "discovery and validation only".
- **Plan v2:** B056.
- **Related:** U02A-SUTC-030, U02A-SUTC-031, U02A-SUTC-060, U02A-SUTC-061.

### U02A-SUTC-030: No reserved host namespace: an extension can claim the host's patch ids

- **Severity:** low
- **Covered by:** TOOLKITCS-061: a reserved-prefix rejection for host ids such as `steam-ui`.
- **Plan v2:** B056.
- **Related:** U02A-SUTC-029.

### U02A-SUTC-031: A rejected extension loses its declared patches, contrary to the manifest contract

- **Severity:** low
- **Covered by:** TOOLKITCS-061: a rejection after a successful parse keeps the parsed manifest.
- **Plan v2:** B056.
- **Related:** U02A-SUTC-029.

### U02A-SUTC-032: SteamUiModuleSet construction depends on registration order, despite the doc

- **Severity:** low
- **Covered by:** TOOLKITCS-041 (confirmed): three indexes (installer, publisher, command handler) and failure
  attribution by the map of the failing callback.
- **Plan v2:** B049.
- **Related:** U02A-SUTC-033, U02A-SUTC-034.

### U02A-SUTC-033: Duplicate publications for one patch id are not rejected

- **Severity:** low
- **Covered by:** TOOLKITCS-041: duplicate publication owners throw `InvalidOperationException` at construction.
- **Plan v2:** B049.
- **Related:** U02A-SUTC-032.

### U02A-SUTC-034: Module quarantine is permanent, opaque, and duplicated by the consumer

- **Severity:** low
- **Covered by:** TOOLKITCS-044 and STEAMHOST-004: the runtime faults the failing module's patches in the manager,
  whose effective switch becomes `enabled && !faulted`; WSGM deletes `_failedPatchIds` and the remount guard. No
  Reset API (R4, verifier-corrected rationale: quarantine lasts until WSGM restarts, as today).
- **Plan v2:** B054.
- **Related:** U02A-SUTC-006, U02A-SUTC-007, U02A-SUTC-035.

### U02A-SUTC-035: Three refusal causes collapse into one "service not active" reason

- **Severity:** low
- **Covered by:** TOOLKITCS-042: one fixed reason per cause (commands disabled, module quarantined, command not
  registered).
- **Plan v2:** B049.
- **Related:** U02A-SUTC-007, U03A-SUTS-030.

### U02A-SUTC-036: Bridge host negative assertions run before asynchronous dispatch could happen

- **Severity:** low
- **Covered by:** TOOLKITCS-067: emit a valid sentinel after the bad payloads, wait for it, assert only the sentinel
  arrived. B049's test list adds this ("malformed binding notifications followed by a sentinel").
- **Plan v2:** B049 (test), B062 (resolves TOOLKITCS-067).
- **Related:** U02A-SUTC-004 (no-change).

### U02A-SUTC-037: Patch manager tests miss the dependency teardown and partial-apply scenarios

- **Severity:** low
- **Covered by:** TOOLKITCS-066: a `FakePatch` "mutate then fail" mode with removal asserted and a bridge-plus-gate
  global disable that ends `Disabled` for both; these tests are in B054's list.
- **Plan v2:** B054 (tests), B062.
- **Related:** U02A-SUTC-002, U02A-SUTC-003.

### U02A-SUTC-038: CDP close/fault and cancel-during-send paths are untested

- **Severity:** low
- **Covered by:** TOOLKITCS-066: `QueueWire` gains close, fault and blocking-send modes; tests in B048's list.
- **Plan v2:** B048 (tests), B062.
- **Related:** U02A-SUTC-011.

### U02B-SUTC-007: An explicit-transport evaluation drops the error when Steam answered with an error

- **Severity:** low
- **Covered by:** TOOLKITCS-003, refinement R2 and plan claim C37 (`lib/Client/SteamClientScript.cs:137-139`, still
  present). After B053 `SteamClientScript` has one evaluation path over the client's transport and an answered error
  is carried as `Rejected` with its text. Appendix B records it as fixed by B053; the critic's "unreferenced" note
  predates that assignment.
- **Plan v2:** B053.
- **Related:** U02A-SUTC-001, U02B-SUTC-042, A01-F004.

### U02B-SUTC-008: Library add swallows purge and label failures and reports Added

- **Severity:** low
- **Covered by:** TOOLKITCS-016 (verifier corrected locations: `lib/Client/SteamInstallFolders.cs:205-209`): every
  reply after the first mutation carries purge and label results; C# maps them to a partial status with detail.
- **Plan v2:** B053.
- **Related:** U02B-SUTC-009, LIBRARY review (consumer side).

### U02B-SUTC-009: Install-folder writes are not serialized

- **Severity:** low
- **Covered by:** TOOLKITCS-018 (confirmed): the client's one write lane covers install folders, apps and
  collections.
- **Plan v2:** B053.
- **Related:** U02B-SUTC-004, U02B-SUTC-008.

### U02B-SUTC-010: A collection created and then failed loses its id

- **Severity:** low
- **Covered by:** TOOLKITCS-016: the reply carries the new collection id from creation on, including failure replies.
- **Plan v2:** B053.
- **Related:** U02B-SUTC-001.

### U02B-SUTC-011: A partial startup-movie set-aside loses the user's original choice

- **Severity:** low
- **Covered by:** TOOLKITCS-016 (toolkit returns the original choice in every reply after the first write) and
  STEAMHOST-016 (WSGM persists `SteamSetAside` whenever a choice is present, accepted or not).
- **Plan v2:** B053.
- **Related:** STEAMHOST-V-005. D10 decided: WSGM hands the set-aside startup-movie choice back to Steam at exit when
  no WSGM movie is chosen, and at uninstall, so the persisted choice from this fix is what gets restored.

### U02B-SUTC-012: AddShortcut can adopt a foreign entry when Steam returns no id

- **Severity:** low (TOOLKITCS-015 was lowered from high to low by the verifier)
- **Covered by:** TOOLKITCS-015: without a returned id, adopt the gained entry only when its exe and name match the
  request; otherwise `Confirmed=false` and write nothing.
- **Plan v2:** B053.
- **Related:** U02B-SUTC-005.

### U02B-SUTC-013: Several reads collapse unreachable, refused and absent into null, empty or 0

- **Severity:** low
- **Covered by:** TOOLKITCS-021: typed read results only; the lossy `ListGamesAsync`, `ListCollectionsAsync`,
  `ListStoreTagsAsync` and `IsLoadedAsync` are deleted with their WSGM consumers.
- **Plan v2:** B053.
- **Related:** U02B-SUTC-004.

### U02B-SUTC-014: App-id types and normalization are inconsistent across client types

- **Severity:** low
- **Covered by:** TOOLKITCS-022: `uint` app ids in every client record and `>>>0` in page scripts.
- **Plan v2:** B053.
- **Related:** U02B-SUTC-013.

### U02B-SUTC-015: SourceGeneration restarts at 1 and the resident observer has no version

- **Severity:** low
- **Covered by:** TOOLKITCS-019 with refinement R9: single-reader contract documented instead of reference counting.
  The version-stamp half is not adopted: TOOLKITCS-019 publishes the observer only after registration, so an observer
  left by an earlier WSGM is a working one of the same shape, and a shape change renames `ObserverProperty`. The
  verifier notes the generation restart is documented, intended behaviour (`lib/Client/SteamRunningApps.cs:15-17`),
  so no time-seeded generation.
- **Plan v2:** B053.
- **Related:** U02B-SUTC-002.

### U02B-SUTC-016: SteamGatePatch.ProbeAsync handles only JsonException and maps an answered-null to "target absent"

- **Severity:** low
- **Covered by:** TOOLKITCS-033 (answered error is `Incompatible`) and TOOLKITCS-035 (gates call
  `SteamUiPatchEvaluation.EvaluateProbeAsync`; `SteamGatePatch.ProbeAsync` is deleted).
- **Plan v2:** B049.
- **Related:** U02A-SUTC-022, U02A-SUTC-023.

### U02B-SUTC-017: The documentation contradicts the "never name a module id" rule

- **Severity:** low
- **Covered by:** TOOLKITCS-060 for the code comments (`lib/Surfaces/SteamUiProbeJs.cs:10`,
  `lib/Surfaces/SteamGatePatch.cs:39`). The reference lines still saying "a literal module id"
  (`toolkit/docs/reference.md:100`, `:764`) are rewritten to the fingerprint and export-shape rule in the B177 pass.
- **Plan v2:** B056 (code comments), B177 (reference.md).
- **Related:** U02A-SUTC-058, TOOLKITJS-007.

### U02B-SUTC-018: reference.md contradicts itself on the inbound bridge payload cap

- **Severity:** low
- **Where:** `toolkit/docs/reference.md:469-470` ("what the document sends has no size limit") against `:540`, `:547`
  and `:722` (16 KiB).
- **Problem:** confirmed. Both statements cannot be true, and the code agrees with neither in full: there is no
  bridge-level cap, but the connection bounds notification parameters at 1 MiB (`lib/SteamUiCdpConnection.cs:157`).
- **Best solution:** the single reference edit described under U02A-SUTC-028: state once that the bridge adds no
  inbound cap and that the CDP connection's 1 MiB notification read bound (D2, decided) is the only limit, and delete the
  16 KiB statements.
- **Tests:** none (documentation).
- **Plan v2:** B177.
- **Related:** U02A-SUTC-028, U02B-SUTC-019.

### U02B-SUTC-019: SteamUiPayload is documented as exact and bounded but strings and arrays are unbounded

- **Severity:** low
- **Covered by:** TOOLKITCS-031: correct the docs (`lib/Surfaces/SteamUiPayload.cs` remarks, reference.md "bounded
  strings"); do not add string or array bounds (no-arbitrary-limits).
- **Plan v2:** B056 (code docs), B177 (reference.md).
- **Related:** U02B-SUTC-018.

### U02B-SUTC-020: The README links to a WSGM plan file that does not exist

- **Severity:** low
- **Where:** `toolkit/README.md:369-371`.
- **Problem:** confirmed. "Its `_plan/steam-ui-toolkit.md` records what has been done and what has not." The parent
  `_plan/` holds only `gpu-vendor-implementation.md`, `implementation-todo.md` and `overlay-tools-cef-parity.md`.
- **Best solution:** delete that sentence; keep the "Extracted from WSGM" sentence and its link. Do not point at
  another WSGM plan file: the toolkit README should not depend on the parent's planning files.
- **Tests:** none.
- **Plan v2:** B177 (child README).
- **Related:** U02B-SUTC-029.

### U02B-SUTC-021: Public XML docs are incomplete although AGENTS.md says the build enforces them

- **Severity:** low
- **Covered by:** TOOLKITCS-024 (`lib/Client/SteamApps.cs` members with a summary only). B053 rewrites those members
  into `SteamClient.Apps` and documents every parameter and return. No new analyzer and no AGENTS.md edit (the
  disposition's "no live instruction edit").
- **Plan v2:** B053.
- **Related:** U02A-SUTC-043, U02B-SUTC-030.

### U02B-SUTC-022: The emitted-asset checks depend on alphabetical file names and regex-scrape C# source

- **Severity:** low
- **Covered by:** TOOLKITJS-011 (checks instantiate whole fragments through builder-inserted `// @fragment` markers)
  and TOOLKITJS-010 (one fragment-order owner).
- **Plan v2:** B047.
- **Related:** U03A-SUTS-018, U02B-SUTC-027.

## Nit

### U02A-SUTC-040: ConsecutiveTimeouts not reset by answered errors; local backpressure counts as a timeout

- **Severity:** nit
- **Covered by:** TOOLKITCS-004 (verifier lowered to low; an answered JavaScript error keeps `Ready` and resets the
  timeout run) and TOOLKITCS-003 (local limits become `NotSent`, not a timeout or incompatibility).
- **Plan v2:** B048 (health), B053 (dispatch classification).
- **Related:** U02A-SUTC-008.

### U02A-SUTC-041: LastFailure overwritten after retirement

- **Severity:** nit
- **Where:** `lib/PersistentSteamUiTransport.cs:812-834` (`DropUnansweredConnection` sets "Steam UI evaluation timed
  out." and disposes the connection), `:733-748` (`OnConnectionClosed`); `lib/SteamUiCdpConnection.cs:332-389`
  (a disposal ends the read loop with `failure == null`).
- **Problem:** confirmed. When a run of unanswered evaluations retires the connection, the dispose closes it with no
  failure, and `OnConnectionClosed` replaces `LastFailure` with the generic "Steam UI target closed the channel."
  The next evaluation that finds no connection returns that generic text as its error (`:192-197`), and the
  transport snapshot shows it. The retirement warning line is still in the log, so the cost is a less useful
  in-band diagnostic.
- **Best solution:** one line in `OnConnectionClosed`: keep an existing reason when the close carries none,
  `channel.LastFailure = failure?.Message ?? channel.LastFailure ?? "Steam UI target closed the channel.";`. A
  successful connect (`:610`) and every answered evaluation (`SetHealth(..., Ready, null)`; after B048's
  TOOLKITCS-004 an answered JavaScript error clears it too) reset the field, so a kept reason is the last thing that
  went wrong on that connection since its last answer: an unanswered evaluation or a CDP `error` reply. A clean close
  after a success still reports the generic text. No per-generation bookkeeping and no new state.
- **Tests:** extend `PersistentSteamUiTransportTests.UnansweredEvaluationsRetireTheConnectionSoTheChannelReconnects`:
  after the two unanswered evaluations (`UnansweredEvaluationsBeforeReconnect` is 2), wait until the
  `SharedJsContext` snapshot reports `Retrying` and assert its `LastFailure` is "Steam UI evaluation timed out." (the
  reconnect waits out its first 1 s delay, so the window is wide), then keep the existing reconnect wait. Filter
  `--filter "FullyQualifiedName~PersistentSteamUiTransportTests"`.
- **Plan v2:** B048 (edits `PersistentSteamUiTransport.cs`).
- **Related:** U02A-SUTC-012.

### U02A-SUTC-042: EnsureRemoteDebuggingEnabled return value poorly documented

- **Severity:** nit
- **Covered by:** TOOLKITCS-060 with plan claim C39 (the ledger's "created now" reading is inaccurate; the doc is
  wrong only for `enabled=false`). The doc becomes "true when enabled and the flag is present".
- **Plan v2:** B056.
- **Related:** none.

### U02A-SUTC-043: Missing parameter/returns docs on public members despite CS1573 policy

- **Severity:** nit
- **Where:** `lib/SteamUiTransportModels.cs:94-98` (`SteamUiEvaluationResult.Unavailable`, summary only);
  `lib/SteamUiPatchManager.cs:166-198` (`ISteamUiPatch.ProbeAsync`, `ApplyAsync`, `VerifyAsync`, `RemoveAsync`:
  no `<param>` or `<returns>`), `:527-528` (`GetSnapshots`, no `<returns>`).
- **Problem:** confirmed. CS1573 fires only when some parameters of a member are documented, so members with none
  pass the documentation gate the AGENTS file describes. `ISteamUiPatch` is part of the plugin contract closure
  (TOOLKITCS-V-001), so its gaps are the ones outside implementers read.
- **Best solution:** document these members in the batches that rewrite them rather than in a separate docs pass:
  B053 deletes the `Unavailable` factory and `Reachable` (TOOLKITCS-003 makes the record
  `(SteamUiDispatch Dispatch, string? Value, string? Error, SteamUiGenerations Generations)`) and documents every
  member it adds with `<param>` and `<returns>`; B054 rewrites `ISteamUiPatch` (no `Version`, `ResourceKey`, `Bounds`) and gives each method
  `<param name="context">`, `<param name="cancellationToken">` and `<returns>`, plus `<returns>` on `GetSnapshots`.
  No new analyzer: the gap is a handful of members and the rewrites touch all of them.
- **Tests:** `dotnet build external\steam-ui-toolkit\SteamUiToolkit.slnx -c Release` stays warning-free.
- **Plan v2:** B053 (`SteamUiEvaluationResult`), B054 (`ISteamUiPatch`, `GetSnapshots`).
- **Related:** U02B-SUTC-021, TOOLKITCS-024, TOOLKITCS-V-001.

### U02A-SUTC-044: Bound helper may exceed its maximum by 3 and split surrogate pairs

- **Severity:** nit
- **Where:** `lib/SteamUiShared.cs` (`MaximumDiagnosticLength = 2048`, `Bound`); callers
  `lib/PersistentSteamUiTransport.cs` (`LastFailure`), `lib/SteamUiBridge.cs` (state `error` member),
  `lib/SteamUiCdpConnection.cs` (JavaScript exception and CDP error texts), `lib/SteamUiPatchEvaluation.cs`
  (public `Bounded`, no caller in the toolkit, its tests or WSGM), `lib/SteamUiPatchManager.cs` (`WriteSnapshot`:
  fingerprint at 512, failure at `Bounds.MaximumDiagnosticCharacters`).
- **Problem:** confirmed. `Bound` returns `maximumLength + 3` characters and can cut between a surrogate pair. The
  helper itself is the larger issue under D2: it is a length cap that truncates diagnostics, and the D2 list of kept
  byte bounds does not include it. TOOLKITCS-040's fix (reserve room for the ellipsis, respect surrogates) would
  polish a cap the decision removes.
- **Best solution:** decided by D2 (only the plan v2 list of byte bounds stays): delete `SteamUiShared.Bound`,
  `SteamUiShared.MaximumDiagnosticLength` and the public `SteamUiPatchEvaluation.Bounded`, and pass every diagnostic
  whole at the five call sites (`Bounded` callers inside the library use `string.IsNullOrEmpty(value) ? null : value`
  where they relied on the null mapping). The texts are already bounded by their source: they arrive through the
  CDP connection's 8 MiB response and 1 MiB notification reads, which D2 keeps. The patch manager's two calls go
  with `SteamUiPatchBounds` in B054; fingerprints are short literals from the probes. The patch log line still goes
  through `SteamUiLog.Change`, which writes only on change, so a long failure is written once. This supersedes
  TOOLKITCS-040 for B049.
- **Tests:** delete any assertion on a truncated diagnostic (none exists today outside
  `SteamUiPatchManagerTests`'s bounds test, which B062 deletes with U02A-SUTC-066); add one case in
  `SteamUiCdpConnectionTests` where the queued `Runtime.evaluate` reply carries `exceptionDetails` longer than 2,048
  characters and the evaluation error contains it whole.
  `dotnet build external\steam-ui-toolkit\SteamUiToolkit.slnx -c Release` warning-free, then
  `--filter "FullyQualifiedName~SteamUiCdpConnectionTests|FullyQualifiedName~PersistentSteamUiTransportTests|FullyQualifiedName~SteamUiPatchManagerTests"`.
- **Plan v2:** B049 (replaces the TOOLKITCS-040 step; D2 decided). B054 removes the manager's bounds parameter.
- **Related:** U02A-SUTC-066, TOOLKITCS-040, TOOLKITCS-031.

### U02A-SUTC-045: Generic toolkit carries WSGM "native-qam" vocabulary

- **Severity:** nit
- **Covered by:** TOOLKITCS-038: `steam-ui:` correlation prefix and `steam-ui-*` fingerprints (log-only strings, no
  WSGM parser).
- **Plan v2:** B054.
- **Related:** U02B-SUTC-031.

### U02A-SUTC-046: Bridge rejection log echoes the raw payload and is floodable

- **Severity:** nit
- **Covered by:** TOOLKITCS-020, which cites the same line (`lib/SteamUiBridge.cs:744-754`): payload lines keep
  property names, kinds, numbers and booleans and replace string values by their length (refinement R11). The flood
  half needs no rate limiter: the line goes through `SteamUiLog.Change` with a fixed key, which writes only when the
  text changes and counts repeats. The redaction is kept for its functional reason (testers send wsgm.log), not as
  hardening; forged envelopes are an accepted same-user boundary (U02A-SUTC-004, dropped by decision).
- **Plan v2:** B049.
- **Related:** U02A-SUTC-062, U02A-SUTC-004 (no-change).

### U02A-SUTC-047: NotificationReceived concurrency not documented

- **Severity:** nit
- **Covered by:** TOOLKITCS-007. Two pumps raise `NotificationReceived` today (`_notificationEvents` and
  `_bindingEvents`, `lib/PersistentSteamUiTransport.cs:115-122`), which is where the concurrency comes from. B048
  deletes the dead notification lane, leaving one pump; B053 renames the event `BindingCalled` and its XML doc states
  the contract: raised serially from one background pump, handlers must not block. `GenerationChanged` gets the same
  sentence.
- **Plan v2:** B048 (lane removal), B053 (rename and doc).
- **Related:** none.

### U02A-SUTC-049: SetRuntimeBindingAsync and EvaluateAsync report failure differently

- **Severity:** nit
- **Covered by:** TOOLKITCS-013 (verifier: the disposed check already exists): keep the throwing contract for the
  single internal caller and document it.
- **Plan v2:** B053.
- **Related:** none.

### U02A-SUTC-050: Receive buffer never shrinks after a large frame

- **Severity:** nit
- **Where:** `lib/SteamUiCdpConnection.cs:81-83` (`readonly ArrayBufferWriter<byte> _received = new(16 * 1024)`),
  `:97-126` (`SteamUiWebSocketWire.ReceiveAsync`).
- **Problem:** confirmed. The writer is reused for every message and grows to the largest frame seen, up to the 8 MiB
  response bound, and keeps that capacity for the connection's life. Each role holds its own wire, so a single large
  answer or console frame can pin megabytes per role until the next reconnect.
- **Best solution:** after copying a message out, replace the writer when it grew past its starting size: make the
  field non-readonly and, after `writer.WrittenMemory.ToArray()`, do
  `if (writer.Capacity > InitialReceiveBytes) _received = new ArrayBufferWriter<byte>(InitialReceiveBytes);` with
  `InitialReceiveBytes = 16 * 1024` shared with the constructor. Ordinary frames keep reusing the buffer, so the
  steady state allocates nothing new; only a frame above 16 KiB costs one fresh 16 KiB buffer afterwards. This is
  simpler than an `ArrayPool` rental, which would need returns on every exit path of the loop.
- **Tests:** none practical (the wire wraps a real `ClientWebSocket`); run
  `--filter "FullyQualifiedName~SteamUiCdpConnectionTests"` to guard the connection.
- **Plan v2:** B048 (edits `SteamUiCdpConnection.cs`).
- **Related:** none.

### U02A-SUTC-051: Binding name read can throw InvalidOperationException

- **Severity:** nit
- **Covered by:** TOOLKITCS-034: check `ValueKind == String` before `GetString()` in `OnNotificationReceived`.
- **Plan v2:** B049.
- **Related:** U02A-SUTC-023.

### U02A-SUTC-053: Library csproj comments are stale

- **Severity:** nit
- **Covered by:** TOOLKITCS-011: the "stack buffer" and module-id rationale comments in
  `lib/SteamUiToolkit.csproj:11-13,25-27`.
- **Plan v2:** B048.
- **Related:** U02B-SUTC-024.

### U02A-SUTC-054: Per-resource gates are redundant under the scheduler gate

- **Severity:** nit
- **Covered by:** TOOLKITCS-026: delete the per-resource semaphores and the `ISteamUiPatch.ResourceKey` member.
- **Plan v2:** B054.
- **Related:** none.

### U02A-SUTC-055: Logging under the entry lock

- **Severity:** nit
- **Covered by:** TOOLKITCS-029: build the line inside `entry.Sync`, write it after release; the warning level uses
  the dispatch outcome instead of the static session.
- **Plan v2:** B054.
- **Related:** none.

### U02A-SUTC-056: In-page dispose keeps the gates map

- **Severity:** nit
- **Covered by:** TOOLKITJS-019 (confirmed): dispose clears `gates`, refuses later deliveries and returns the failed
  gate names for the C# bootstrap to log.
- **Plan v2:** B050.
- **Related:** U02A-SUTC-017.

### U02A-SUTC-058: rpc.ts comment names a module id; failures swallowed without a diagnostic

- **Severity:** nit
- **Covered by:** TOOLKITJS-007 for the module id in the comment (`lib/SteamUiAssets/Source/rpc.ts:33`; delete the id,
  keep the "renumbered" rationale). The swallowed `invalidateQuery` failure stays as written: the comment at
  `:37-39` records the decision (a moved query layer leaves a stale row, never a reason to tear down a working gate),
  and the injected script has no log channel (TOOLKITJS claim C26), so a diagnostic would be new mechanism.
- **Plan v2:** B050 (`rpc.ts` is in B050's file list and TOOLKITJS-007 places its comment fix there; the other
  TOOLKITJS-007 comments are B051).
- **Related:** U02B-SUTC-017.

### U02A-SUTC-060: Discovery order and script-limit naming do not match their docs

- **Severity:** nit
- **Covered by:** TOOLKITCS-060 (the "ordered by id" doc versus the directory sort, `lib/SteamUiExtensionHost.cs:51-53`)
  and TOOLKITCS-031 with R7 (the script cap is deleted, so `MaximumScriptCharacters` needs no rename).
- **Plan v2:** B056.
- **Related:** U02A-SUTC-029.

### U02A-SUTC-062: Command refusal log includes the full request payload

- **Severity:** nit
- **Covered by:** TOOLKITCS-020 (refinement R11): redact string values instead of bounding the payload, which also
  stops plugin `secret` settings from reaching wsgm.log.
- **Plan v2:** B049.
- **Related:** U02A-SUTC-046.

### U02A-SUTC-063: "Never null on failure" is not enforced; Command builder skips argument validation

- **Severity:** nit
- **Covered by:** TOOLKITCS-045 as replaced by toolkitcs.verify batch problem 4: the runtime normalizes a refusal
  without detail to the fixed "no reason reported" text instead of the constructor throwing (a throw would quarantine
  the module through `FailModule`); builders null-check delegates and readers and refuse a blank patch id or command
  name with `ArgumentException.ThrowIfNullOrWhiteSpace` (that runs once at module construction, never in a handler).
- **Plan v2:** B049.
- **Related:** none.

### U02A-SUTC-064: Timing-sensitive tests and an opaque wait helper

- **Severity:** nit
- **Covered by:** TOOLKITCS-068: descriptive timeout failures in `Fakes/TestJson.cs`; the manager's settle delay and
  the transport's retry delays become values passed through internal constructors (the transport's lands in B048 with
  TOOLKITCS-006), so tests pass milliseconds. No `TimeProvider` (TOOLKITCS-068 rejects the fake-clock plumbing).
- **Plan v2:** B062.
- **Related:** none.

### U02A-SUTC-065: Process-wide test state not isolated; fake generations unsynchronized

- **Severity:** nit
- **Covered by:** TOOLKITCS-067: non-parallel collection for tests that touch statics (the session test disappears
  with B053) and a lock around `FakeSteamUiTransport.Generations`.
- **Plan v2:** B062.
- **Related:** U02A-SUTC-006.

### U02A-SUTC-066: A getter-only test

- **Severity:** nit
- **Covered by:** TOOLKITCS-065: delete `PatchBoundsPreservePublishedNamedArguments` (and `SteamUiPatchBounds` itself
  goes in B054).
- **Plan v2:** B062.
- **Related:** none.

### U02B-SUTC-023: ci.yml has a stale comment, a misleading step name and unpinned actions

- **Severity:** nit
- **Where:** `toolkit/.github/workflows/ci.yml:41-49` (step "Ownership claims" and its comment), `:18-28` (actions on
  `@v5` tags).
- **Problem:** partly confirmed. The step named "Ownership claims" runs `npm run prelude:claims`, which is every
  `eng/check-*.mjs`, not only the ownership check, and its comment describes only the ownership scenarios. The
  comment's "the C# tests never execute the injected script" is literally true (no C# test loads the composed
  prelude), but it hides that `dotnet test` needs Node too: `Fakes/NodeScript.cs` starts `node` for the client and
  surface expressions in `SteamWindowSurfaceTests` and `SteamRouteNavigationTests`, and the workflow only passes
  because "Set up Node" happens to come before "Test". The action tags are a preference.
- **Best solution:** rename the step "Prelude checks"; replace its comment with two accurate lines: it builds the
  prelude and runs every emitted-asset check against the emitted JavaScript (the C# suite runs single expressions in
  Node, never the composed asset), and the prelude build proves the bridge names no consumer gate. Add a one-line
  comment on "Set up Node" that the Test step needs it too, so nobody moves it below. The em dash in the old comment
  goes with it. Keep the `@v5` tags: SHA pins add an update chore with no defect behind it.
- **Tests:** none locally; the next child push runs the workflow.
- **Plan v2:** B047 (toolkit check infrastructure; it changes what `prelude:claims` runs). Add
  `external/steam-ui-toolkit/.github/workflows/ci.yml` to B047's file list.
- **Related:** TOOLKITJS-038, TOOLKITJS-012, U02B-SUTC-041.

### U02B-SUTC-024: Directory.Build.props and .editorconfig carry WSGM leftovers

- **Severity:** nit
- **Where:** `toolkit/Directory.Build.props:9-11` (`AVLN3001` NoWarn with an Avalonia comment), `:6-8`
  (`NuGetAudit=false`); `toolkit/.editorconfig:22` (`[*.{axaml,xml,csproj,props,slnx}]`).
- **Problem:** confirmed. The toolkit has no Avalonia reference and no `.axaml` file, so the NoWarn and the glob
  entry are copied from WSGM and mislead a reader about the toolkit's dependencies. BUILD-032 covers only the
  parent's own `Directory.Build.props`.
- **Best solution:** delete the `AVLN3001` `<NoWarn>` line and its comment; change the glob to
  `[*.{xml,csproj,props,slnx}]`. Keep `NuGetAudit=false` and its comment: offline determinism is a real reason and
  GitHub's dependency alerts work without a tracked configuration file.
- **Tests:** `dotnet build external\steam-ui-toolkit\SteamUiToolkit.slnx -c Release` warning-free.
- **Plan v2:** B047 (toolkit repo metadata, beside TOOLKITJS-038). Add `external/steam-ui-toolkit/Directory.Build.props`
  and `.editorconfig` to B047's file list.
- **Related:** BUILD-032, PV08-011, U02A-SUTC-053.

### U02B-SUTC-025: .gitattributes and .editorconfig disagree on line endings

- **Severity:** nit
- **Where:** `toolkit/.gitattributes:1-5` (CRLF pinned for `*.cs`, `*.csproj`, `*.props` only);
  `toolkit/.editorconfig:3-5` (`[*] end_of_line = crlf`).
- **Problem:** confirmed, low impact. Editors are told CRLF for every file, while git pins CRLF only for the C#
  family; TypeScript, `.mjs`, Markdown and JSON have no `text` attribute and follow each checkout's `core.autocrlf`.
  The index is LF throughout and the working tree on this machine (autocrlf true) shows mixed endings in
  `components.ts`, `gates/navigation.ts` and `eng/check-navigation-panel.mjs`, which git normalizes on commit. The
  real exposure is a checkout with autocrlf off: an editor honouring `[*] end_of_line = crlf` writes CRLF into a
  file with no `text` attribute, and git commits those bytes as they are.
- **Best solution:** make `.editorconfig` say what git enforces: move `end_of_line = crlf` out of `[*]` into a
  `[*.{cs,csx,csproj,props}]` section and leave other files to their checkout. Do not widen `.gitattributes` to
  `* text eol=crlf`: it would rewrite every non-C# file's checkout for no defect. (Endings cannot reach WSGM's asset
  hash either way: `eng/build-steam-assets.mjs` formats the composed asset with Prettier, which normalizes them.)
  The parent's `.editorconfig` has the same `[*]` line but pins its hashed TypeScript in `.gitattributes`; it is out
  of this scope and stays.
- **Tests:** none; the Rider cleanup and Roslyn style checks still see CRLF for C#.
- **Plan v2:** B047 (with the `.editorconfig` edit of U02B-SUTC-024).
- **Related:** U02B-SUTC-024.

### U02B-SUTC-026: The lockfile names a package that package.json does not

- **Severity:** nit
- **Covered by:** TOOLKITJS-038 (confirmed): align `package.json` and `package-lock.json` metadata.
- **Plan v2:** B047.
- **Related:** U02B-SUTC-023.

### U02B-SUTC-027: Node is resolved inconsistently

- **Severity:** nit
- **Covered by:** TOOLKITJS-010 (`build-prelude.mjs` spawns `process.execPath`) and TOOLKITCS-071 (`NodeScript`
  resolution and stderr on timeout).
- **Plan v2:** B047 (eng scripts), B062 (`NodeScript`).
- **Related:** U02B-SUTC-041, U02B-SUTC-022.

### U02B-SUTC-028: The AGENTS.md list of checks is incomplete

- **Severity:** nit
- **Covered by:** TOOLKITJS-012: `run-checks.mjs` discovers `eng/check-*.mjs`, so no hand list decides what runs.
  The AGENTS.md map (`toolkit/AGENTS.md:39-45`, still missing check-power-menu, check-theme-styles,
  check-sound-overrides and check-ui-kit) is replaced by "every `eng/check-*.mjs` except the harness" as a separate
  guidance diff for sign-off, per the disposition's "no live instruction edit".
- **Plan v2:** B047 (discovery), B177 (AGENTS proposal).
- **Related:** U03B-SUTS-044.

### U02B-SUTC-029: README and reference.md have grown by prepending and appending

- **Severity:** nit
- **Where:** `toolkit/README.md:3-5` (sound overrides before the introduction), `:377-383` (two paragraphs after
  "Licence"); `toolkit/docs/reference.md:3-4` (sound override pointer), `:13-37` (route navigation and game window
  activation before the facts table), `:50-85` ("Steam surface observations" inside "1. The shape"), `:1643-1662`
  (Quick Access host settings sections under "16. The client layer").
- **Problem:** confirmed. Each addition was placed at the top or bottom instead of in its section, so the README
  opens with one surface before saying what the library is, and the reference's ordering promise ("in the order a
  consumer meets it") no longer holds.
- **Best solution:** move each block into its home without rewriting it: the README sound-override line and the two
  post-Licence paragraphs into the surfaces and client how-to sections; in the reference, route navigation, game
  window activation, surface observations and the QAM host settings sections into section 15 (Surfaces), and the
  sound-override pointer into section 15's table. Do it inside the B177 rewrite so the moved text also reflects the
  refactor (for example, the client-layer intro that still promises "unreachable changed nothing").
- **Tests:** none (documentation).
- **Plan v2:** B177.
- **Related:** U02B-SUTC-030, U02B-SUTC-020.

### U02B-SUTC-030: The reference's API and test inventories are incomplete

- **Severity:** nit
- **Where:** `toolkit/docs/reference.md:105-153` and `:1577-1586` (API tables omit `SteamShortcut*`,
  `SteamLogoPosition`, `SteamAppDetailsResult`, `SteamLibraryReadResult` and the icon, account and logo APIs);
  `:732-758` (test table omits `SteamLibraryReadTests`, `SteamStartupMovieTests`, `SteamUiPayloadTests`,
  `SteamPagePatchTests`, `SteamPerformanceTests`, `SteamPowerMenuTests`, `SteamQuickAccessRowPatchTests`,
  `SteamRouteNavigationTests`, `SteamSettingsQuickAccessRowTests`, `SteamSettingsRowsTests`).
- **Problem:** confirmed. Both inventories are hand lists that drifted; a reader trusting them misses public types
  and test files.
- **Best solution:** API: B177 writes the toolkit C# public API inventory with the plugin-contract closure marked (its
  stated step), generated by reading the final public types after B056, so it is complete for 0.2.0. Tests: delete the
  per-file test table and replace it with a short paragraph naming the test layers (C# fakes, `NodeScript`
  executions, `eng/check-*.mjs` on the emitted asset) and how to filter them. Before deleting a row, move any rule
  its "Locks" column states that its surface or component section does not already say into that section, so no
  contract text is lost. A per-file list duplicates the tree and will drift again, the same reason TOOLKITJS-012
  removes the hand list of checks.
- **Tests:** none (documentation).
- **Plan v2:** B177.
- **Related:** U02B-SUTC-021, TOOLKITCS-V-001, TOOLKITJS-028.

### U02B-SUTC-031: The library uses WSGM's "Native-QAM" vocabulary

- **Severity:** nit
- **Covered by:** TOOLKITCS-038: `lib/Surfaces/SteamUiBridgePatch.cs:93,108` texts and the `native-qam:` correlation
  id become `steam-ui` wording (update the `SteamSurfaceModuleTests` expectation).
- **Plan v2:** B054.
- **Related:** U02A-SUTC-045, U02B-SUTC-003.

### U02B-SUTC-032: SteamUiText is a one-line public API used only by WSGM

- **Severity:** nit
- **Covered by:** TOOLKITCS-057 and STEAMHOST-044: delete `SteamUiText` and normalize at the WSGM call sites in
  `src/WSGM/Shell/NativeQamSemanticServices.cs`.
- **Plan v2:** B056.
- **Related:** none.

### U02B-SUTC-033: SteamSharedContext is in a different namespace from its siblings

- **Severity:** nit
- **Covered by:** TOOLKITCS-056: one `SteamUiToolkit` namespace, including the parent usings in `ShellSession.cs` and
  `SteamUiSessionHost.cs` (toolkitcs.verify batch problem 2).
- **Plan v2:** B056.
- **Related:** none.

### U02B-SUTC-034: The SteamSurfaceJsonContext remark is inaccurate

- **Severity:** nit
- **Covered by:** TOOLKITCS-060 (`lib/Surfaces/SteamSurfaceJsonContext.cs:10-11`).
- **Plan v2:** B056.
- **Related:** none.

### U02B-SUTC-035: Gate expressions use misleading names and a hard-coded version

- **Severity:** nit
- **Where:** `lib/Surfaces/SteamGatePatch.cs:72-80` (bodies call `bridge.install()`, `bridge.status()`,
  `bridge.remove()`), `:217-228` (`GateExpression` binds the gate to a local named `bridge`);
  `lib/Surfaces/SteamQuickAccessRowPatch.cs:83-93` (same local in its bodies); `lib/Surfaces/SteamUiBridgePatch.cs:27-29`
  (`b.version===1`).
- **Problem:** confirmed. The local named `bridge` holds a gate, not the bridge, so every gate body reads as if it
  installed the bridge. The version half is TOOLKITCS-039; the naming half is in no domain finding.
- **Best solution:** in `GateExpression` name the local `gate` (`const gate=b&&b.gate?b.gate(name):null;if(!gate)...`)
  and update the body strings in `SteamGatePatch`'s constructor and `SteamQuickAccessRowPatch`, the only two callers
  (both internal), plus the two doc comments that name the local (`SteamGatePatch` class remarks "`bridge.install()`
  apply" and `GateExpression`'s `<param name="body">`). No `verifyOk`/`removeOk` string passed by a toolkit surface
  or WSGM names `bridge` (grep `src` and `external/steam-ui-toolkit/src` again before the rename). Build the bridge
  verify expression from `SteamUiBridgeHost.SchemaVersion` (TOOLKITCS-039). These are C# expressions, so the
  injected asset and its hash do not change.
- **Tests:** update the substring expectations at `SteamQuickAccessRowPatchTests.cs:171,176` (`bridge.install(`,
  `bridge.remove(`); filter
  `--filter "FullyQualifiedName~SteamQuickAccessRowPatchTests|FullyQualifiedName~SteamGatePatch|FullyQualifiedName~SteamUiBridge"`.
- **Plan v2:** B054 (edits all three files).
- **Related:** TOOLKITCS-039.

### U02B-SUTC-037: RecordingBackend conflates calls and always succeeds

- **Severity:** nit
- **Covered by:** TOOLKITCS-070: distinct labels, a configurable result, `CollapseAsync` deleted, the folds test reuses
  the shared fake.
- **Plan v2:** B062.
- **Related:** U03B-SUTS-045.

### U02B-SUTC-038: The "full set registers together" test omits about ten surfaces

- **Severity:** nit
- **Covered by:** TOOLKITCS-069: derive each surface's `Commands` from its handlers and build the set from every
  surface.
- **Plan v2:** B062.
- **Related:** U03A-SUTS-016.

### U02B-SUTC-039: SteamGatePatchContractTests.Gate has unused cases

- **Severity:** nit
- **Where:** `toolkit/tests/SteamUiToolkit.Tests/SteamGatePatchContractTests.cs:41-54` (`"library-details"` and
  `"storage"` arms); no caller passes either (grep over the tests).
- **Problem:** confirmed. Two dead switch arms in an `internal static` helper suggest coverage of the details and
  storage gates that does not exist.
- **Best solution:** B062 rewrites this file to execute probes and predicates in Node (U02B-SUTC-006). In that
  rewrite, add both gates to the predicate cases, since both verify on a claim today
  (`SteamLibraryBadgeSurface.DetailsPatch` and `SteamStorageSurface.Patch` pass
  `"status.installed&&status.resolved&&status.claimed"` and `"!status.claimed"`); add the storage gate to the
  already-claimed probe case too, because its probe reports `claimed` from `__steamUiStorageClaimed` (re-read after
  B049 rewrites that probe). The details probe has no claim marker, so it stays out of that case. Make the helper
  `private`, since nothing outside the file uses it.
- **Tests:** `--filter "FullyQualifiedName~SteamGatePatchContractTests"`.
- **Plan v2:** B062.
- **Related:** U02B-SUTC-006, TOOLKITCS-064.

### U02B-SUTC-040: The running-apps lease ignores the result of its cleanup evaluation

- **Severity:** nit
- **Covered by:** TOOLKITCS-019: log a refused cleanup (`ok:false` or not sent) once.
- **Plan v2:** B053.
- **Related:** U02B-SUTC-002.

### U02B-SUTC-041: NodeScript loses diagnostics on timeout

- **Severity:** nit
- **Covered by:** TOOLKITCS-071: include stdout and stderr in the timeout failure and resolve Node consistently.
- **Plan v2:** B062.
- **Related:** U02B-SUTC-027.

### U02B-SUTC-042: The EvaluateAsync doc says it never throws on cancellation, but it does

- **Severity:** nit
- **Covered by:** TOOLKITCS-014: the session path is deleted, leaving one `SteamClientScript.EvaluateAsync` path whose
  cancellation behaviour B053 documents (cancel before send is `NotSent`, after send `Unknown`).
- **Plan v2:** B053.
- **Related:** U02B-SUTC-007.

### U02B-SUTC-043: Prose uses em dashes, unlike the maintainer's stated style

- **Severity:** nit
- **Where:** `toolkit/docs/reference.md` (26 lines), `toolkit/.github/workflows/ci.yml:45`,
  `toolkit/eng/build-prelude.mjs` (2) and six `eng/check-*.mjs` scripts (library, navigation-panel,
  ownership-claims, pages, power-profile, storage), `lib/SteamUiAssets/Source/tsconfig.json` (1 comment), about 100
  comment lines across the TypeScript fragments under `lib/SteamUiAssets/Source/` (`bridge.ts`, `components.ts`,
  `epilogue.ts`, `gate-helpers.ts` and most gates), `lib/Surfaces/SteamUiProbeJs.cs:11,73-74`, comments in 30 C#
  files under `lib/` and 14 lines under `toolkit/tests/`, and one log separator at `lib/SteamUiPatchManager.cs:1100`
  (an em dash before `entry.Snapshot.LastFailure`). Outside that separator every occurrence is in a comment; none is
  in a TypeScript string or any text Steam renders.
- **Problem:** confirmed. The maintainer's prose rule (no em dashes) is not followed in the toolkit's docs and
  comments. The TypeScript comments survive type stripping into WSGM's composed asset
  (`src/WSGM/Core/SteamUiAssets/NativeQamBootstrap.js` carries 99 today).
- **Best solution:** one mechanical pass in the B177 child commit, after every code batch, so it does not collide with
  their edits: replace each em dash in Markdown, YAML, `.mjs`, `.ts`, `tsconfig.json` and C# comments with a comma,
  colon or parentheses as the sentence needs, and change the log separator at `SteamUiPatchManager.cs:1100` to
  `": "`. Do not touch string literals that reach Steam's UI (none exist today; check again at the pass). Leave
  `node_modules` and generated `dist/` alone. Because the `.ts` comment edits change the composed asset, the
  parent gitlink commit regenerates `NativeQamBootstrap.js` with `npm run steam-assets:build` (B139's STEAMHOST-017
  computes the hash at load, so no hash constant remains to update by then); say in the commit that the asset
  changed only in comments. The toolkit `AGENTS.md` has none.
- **Tests:** `dotnet build external\steam-ui-toolkit\SteamUiToolkit.slnx -c Release`, `npm run prelude:claims`, a
  grep for U+2014 over tracked toolkit files returning nothing, then in the parent `npm run steam-assets:build`,
  `npm run steam-assets:check` and
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamUiAssetTests"`.
- **Plan v2:** B177 (its file list gains the regenerated parent asset).
- **Related:** none.

## Missing bodies

None. All 109 U02A-SUTC and U02B-SUTC bodies are present in `claude-findings-raw.json` (the disposition's missing
bodies are U04B-LFA-013 to 049, outside this area).

## Refuted or no-change

- **U02A-SUTC-004** (medium, exploitability a hypothesis): dropped by maintainer decision (security theater,
  DECISIONS.md). The finding is that any SharedJSContext script can call the bridge or forge an envelope whose high
  `sequence` makes later requests fail as replays until the next bootstrap. Only a hostile script in Steam can do
  that, and such a script already drives Steam through the unauthenticated debug port; it has no functional part.
  No nonce, host id, private `send` capture or confirmation step, and B177 drops its "bridge trust boundary"
  paragraph for this id (toolkit `docs/reference.md` section 8 and `docs/steam-cef-system.md` stay as they are).
- **U02A-SUTC-016** (low, hypothesis): no change. The bootstrap acknowledgement is the direct return value of the
  evaluation sent to that context, and `IsPositiveAcknowledgement` already checks it against the expected execution
  context and document generations while `BootstrapAsync` checks the generation epoch (`lib/SteamUiBridge.cs:371-410`,
  `:899-927`). A "concurrent host" does not exist (WSGM is single-instance); the nonce was dropped (R6, critic
  conflict 6, and dropped by maintainer decision as security theater, DECISIONS.md).
- **U02A-SUTC-024** (low): no change. `SteamUiBridgeIdentity` already documents the one-host contract and how names
  become host-supplied if a second host ever exists (`lib/SteamUiBridgeIdentity.cs:18-22`); replacing an older
  build's bridge is the intended update path (R6). Same-user identity checks are dropped by maintainer decision
  (security theater, DECISIONS.md; see U02A-SUTC-004).
- **U02A-SUTC-026** (low): wrong premise. `eng/check-ownership-claims.mjs` is not a static scanner of claim call
  forms; it executes the claim primitives from the emitted asset. The shared claims (`memoClaim`, `elementClaim`)
  are exercised behaviourally by `eng/check-screensaver.mjs:275-290` and `eng/check-library.mjs:393-402`. The one real
  shared-claim edge case is TOOLKITJS-020 (B050).
- **U02A-SUTC-039** (nit): stale (plan claim C40). The malformed and orphan counters throttle their warnings to three
  per connection (`lib/SteamUiCdpConnection.cs:418-424`, `:459-462`), so they are used; exposing them in snapshots
  adds API with no consumer.
- **U02A-SUTC-048** (nit, preference): no change. `userGesture: true` is documented (`toolkit/docs/reference.md:266`),
  no defect is attributed to it, and making it opt-in would thread a flag through every evaluation and risk Steam
  calls that need user activation.
- **U02A-SUTC-052** (nit): dropped by maintainer decision (security theater, DECISIONS.md). Adding
  `[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]` to the `iphlpapi` import in `lib/NativeTcp.cs` only
  defends against DLL planting by the same user; `iphlpapi.dll` is a known system DLL and loads today. B048 drops
  TOOLKITCS-010.
- **U02A-SUTC-057** (nit): refuted with TOOLKITCS-017. The connection logs the drop with the method name
  (`DropMalformed($"{method} parameters exceeded the byte limit")`, `lib/SteamUiCdpConnection.cs:500-503`); the page's
  request settles on its own timeout, and a binding payload over 1 MiB is not a legitimate request.
- **U02A-SUTC-059** (nit, preference): no change, per the disposition ("no broad typing rewrite") and TOOLKITJS claim
  C27: the loose `types.ts` index signature and `noImplicitAny: false` are deliberate at the minified boundary.
- **U02A-SUTC-061** (nit): dropped by maintainer decision (security theater, DECISIONS.md). The reparse-point half
  (refusing `FileAttributes.ReparsePoint` between the extension root and the target) only stops a same-user link out
  of the user's own extension folder, and the manifest size cap was already not adopted (TOOLKITCS-031,
  no-arbitrary-limits, not in D2's list). The existing lexical containment check stays as it is. B056 keeps the rest
  of TOOLKITCS-061 (U02A-SUTC-030, U02A-SUTC-031) and drops its reparse step.
- **U02B-SUTC-036** (nit, preference): no change, per TOOLKITJS-027's disposition in plan v2: a toolkit-wide Prettier
  pass contradicts the toolkit AGENTS style rule. The check-style half (`node:assert` instead of a private `check()`)
  is TOOLKITJS-036 in B051 and concerns `check-ownership-claims.mjs`, not this id.
