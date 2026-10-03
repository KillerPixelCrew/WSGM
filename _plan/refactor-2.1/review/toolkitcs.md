# SteamUiToolkit C# review (toolkitcs)

Scope: `external/steam-ui-toolkit/src/SteamUiToolkit/**` (C#, csproj, embedded resolver wiring; not the
SteamUiAssets TS sources) and `external/steam-ui-toolkit/tests/SteamUiToolkit.Tests/**`. Every C# source file
(79 files, ~12.7 k lines) and every test file (43 files, ~7.7 k lines) was read in full at toolkit `main`
`388dd1b`. Consumers were traced with grep across the parent repo (`src/**`, `tests/**`). Read-only review: no
build, no tests, no live Steam.

Ledger references use the Claude raw IDs (`U02A-SUTC-nnn`, `U02B-SUTC-nnn`, `U03A-SUTS-nnn`, `U03B-SUTS-nnn`)
abbreviated as `U02A-nnn` etc., and Codex audit IDs (`A01-Fnnn`).

---

## 1. Plan claims check

| # | Claim (refactor-plan.md / corrections / batches) | Verdict | Evidence | Correction |
| --- | --- | --- | --- | --- |
| C1 | "toolkit client calls consult ambient `SteamUiTransportSession`" (Source diagnosis) | accurate | `Client/SteamApps.cs:149,176,301,397,592,823,843`; `SteamInstallFolders.cs:137,161,186`; `SteamLibraryData.cs:98,128,252`; `SteamDownloadActivity.cs:176`; `SteamCurrentPage.cs:59-70`; `SteamClientScript.cs:124-131`; `SteamRunningApps.cs:131` | Also: `PersistentSteamUiTransport.cs:76,370` and `SteamUiPatchManager.cs:1107` read the static session's reason, so the transport and manager depend on it too. |
| C2 | "`SteamUiSessionHost` ... duplicates module quarantine/retraction policy" | accurate | WSGM `SteamUiSessionHost.cs:68,1466-1491,1532`; toolkit `SteamUiModuleRuntime.cs:42,440-471` | — |
| C3 | "`Plugin.Sdk` really references Device.Sdk and SteamUiToolkit" | accurate | `src/WSGM.Plugin.Sdk/PluginSteamUi.cs:3,51` (`ISteamUiModule`), csproj ref | Only `ISteamUiModule`/`SteamPage` cross the boundary; no plugin package implements modules today. |
| C4 | "Remove ambient mutable `SteamUiTransportSession` client routing; construct `SteamClient` from one transport, a client write lane and `ISteamUiLog`" | partially | session: `SteamUiTransportSession.cs:44-226`; write lane: `SteamApps.cs:176` shared by `SteamCollections.cs:76`, absent from `SteamInstallFolders.cs:130-194` | Accurate for transport + write lane. Drop the `ISteamUiLog` parameter: the log sink is write-only diagnostics, one per process (`SteamUiLog.cs:46-56`); injecting it into every owner is churn with no defect. Make `_sink` volatile instead (TOOLKITCS-043). |
| C5 | "Consumers retain a transport subscription for their lifetime. One-shot borrowers do not imply an immortal idle socket." | accurate (behaviour) / incomplete | one-shot lease `PersistentSteamUiTransport.cs:348-361` → `SubscribeAsync` starts a reconnect loop `152-157,408-418` for every one-shot call; test pins 2 wires for 2 calls `PersistentSteamUiTransportTests.cs:84-102` | Keep per-call connect, but a request lease must not start a reconnect loop (TOOLKITCS-005). |
| C6 | "Pin endpoint connections to `127.0.0.1`; owner validation accepts only loopback/wildcard rows" | accurate (defect present) | `SteamCef.cs:85` accepts `localhost`; `SteamCef.cs:121-156` treats any specific non-loopback row like a wildcard; IPv4-only table `NativeTcp.cs:20-24,104-105` | — |
| C7 | "Preserve framing/channel limits" | accurate | `SteamUiCdpConnection.cs:79,151-157`; transport channels `PersistentSteamUiTransport.cs:34-70` | Keep the CDP read bounds (8 MiB response, 1 MiB notification) and request backpressure (32). The notification lane `_notificationEvents` is dead (TOOLKITCS-007) and should be removed, not preserved. |
| C8 | "Send under connection lifetime; caller cancellation controls waiting" | accurate (defect present) | `SteamUiCdpConnection.cs:282-317` sends under `deadline.Token` (linked to the caller) and `_shutdown.Cancel()` on any send failure | — (U02A-011). |
| C9 | "Track retired connections and await them on disposal" | accurate | `PersistentSteamUiTransport.cs:403,414,834`; `SteamUiPatchManager.cs:459,967-974`; `SteamUiModuleRuntime.cs:174` | Bound by the caller's shutdown token, not a fixed ceiling (TOOLKITCS-012). |
| C10 | "Distinguish NoTarget/NeverSent, TransportFailure, PageError, Refused, Applied and DispatchedUnknown" | partially / over-specified | reachable+error dropped `SteamUiTransportSession.cs:208-211`, `SteamClientScript.cs:137-139`; post-send timeout reported unreachable `PersistentSteamUiTransport.cs:211-226` | Six outcomes collapse to four at the transport (`NotSent`, `Closed`, `Unanswered`, `Answered`) and four at the client (`NotSent`, `Unknown`, `Rejected`, `Applied`). "TransportFailure" is either NotSent or Unanswered depending on the phase; "PageError" is an Answered result with `Error`. See refinement R2. |
| C11 | "reads distinguish None from Unavailable/Error" | accurate | lossy reads `SteamLibraryData.cs:106-121,177-182,250-255`; `SteamApps.cs:526-547,584-624,840-860`; `SteamCurrentPage.cs:82-87` | — (U02B-013). |
| C12 | "Normalize app IDs to uint32 at the page boundary" | accurate | `SteamLibraryData.cs:23,32,63-71` (long, `appid>=2147483648` never matches signed ids); `SteamCurrentPage.cs:14,62`; `SteamDownloadActivity.cs:131` (int) | — (U02B-014). |
| C13 | "Logging records bounded codes/correlation without raw paths/credentials/payloads, outside locks" | partially / conflicts | payload logged verbatim `SteamUiModuleRuntime.cs:231-236`, 200 chars `SteamUiBridge.cs:749-754`; under lock `SteamUiPatchManager.cs:1081-1107` | "No raw paths/payloads" conflicts with the maintainer rule that testers are diagnosed from wsgm.log only; the payload line is the diagnostic (comment at `SteamUiModuleRuntime.cs:227-230`). Keep payload *shape*, redact string values (secret settings reach this log today, TOOLKITCS-020). Logging outside locks: accurate. |
| C14 | "Patches declare `DependsOn`; validate missing/cyclic dependencies at registration, apply topologically and remove reverse-topologically on every disable/dispose path" | partially / over-engineered | the only dependency is gate→bridge: `SteamUiPatchManager.cs:514-525`; defect is only the removal order on `SynchronizeAsync` (`502,555-558`) vs `DisposeAsync` (`288`) | Replace with "bridge first on apply, bridge last on every removal path" (R3). No DAG, no cycle validation. |
| C15 | "The bridge has only webpack/React/asset preconditions; QAM/TDP matching belongs to its surface" | accurate (defect present) | `SteamUiBridgePatch.cs:24-46,79-82` requires four QAM/TDP fingerprints | — (U02B-003). |
| C16 | "Any apply that began and then failed/timed out is potentially mutated: run a bounded idempotent removal, retain unresolved ownership, and do not blindly reapply" | partially | no removal on failed/thrown/timed-out apply `SteamUiPatchManager.cs:702-714,759-770` | Removal: accurate. "Do not blindly reapply" must not block the next synchronization's idempotent re-apply of a UI patch: the no-retry rule is for device writes. Retrying-after-timeout should reuse the existing settle retry (`947-975`) rather than wait for an unrelated event. |
| C17 | "Generation change/timeout/kill-switch cancellation have distinct diagnostics" | accurate | `SteamUiPatchManager.cs:759-766` ("Patch operation timed out." for all three) | — (U02A-018). |
| C18 | "The manager coalesces generation/sync notifications; host policy supplies enablement" | accurate (direction) | manager handler only invalidates `977-1018`; WSGM runs its own loop `SteamUiSessionHost.cs:900-972` alongside the manager's own queue `SteamUiPatchManager.cs:445-491` | Make the manager's existing queue the only loop; WSGM subscribes to a `Synchronized` notification for its post-pass reconcile and deletes its loop (R5). |
| C19 | "Remove redundant per-resource locks under the serialized scheduler" | accurate | `SteamUiPatchManager.cs:228-229,337,543-544,786`; every path holds `_schedulerGate` | Also remove `ISteamUiPatch.ResourceKey`, which exists only for those gates (TOOLKITCS-026). |
| C20 | "Module sets index patch installer, command handler and publisher separately; reject duplicate publication owners and attribute failure to the correct callback" | accurate | `SteamUiModule.cs:202` `Add` throws `ArgumentException` when a command-only module precedes the installer; `205-210` no duplicate check; `SteamUiModuleRuntime.cs:440-458` attributes by first registrant | — (U02A-032/033). |
| C21 | "Runtime exposes quarantine/state/reason and owns corresponding manager retraction; WSGM stops mirroring `_failedPatchIds`. Reset is explicit after recreation or user enable" | partially / over-engineered | `ModuleFailed` event already exposed `SteamUiModuleRuntime.cs:54,457`; quarantine never cleared `42` | Ownership move: accurate. The Reset API adds a state transition nothing uses today (quarantine lasts for the runtime instance; WSGM recreates the host on recreation). Drop Reset (R4). |
| C22 | "Bridge identity includes caller host ID and per-bootstrap nonce; acknowledgement checks nonce ... Foreign owners are refused, not overwritten" | over-engineered | identity is constant `SteamUiBridgeIdentity.cs:24-31` and documented as one-host; ack already checks generations `SteamUiBridge.cs:924-926` and epoch `401` | No concrete second host exists (WSGM is single-instance). Keep asset hash + generations; replacing an older build's bridge is the desired update path. Drop nonce/host-id/refusal (R6). |
| C23 | "enforce one explicit inbound UTF-8 bound" | partially | inbound bound already exists at the connection `SteamUiCdpConnection.cs:157,500-503`; the oversized binding is silently reduced to `{}` and dropped by the bridge | Do not add a second bound. Make the existing drop visible (log once per drop) (TOOLKITCS-017). |
| C24 | "Read JSON root/kinds before fields" | accurate | `SteamUiBridge.cs:719-724` (`TryGetProperty` on a non-object root, `GetString` on a non-string); `SteamUiPatchEvaluation.cs:71,185,236` | — (U02A-023/051). |
| C25 | "Removal verifies structured acknowledgement" | accurate | `SteamUiBridge.cs:692-698` discards the result | — (U02A-017). |
| C26 | "never sweep exports by executing arbitrary functions" | accurate but misses the C# probe | the C# storage probe calls every function export of the transport module on every probe: `Surfaces/SteamStorageSurface.cs:178-184` | Name this C# probe explicitly; it runs on every synchronization (TOOLKITCS-001). |
| C27 | "Keep extension discovery ... Compose validated extensions into modules/declared patch cleanup through a concrete adapter; ... enforce host-reserved prefixes, bounded UTF-8 and non-reparse containment" | partially / over-engineered | discovery only `SteamUiExtensionHost.cs:55-100`; unused by WSGM (0 production references); manifest dropped on rejection `276-282`; 256 KiB cap `41,251-271` | Keep discovery; keep manifest on rejection; reserved prefix; reparse check. Drop the adapter (no consumer; fix the docs instead) and drop the byte cap (arbitrary limit) (R7). |
| C28 | "Original startup choice is returned/persisted before any setter; partial outcomes retain it" | accurate (defect present) | `Client/SteamStartupMovie.cs:294-296,336-338` | — (U02B-011). |
| C29 | "A shortcut gained during a no-ID add must match the requested target/name before adoption" | accurate (defect present) | `Client/SteamApps.cs:358-374` adopts the single gained entry when `returned==0` | — (U02B-012). |
| C30 | "running-app observer install/version/reference counting" | partially / over-engineered | install before registration `SteamRunningApps.cs:59-68`; one reader in WSGM (`RunningApplicationTarget`) | Install-after-register and a version stamp: accurate. Reference counting adds in-page mechanism for a second reader that does not exist; document single-reader instead (R9). |
| C31 | "Route length remains unbounded ..., but both C# and JS reject controls and root-only routes; fix contradictory docs" | partially | C# already rejects controls and `/`: `Surfaces/SteamRouteNavigation.cs:29-34`; contradicting doc `SteamNavigationPanelSurface.cs:29-30` ("longer than 256 characters is not drawn") | C# side needs only the doc fix. |
| C32 | "Remove magic `custom` ... from generic code" | accurate (C# part) | `Surfaces/SteamPowerPresetRow.cs:90` | — (U03A-005). |
| C33 | "impose limits only on actual byte/IO/transport safety boundaries, not arbitrary valid plugin content" | accurate, incomplete inventory | arbitrary caps in C#: expression bound `SteamUiPatchManager.cs:155-159` (WSGM glyph patch sets 2 MiB, `src/WSGM/Core/SteamInputGlyphStylePatch.cs:52-55`); 32 M delivery cap `SteamUiBridge.cs:225`; extension 256 KiB; side-menu 8192/33 `SteamSideMenuSnapshot.cs:68,100,109`; overlay windows >32 `SteamGameWindowActivation.cs:50`, `SteamNativeSurfaceCommands.cs:93`; documented caps in XML (TOOLKITCS-031) | Add these to the plan's removal list (R12). |
| C34 | B2 picker: injected filesystem backend, 200-entry pages, temp spool, 5 s waits from an injected clock, ≤4 workers, ≤1 per root, `ProviderBusy`/`ProviderCapacity`, `FilePickerPathPolicy` | over-engineered; one inaccurate premise | current code `Surfaces/SteamFilePickerSurface.cs:63-243`; Downloads is `profile\Downloads` (`93`), so a redirected Downloads is *not* supported today | Keep off-thread listing, wait cancellation/timeout, typed outcomes, extended-path normalization, device-namespace refusal, renderer paging. Drop spool, worker caps, Busy/Capacity outcomes, clock injection and the policy type (R8). Use the known-folder API for Downloads to make the "preserve redirected folders" premise true. |
| C35 | "Advance toolkit package to 0.2.0 for breaking APIs" | accurate | `SteamUiToolkit.csproj:27` (`0.1.0`) | — |
| C36 | "Replace slice/regex-derived test entry points with ... C#-emitted probe fixtures. Test actual shipped expressions" | accurate | substring-only probe tests e.g. `SteamHomeCarouselTests.cs:21-49`; NodeScript already exists `Fakes/NodeScript.cs` | — (U02B-005, U03B-006). |
| C37 | T01_01: "Both adapters turn any reachable transport result into Ok(Value), erasing Error"; "ParseWrite currently tests Value before Error" | accurate (code) / stale (sequencing) | `SteamUiTransportSession.cs:208-211`; `SteamClientScript.cs:137-139,157-184` | T01_01 edits `SteamUiTransportSession.EvaluateAsync`, which the plan deletes; maintainer rule: do not patch code a later change deletes. Fold T01_01 into TOOLKITCS-B3 (R1). |
| C38 | A01-F004 "Both convert Reachable+Error to Ok(Value)" | accurate | as C37 | — |
| C39 | Ledger U02A-042 "EnsureRemoteDebuggingEnabled returns 'created now'" | inaccurate | `SteamCef.cs:39-53` returns `true` when the flag already exists and `enabled`; `false` whenever `enabled` is false even if the file exists (`SteamCefTests.cs:22`) | The doc line "returns whether the flag is present afterwards" is wrong only for `enabled=false`; fix the doc to "true when enabled and the flag is present". |
| C40 | Ledger U02A-039 "Malformed/orphan frame counters are never reported" | stale | counters throttle logging to three lines per connection `SteamUiCdpConnection.cs:420,459` | Counters are used; no change needed beyond optional snapshot exposure (not recommended). |
| C41 | Ledger U02A-034 "quarantine ... never cleared or exposed" | partially | `ModuleFailed` event is exposed `SteamUiModuleRuntime.cs:54` | Exposure exists; the defect is retraction ownership (C21). |

---

## 2. Findings

Severity: critical / high / medium / low / nit. "Ledger" cites an existing Claude/Codex ID or says NEW.

### Transport, connection, discovery

**TOOLKITCS-001 — high — `Surfaces/SteamStorageSurface.cs:174-184`.** The storage probe calls every function
export of the transport module with no arguments until one returns an object with `GetDefaultTransport`. It runs on
every synchronization pass. The toolkit's own rule (`SteamUiProbeJs.cs:9-11`: constructing exports "has restarted a
machine and signed Steam out") and the parent safety rule forbid exactly this. No test catches it
(`SteamStorageTests.cs:445-454` only substring-checks). Ledger: U03A-003 (raw), plan C26 omits the C# file.
Recommendation: resolve the provider by source shape without invoking (`req.exported([...], v => ...)` on a non-call
predicate, or the existing transport handle path), and add a Node test that fails if any export is invoked.

**TOOLKITCS-002 — medium — `SteamUiCdpConnection.cs:282-317`.** The request is sent under `deadline.Token`, linked
to the caller's token; a caller cancel or a per-call timeout during `SendAsync` can leave a partial frame, and any send
exception cancels `_shutdown`, killing the shared connection for all users. Ledger: U02A-011, C8.
Recommendation: send under `_shutdown.Token` only; apply caller cancellation/timeout to `completion.Task.WaitAsync`.

**TOOLKITCS-003 — medium — `PersistentSteamUiTransport.cs:211-237`.** Failure classification is not dispatch-aware:
a caller cancel or a timeout after the expression was sent returns `Reachable=false`, which client writes document as
"changed nothing, may offer again" (`SteamClientScript.cs:10-13`). Local framing errors (`InvalidDataException` from
discovery, the 8 MiB read bound, the notification queue overflow) are reported `Reachable=true` as an answered
incompatibility. Ledger: U02B-001, U02A-008, C10. Recommendation: add `SteamUiDispatch { NotSent, Closed,
Unanswered, Answered }` to `SteamUiEvaluationResult`, set from whether `SendAsync` completed; map discovery/connect
errors to NotSent, post-send failures to Unanswered.

**TOOLKITCS-004 — medium — `PersistentSteamUiTransport.cs:227-237`.** A JavaScript exception from one expression
sets the shared channel's health to `Incompatible` and does not reset `ConsecutiveTimeouts`. Health gates other
callers: `SteamSharedContext.IsReadyAt` (`Surfaces/SteamSharedContext.cs:245-250`) requires `Ready`, so a Quick
Access/Home replay (`SteamNativeSurfaceCommands.cs:38`), a game raise (`SteamGameWindowActivation.cs:26-30`) and the
side-menu snapshot return false/unknown until another evaluation succeeds. Ledger: NEW (U02A-040 covers the timeout
counter only). Recommendation: an answered error proves the renderer is alive: keep `Ready`, reset the timeout run,
return the error in the result.

**TOOLKITCS-005 — low — `PersistentSteamUiTransport.cs:348-361,140-161,408-418`.** Every one-shot request lease
goes through `SubscribeAsync`, which starts a reconnect loop when none is running; the loop and the request then race
on `ConnectGate`, and the lease release cancels the loop. One wasted task and discovery race per one-shot. Ledger:
NEW (related U02A-010). Recommendation: a request lease increments the subscriber count without starting the loop.

**TOOLKITCS-006 — low — `PersistentSteamUiTransport.cs:420-486,15-21`.** An absent target (Steam starting, no main
window yet with `requireMainWindow`) escalates the same 1/4/16/30 s backoff as a failure, so attachment after Big
Picture appears can lag up to 30 s. Ledger: U02A-013. Recommendation: absent target retries on the first delay; only
connection failures escalate.

**TOOLKITCS-007 — low (simplification) — `PersistentSteamUiTransport.cs:57-70,115-118,727-729,886-889`.** The
`_notificationEvents` lane and its pump deliver only generation-method notifications through `NotificationReceived`;
the sole consumer (`SteamUiBridge.cs:706-715`, grep confirms no WSGM consumer) ignores everything but
`Runtime.bindingCalled`, which uses `_bindingEvents`. Dead channel, dead pump. Ledger: NEW. Recommendation: delete the
lane; rename the event `BindingCalled`.

**TOOLKITCS-008 — low — `PersistentSteamUiTransport.cs:48-55,891-894`.** Generation snapshots share one
`DropOldest(64)` channel across roles. A burst of MainWindow navigations can evict a SharedJSContext snapshot; the
bridge invalidates readiness only from this event (`SteamUiBridge.cs:831-856`) and would keep delivering under stale
generations until the next event. Ledger: NEW. Recommendation: latest-wins slot per role (two slots) instead of a
cross-role lossy queue.

**TOOLKITCS-009 — low — `SteamCef.cs:81-87,108-163`; `NativeTcp.cs:20-24,95-138`.** `localhost` is accepted (may
resolve to `::1`, not covered by the IPv4 owner check) and a specific non-loopback listener row decides the owner
verdict as if it served 127.0.0.1. Ledger: U02A-014/015, C6. Recommendation: accept only `127.0.0.1` socket URLs;
consider only loopback and `0.0.0.0` rows.

**TOOLKITCS-010 — nit — `NativeTcp.cs:50-53`.** `LibraryImport("iphlpapi.dll")` without
`DefaultDllImportSearchPaths(System32)`. Ledger: U02A-052.

**TOOLKITCS-011 — nit — `SteamUiToolkit.csproj:11-13,25-27`.** "read through a stack buffer" is stale
(`NativeTcp.cs:116` uses `AllocHGlobal`); the pre-1.0 comment cites module ids. Ledger: U02A-053.

**TOOLKITCS-012 — medium — `PersistentSteamUiTransport.cs:292-341`, `SteamUiPatchManager.cs:254-309`,
`SteamUiModuleRuntime.cs:82-118`, `SteamUiBridge.cs:302-347`.** Teardown has no caller deadline: the manager waits up
to 30 s for the scheduler and then removes every patch sequentially with an 8 s budget each; the runtime awaits
in-flight handlers with no bound; transport reconnect loops and retired-connection disposals are untracked. WSGM
calls these with no token (`src/WSGM/Shell/ShellSession.Shutdown.cs:360,382`), so the plan's B3 phase cutoffs
cannot be honored. Ledger: U02A-012 (partly), NEW for the deadline. Recommendation: `DisposeAsync` becomes
`ShutdownAsync(CancellationToken)` on manager, runtime, bridge and transport (DisposeAsync forwards with
`CancellationToken.None`); on cancellation report what was not removed and stop waiting; track and await retirement
tasks within the same token.

**TOOLKITCS-013 — low — `SteamUiTransportModels.cs:146-151`, `PersistentSteamUiTransport.cs:250-283`.**
`SetRuntimeBindingAsync` throws (including `InvalidOperationException` when disabled) while `EvaluateAsync` returns a
result; no disposed check. Ledger: U02A-049. Recommendation: keep the throwing contract (single internal caller
catches it) and document it; add the disposed check.

### Ambient session and client

**TOOLKITCS-014 — medium (architecture) — `SteamUiTransportSession.cs:44-226`; all `Client/*.cs` statics.**
Process-global attach/enable/evaluate routing, a process-wide write gate (`SteamApps.cs:176`), and two inconsistent
evaluation paths (`SteamClientScript.cs:117-153`: session path never throws on caller cancel, explicit path
rethrows). Ledger: U02B-004, U02B-042, C1/C4. Recommendation: delete the session; one `SteamClient` instance per
transport (Target design §4).

**TOOLKITCS-015 — high — `Client/SteamApps.cs:358-374`.** When `AddShortcut` returns no id (`returned==0`) and
exactly one shortcut appears within 2 s (another tool, the user, Steam's own import), the call adopts it and
overwrites its name, Target, start directory and arguments. Silent corruption of someone else's library entry.
Ledger: U02B-012, C29. Recommendation: adopt without a returned id only when the gained entry's exe and name match
the request; otherwise return `Confirmed=false` and write nothing.

**TOOLKITCS-016 — medium — `Client/SteamCollections.cs:109-126`; `Client/SteamStartupMovie.cs:294-296,336-338`;
`Client/SteamInstallFolders.cs:210-215,263-283`.** Partial outcomes lose ownership evidence: a created collection's
id is not returned when a later step throws (next sync creates a duplicate); the startup-movie set-aside clears
settings before the reply carries the original choice; library add swallows purge and label failures and reports
`Added`. Ledger: U02B-010, U02B-011, U02B-008. Recommendation: every reply after the first mutation carries the
ownership token (`id`, `choice`, purge/label results); C# maps to a partial status with detail.

**TOOLKITCS-017 — low — `SteamUiCdpConnection.cs:494-508`; `SteamUiBridge.cs:719-727`.** An oversized binding
payload is forwarded as `{}` and the bridge drops it with no line naming the refused request. Ledger: U02A-057, C23.
Recommendation: log the drop with the method name at the connection; no new inbound bound.

**TOOLKITCS-018 — medium — `Client/SteamInstallFolders.cs:130-194`.** Read-modify-write scripts are not serialized;
concurrent adds at one path can both register, recreating the duplicate-library defect the class exists to prevent.
Ledger: U02B-009. Recommendation: the client's one write lane covers install folders, apps and collections.

**TOOLKITCS-019 — medium — `Client/SteamRunningApps.cs:58-75,198-228`.** The observer object is published on
`window` before `RegisterForAppLifetimeNotifications` runs; if that throws, later reads return a frozen set as
healthy. The generation restarts at 1 on reinstall; cleanup ignores an `ok:false` reply; the observer has no version
stamp. Ledger: U02B-002, U02B-015, U02B-040. Recommendation: assign after successful registration, seed `gen` from
`Date.now()`, stamp a version and reinstall on mismatch, log a refused cleanup. No in-page reference counting (R9).

**TOOLKITCS-020 — medium — `SteamUiModuleRuntime.cs:231-236`; `SteamUiBridge.cs:749-754`.** Refusal and rejection
lines write the request payload. Plugin `secret` settings travel as command payloads (`configure`/`set`;
`src/WSGM/Shell/WsgmSteamSettingsService.cs:414`, `CommonPluginSteamUiSource.cs:387`), and a stale-revision refusal
is ordinary, so secrets reach wsgm.log, which testers paste. Ledger: NEW (U02A-062/046 cover size only).
Recommendation: log the payload with string values replaced by their length (`"value":"…(32)"`); numbers, booleans
and property names stay, which keeps the diagnosis the comment at `227-230` wants.

**TOOLKITCS-021 — low — `Client/SteamLibraryData.cs:96-121,177-182,250-255`; `Client/SteamApps.cs:526-547,
584-624,840-860`; `Client/SteamCurrentPage.cs:82-87`.** Lossy reads collapse unreachable, refused and absent into
`null`/empty/0, and WSGM still uses the lossy `ListGamesAsync` in `Overlay/CardManagerView.cs:181`,
`Overlay/LibraryTabsView.cs:651,676,700`, `Shell/SteamArtworkBrowserSource.cs:754`. Ledger: U02B-013, C11.
Recommendation: typed read results only; delete `ListGamesAsync`/`ListCollectionsAsync`/`ListStoreTagsAsync`/
`IsLoadedAsync` lossy forms with their consumers.

**TOOLKITCS-022 — low — `Client/SteamLibraryData.cs:23,32,68`; `Client/SteamCurrentPage.cs:14,62`;
`Client/SteamDownloadActivity.cs:131`.** App ids are `long`/`int`/`uint` per type; `GamesExpression` returns raw
signed ids and its fallback `a.appid>=2147483648` never matches them; `SteamCurrentPage` drops negative ids. Ledger:
U02B-014, C12. Recommendation: `uint` everywhere, `>>>0` in page scripts.

**TOOLKITCS-023 — nit — `Client/SteamClientScript.cs:27-28` vs `Client/SteamInstallFolders.cs:99-100`.** Two
`ErrorReply` shapes (`err` vs `result`/`message`). Ledger: NEW. Recommendation: one reply shape with an optional
`result` code.

**TOOLKITCS-024 — nit — `Client/SteamApps.cs:66-67,496-591`.** Public members document only a summary
(`SteamLogoPosition`, icon/account/logo APIs). Ledger: U02B-021.

### Patch manager, evaluation, bridge

**TOOLKITCS-025 — medium — `SteamUiPatchManager.cs:495-525,555-558`.** Synchronization applies and removes in the
same bridge-first order, so a global disable removes the bridge first and every gate's removal then reports
`RemoveFailed`; WSGM compensates with a two-pass disable (`src/WSGM/Shell/SteamUiSessionHost.cs:857-862`).
`DisposeAsync` uses the reverse order (`288`). Ledger: U02A-002, U02A-020, C14. Recommendation: in every pass,
process removals in reverse (bridge last) before applies (bridge first).

**TOOLKITCS-026 — low (simplification) — `SteamUiPatchManager.cs:228-229,337,543-544,786`; `ISteamUiPatch.ResourceKey`
`178-179`.** Per-resource semaphores are always taken under `_schedulerGate`; `ResourceKey` exists only to key them
(tests pin it: `SteamQuickAccessRowPatchTests.cs:327-350`). Ledger: U02A-054, C19. Recommendation: delete the gates
and the `ResourceKey` member.

**TOOLKITCS-027 — medium — `SteamUiPatchManager.cs:702-714,759-770`.** An apply that failed, threw or timed out is
left in place; after a timeout the state is `Retrying` with no scheduled retry. Ledger: U02A-003, C16.
Recommendation: on any non-success after apply started, run `RemoveAsync` once; schedule the existing settle retry
for `Retrying`.

**TOOLKITCS-028 — low — `SteamUiPatchManager.cs:759-766`.** Kill switch, generation change and phase timeout share
"Patch operation timed out." Ledger: U02A-018. Recommendation: check which source cancelled.

**TOOLKITCS-029 — low — `SteamUiPatchManager.cs:1081-1108`.** Logging runs under `entry.Sync`; warning level
consults the static `SteamUiTransportSession.IsClosedReason`. Ledger: U02A-055, NEW for the static coupling.
Recommendation: compute the line inside the lock, write it after; use `SteamUiDispatch.Closed` instead of the static.

**TOOLKITCS-030 — low — `SteamUiPatchManager.cs:374-387`.** `SetPatchEnabledAsync` runs a full pass over every
patch. Ledger: U02A-021. Recommendation: after R5 the manager owns one coalesced loop; the awaited form waits for the
next completed pass rather than running its own.

**TOOLKITCS-031 — low (no-arbitrary-limits) — multiple.** Caps that refuse or drop valid content:
`SteamUiPatchBounds.MaximumExpressionCharacters` (`SteamUiPatchManager.cs:56-92,155-159`; WSGM raises it to 2 MiB for
the glyph stylesheet, `src/WSGM/Core/SteamInputGlyphStylePatch.cs:52-55`); `SteamUiBridgeHost.MaximumDeliveryCharacters`
32 M (`SteamUiBridge.cs:219-225`, chunked delivery already exists); `SteamUiExtensionHost.MaximumScriptCharacters`
(`SteamUiExtensionHost.cs:36-41`); side-menu `8192`/`33`/`>32` (`Surfaces/SteamSideMenuSnapshot.cs:68,100,109`);
`windows.length>32` (`SteamGameWindowActivation.cs:50`, `SteamNativeSurfaceCommands.cs:93`); overlay activation
33rd identity (`SteamOverlayActivationPatch.cs:37`). Documented-only caps: "at most 8" targets
(`SteamControllerTargetRow.cs:23`), "16" zones (`SteamDeviceControlsRow.cs:62`), "64" options
(`SteamPowerProfileRow.cs:11,16`, `SteamHybridCoreRow.cs:15`, `SteamCpuBoostRow.cs:16`, `SteamResolutionRow.cs:12`),
"256 of up to 160" folds (`SteamPanelFoldsSurface.cs:19`), theme 96/4 MiB/32/64 (`SteamThemeStyleSurface.cs:9-22`),
screensaver "four" rows and 32-char ids (`SteamScreensaverSurface.cs:12,17-18,34`), navigation 256-char routes
(`SteamNavigationPanelSurface.cs:29-30`). Ledger: U03A-009, U03B-010, U03B-012, U02B-019, NEW for the expression,
delivery, side-menu and window caps. Recommendation: delete the enforced caps (keep the CDP read bounds), delete the
documented caps from XML and reference.md. Do not adopt U02A-061's manifest cap or U02B-019's string bounds.

**TOOLKITCS-032 — low (simplification) — `SteamUiPatchManager.cs:110-120,638`; every probe.** `Compatible` and
`Unique` are always set to the same value (`SteamGatePatch.cs:195-200`, `SteamUiPatchEvaluation.cs:150-155`,
`SteamOverlayActivationPatch.cs:80`, test fake). `ISteamUiPatch.Version` is `1` everywhere and logged as `v1`
(`SteamGatePatch.cs:99`, `SteamQuickAccessRowPatch.cs:104`, `SteamUiBridgePatch.cs:61`). Ledger: U03A-022, NEW for
`Unique`. Recommendation: remove `Unique` and `Version`; the fingerprint carries the revision.

**TOOLKITCS-033 — low — `SteamUiPatchEvaluation.cs:62-65,130-138`; `Surfaces/SteamGatePatch.cs:175-183`.** A probe
that the page answered with an error (`Reachable && Error`) is recorded as `AbsentTarget` and re-probed on the settle
schedule instead of `Incompatible` with the diagnostic. Ledger: U02A-022, U02B-016.

**TOOLKITCS-034 — low — `SteamUiPatchEvaluation.cs:67-95,183-198,229-256`; `SteamUiBridge.cs:719-727`.**
`TryGetProperty`/`GetString` on non-object roots or non-string values throw `InvalidOperationException`, which only
`JsonException` handlers do not catch. Ledger: U02A-023, U02A-051, C24.

**TOOLKITCS-035 — low (duplication) — `Surfaces/SteamGatePatch.cs:164-206` vs `SteamUiPatchEvaluation.cs:115-161`.**
Two probe-result parsers; the gate copy keeps the raw unbounded page value as diagnostic. Ledger: NEW.
Recommendation: gates call `EvaluateProbeAsync`; delete `SteamGatePatch.ProbeAsync`.

**TOOLKITCS-036 — high — `Surfaces/SteamUiBridgePatch.cs:24-46,79-82`.** The bridge, which every gate lives in
(`SteamGatePatch.cs:217-228`), is refused unless four Quick Access/TDP fingerprints match exactly once. A Steam
build that moves one QAM module takes down host pages, library badges, the Home carousel, the Extensions tab and
every other gate. Ledger: U02B-003, C15. Recommendation: bridge probe = webpack runtime resolvable + React unique;
move the QAM/TDP counts to the row patches, which already probe `performanceRoot` (`SteamQuickAccessRowPatch.cs:74`).

**TOOLKITCS-037 — low — `SteamUiBridge.cs:378-388,692-698`.** A bootstrap the page answered with an error returns
`false` with no line (the manager then logs the fixed text "Native-QAM bridge handshake failed.",
`SteamUiBridgePatch.cs:93`); removal discards the evaluation result. Ledger: U02A-017 (removal), NEW (bootstrap).
Recommendation: carry the page's error into the apply diagnostic; check `{ok:true}` on removal.

**TOOLKITCS-038 — nit — `SteamUiBridge.cs:42-45`; `Surfaces/SteamUiBridgePatch.cs:93,108`; fingerprints
`native-qam-*` across row files.** WSGM vocabulary in the library. Ledger: U02A-045, U02B-031. Recommendation:
`steam-ui:` correlation prefix and `steam-ui-*` fingerprints (log-only strings; WSGM has no parser of them, grep
confirmed).

**TOOLKITCS-039 — nit — `Surfaces/SteamUiBridgePatch.cs:27-29`.** `b.version===1` instead of
`SteamUiBridgeHost.SchemaVersion`. Ledger: U02B-035.

**TOOLKITCS-040 — nit — `SteamUiShared.cs:22-27`.** `Bound` exceeds the maximum by three and can split a surrogate
pair. Ledger: U02A-044.

### Modules and runtime

**TOOLKITCS-041 — medium — `SteamUiModule.cs:184-230`.** A command-only or publication-only module declared before
the installer of the same patch id makes the installer's `Add` throw `ArgumentException` (docs promise order
independence and `InvalidOperationException`); duplicate publications are accepted; failure attribution goes to
whichever module registered first. Ledger: U02A-032, U02A-033, C20. Recommendation: three maps (installer, publisher,
command handler) and attribute by the map of the failing callback.

**TOOLKITCS-042 — low — `SteamUiModuleRuntime.cs:202-210`; `SteamUiModule.cs:48-50`.** Commands disabled,
quarantined and unregistered all answer "The requested semantic service is not active."; `ISteamPowerLimitBackend`
default (`SteamPowerLimitSurface.cs:54-57`) reuses it for "mode selection unsupported". Ledger: U02A-035, U03A-030.
Recommendation: one fixed reason per cause.

**TOOLKITCS-043 — low — `SteamUiLog.cs:48-56`.** `_sink` is a non-volatile static written after construction; no
test observes diagnostics. Ledger: U02A-006, C4. Recommendation: keep the single process sink (R10), mark it
volatile, put sink-observing tests in a non-parallel xUnit collection.

**TOOLKITCS-044 — medium — `SteamUiModuleRuntime.cs:42,440-471`; WSGM `SteamUiSessionHost.cs:68,1466-1491,1532`.**
Quarantine is permanent per runtime but retraction is done by the consumer, which mirrors `_failedPatchIds` so its
own `SetPatchStates` does not remount the patches. Ledger: U02A-034, C21. Recommendation: the runtime holds the
manager and calls `manager.Fault(patchId, reason)` for the failing module's patches; the manager's effective switch is
`enabled && !faulted`; WSGM deletes its mirror. No Reset (R4).

**TOOLKITCS-045 — low — `SteamUiModuleBuilder.cs:85-112`; `SteamUiModule.cs:33-37`.** `Command` builders skip
argument validation while `Publication` validates; `new SteamUiCommandResult(false, null)` is accepted despite "never
null on failure". Ledger: U02A-063.

**TOOLKITCS-046 — low (duplication) — `Surfaces/SteamSurfaceModule.cs:112-195` vs `SteamUiModuleBuilder.cs:14-112`.**
Internal `SteamPayloadReader<T>`, `Publication`, `Command` duplicate the public builder; `SteamSoundOverrideSurface`
and `SteamThemeStyleSurface` already use the public builder. Ledger: NEW (related U03A-016). Recommendation: delete
the internal copies; surfaces use `SteamUiModuleBuilder`.

### Surfaces

**TOOLKITCS-047 — low — `Surfaces/SteamPowerPresetRow.cs:68-93`.** Magic WSGM id `"custom"` refused in the library;
module id fixed and no `Serialize`/`id` parameter unlike siblings. Ledger: U03A-005, U03A-016, C32.
Recommendation: `SteamPowerProfileOption.Selectable` (default true) published by WSGM; the row refuses unselectable
ids generically.

**TOOLKITCS-048 — low — `Surfaces/SteamQuickAccessRowPatch.cs:50-81,164-166`.** `primaryCountName` is interpolated
raw into evaluated JavaScript and read back by name. Ledger: U03A-021. Recommendation: validate as an identifier.

**TOOLKITCS-049 — low — `Surfaces/SteamPagePatch.cs:10,69-71`.** `SteamPageProbe(Name, Tokens)` takes raw JS
(`Tokens` is an array literal string); no escaping or duplicate-name check. Ledger: U03B-020. Recommendation:
`IReadOnlyList<string>` tokens serialized with `SteamUiProbeJs.Tokens`; validate names.

**TOOLKITCS-050 — low — payload shape gaps: `SteamBrightnessSurface.cs` (`setBrightness` no arity),
`SteamNavigationPanelSurface.cs:184-190` and `SteamExtensionsTabSurface.cs:195-201` (`activate` no arity),
`SteamStorageSurface.cs:239-266` (adopt/eject/format no arity; `TryReadId` caps at `int.MaxValue` though ids are
uint32), `SteamFrameLimitRow.cs:156-162` (`setRefreshRate` requires a `persistence` it discards).** Ledger: U03A-010,
U03A-026. Recommendation: exact shape per command (type checks only, no new ranges).

**TOOLKITCS-051 — low — `Surfaces/SteamPerformanceSurface.cs:505`.** `ulong.TryParse` without invariant culture.
Ledger: U03A-027.

**TOOLKITCS-052 — nit — `Surfaces/SteamScreensaverSurface.cs:94`.** `$` matches before a trailing `\n`. Ledger:
U03A-025. Recommendation: `\z`.

**TOOLKITCS-053 — low — `Surfaces/SteamSideMenuSnapshot.cs:29-34`.** `KeyboardOpen` defaults to `false` ("confirmed
closed") while `OverlayActive` defaults to `null`. Ledger: U03B-011.

**TOOLKITCS-054 — low — `Surfaces/SteamOverlayActivationPatch.cs:28-39`.** The activation map never prunes and the
overflow flag is sticky, so after 33 identities or one malformed callback every overlay reads unknown until
re-apply. Ledger: U03B-010. Recommendation: drop the cap; on a malformed callback record that identity unknown
instead of clearing everything.

**TOOLKITCS-055 — medium — `Surfaces/SteamFilePickerSurface.cs:63-103,109-161,200-222`.** `listPlaces` runs
`DriveInfo.IsReady`/free space synchronously on the request pump (a disconnected network drive blocks); listing has
no cancellation after start and returns raw `ex.Message`; device namespaces (`\\.\`) pass `IsPathFullyQualified`;
Downloads is `profile\Downloads`, so a redirected Downloads is not offered. Ledger: U03B-004 (parts), NEW for
Downloads. Recommendation: see R8.

**TOOLKITCS-056 — nit — namespace split:** `SteamGameWindowActivation.cs:7`, `SteamOverlayActivationPatch.cs:5`,
`SteamRouteNavigation.cs:7`, `SteamSideMenuSnapshot.cs:8`, `SteamNativeSurfaceCommands.cs:6`,
`SteamSharedContext.cs:3` use `SteamUiToolkit.Surfaces`. Ledger: U03B-029, U02B-033, U03A-024.

**TOOLKITCS-057 — nit — `Surfaces/SteamUiText.cs:4-12`.** One-line public helper used only by WSGM
(`NativeQamSemanticServices.cs`). Ledger: U02B-032. Recommendation: move to WSGM.

**TOOLKITCS-058 — nit — `Surfaces/SteamSoundOverrideSurface.cs:10,25,43-49`.** Mutable `string[]` in a record,
chunk label `sound-overrides-probe` breaks the `steam_ui_*_probe_` pattern, no `id` parameter. Ledger: U03A-031,
U03A-029.

**TOOLKITCS-059 — nit — contract docs that read as readback gates:** `ISteamCpuBoostBackend`/`ISteamHybridCoreBackend`/
`ISteamPowerProfileBackend` "Selects and verifies ... The verified outcome" (`SteamCpuBoostRow.cs:30-33`,
`SteamHybridCoreRow.cs:27-30`, `SteamPowerProfileRow.cs:28-31`); `SteamBrightnessSurface.cs:459,467-469` ("confirmed
readback"); `SteamVariableRefreshRow.cs:11` ("What the device reports"). Ledger: NEW. Recommendation: describe a
truthful outcome and the written value published as observed when no readback exists (maintainer rule).

**TOOLKITCS-060 — nit — stale docs in code:** `SteamPageSurface.cs:53-56` (router-backstack fallback not
implemented, U03B-014); `SteamThemeStyleSurface.cs` vs reference polling (U03B-013); `SteamGatePatch.cs:39` and
`SteamUiProbeJs.cs:8-11` ("literal module ids", U02B-017); `SteamUiModule.cs:100-101` (order); `SteamUiBridgePatch.cs:
12-14` ("manager orders by stable patch id"); `SteamCef.cs:19-23,33` (C39); `SteamPowerMenuSurface.cs:73`
(U03A-023); `SteamSettingsRows.cs:86-87` names WSGM (U03A-028); `SteamSurfaceJsonContext.cs:10-11` (U02B-034);
`SteamUiExtensionHost.cs:51-53` "ordered by id" vs directory sort (U02A-060).

### Extensions

**TOOLKITCS-061 — low — `SteamUiExtensionHost.cs:189-200,231-239,276-282`.** No host-reserved prefix (an extension
named `steam-ui` may claim `steam-ui.power-limit`); containment ignores junctions/symlinks; every rejection drops the
parsed manifest, contradicting "an extension that fails to load still has its patches removed". Ledger: U02A-030,
U02A-061 (reparse), U02A-031.

**TOOLKITCS-062 — low (docs) — `SteamUiExtensionHost.cs:9-27`.** "same patch lifecycle, same clean removal" is
promised but nothing turns an extension into a module; zero production references. Ledger: U02A-029, C27.
Recommendation: document discovery/validation only (R7).

### Public seams

**TOOLKITCS-063 — low — `SteamUiTransportModels.cs`/`SteamUiEndpointDiscovery.cs:12-48`,
`SteamUiCdpConnection.cs:15-48`, `PersistentSteamUiTransport.cs:98-102`.** `ISteamUiCdpWire`,
`ISteamUiCdpWireFactory`, `ISteamUiEndpointDiscovery`, `SteamUiEndpoint` are public "for consumer testing" while the
only constructor that accepts them is internal; consumers fake `ISteamUiTransport` instead (four WSGM fakes).
`SteamUiBridgeAuthorizer`/`SteamUiBridgeAuthorizationResult` are public with no consumer. Ledger: U02A-009, NEW for
the authorizer. Recommendation: internalize all six.

### Tests

**TOOLKITCS-064 — medium (test quality) — substring/predicate copies.** Probe and client-script tests assert
fragments of generated JavaScript (`SteamClientTests.cs:241-261,455-495`, `SteamStartupMovieTests.cs:591-600`,
`SteamHomeCarouselTests.cs:259-286`, `SteamLibraryBadgeTests.cs:400-425,465-474`, `SteamNavigationPanelTests.cs:
559-581`, `SteamPageTests.cs:680-717`, `SteamScreensaverTests.cs:278-300`, `SteamStorageTests.cs:445-454`,
`SteamPowerMenuTests.cs:216-231`, `SteamExtensionsTabTests.cs:12-41`, `SteamGameContextMenuTests.cs:175-192`,
`SteamThemeStyleTests.cs:590-609`) and gate predicates are restated as strings (`SteamGatePatchContractTests.cs:
171-203`, `SteamPagePatchTests.cs:649-656`, `SteamThemeStyleTests.cs:612-617`). `DoesNotMatch` of an old regex
against a test-local literal (`SteamPageTests.cs:710-716`) tests nothing in production. Ledger: U02B-005, U02B-006,
U03B-006, U03A-018, U03B-042, C36. Recommendation: run the probe and client expressions through `NodeScript`
against fiber/store fixtures (pattern exists in `SteamWindowSurfaceTests.cs:505-556`).

**TOOLKITCS-065 — low (test quality) — getter-only tests.** `SteamUiPatchManagerTests.cs:7-18`
(`PatchBoundsPreservePublishedNamedArguments`), `SteamUiModuleTests.cs:108-114`, `SteamStorageTests.cs:437-442`,
`SteamPageTests.cs:671-677,770-777`, `SteamThemeStyleTests.cs:581-587`, `SteamPanelFoldsTests.cs:683-691`,
`SteamStartupMovieTests.cs:583-588`, `SteamScreensaverTests.cs:414` (restates `Commands`),
`PersistentSteamUiTransportTests.cs:332-343` (constant table). Ledger: U02A-066, NEW for the rest.
Recommendation: delete; the command-vocabulary contract is covered by deriving `Commands` from the handler table
(TOOLKITCS-069).

**TOOLKITCS-066 — medium (coverage) — untested production paths.** Module runtime command path (refusal, quarantine,
cancel, duplicate sequence, undelivered response) `SteamUiModuleRuntime.cs:155-273` (U02A-007); CDP close/fault and
cancel-during-send (`Fakes/QueueWire.cs` cannot close or fault; U02A-038); apply-then-fail removal and ordered
teardown (`Fakes/FakePatch.cs:67-81` throws before mutating; U02A-037); `SteamNativeSurfaceCommands.ReplayAsync`
(U03A-020); `SteamFilePickerSurface` (U03B-005); `SteamSoundOverrideSurface` (U03A-019); `SteamClient` writes, the
write lane and timeout mapping (U02B-004); the storage probe's invocation safety (TOOLKITCS-001); the bridge
bootstrap error path.

**TOOLKITCS-067 — low (test quality) — async negatives and statics.** `SteamUiBridgeHostTests.cs:41-55` asserts zero
deliveries immediately after emitting to a pumped channel (U02A-036); `PersistentSteamUiTransportTests.cs:345-370`
mutates the process-wide session without a non-parallel collection (U02A-065); `FakeSteamUiTransport.Generations`
is written across threads unsynchronized (`Fakes/FakeSteamUiTransport.cs:31,152`).

**TOOLKITCS-068 — low (test quality) — timing.** 1 s waits and real backoff (`Fakes/TestJson.cs:16-23` throws a bare
`TaskCanceledException`; `PersistentSteamUiTransportTests.cs:210-242` waits out the 1 s reconnect with a 10 s budget;
`SteamUiPatchManagerTests.cs:131-150,309-330`). Ledger: U02A-064. Recommendation: descriptive timeout message; after
R5 the settle delay is injectable through an internal constructor parameter (`TimeProvider`) rather than real time.

**TOOLKITCS-069 — low (test quality) — `SteamSurfaceModuleTests.cs:13-96`.** The "full set" test lists 21 of ~31
surfaces and compares hand-kept `Commands` lists. Ledger: U02B-038, U03A-016. Recommendation: derive each surface's
`Commands` from its handlers (or drop the property) and build the set from all surfaces.

**TOOLKITCS-070 — nit (fakes) — `Fakes/RecordingBackend.cs:105-108,194-197,166-169,262-265,267-272`.** "boost" labels
both CPU boost and boost power; one `ActivateAsync(string)` serves two backends; dead `CollapseAsync`; always
`Applied`. `SteamPanelFoldsTests.cs:729-738` shadows the shared fake. Ledger: U02B-037, U03B-045, NEW (dead method).

**TOOLKITCS-071 — nit — `Fakes/NodeScript.cs:14,29-38`.** Requires `node` on PATH; timeout loses stdout/stderr.
Ledger: U02B-041, U02B-027.

---

## 3. Plan refinements

### Changes and removals (over-engineering under the simplify / no-arbitrary-limits rules)

- **R1 — Fold T01_01.** Do not land T01_01 as written: it edits `SteamUiTransportSession.EvaluateAsync` and adds
  `CefEvalResult.FromTransport`, both deleted by the client redesign (maintainer rule: do not patch code a later change
  deletes). Its tests become TOOLKITCS-B3 tests against `SteamUiDispatch`.
- **R2 — Four outcomes, not six.** Transport: `SteamUiEvaluationResult(SteamUiDispatch Dispatch, string? Value,
  string? Error, SteamUiGenerations Generations)` with `NotSent | Closed | Unanswered | Answered`. Client writes:
  `SteamClientWriteResult(SteamClientWriteOutcome Outcome, string? Error)` with `NotSent | Unknown | Rejected |
  Applied`. A JavaScript exception is `Answered` + `Error` → `Rejected`. Caller cancellation before send →
  `NotSent`; after send → `Unknown`. `CefEvalResult` is deleted.
- **R3 — No `DependsOn` graph.** The manager keeps the toolkit's own bridge first on apply and last on every removal
  path (sync, global disable, dispose). One rule in one method; no registration-time cycle validation.
- **R4 — No quarantine Reset API.** Quarantine lives for the runtime instance (today's behaviour); a recreated host
  is a new runtime. The runtime marks the failing module's patches faulted in the manager; WSGM deletes
  `_failedPatchIds` and the remount guard.
- **R5 — One synchronization loop.** The manager's existing coalescing queue becomes the only loop; generation events
  queue it; it raises `Synchronized` after each pass. WSGM deletes `SynchronizeLoopAsync`, its signal and the two-pass
  disable, and does its post-pass reconcile in the handler. Settle retry and Retrying-after-timeout share the
  existing `ScheduleSettleRetry`.
- **R6 — Drop bridge host ID and per-bootstrap nonce.** Keep asset hash, schema version and generations. Replacing a
  bridge with a different asset hash is the intended update path, so do not refuse it.
- **R7 — Extensions: discovery only.** Keep `Discover`, keep the manifest on rejection, add the reserved-prefix
  check and the reparse check. Drop the "concrete adapter" (no consumer) and the 256 KiB script cap; fix the docs.
- **R8 — Simpler file picker than B2.** Keep: places and listings on a worker; the request waits with the caller's
  cancellation plus one fixed wait, after which it answers `TimedOut` and the worker's late result is discarded;
  outcomes `Listed | Empty | Inaccessible | TimedOut | Cancelled`; extended `\\?\` normalization; refuse `\\.\`,
  `\\?\GLOBALROOT`, `\Device\`, `\??\`; Downloads via the known-folder API; the TypeScript picker renders in pages
  with the existing "More" control and settles `null` on unmount. Drop: temp spool, worker counters,
  `ProviderBusy`/`ProviderCapacity`, injected clock, `FilePickerPathPolicy` type (one fixed rule in the library;
  WSGM is the only consumer). Listing stays in memory: a directory listing is names only.
- **R9 — Running-apps observer without reference counting.** Single-reader contract (WSGM has one reader);
  install-after-register, `Date.now()` generation seed, version stamp.
- **R10 — Keep the static log sink.** Drop the plan's `ISteamUiLog` parameter on `SteamClient` and every owner; it is
  write-only diagnostics, not behavioural state. Volatile write; tests that observe logs use one non-parallel
  collection.
- **R11 — Logging rule.** Replace "no raw paths/payloads" with "redact string values in payload lines; keep property
  names, kinds, numbers and booleans", which removes the secret leak and keeps tester diagnosis from wsgm.log.
- **R12 — Arbitrary caps to delete** (add to the plan's list): patch expression bound, 32 M delivery cap, extension
  script cap, side-menu/overlay window caps, overlay activation identity cap, and the documented caps in
  TOOLKITCS-031. Keep: CDP read bounds (8 MiB response, 1 MiB notification), request backpressure (32 outstanding,
  64 queued bridge requests), the 30 s operation ceiling.

### Additions the plan is missing

- **A1** Storage probe must not invoke exports (TOOLKITCS-001), named as its own first fix with a Node test.
- **A2** Answered JavaScript errors are not a health signal (TOOLKITCS-004).
- **A3** Remove the dead notification lane; per-role latest-wins generation delivery (TOOLKITCS-007/008).
- **A4** Request leases do not start reconnect loops; absent targets do not escalate backoff (TOOLKITCS-005/006).
- **A5** Teardown takes the caller's deadline token on transport, bridge, manager and runtime (TOOLKITCS-012), so
  the B3 shutdown phases can actually bound Steam UI teardown.
- **A6** Contract simplification: remove `SteamUiPatchBounds`, `ISteamUiPatch.Version`, `ResourceKey`,
  `SteamUiPatchProbeResult.Unique` (TOOLKITCS-026/031/032).
- **A7** Merge duplicate builders/parsers (TOOLKITCS-035/046) and internalize unused seams (TOOLKITCS-063).
- **A8** Secret redaction in refusal lines (TOOLKITCS-020).
- **A9** Contract docs must not describe readback as a precondition (TOOLKITCS-059).

---

## 4. Target design

### Owners

| Owner | Responsibility | Notes |
| --- | --- | --- |
| `PersistentSteamUiTransport` (public, sealed) | Discovery, one connection per role, generations, dispatch-aware evaluation, enable/close state (`SetEnabled(bool, string? reason)` becomes public) | Takes `requireMainWindow`; internal ctor for tests. `ShutdownAsync(CancellationToken)`. Raises `GenerationChanged` (latest per role) and `BindingCalled`. |
| `SteamUiCdpConnection` (internal) | Framing, request correlation, send under lifetime token, sent/not-sent distinction | — |
| `SteamClient` (public, new) | All one-shot client operations over one `ISteamUiTransport`; one write lane for apps, collections and install folders | Sub-objects `Apps`, `Collections`, `InstallFolders`, `Library`, `Downloads`, `CurrentPage`, `StartupMovie`, `RunningApps`; pure parsers stay internal static. |
| `SteamUiPatchManager` | Registry, single coalesced loop, bridge-first apply/bridge-last removal, faults, `Synchronized` event, `ShutdownAsync(ct)` | No resource gates. |
| `SteamUiBridgeHost` | Binding, bootstrap, delivery, authorization (internal `SteamUiBridgeAuthorizer`) | No 32 M cap. |
| `SteamUiModuleRuntime` | Publication pump, command dispatch, quarantine → `manager.Fault` | Takes the manager. `ShutdownAsync(ct)`. |
| `SteamUiModuleSet` | Three indexes (installer, publisher, handler) | — |
| `SteamUiModuleBuilder` | The only module/publication/command builder | Absorbs `SteamSurfaceModule`. |
| Surfaces (static factories) | Patch + state + commands per Steam surface | One namespace `SteamUiToolkit`; `Patch` typed `ISteamUiPatch`. |

### Dissolved files: old symbol → new owner

`SteamUiTransportSession.cs` (deleted)

| Old symbol | New owner |
| --- | --- |
| `CefEvalResult` (+ `Ok`, `Unreachable`) | deleted; `SteamUiEvaluationResult` with `SteamUiDispatch` |
| `SteamUiTransportSession.DisabledReason` | `PersistentSteamUiTransport.DefaultClosedReason` (const) |
| `Enabled`, `ClosedReason`, `SetEnabled` | `PersistentSteamUiTransport.SetEnabled` (public), state visible as `Dispatch == Closed` |
| `IsClosedReason` | `SteamUiEvaluationResult.Dispatch == SteamUiDispatch.Closed` (manager log level, running-apps parse) |
| `Attach`, `Detach` | deleted; `ShellSession` passes its transport to `SteamClient` and consumers |
| `EvaluateAsync`, `EvaluateOnVisibleWindowAsync` | `ISteamUiTransport.EvaluateAsync(role, …)` |
| private `Gate`, `_transport`, `_enabled`, `_closedReason` | deleted |

`Surfaces/SteamSurfaceModule.cs` (deleted)

| Old symbol | New owner |
| --- | --- |
| `SteamPayloadReader<T>` | `SteamUiPayloadReader<T>` (public, existing) |
| `Declare<T>` | `SteamUiModuleBuilder.Module<T>(id, patchId, enabled, read, typeInfo, patches, commands)` |
| `Publication<T>` | `SteamUiModuleBuilder.Publication<T>` (existing) |
| `Command<T>`, `Command` | `SteamUiModuleBuilder.Command<T>`, `Command` (existing) |
| `TryReadValueWrite` | `SteamUiPayload.TryReadValueWrite` (internal) |
| `Invalid` | `SteamUiCommandResult.Invalid(string)` (public static factory) |
| `SteamSettingPersistence` enum | moved to `Surfaces/SteamFrameLimitRow.cs` (unchanged values) |

`Surfaces/SteamUiText.cs` (deleted) — `SteamUiText.Of` → private helper in WSGM `NativeQamSemanticServices.cs`.

`Client/SteamApps.cs`, `SteamCollections.cs`, `SteamInstallFolders.cs`, `SteamLibraryData.cs`,
`SteamDownloadActivity.cs`, `SteamCurrentPage.cs`, `SteamStartupMovie.cs`, `SteamRunningApps.cs` (static classes
dissolved into `SteamClient`; record/enum types stay in their files)

| Old symbol | New owner |
| --- | --- |
| `SteamApps.Writes` | `SteamClient._writes` (private, shared by Apps/Collections/InstallFolders) |
| `SteamApps.*Async` (13 methods) | `SteamClient.Apps.*Async` (same names) |
| `SteamApps.NormalizeAppId`, `IsShortcutAppId` | stay `public static` on `SteamApps` (pure) — `SteamApps` becomes the sub-object class name with static helpers |
| `SteamApps.ReadOfficialIconUrlAsync`, `ReadAccountIdAsync`, `ReadLogoPositionAsync` | `SteamClient.Apps.*` returning `SteamReadResult<T>` |
| `SteamCollections.SyncAsync(…, ISteamUiTransport?, …)` | `SteamClient.Collections.SyncAsync(…)` (transport param removed) |
| `SteamInstallFolders.AddAsync/RemoveAllAtPathAsync/SetLabelAsync` | `SteamClient.InstallFolders.*` (write lane); `NormalizePath` stays public static |
| `SteamLibraryData.ReadGamesAsync` | `SteamClient.Library.ReadGamesAsync` |
| `SteamLibraryData.ListGamesAsync/ListCollectionsAsync/ListStoreTagsAsync/IsLoadedAsync` | deleted; `ReadCollectionsAsync`/`ReadStoreTagsAsync` typed; `IsLoadedAsync` → `Library.ReadGamesAsync` failure detail |
| `SteamDownloadActivity.QueryAsync`, `IsActive` | `SteamClient.Downloads.QueryAsync` (typed), `IsActive` static |
| `SteamCurrentPage.GetAsync` | `SteamClient.CurrentPage.GetAsync` |
| `SteamStartupMovie.SetAsideAsync/RestoreAsync(…, ISteamUiTransport?, …)` | `SteamClient.StartupMovie.*` (transport param removed) |
| `SteamRunningAppsProbe` (ctor transport) | `SteamClient.RunningApps` (same members) |
| `SteamClientScript` | unchanged internal; `EvaluateAsync` single path over the client's transport |

`SteamUiPatchManager.cs` contract members removed

| Old symbol | New owner |
| --- | --- |
| `SteamUiPatchBounds` (record, `Default`, `Deconstruct`) | deleted; `ISteamUiPatch.OperationTimeout` (TimeSpan, default 8 s via `SteamUiPatch.DefaultTimeout`) |
| `MaximumExpressionCharacters` | deleted (TOOLKITCS-031) |
| `MaximumDiagnosticCharacters` | `SteamUiShared.MaximumDiagnosticLength` |
| `ISteamUiPatch.Version`, `SteamUiPatchSnapshot.Version` | deleted |
| `ISteamUiPatch.ResourceKey`, `_resourceGates` | deleted |
| `SteamUiPatchProbeResult.Unique` | deleted |
| `BridgeFirst()` | private `ApplyOrder()`/`RemovalOrder()` |

`Surfaces/SteamGatePatch.cs` — `internal static ProbeAsync` → `SteamUiPatchEvaluation.EvaluateProbeAsync`
(role `SharedJsContext`); ctor `resourceKey` parameter removed.

### Public API inventory (229 public types) — decision per type

Decision key: **R** retain (unchanged), **R*** retain with a breaking change, **I** internalize, **X** remove.

Transport and core

| Type | Decision | Note / WSGM consumers |
| --- | --- | --- |
| `ISteamUiTransport` | R* | `EvaluateAsync` result gains `Dispatch`; `NotificationReceived`→`BindingCalled`; `ShutdownAsync`. WSGM fakes: `tests/WSGM.Tests/Shell/RunningApplicationTargetTests.cs:487`, `SteamDownloadSortPatchTests.cs:55`, `SteamUiSessionHostTests.cs:432,656` |
| `PersistentSteamUiTransport` | R* | public `SetEnabled`; `ShellSession.cs`, `ShellSession.SteamUi.cs`, `ShellSession.Shutdown.cs` |
| `SteamUiTargetRole`, `SteamUiTransportHealth`, `SteamUiGenerations`, `SteamUiTransportSnapshot` | R | — |
| `SteamUiEvaluationResult` | R* | `Reachable` → `Dispatch`; `Unavailable` factory replaced |
| `SteamUiDispatch` | new | — |
| `SteamUiNotification` | R | binding calls only |
| `ISteamUiCdpWire`, `ISteamUiCdpWireFactory`, `ISteamUiEndpointDiscovery`, `SteamUiEndpoint` | I | no consumer; tests via InternalsVisibleTo |
| `SteamUiTransportSession` | X | 6 WSGM files |
| `CefEvalResult` | X | `SteamLibraryTabs.cs` |
| `ISteamUiLog`, `SteamUiLog` | R | `WsgmSteamUiLog.cs` |
| `SteamCef` | R* | doc fix only (no signature change) |

Bridge, patches, modules

| Type | Decision | Note |
| --- | --- | --- |
| `SteamUiBridgeHost` | R* | `MaximumDeliveryCharacters` removed; `ShutdownAsync` |
| `SteamUiBridgeRequest` | R* | `ToCorrelationId` prefix `steam-ui:` |
| `SteamUiBridgeAuthorizer`, `SteamUiBridgeAuthorizationResult` | I | no consumer |
| `SteamUiBridgeIdentity` | R | `SteamDownloadSort.cs`, `SteamLibraryTabs.cs`, `LibraryTabManager.cs` |
| `SteamUiInjectedAsset` | R | — |
| `SteamUiBridgePatch` | R* | probe preconditions only |
| `SteamUiPatchManager` | R* | `Fault`, `Synchronized`, `ShutdownAsync`; awaited enable waits for next pass |
| `ISteamUiPatch` | R* | `OperationTimeout`; no `Version`, `ResourceKey`, `Bounds`. WSGM implementers: `SteamDownloadSort.cs`, `SteamInputGlyphStylePatch.cs` |
| `SteamUiPatchBounds` | X | WSGM `SteamDownloadSort.cs:316`, `SteamInputGlyphStylePatch.cs:52` |
| `SteamUiPatchContext` | R* | no expression bound |
| `SteamUiPatchProbeResult` | R* | no `Unique` |
| `SteamUiPatchOperationResult`, `SteamUiPatchState` | R | — |
| `SteamUiPatchSnapshot` | R* | no `Version` |
| `SteamUiPatchEvaluation` | R | root-kind checks |
| `SteamUiProbeJs`, `SteamUiModuleResolver` | R | — |
| `ISteamUiModule`, `SteamUiModule`, `SteamUiStatePublication`, `SteamUiCommandHandler`, `SteamUiCommandDelegate` | R | Plugin.Sdk boundary |
| `SteamUiCommandResult` | R* | `Invalid(reason)`; validated ctor; distinct refusal reasons |
| `SteamUiModuleSet` | R* | three indexes |
| `SteamUiModuleRuntime` | R* | takes manager; `ShutdownAsync` |
| `SteamUiModuleFailure` | R | — |
| `SteamUiModuleBuilder`, `SteamUiPayloadReader` | R* | absorbs internal duplicates; validates `Command` args |
| `SteamUiPayload` | R | + internal `TryReadValueWrite` |
| `SteamUiExtensionHost`, `SteamUiExtension`, `SteamUiExtensionManifest`, `SteamUiExtensionRejection` | R* | no script cap; manifest kept on rejection; new `ReservedPrefix` rejection |

Client (all records/enums retained unless listed)

| Type | Decision | Note |
| --- | --- | --- |
| `SteamClient` | new | — |
| `SteamApps` | R* | static operations → instance `SteamClient.Apps`; `NormalizeAppId`/`IsShortcutAppId` static |
| `SteamCollections`, `SteamInstallFolders`, `SteamLibraryData`, `SteamDownloadActivity`, `SteamCurrentPage`, `SteamStartupMovie` | R* | instance sub-objects |
| `SteamRunningAppsProbe` | R* | becomes `SteamClient.RunningApps` |
| `SteamClientWriteResult` | R* | `Outcome` enum |
| `SteamShortcutAddResult`, `SteamCollectionSyncResult`, `SteamStartupMovieResult` | R* | dispatch-aware outcome, partial fields |
| `SteamLibraryAddResult`/`Status`, `SteamLibraryRemoveResult`/`Status`, `SteamLibraryLabelResult`/`Status` | R* | `Unavailable` split into `NotSent`/`Unknown`; add `Partial` with detail for add |
| `SteamLibraryApp`, `SteamCollectionInfo`, `SteamCurrentApp`, `SteamDownloadOverview` | R* | `uint` app ids |
| `SteamLibraryReadResult`, `SteamAppDetails`, `SteamAppDetailsResult`, `SteamShortcut`, `SteamShortcutListResult`, `SteamStoreTag`, `SteamArtworkSlot`, `SteamArtworkFormat`, `SteamLogoPosition`, `SteamStartupMovieChoice`, `SteamRunningAppsObservation` | R | — |

Surfaces (all 30+ surface classes, their states, backends and records): **R**, except

| Type | Decision | Note |
| --- | --- | --- |
| `SteamQuickAccessRowPatch` | I | surfaces expose `ISteamUiPatch` |
| `SteamGatePatch` | R* | no `resourceKey`; uses `EvaluateProbeAsync`. WSGM: `SteamChordResetSurface.cs`, `SteamControllerCapsSurface.cs` |
| `SteamPageProbe` | R* | `Tokens` as `IReadOnlyList<string>`. WSGM: 6 page surfaces |
| `SteamPowerProfileOption` | R* | `Selectable` (default true) |
| `SteamPowerPresetRow` | R* | no magic `custom`; `id` param + `Serialize` |
| `SteamWindowSideMenu` | R* | `KeyboardOpen` default `null` |
| `SteamSoundOverrideState` | R* | `IReadOnlyList<string>` |
| `SteamUiText` | X | moves to WSGM |
| `SteamGameWindowActivation`, `SteamNativeSurfaceCommands`, `SteamNativeSurfaceAction`, `SteamOverlayActivationPatch`, `SteamRouteNavigation`, `SteamSideMenu`, `SteamSideMenuObserver`, `SteamSideMenuSnapshot`, `SteamWindowSideMenu` | R* | namespace `SteamUiToolkit` |
| `SteamFilePickerSurface`, `SteamFileListing` | R* | typed outcome, page token |
| `SteamRouteNavigation`, `SteamSettingsRows`, `SteamPerformanceDeltaReader` | R | no WSGM production caller; library features with tests (plan: no deletion for non-use) |

### Consumers that must change (parent repo)

| Change | Files |
| --- | --- |
| Session removal, transport enable | `src/WSGM/Shell/ShellSession.cs` (225, 599, 813-817), `ShellSession.SteamUi.cs:83`, `ShellSession.Shutdown.cs:381` |
| Raw evaluation via transport | `src/WSGM/Core/LibraryFilter.cs:641`, `Core/SteamLibraryTabs.cs:169,220,276`, `Shell/LibraryTabManager.cs:228` |
| `SteamClient` instance | `Core/Artwork/SteamArtwork.cs`, `Core/SteamLaunchConfig.cs`, `Core/SteamLibraryFolders.cs`, `Core/SteamLibraryVdf.cs` (`NormalizePath` only, static stays), `Core/Animations/AnimationOverrides.cs`, `Overlay/CardManagerView.cs`, `Overlay/LibraryTabsView.cs`, `Overlay/OverlayWindow.LaunchFixes.cs`, `Overlay/ArtworkView.cs`/`LaunchWrapperView.cs`/`OverlaySubView.cs` (`SteamLibraryApp.AppId` type), `Shell/KeepAwakeService.cs`, `Shell/LibraryPolicy.cs`, `Shell/OverlayLibraryLookup.cs`, `Shell/RunningApplicationTarget.cs`, `Shell/ShellSession.GameLibrary.cs`, `Shell/SteamArtworkBrowserSource.cs`, `Shell/GameLibraryService.cs`, `Shell/AnimationService.cs`, `Shell/SdFormatManager.cs`, `Shell/LibraryTabManager.cs` |
| Patch contract | `Core/SteamDownloadSort.cs`, `Core/SteamInputGlyphStylePatch.cs`, `Shell/SteamChordResetSurface.cs`, `Shell/SteamControllerCapsSurface.cs`, 6 `SteamPagePatch` users (`Shell/SteamAnimationsSurface.cs`, `SteamArtworkBrowserSurface.cs`, `SteamGraphicsSurface.cs`, `SteamLibraryImportSurface.cs`, `SteamThemesSurface.cs`, `SteamWsgmSettingsSurface.cs`) |
| Manager/runtime ownership | `Shell/SteamUiSessionHost.cs` (delete `_failedPatchIds`, `SynchronizeLoopAsync`, two-pass disable; subscribe `Synchronized`; pass deadline to `ShutdownAsync`) |
| Small API moves | `Shell/NativeQamSemanticServices.cs` (`SteamUiText`), `Shell/NativeQamPowerPresetService.cs`/`DevicePowerAssignments.cs` (`Selectable`), `Shell/ShellSession.Actions.cs` (namespace) |
| Tests | the four WSGM `ISteamUiTransport` fakes; `tests/WSGM.Tests` cases touching `SteamUiEvaluationResult`, `SteamAutoTdpState`, `SteamCollectionSyncResult`, `SteamStartupMovieResult` |
| TypeScript (other domain) | storage gate provider resolution (pairs with TOOLKITCS-001), picker paging/unmount, power-preset `selectable`, bridge probe split |

---

## 5. Implementation batches

All batches: child commit and push first, then the parent gitlink plus the consumer edits listed, in one parent
commit (CLAUDE.md submodule rule). Child build: `dotnet build external/steam-ui-toolkit/SteamUiToolkit.slnx -c
Release`; child tests with the filter given; parent compile `dotnet build WSGM.slnx -c Release -p:SkipNativeArtifacts=true`
after consumer edits.

**TOOLKITCS-B1 — Transport and connection correctness (no public API change)** — ~700 lines
- Files: `SteamUiCdpConnection.cs`, `PersistentSteamUiTransport.cs`, `SteamCef.cs`, `NativeTcp.cs`,
  `SteamUiToolkit.csproj` (comments), tests `SteamUiCdpConnectionTests.cs`, `PersistentSteamUiTransportTests.cs`,
  `SteamCefTests.cs`, `Fakes/QueueWire.cs`.
- Steps: send under `_shutdown.Token` (002); answered JS error keeps `Ready` and resets timeouts (004); request lease
  without reconnect loop (005); absent target no escalation (006); delete `_notificationEvents` lane, keep the event
  name for now (007); per-role latest generation slot (008); `127.0.0.1` only and loopback/any rows (009);
  `DefaultDllImportSearchPaths` (010); track reconnect/retirement tasks for later B4 deadline (012 part).
- Tests: QueueWire close/fault/cancel-during-send; answered-error health; lease does not spawn a loop (count
  `FixtureDiscovery` calls); squatter on specific address; generation slot keeps both roles.
- Filter: `--filter "FullyQualifiedName~SteamUiCdpConnectionTests|FullyQualifiedName~PersistentSteamUiTransportTests|FullyQualifiedName~SteamCefTests"`.
- Dependencies: none.

**TOOLKITCS-B2 — Probe safety, evaluation parsing, bridge and runtime fixes (no public API change)** — ~900 lines
- Files: `Surfaces/SteamStorageSurface.cs` (probe), `SteamUiPatchEvaluation.cs`, `Surfaces/SteamGatePatch.cs`,
  `SteamUiBridge.cs`, `SteamUiModule.cs`, `SteamUiModuleRuntime.cs`, `SteamUiModuleBuilder.cs`, `SteamUiShared.cs`,
  `Surfaces/SteamPerformanceSurface.cs`, `Surfaces/SteamScreensaverSurface.cs`, `Surfaces/SteamQuickAccessRowPatch.cs`,
  `Surfaces/SteamOverlayActivationPatch.cs`, `SteamUiLog.cs`; tests `SteamStorageTests.cs`, `SteamUiBridgeHostTests.cs`,
  `SteamUiModuleTests.cs`, `SteamUiPatchManagerTests.cs`, `SteamWindowSurfaceTests.cs`, new `SteamUiModuleRuntimeTests.cs`.
- Steps: storage probe resolves by source shape, no invocation (001); answered-error probe → Incompatible, root-kind
  checks (033/034); gate probe uses `EvaluateProbeAsync` (035); bridge JSON root/name checks, bootstrap error
  diagnostic, removal ack check (034/037); module set three indexes and duplicate publication refusal (041);
  distinct refusal reasons (042); builder argument validation (045); payload redaction (020); oversized binding drop
  logged (017); `Bound` fix (040); invariant parse (051); `\z` (052); `primaryCountName` validation (048);
  overlay-activation map without cap or sticky overflow (054); volatile log sink (043).
- Tests: Node run of the storage probe asserting no export is called (TS gate counterpart is the TS domain);
  runtime command path matrix (refused/quarantine/cancel/duplicate/undelivered) via `EmitBindingPayload`; module
  order independence; malformed binding notifications followed by a sentinel (fixes 067); redaction line.
- Filter: `--filter "FullyQualifiedName~SteamStorageTests|FullyQualifiedName~SteamUiBridgeHostTests|FullyQualifiedName~SteamUiModule|FullyQualifiedName~SteamUiPatchManagerTests|FullyQualifiedName~SteamWindowSurfaceTests"`.
- Dependencies: TS domain for the storage gate (`gates/storage.ts` resolve) so probe and gate agree.

**TOOLKITCS-B3 — Dispatch outcome, session removal, `SteamClient` (breaking)** — ~1450 lines (child ~900, parent ~550)
- Files: `SteamUiTransportModels.cs`, `PersistentSteamUiTransport.cs`, `SteamUiCdpConnection.cs`, delete
  `SteamUiTransportSession.cs`, new `Client/SteamClient.cs`, all `Client/*.cs`, `SteamUiPatchManager.cs` (closed
  reason), tests `SteamClientTests.cs`, `SteamLibraryReadTests.cs`, `SteamStartupMovieTests.cs`,
  `PersistentSteamUiTransportTests.cs`, `Fakes/FakeSteamUiTransport.cs`.
- Steps: `SteamUiDispatch` set by the connection's send phase (003); public `SetEnabled`; delete session and
  `CefEvalResult`; `SteamClient` with one write lane covering install folders (014/018); dispatch-aware write
  results; typed reads, delete lossy reads (021); `uint` app ids (022); AddShortcut adoption guard (015); partial
  ownership in collections/startup movie/install folders (016); running-apps fixes (019); one reply shape (023);
  docs (024). Supersedes T01_01 (R1).
- Tests: table of dispatch phases via `ResponsiveWire` (not sent, closed, unanswered, answered error, answered
  value); `NodeScript` runs of AddShortcut (no-id adoption refused unless exe+name match), collection create-then-throw
  returns id, startup set-aside partial keeps choice, install-folder purge failure reported, running-apps observer
  register-throws leaves no observer.
- Filter: `--filter "FullyQualifiedName~SteamClientTests|FullyQualifiedName~SteamLibraryReadTests|FullyQualifiedName~SteamStartupMovieTests|FullyQualifiedName~PersistentSteamUiTransportTests"`; parent `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~RunningApplicationTarget|FullyQualifiedName~SteamDownloadSort|FullyQualifiedName~SteamUiSessionHost|FullyQualifiedName~GameLibrary|FullyQualifiedName~Animation"`.
- Dependencies: WSGM Core/Shell/Overlay domain owns the consumer edits listed in §4 (19 files) and the composition
  of one `SteamClient` in `ShellSession`; must land in the same parent commit.

**TOOLKITCS-B4 — Patch contract, manager loop, quarantine, teardown deadline (breaking)** — ~1300 lines
- Files: `SteamUiPatchManager.cs`, `SteamUiModuleRuntime.cs`, `SteamUiBridge.cs`, `Surfaces/SteamUiBridgePatch.cs`,
  `Surfaces/SteamGatePatch.cs`, `Surfaces/SteamQuickAccessRowPatch.cs`, all surfaces constructing gates (ctor arg),
  `Surfaces/SteamOverlayActivationPatch.cs`, tests `SteamUiPatchManagerTests.cs`, `SteamQuickAccessRowPatchTests.cs`,
  `Fakes/FakePatch.cs`.
- Steps: removal reverse/bridge-last on every path (025, R3); delete resource gates and `ResourceKey` (026); removal
  after failed apply, settle retry for Retrying, distinct cancel diagnostics (027/028); log outside lock (029);
  single coalesced loop + `Synchronized` + awaited switch waits for next pass (030, R5); remove `SteamUiPatchBounds`,
  `Version`, `Unique`, expression cap (031/032); bridge probe preconditions only, QAM counts into row patches (036);
  delete 32 M delivery cap; `steam-ui:` prefix and fingerprints (038/039); runtime faults patches in the manager
  (044, R4); `ShutdownAsync(CancellationToken)` on transport/bridge/manager/runtime (012).
- Tests: `FakePatch` "mutate then fail" mode with removal asserted; global disable with bridge + gate ends Disabled
  for both (no RemoveFailed); shutdown with a never-completing patch returns at the caller deadline and reports the
  patch; runtime fault keeps the patch disabled after the host re-enables it; bridge probe compatible without QAM
  counts.
- Filter: `--filter "FullyQualifiedName~SteamUiPatchManagerTests|FullyQualifiedName~SteamQuickAccessRowPatchTests|FullyQualifiedName~SteamUiModule|FullyQualifiedName~SteamUiBridge"`; parent `--filter "FullyQualifiedName~SteamUiSessionHost|FullyQualifiedName~SteamDownloadSort"`.
- Dependencies: Steam UI host domain (WSGM `SteamUiSessionHost`: delete loop, mirror, two-pass disable; pass the
  shutdown phase token) and shutdown domain (B3 phase that owns the Steam UI teardown); TS domain for the row-patch
  probe split if row fingerprints move into TS-shared constants.

**TOOLKITCS-B5 — API surface cleanup and surface shape fixes (breaking, mechanical)** — ~1000 lines
- Files: `Surfaces/SteamSurfaceModule.cs` (delete), `SteamUiModuleBuilder.cs`, `SteamUiPayload.cs`, every surface
  file (builder calls, namespace, docs), `Surfaces/SteamUiText.cs` (delete), `Surfaces/SteamPowerPresetRow.cs`,
  `Surfaces/SteamPowerProfileRow.cs`, `Surfaces/SteamPagePatch.cs`, `Surfaces/SteamSideMenuSnapshot.cs`,
  `Surfaces/SteamSoundOverrideSurface.cs`, `SteamUiEndpointDiscovery.cs`, `SteamUiCdpConnection.cs` (internalize
  seams), `SteamUiBridge.cs` (internalize authorizer), `SteamUiExtension*.cs`, `SteamUiToolkit.csproj` (0.2.0); tests
  touching moved namespaces.
- Steps: merge builders (046); payload arity fixes (050); `Selectable` replaces `custom` (047); `SteamPageProbe`
  tokens list (049); side-menu/window caps removed and `KeyboardOpen` null (031/053); sound override record (058);
  namespace unify (056); delete `SteamUiText` (057); internalize seams and authorizer (063); extension reserved
  prefix, reparse check, manifest kept on rejection, no script cap (061/062, R7); remove documented caps and fix
  readback-wording docs (031/059/060); version 0.2.0.
- Tests: rejection theories per command shape; extension reserved prefix and junction escape (temp dir); side-menu
  parse of 40 windows.
- Filter: `--filter "FullyQualifiedName~SteamSurfaceModuleTests|FullyQualifiedName~SteamChoiceRowTests|FullyQualifiedName~SteamUiExtensionHostTests|FullyQualifiedName~SteamWindowSurfaceTests|FullyQualifiedName~SteamPagePatchTests"`.
- Dependencies: WSGM consumers (`NativeQamSemanticServices.cs`, `NativeQamPowerPresetService.cs`,
  `DevicePowerAssignments.cs`, `ShellSession.Actions.cs`, 6 page surfaces); TS domain for `selectable` in the preset
  row and removal of the `custom` filter.

**TOOLKITCS-B6 — File picker (R8)** — ~600 lines
- Files: `Surfaces/SteamFilePickerSurface.cs`, `Surfaces/SteamSurfaceJsonContext.cs`, new `SteamFilePickerTests.cs`.
- Steps: places and listings on a worker with caller cancellation and one fixed wait; typed outcomes; extended-path
  normalization; device-namespace refusal; Downloads via known folder; fixed refusal strings instead of
  `ex.Message`; listing returned whole to C#, paged by the renderer.
- Tests: temp-tree listing order, hidden/system skip, extension filter, device namespace refusal, `\\?\` and
  `\\?\UNC\` normalization, timeout with an internal blocking enumerator seam, cancellation.
- Filter: `--filter "FullyQualifiedName~SteamFilePicker"`.
- Dependencies: TS domain (`file-picker.ts` paging and unmount settle); maintainer decision Q1.

**TOOLKITCS-B7 — Test quality** — ~1200 lines
- Files: probe/surface tests listed in 064/065/069, `Fakes/RecordingBackend.cs`, `Fakes/TestJson.cs`,
  `Fakes/NodeScript.cs`, `SteamGatePatchContractTests.cs` (rewrite), new xUnit collection for log-sink tests.
- Steps: NodeScript runs of every gate probe against claimed/unclaimed/ambiguous/absent fixtures and of verify/remove
  predicates against status objects; delete getter-only tests; derive surface `Commands` from handlers and cover all
  surfaces; distinct fake labels, configurable results, delete `CollapseAsync`, reuse shared fake for folds;
  descriptive wait failures; `process.execPath`-style node resolution and stderr on timeout.
- Filter: whole child suite once (`dotnet test external/steam-ui-toolkit/tests/SteamUiToolkit.Tests`), since this
  batch only touches tests.
- Dependencies: none (Node already required by existing tests).

Order: B1 → B2 → B3 → B4 → B5 → B6 → B7 (B6 and B7 independent of each other after B5).

---

## 6. Risks and open questions

Risks
- B3 and B4 change behaviour seen only against live Steam (dispatch timing, bridge probe split, single sync loop).
  They need the manual Steam UI rows of the matrix after landing; offline tests cover only the state machines.
- Moving the QAM fingerprint off the bridge (036) lets the bridge install on clients where it previously refused;
  row patches must then refuse on their own, which their probes already do (`SteamQuickAccessRowPatch.cs:74-78`).
- Keeping the static log sink (R10) departs from the plan's injection wording; behaviour is unaffected.

Open questions for the maintainer
1. The B2 picker correction is recorded as binding. Accept the simpler shape in R8 (no spool, no worker caps, no
   `ProviderBusy`/`ProviderCapacity`, no policy type), or keep B2 as written?
2. Library badge before the first publication draws "Internal" (`SteamLibraryBadgeSurface.cs:25-28` default, gate
   pinned by `eng/check-library.mjs`). The plan says draw nothing; that is a visible change. Confirm.
