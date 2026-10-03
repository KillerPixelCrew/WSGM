# Steam UI toolkit scripts and surfaces ledger (U03A-SUTS, U03B-SUTS) findings

Scope: the 79 ids of the first Claude audit's units U03A-SUTS (33 ids: gates, surfaces, settings renderer and their
checks) and U03B-SUTS (46 ids: component host, page and library gates, UI kit, file picker, side-menu and window
surfaces). Every body was recovered from `claude-findings-raw.json`; none is missing. The provisional record PV09
(observations PV09-001 to PV09-034, from `reports/U03B-SUTS.wip.md`) only restates canonical U03B ids and closes
with them, so it gets no separate entry. No Codex A01/A02/A02S01 finding touches this scope.

Baseline: toolkit `main` 388dd1b, parent `master` 1329813f. Path shorthand: `Source/` is
`external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/`, `Surfaces/` is
`external/steam-ui-toolkit/src/SteamUiToolkit/Surfaces/`, `tk/` is `external/steam-ui-toolkit/src/SteamUiToolkit/`,
`tkt/` is `external/steam-ui-toolkit/tests/SteamUiToolkit.Tests/`, `toolkit/` is `external/steam-ui-toolkit/`.
Ledger line numbers are from the audit; lines given in new sections were re-read on the baseline. Anchor edits by
symbol.

Maintainer decisions (DECISIONS.md, 2026-10-03) are applied: D13 rewrites U03B-SUTS-016 (WSGM names every library,
no "Internal" label); the dropped security hardening trims U03B-SUTS-004 to its functional part (no device-namespace
refusal, exception text unchanged) and reduces U03A-SUTS-021 and U03B-SUTS-020 to construction checks against broken
probes. No section here depends on readback, so D9 changes nothing in this scope.

Counts: 71 sections (12 medium, 32 low, 27 nit) and 8 ids in the no-change list. 53 sections are fully covered
by a current domain finding (TOOLKITJS-*, TOOLKITCS-*, DEVICE-025) and only name it; 8 are covered with a small
residual fix written out (U03A-SUTS-009, 016, 019, 024, 029; U03B-SUTS-005, 016, 018); 10 are written in full because
no domain finding covers them. Plan v2 parks U03A-SUTS-008, 011, 012, 013, 017, 032, 033 and U03B-SUTS-025, 026,
028, 032, 034, 040, 043 in B178 "reconcile later"; all 14 were checked against the baseline here: 10 are still
valid (the full sections) and 4 need no change (U03A-SUTS-011, U03A-SUTS-012, U03A-SUTS-033, U03B-SUTS-025). Plan v2 batches that carry this area: B047 (check infrastructure), B049 (probe safety,
runtime, storage resolution), B050 (bridge and module resolution), B051 (gate lifecycle, content caps, missing
checks), B054 (patch contract), B055 (component host split), B056 (public surface cleanup), B057 (host-supplied
Quick Access presentation; decided D13: WSGM names every library on the badge), B058 (drafts and refusals), B059 (file picker), B060
(theme cache, carousel and small JS items), B062 (test quality), B093 (device host side of the preset sentinel)
and B177 (docs).

Every toolkit batch commits and pushes the child on `main` first, then one parent commit carries the gitlink, the
regenerated `src/WSGM/Core/SteamUiAssets/NativeQamBootstrap.js`, its hash in `src/WSGM/Core/SteamUiAssetCatalog.cs`
and any consumer edits. Toolkit validation for any JS change is `npm run prelude:claims` in `external/steam-ui-toolkit`,
then `npm run steam-assets:build`, `npm run steam-assets:check` and `npm run steam-assets:claims` in the parent.
The toolkit .NET filter form is
`dotnet test external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj --filter "..."`.

## Medium

### U03A-SUTS-001: Bluetooth removal can verify while Steam's stub methods are still claimed

- **Severity:** medium
- **Where:** `Source/gates/bluetooth.ts` `remove` (audit L153-170, domain review L318-335); `Surfaces/SteamBluetoothSurface.cs:192` (`!status.installed`).
- **Problem / solution:** covered by TOOLKITJS-008 (release every claim first, clear `installed` only after all releases succeed, mirroring `pages.ts` `remove`), with `assertRemoveRetries` in `check-service-gates.mjs`.
- **Plan v2:** B051.
- **Related:** TOOLKITJS-008, U03A-SUTS-002, U03A-SUTS-007, U03B-SUTS-001, U03B-SUTS-027.

### U03A-SUTS-002: Gates clear `installed` before cleanup, so a partial removal failure cannot be retried

- **Severity:** medium
- **Where:** `Source/gates/audio.ts` `remove` (L347-379), `Source/gates/performance.ts` (L118-146), `Source/gates/screensaver.ts` (L346-367), `Source/gates/storage.ts` (L317-335), `Source/gates/brightness.ts` (L174-191).
- **Problem / solution:** covered by TOOLKITJS-008 (same release-then-forget order in all ten gates, no shared lifecycle framework; storage's wrapper also passes through when `!installed`).
- **Plan v2:** B051.
- **Related:** TOOLKITJS-008, U03A-SUTS-001, U03A-SUTS-007, U03A-SUTS-008 (the network gate, not in TOOLKITJS-008's list), U02A-SUTC-002.

### U03A-SUTS-003: Storage probe and gate invoke every function export of the transport module

- **Severity:** medium in the ledger; the covering TOOLKITCS-001 rates it high.
- **Where:** `Surfaces/SteamStorageSurface.cs:174-184` (probe); `Source/gates/storage.ts:186-210` (`resolve`).
- **Problem / solution:** covered by TOOLKITCS-001 (C# probe) and TOOLKITJS-018 (gate): select the provider export by its own source text (`sourceMatches(fn, tokens)`, which never calls it), require exactly one, invoke only that one, once, at install; probe and gate share one token list in the same child commit. `GetDefaultTransport` alone is not the token list (a class export that defines it matches and must never be called); the tokens are read from the accessor's source on the current client by the maintainer as an attended precondition of B049 (by the transport module's literal id, never during a Steam cold start), not in B061.
- **Plan v2:** B049.
- **Related:** TOOLKITCS-001, TOOLKITJS-018, U03A-SUTS-018.

### U03A-SUTS-004: Performance gate turns an undecodable settings update into an empty, accepted delta

- **Severity:** medium
- **Where:** `Source/gates/performance.ts` `decodeSettingsUpdate` (L19-43, L90-91); `Surfaces/SteamPerformanceSurface.cs:366-375`.
- **Problem / solution:** covered by TOOLKITJS-V-004 (same batch, same file: the decoder refuses the update instead of returning `{}`), tested by the new `check-performance.mjs` from TOOLKITJS-V-003. Plan v2 Appendix B assigns it explicitly.
- **Plan v2:** B051.
- **Related:** TOOLKITJS-V-004, TOOLKITJS-V-003, U03A-SUTS-006, U03A-SUTS-013.

### U03A-SUTS-005: WSGM's `"custom"` preset sentinel is hard-coded in the reusable library

- **Severity:** medium
- **Where:** `Surfaces/SteamPowerPresetRow.cs:90`; `Source/components.ts:996-999, 1036, 1044`; consumers `src/WSGM/Shell/NativeQamPowerPresetService.cs:43-45`, `src/WSGM/Shell/DevicePowerAssignments.cs:78-79, 234`.
- **Problem / solution:** covered by TOOLKITCS-047 and TOOLKITJS-V-006 (a generic `Selectable = false` option flag with "listed only where it is the current value, never sent, no whole-state rejection" semantics; WSGM marks its custom option) inside TOOLKITJS-003's presentation contract. The device host side is DEVICE-025.
- **Plan v2:** B057 (library and WSGM consumer), B093 (DEVICE-025 host side).
- **Related:** TOOLKITCS-047, TOOLKITJS-V-006, TOOLKITJS-003, DEVICE-025, U03A-SUTS-016.

### U03A-SUTS-006: No emitted-asset behavioural checks for the audio, performance and network gates

- **Severity:** medium
- **Where:** `toolkit/eng/run-checks.mjs` (no audio, performance or network gate check); `toolkit/eng/check-startup.mjs:197-214` (network probe only); `toolkit/eng/check-service-gates.mjs:58` (`remove()` result ignored).
- **Problem / solution:** covered by TOOLKITJS-V-003 (new `check-audio.mjs`, `check-performance.mjs`, `check-network.mjs` with install, publication, the main command path and `assertRemoveRetries`) and TOOLKITJS-008 (every gate check asserts the remove result). Plan v2 Appendix B assigns it explicitly.
- **Plan v2:** B051.
- **Related:** TOOLKITJS-V-003, TOOLKITJS-008, U03A-SUTS-004, U03A-SUTS-008.

### U03B-SUTS-001: Four gates forget ownership before a fallible release; failed cleanup cannot be retried

- **Severity:** medium
- **Where:** `Source/gates/home-carousel.ts:502-518`, `Source/gates/library-badge.ts:340-350, 517-526`, `Source/gates/navigation.ts:376-388`.
- **Problem / solution:** covered by TOOLKITJS-008 (release first, then forget, in each gate).
- **Plan v2:** B051.
- **Related:** TOOLKITJS-008, U03B-SUTS-027, U03B-SUTS-016, STEAMHOST-033 (WSGM's own gates, same order defect, B139).

### U03B-SUTS-002: WSGM product policy and fixed English vocabulary inside the reusable Quick Access component host

- **Severity:** medium
- **Where:** `Source/components.ts:1898-1905, 2094-2096, 2216-2223` (Valve FPS rows hidden), `:2019-2063, 2149, 2196-2202` (sections), `:1133`, `:296-309, 1738-1744`.
- **Problem / solution:** covered by TOOLKITJS-003: a `steam-ui.quick-access-layout` publication (sections with ids, titles, icons, folds, kinds; `hideValveFpsRows`), accent descriptions built by WSGM, the restart sentence moved to WSGM's status text; generic row labels stay as toolkit defaults (R4). WSGM publishes today's exact strings, so the Quick Access looks identical.
- **Plan v2:** B057 (after B055 and B056).
- **Related:** TOOLKITJS-003, TOOLKITJS-V-007, U03B-SUTS-008, U03A-SUTS-015, U03B-SUTS-041.

### U03B-SUTS-003: `createNativeComponentHost` is a single 2,490-line closure with about 25 mutable bindings

- **Severity:** medium
- **Where:** `Source/components.ts:1-2490`.
- **Problem / solution:** covered by TOOLKITJS-004 (pure move into `qam-validators.ts`, `qam-hooks.ts`, `qam-rows.ts`, `qam-sections.ts`; `components.ts` keeps owner state).
- **Plan v2:** B055.
- **Related:** TOOLKITJS-004, U03B-SUTS-007, U03B-SUTS-018.

### U03B-SUTS-004: File picker lists synchronously on the bridge pump and a listing cannot be cancelled

- **Severity:** medium
- **Where:** `Surfaces/SteamFilePickerSurface.cs:63-103` (`ListPlaces`), `:109-161` (`ListFolder`), `:200-222` (handlers); `Source/file-picker.ts:154, 170`.
- **Problem / solution:** the functional part is covered by TOOLKITCS-055 (both handlers enumerate on a worker awaited with the request's cancellation, `\\?\X:\` and `\\?\UNC\` forms normalized to ordinary paths, Downloads from `FOLDERID_Downloads`), TOOLKITCS-V-002 (handlers off the bridge pump) and TOOLKITJS-V-008 (32 M delivery cap deleted). The audit's framing as an any-path listing open to every bridge client is a same-user threat and is dropped by maintainer decision (security theater, DECISIONS.md): TOOLKITCS-055's `\\.\`/`GLOBALROOT`/`\Device\`/`\??\` refusal and its fixed refusal strings in place of `ex.Message` are not done, so the page keeps showing today's text and the device-namespace refusal test goes. The ledger's entry cap, paging and UNC refusal are not adopted either: no arbitrary limits, no picker paging (DECISIONS.md), the picker keeps rendering the whole list (critic conflict 7), and reachable UNC keeps working.
- **Plan v2:** B059 (cap deletion in B056, pump change in B049).
- **Related:** TOOLKITCS-055, TOOLKITCS-V-002, TOOLKITJS-021, TOOLKITJS-V-008, U03B-SUTS-005, U03B-SUTS-023, U03B-SUTS-039.

### U03B-SUTS-005: File picker (C# and TS) and library capsule have no tests or emitted-asset checks

- **Severity:** medium
- **Where:** `Surfaces/SteamFilePickerSurface.cs`, `Source/file-picker.ts`, `Source/library-capsule.ts`.
- **Problem / solution:** the picker is covered by TOOLKITJS-021 (new `check-file-picker.mjs`) and TOOLKITCS-066 (new `tkt/SteamFilePickerTests.cs` in B059). The capsule (`library-capsule.ts`: `resolveSteamLibraryClasses` and `createSteamCapsule`, used by WSGM's library-import and artwork pages) has no domain finding. Residual: add capsule cases to `toolkit/eng/check-library.mjs`. Its badge class map (`classMap` in the badge section) carries only `LibraryItemBox`, `LibraryItemIcons` and `ControllerSupportIcon`, so the capsule cases build their own map with all seven `SteamLibraryClassNames` and a module whose source carries `SteamLibraryClassTokens`. Assert: `resolveSteamLibraryClasses` returns the seven classes from the complete map, and `null` when one name is missing or empty and when `findUnique` answers null; a `grid` capsule's box gets the box plus portrait class and a shine element, a `wide` one the landscape class and no shine; a capsule without `image` draws the placeholder text instead of an `img`. No source change.
- **Tests:** child `npm run prelude:claims`; parent `npm run steam-assets:claims`.
- **Plan v2:** B059 (picker), B051 (capsule cases, since B051 edits `check-library.mjs` for TOOLKITJS-008 anyway).
- **Related:** TOOLKITJS-021, TOOLKITCS-066, U03B-SUTS-004.

### U03B-SUTS-006: Probe JavaScript, the compatibility gate for every surface, is almost never executed offline

- **Severity:** medium
- **Where:** probe tests in `tkt/SteamHomeCarouselTests.cs`, `SteamLibraryBadgeTests.cs`, `SteamNavigationPanelTests.cs`, `SteamPageTests.cs`, `SteamGameContextMenuTests.cs`, `SteamThemeStyleTests.cs`, `SteamExtensionsTabTests.cs`.
- **Problem / solution:** covered by TOOLKITCS-064 (every probe runs through `tkt/Fakes/NodeScript.cs` against claimed, unclaimed, ambiguous, absent and already-owned models; token assertions stay secondary).
- **Plan v2:** B062.
- **Related:** TOOLKITCS-064, U03A-SUTS-018, U03B-SUTS-042, U03B-SUTS-026.

## Low

### U03A-SUTS-007: Brightness removal ignores a failed setter release, and verification does not check it

- **Severity:** low
- **Where:** `Source/gates/brightness.ts:129-138` (`restoreSetter`), `:182-190`; `Surfaces/SteamBrightnessSurface.cs:80`.
- **Problem / solution:** covered by TOOLKITJS-008 (brightness checks the release result it ignores today and keeps `installed` on failure). With release-then-forget, `!status.available` cannot pass while the setter is still ours, so no extra `setterOwned` term is needed.
- **Plan v2:** B051.
- **Related:** TOOLKITJS-008, U03A-SUTS-002.

### U03A-SUTS-008: Network removal ignores scan-release results and can throw before releasing the getter

- **Severity:** low
- **Where:** `Source/gates/network.ts:30-44` (`removeNetworkState`), `:162-168` (`unwrapScanning`), `:170-183` (`remove`); `Surfaces/SteamNetworkSurface.cs:94-95` (verify and removal predicates).
- **Problem:** `unwrapScanning` discards both `releaseMember` results and sets `scanWrapped = false`, so a failed release of `StartScanningForNetworks` or `StopScanningForNetworks` leaves WSGM's wrapper in Steam while the gate forgets it. `remove` also calls `removeNetworkState` before `releaseAccessor`, and `removeNetworkState` calls `instance.IsAnyDeviceConnected()` and `IsAnyDeviceConnecting()` outside any try. On a client where either method is missing or throws, `remove` throws on every attempt, the `networkManagementAvailable` getter on the store prototype stays overridden, and Steam's network page stays revealed with nothing driving it. This is the one claim-holding gate TOOLKITJS-008's list does not name.
- **Best solution:** apply the TOOLKITJS-008 order to `network.ts`, in place, no helper:
  1. `remove` returns `{ok: true, absent: true}` only when `!target && !scanWrapped`.
  2. If `scanWrapped`, release both scan members (`releaseMember` already answers ok for a member that is not claimed or a null host). On the first failure set `lastError` and return `{ok: false, error}` with `scanWrapped`, `target` and the subscription untouched. Delete `unwrapScanning`; its body moves here.
  3. If `target`, `releaseAccessor(target, property, availability)`; on failure the same early return.
  4. Only then: `scanWrapped = false`, `target = null`, `unsubscribe = endSubscription(unsubscribe)`, and the store cleanup (`removeNetworkState(true)`, which today runs before any release). In `removeNetworkState`, recompute `m_bIsConnectedToANetwork`/`m_bIsConnectingToANetwork` only when both methods are functions, inside `try`; a failure there sets `lastError` but does not fail removal, because the claims are already back and only our synthetic access points are being dropped.
  This removes state (the separate unwrap step) rather than adding any; the retry is the manager's next ordinary remove call, not an automatic loop.
- **Tests:** in the new `toolkit/eng/check-network.mjs` (TOOLKITJS-V-003): a store without `IsAnyDeviceConnected` removes cleanly and the prototype getter reads Steam's original afterwards; `assertRemoveRetries` with the `StopScanningForNetworks` release throwing once (first remove fails and `status().scanWrapped` stays true, second succeeds). Child `npm run prelude:claims`; toolkit filter `FullyQualifiedName~SteamSurfaceModuleTests` for the unchanged C# predicates.
- **Plan v2:** B051 (add `network.ts` to TOOLKITJS-008's gate list).
- **Related:** TOOLKITJS-008, TOOLKITJS-V-003, U03A-SUTS-002, U03A-SUTS-006.

### U03A-SUTS-009: XML-documented bounds contradict the gates and renderers, and the checks assert the contradiction

- **Severity:** low
- **Where:** `Surfaces/SteamPowerProfileRow.cs:11, 16`, `SteamHybridCoreRow.cs:15`, `SteamCpuBoostRow.cs:16`, `SteamScreensaverSurface.cs:12, 17-18, 34`, `SteamStorageSurface.cs` `ReadLabel` `<returns>` (audit L268-274).
- **Problem / solution:** covered by TOOLKITCS-031 (every documented-only cap is deleted from the XML docs and `toolkit/docs/reference.md`; the checks that accept 65 options and any number of rows already match the code) and TOOLKITJS-005 (the JS content caps go). The storage label line is not in TOOLKITCS-031's list: in the same pass rewrite `ReadLabel`'s `<returns>` to "The label, or empty when none was typed." since nothing bounds it and nothing should.
- **Plan v2:** B056 (docs), B051 (JS caps).
- **Related:** TOOLKITCS-031, TOOLKITJS-005, U03B-SUTS-012, U03A-SUTS-025.

### U03A-SUTS-010: Command payload checks do not follow the documented exact-shape rule consistently

- **Severity:** low
- **Where:** `Surfaces/SteamBrightnessSurface.cs:115-116`, `SteamFrameLimitRow.cs:150-162`, `SteamSurfaceModule.cs:120`, `SteamStorageSurface.cs:239-266`, `SteamNetworkSurface.cs:145-146`, `SteamPowerMenuSurface.cs:93`.
- **Problem / solution:** covered by TOOLKITCS-050 (exact property set and kinds per command, storage ids as `uint`, `setRefreshRate` stops requiring `persistence`). The ledger's value ranges (frame cap at most 1000, refresh rate ranges) are not added: type checks only; the backend clamps to what the device supports.
- **Plan v2:** B056.
- **Related:** TOOLKITCS-050, TOOLKITJS-014, U03A-SUTS-026.

### U03A-SUTS-013: The delta reader silently drops unknown top-level fields and leaves values unbounded

- **Severity:** low
- **Where:** `Surfaces/SteamPerformanceSurface.cs:260-266` (enum remarks: "reported as unsupported rather than silently dropped"), `:351-403` (`SteamPerformanceDeltaReader.TryRead`), `:409-456` (`ReadFields`); consumer `src/WSGM/Shell/NativeQamSemanticServices.cs:276-283` (logs `delta.Unsupported`).
- **Problem:** `ReadFields` reports unknown keys inside `settings_delta.global` and `per_app`, but `TryRead` never looks at other keys of the message itself or of `settings_delta`. A new top-level field or a new `settings_delta` section that a client update starts sending is dropped without the warning WSGM logs for unsupported fields, so a Performance-tab control that silently does nothing cannot be diagnosed from `wsgm.log`. The second half of the ledger item (values such as `fps_limit: 100000` pass unbounded) is not a defect under the no-arbitrary-limits rule: the reader type-checks, and the backend applies what the device supports.
- **Best solution:** in `TryRead`, after reading the known fields, enumerate `message` and add to `unsupported` every property whose value is not `null`/undefined and whose name is not `reset_to_default`, `gameid` or `settings_delta`; when `settings_delta` is an object, do the same for its properties other than `global` and `per_app` (reported as `settings_delta.<name>`). Same null skip as `ReadFields`, because `toObject()` emits unset fields as null or omits them. No range checks.
- **Tests:** `tkt/SteamPerformanceTests.cs`: a delta with `settings_delta.foo: 1` and a top-level `bar: true` lists both in `Unsupported`; a delta whose extra top-level field is `null` lists nothing. Toolkit filter `FullyQualifiedName~SteamPerformanceTests`. In the maintainer's manual pass, confirm that ordinary Performance-tab moves log no unsupported-field warning on the current client.
- **Plan v2:** B049 (it already edits `SteamPerformanceSurface.cs` for TOOLKITCS-051).
- **Related:** U03A-SUTS-004, U03A-SUTS-027, TOOLKITCS-051.

### U03A-SUTS-014: Settings drafts survive a refused change until the next revision

- **Severity:** low
- **Where:** `Source/settings.ts:390-398` (`SteamSettingsView`).
- **Problem / solution:** covered by TOOLKITJS-015 (`useSteamSettingDrafts`: `onChange` returns the request promise, a refusal drops that row's draft and shows the reason) and TOOLKITJS-V-009.
- **Plan v2:** B058.
- **Related:** TOOLKITJS-015, TOOLKITJS-V-009, U03B-SUTS-019.

### U03A-SUTS-015: Hard-coded English strings in the settings renderer

- **Severity:** low
- **Where:** `Source/settings.ts:165-168, 172, 181, 202, 295, 347, 355`.
- **Problem / solution:** the policy string ("Game override", `settings.ts:193-204`) is covered by TOOLKITJS-003 (WSGM builds the accented description). The generic chrome (Hue, Saturation, Lightness, Opacity, Cancel, Save, Edit, Move up, Move down) stays as it is: the domain review's R3 rejected a host label hook for generic chrome, because it is not product policy and changing it would change visible text (same disposition as U03B-SUTS-041).
- **Plan v2:** B057.
- **Related:** TOOLKITJS-003, U03B-SUTS-041, U03A-SUTS-028.

### U03A-SUTS-016: Surface factories repeat their boilerplate and are inconsistent with each other

- **Severity:** low
- **Where:** about 20 surface classes under `Surfaces/`; outliers `SteamPowerPresetRow.cs:50-95`, `SteamSettingsQuickAccessRow.cs:52-57`, `SteamSoundOverrideSurface.cs:43-49`, `SteamAudioSurface.cs:156`.
- **Problem / solution:** covered by TOOLKITCS-046 (delete `SteamSurfaceModule.cs`, every surface uses the public `SteamUiModuleBuilder`), TOOLKITCS-069 (`Commands` derived from each surface's handler table and checked by reflection over all surfaces), TOOLKITCS-047 (`SteamPowerPresetRow` gains `id` and `Serialize`) and TOOLKITCS-058 (`SteamSoundOverrideSurface` gains `id`). The one outlier none of them names: give `SteamSettingsQuickAccessRow.Module` the same optional `string id` parameter, defaulting to today's `settingsSections` so no module id changes. The builder's own `ArgumentNullException` checks replace the per-surface null checks.
- **Plan v2:** B056 (B062 for the Commands test, B057 for the preset row).
- **Related:** TOOLKITCS-046, TOOLKITCS-069, TOOLKITCS-047, TOOLKITCS-058, U03A-SUTS-005, U03A-SUTS-029.

### U03A-SUTS-017: The row-patch test fake never exercises the verification predicate

- **Severity:** low
- **Where:** `tkt/SteamQuickAccessRowPatchTests.cs:107-131` (`EveryRowKindIsDistinctAndSharesTheMountResource` lists 12 rows), `:134-188` (`RowClient`: every non-probe call answers `{"ok":true}`, unknown removals counted as `powerLimit`); `Surfaces/SteamQuickAccessRowPatch.cs:88-93` (verify and remove expressions), `:191-227` (`LogAppendOutcome`).
- **Problem:** the verify expression computes `status.ok && status.registered && status.hostVersion === 1 && status.performanceRootWrapped` inside JavaScript, but the fake answers `{"ok":true}` to it, so no test fails if that predicate, the remove predicate (`removed.ok && !status.registered`) or `LogAppendOutcome`'s reading of `status.lastAppend`, `renderOutcomes` and `toggleResolved` breaks. The distinct-kind test omits the CPU boost, hybrid core, power profile, power preset and settings-sections rows, so a duplicated component kind among those ships unnoticed.
- **Best solution:** three test-only changes plus two internal accessors, no production behaviour change:
  1. Expose the private `_verifyExpression` and `_removeExpression` as `internal string VerifyExpression` and `RemoveExpression`, as `SteamGatePatch` already exposes `ProbeExpression`, `VerifyOk` and `RemoveOk` to the tests.
  2. Add one Node test in the `SteamWindowSurfaceTests` pattern (`NodeScript.RunAsync` with the expressions on stdin, `vm` plus `node:assert`): a stub `window[<bridge namespace>].gate("nativeComponents")` whose `status(kind)` and `remove(kind)` answer per-case objects; evaluate the row's real expressions and assert the parsed `ok`. Keep `RowClient` for the manager tests (spawning Node per evaluate inside the manager loop is slow and not needed): give it a settable verify answer so one case returns `{"ok":true,"status":{...}}` with `lastAppend`, `renderOutcomes` and `toggleResolved` for the `LogAppendOutcome` line.
  3. Record removals by matching each declared `ComponentKind` (`bridge.remove("<kind>")`) in the expression instead of falling back to `powerLimit`, and build the distinct-kind row list by reflection over every public static `SteamQuickAccessRowPatch` property in the toolkit assembly (17 today, the same discovery TOOLKITCS-069 uses for surfaces). The `Assert.Single(...ResourceKey)` line goes with B054, which deletes `ResourceKey` (TOOLKITCS-026).
- **Tests:** in Node: verify `ok` is false for `registered: false`, for `hostVersion: 2` and for `performanceRootWrapped: false`, true for the compatible status; remove `ok` is false when `remove` answers ok but `status` still says registered, and the absent-bridge path answers `{ok:true,absent:true}`. In the manager test, the status carrying `lastAppend` and `renderOutcomes` produces the append log line through the test log sink. Toolkit filter `FullyQualifiedName~SteamQuickAccessRowPatchTests`.
- **Plan v2:** B062 ("verify/remove predicates against status objects").
- **Related:** TOOLKITCS-064, TOOLKITCS-069, U03A-SUTS-016, U03A-SUTS-020.

### U03A-SUTS-018: Brittle source-text and marker-slice assertions

- **Severity:** low
- **Where:** `tkt/SteamPowerMenuTests.cs:14-18`, `SteamScreensaverTests.cs:22-42`, `SteamStorageTests.cs:31-38`; `toolkit/eng/check-power-profile.mjs:83, 146, 220, 239, 313, 374, 494-495`; `check-settings-fields.mjs:90`.
- **Problem / solution:** covered by TOOLKITCS-064 (probes executed through NodeScript; substring checks kept only as secondary guards) and TOOLKITJS-011 (checks instantiate whole fragments by `// @fragment` marker instead of slicing between declarations).
- **Plan v2:** B062 (C#), B047 (JS checks).
- **Related:** TOOLKITCS-064, TOOLKITJS-011, U03B-SUTS-006, U03B-SUTS-042.

### U03A-SUTS-019: Sound overrides drop invalid entries silently, and their limits are untested

- **Severity:** low
- **Where:** `Source/gates/sound-overrides.ts:30-70` (`reconcile`); `toolkit/eng/check-sound-overrides.mjs`; `Surfaces/SteamSoundOverrideSurface.cs` (no C# test).
- **Problem / solution:** the 128/16/1.4 MB/24 MB caps are deleted by TOOLKITJS-005 (so there are no limits left to test), and C# coverage of the surface comes from TOOLKITCS-066. Residual not in either: a name failing the file-name regex, a non-array value or a URL failing the data-URL validator still `continue`s without a trace. Set `lastError = \`Rejected sound: ${name}\`` at each of those `continue`s, guarded by `current === generation && installed` exactly as the decode failure's `Unreadable sound: ${name}` is (the URL loop runs after awaits, so an unguarded write from a superseded reconcile would overwrite the newer one's status); no new state. The lowercase-only extension regex stays: Steam's stock resource names are lowercase and `toolkit/docs/sound-overrides.md` names the same four formats.
- **Tests:** `check-sound-overrides.mjs`: an entry with an invalid name and one with a malformed URL each leave the valid entries loaded and set `status().lastError`; a publication that supersedes an in-flight one wins (generation check).
- **Plan v2:** B051 (JS), B062 (C# surface test).
- **Related:** TOOLKITJS-005, TOOLKITCS-066, U03A-SUTS-031.

### U03A-SUTS-020: `SteamNativeSurfaceCommands.ReplayAsync` has no test

- **Severity:** low
- **Where:** `Surfaces/SteamNativeSurfaceCommands.cs:33-48`.
- **Problem / solution:** covered by TOOLKITCS-066 (`ReplayAsync` against `FakeSteamUiTransport`: ready, not ready, stale generation, false result).
- **Plan v2:** B062.
- **Related:** TOOLKITCS-066, U03A-SUTS-024.

### U03A-SUTS-021: `SteamQuickAccessRowPatch` interpolates `primaryCountName` raw into evaluated JavaScript

- **Severity:** low
- **Where:** `Surfaces/SteamQuickAccessRowPatch.cs:50-81, 164-166`.
- **Problem / solution:** covered by TOOLKITCS-048 (constructor requires an identifier, `^[A-Za-z_$][A-Za-z0-9_$]*$`, no length limit). Only the functional part counts: a name that is not an identifier yields a probe expression that does not parse, so the row never installs. Script injection by the host into its own probe is a same-user threat and is not a reason for the check (security hardening dropped, DECISIONS.md).
- **Plan v2:** B049.
- **Related:** TOOLKITCS-048, U03B-SUTS-020.

### U03B-SUTS-007: `nativeRowsHidden` diagnostic systematically undercounts

- **Severity:** low
- **Where:** `Source/components.ts:1924-1951` (`hideNativeRows`), `:1973-1979`.
- **Problem / solution:** covered by TOOLKITJS-031 (count per wrapper render inside `createValveFpsFilter` and publish after render).
- **Plan v2:** B060 (after the B055 move).
- **Related:** TOOLKITJS-031, TOOLKITJS-004.

### U03B-SUTS-008: Fold ids are English display titles and are persisted by WSGM

- **Severity:** low
- **Where:** `Source/components.ts:2070-2089`; `Surfaces/SteamPanelFoldsSurface.cs:9-19`; `src/WSGM/Core/QuickAccessFolds.cs:26-30`.
- **Problem / solution:** covered by TOOLKITJS-003: the layout publication carries section ids as opaque strings and WSGM publishes today's exact titles as those ids, so saved folds keep working with no migration (review R1, critic conflict 8; STEAMHOST-045 is recorded as no change for the same reason).
- **Plan v2:** B057.
- **Related:** TOOLKITJS-003, STEAMHOST-045, U03B-SUTS-002.

### U03B-SUTS-009: Route bounds are documented but not implemented, and a test pins the opposite

- **Severity:** low
- **Where:** `toolkit/docs/reference.md:13-17`; `Surfaces/SteamRouteNavigation.cs:17-34`; `Surfaces/SteamNavigationPanelSurface.cs:29-30`; `Source/gate-helpers.ts:361`; `tkt/SteamRouteNavigationTests.cs`.
- **Problem / solution:** covered by TOOLKITJS-035 (JS refuses control characters, as C# does; no length limit) and TOOLKITCS-031 (the documented 256-character route cap is deleted from the XML and `reference.md`). `ALongRouteIsStillNavigable` is then correct and stays.
- **Plan v2:** B057 (JS), B056 (docs).
- **Related:** TOOLKITJS-035, TOOLKITCS-031.

### U03B-SUTS-010: Overlay activation map never prunes and its overflow flag is sticky

- **Severity:** low
- **Where:** `Surfaces/SteamOverlayActivationPatch.cs:28-39`; `Surfaces/SteamSideMenuSnapshot.cs:71-73`.
- **Problem / solution:** covered by TOOLKITCS-054 (delete `overflow` and the 32-identity check; a malformed callback affects only its own identity).
- **Plan v2:** B049.
- **Related:** TOOLKITCS-054, TOOLKITCS-031, U03B-SUTS-011.

### U03B-SUTS-011: `SteamWindowSideMenu.KeyboardOpen` defaults to `false` although unknown is null

- **Severity:** low
- **Where:** `Surfaces/SteamSideMenuSnapshot.cs:28-34`.
- **Problem / solution:** covered by TOOLKITCS-053 (`bool? KeyboardOpen = null`).
- **Plan v2:** B056.
- **Related:** TOOLKITCS-053, U03B-SUTS-010.

### U03B-SUTS-012: Theme-style limits are documented but enforced nowhere

- **Severity:** low
- **Where:** `Surfaces/SteamThemeStyleSurface.cs:9-22`; `toolkit/docs/reference.md` (around L1524-1525).
- **Problem / solution:** covered by TOOLKITCS-031 (the documented theme caps are deleted, not enforced; the gate installs every theme however large, TOOLKITJS-023).
- **Plan v2:** B056.
- **Related:** TOOLKITCS-031, TOOLKITJS-023, U03A-SUTS-009.

### U03B-SUTS-013: Docs and comments still describe 2-second theme polling the gate no longer does

- **Severity:** low
- **Where:** `toolkit/docs/reference.md` (around L1549-1550); `Source/gates/theme-styles.ts:171-172`.
- **Problem / solution:** covered by TOOLKITJS-023 (the comment) and TOOLKITCS-060 (the XML and reference text).
- **Plan v2:** B060 (comment), B056 (docs).
- **Related:** TOOLKITJS-023, TOOLKITCS-060.

### U03B-SUTS-014: The documented "router-backstack" Route fallback does not exist

- **Severity:** low
- **Where:** `Surfaces/SteamPageSurface.cs:53-56`; `Source/gates/pages.ts:20-23` (header).
- **Problem / solution:** covered by TOOLKITCS-060 (delete the claim from the XML remarks). The same false sentence in the `pages.ts` header is deleted in B051 with TOOLKITJS-006's `pages.ts` comment rewrite. The fallback is not implemented: nothing shows a client that needs it.
- **Plan v2:** B056, B051 (header comment).
- **Related:** TOOLKITCS-060, TOOLKITJS-006.

### U03B-SUTS-015: Theme targets are compiled as host-supplied RegExps on Steam's UI thread; the pattern cache is unbounded

- **Severity:** low
- **Where:** `Source/gates/theme-styles.ts:55, 148-157, 295-309`.
- **Problem / solution:** the cache part is covered by TOOLKITJS-023 (`remove` clears the pattern cache after its releases succeed). The ReDoS part stays a hypothesis and gets no change (hardening against the host's own patterns is also dropped by maintainer decision, DECISIONS.md): the patterns come from CSSLoader theme manifests and CSSLoader compiles the same patterns itself; a time budget or pattern limit would be new mechanism without an observed defect.
- **Plan v2:** B060.
- **Related:** TOOLKITJS-023, U03B-SUTS-012.

### U03B-SUTS-016: The library badge labels every installed game "Internal" when no publication exists

- **Severity:** low
- **Where:** `Source/gates/library-badge.ts:38-57` (`readLibraryBadgeState`, default at L54-55), `:62-71` (`libraryForOverview`), header comment L23-29; `Surfaces/SteamLibraryBadgeSurface.cs:18-28` (`InternalLabel = "Internal"`); `toolkit/eng/check-library.mjs:161-166, 174, 357`; `tkt/SteamLibraryBadgeTests.cs:115-125`; `toolkit/README.md:88-90`, `toolkit/docs/reference.md:879`; WSGM `src/WSGM/Shell/LibraryBadges.cs:63-74` (`Build` publishes the tracked cards plus `"Internal"`), `src/WSGM/Core/SteamLibraryVdf.cs` `ReadEntries`/`ConfigEntry`.
- **Problem:** the toolkit invents a product label, and one "Internal" label for every game outside a tracked card is wrong when several internal libraries exist (desktop PCs with games on more than one disk). Decided by D13: WSGM shows the name of the library that holds the game, and the toolkit holds no label of its own. This is a visible change: a game on an untracked Steam library shows that library's name instead of "Internal". TOOLKITJS-025 covers only the toolkit default and still has WSGM supply "Internal", so the WSGM half is written here.
- **Best solution:**
  1. Toolkit (TOOLKITJS-025, widened): remove the internal fallback, not only its default. `SteamLibraryBadgeState` loses `InternalLabel` (`(Libraries, Revision = 0)`), `readLibraryBadgeState` returns `{libraries, count}`, and `libraryForOverview` returns null when no published library holds the app, so such a game draws no badge, before WSGM's first publication as well. Rewrite the gate's header comment, the record's XML remarks, `toolkit/README.md` and `reference.md` to say the host publishes every library it wants named.
  2. WSGM: `LibraryBadges.Build` publishes every Steam library, not only the tracked cards. Tracked card libraries stay exactly as today (marker name, presence by content id, the config's app ids, absent cards kept). Each top-level `libraryfolders.vdf` registration whose content id is not a tracked card becomes one more entry: its name is Steam's `label` for that folder when non-empty (the Shell AGENTS rule forbids that label only for naming a card), otherwise the folder's drive as `C:` (`SteamLibraryVdf.VolumeRoot(path)` without the trailing backslash); `Connected` is true, so `HomeCarousel.Build` excludes nothing it does not exclude today; its app ids are the keys of that registration's `apps` block. `ConfigEntry` gains `Label` and `AppIds`, read by `ReadEntries` from the entry's own block (the nested `apps` block tracked by depth), keeping the per-block pairing the method exists for. `Build` takes the vdf text as a parameter (null when Steam has none: only the cards are published); `Update` reads it with `Steam.TryReadLibraryFolders` on the existing sync triggers. No new watcher: a game installed since the last sync draws no badge until the next one, where today it reads "Internal". Two registrations with the same name stay two entries; nothing deduplicates by name.
- **Tests:** `check-library.mjs`: an installed app that no published library holds draws no badge, with and without a publication; the `internalLabel: "Claw"` fixtures go. `tkt/SteamLibraryBadgeTests.cs`: the serialized state carries no `internalLabel`. `tests/WSGM.Tests/Shell/LibraryBadgesTests.cs`: a vdf with a labelled and an unlabelled internal folder plus a tracked card publishes three libraries, the card under its marker name even when its vdf label differs, the unlabelled folder as its drive letter, each with its `apps` ids; a null vdf publishes the cards only; `HomeCarouselTests` unchanged. Toolkit filter `FullyQualifiedName~SteamLibraryBadgeTests`; WSGM filter `FullyQualifiedName~LibraryBadges|FullyQualifiedName~HomeCarousel|FullyQualifiedName~CardNameAuthority`; child `npm run prelude:claims`, parent `npm run steam-assets:claims`. Manual: on the handheld and on a desktop with two Steam libraries, focus a tile from each library and read its name; the maintainer confirms the drive-letter fallback text.
- **Plan v2:** B057, decided: D13, WSGM names every library on the badge (visible change, no "Internal").
- **Related:** TOOLKITJS-025, U03B-SUTS-001, U03B-SUTS-005 (also edits `check-library.mjs`).

### U03B-SUTS-017: The home carousel's `status()` walks up to 250,000 fibers on every verify

- **Severity:** low
- **Where:** `Source/gates/home-carousel.ts:538`.
- **Problem / solution:** covered by TOOLKITJS-024 (stale count computed during the adoption walk and cached; `status` returns cached values). The 250,000 walk bound itself stays (TOOLKITJS-005).
- **Plan v2:** B060.
- **Related:** TOOLKITJS-024, U03B-SUTS-046.

### U03B-SUTS-018: The settings-sections Quick Access control trusts its payload and renders null without a diagnostic

- **Severity:** low
- **Where:** `Source/components.ts:1188-1214` (validator L1190-1191, `return null` L1196).
- **Problem / solution:** the missing diagnostic is covered by TOOLKITJS-031 (`note("settingsSections", ...)` before returning null). Residual type check (allowed: type checks only, nothing dropped or truncated): today's selector checks only `Array.isArray(value.pages)` and the revision, and the render then reads `page.id`, `(page.sections ?? []).map` and `row.key`, so a null page or a non-array `sections` throws inside Steam's Performance panel render. Move the selector into `qam-validators.ts` as `normalizeSettingsSectionsState`, beside the other `normalize*State` functions, and make it also require every page to be an object with a string `id`, `sections` absent or an array of objects, each section's `rows` absent or an array of objects with a string `key`; anything else returns null for the whole state, and the existing null path carries TOOLKITJS-031's single `note("settingsSections", "no sections")`. No per-field reasons: the C# side serializes the typed `SteamSettingsQuickAccessState`, so this only turns a render throw into the same visible outcome as no state.
- **Tests:** `check-power-profile.mjs`: a page without `id` and a row without `key` each render nothing and report the note; a valid state renders as today.
- **Plan v2:** B060.
- **Related:** TOOLKITJS-031, TOOLKITJS-004.

### U03B-SUTS-019: Row command refusals are swallowed inconsistently

- **Severity:** low
- **Where:** `Source/components.ts:229, 919-921, 983-985, 1049-1051, 1131, 1169, 1244-1246, 1317-1319, 1647-1648`.
- **Problem / solution:** covered by TOOLKITJS-015 and TOOLKITJS-016 (one `sendPending` helper; a refusal shows in the row description, never truncated), named explicitly in plan v2's B058 steps.
- **Plan v2:** B058.
- **Related:** TOOLKITJS-015, TOOLKITJS-016, TOOLKITJS-031, U03B-SUTS-031, U03A-SUTS-014.

### U03B-SUTS-020: `SteamPagePatch.Create` interpolates raw JavaScript from a public record

- **Severity:** low
- **Where:** `Surfaces/SteamPagePatch.cs:10, 69-71`.
- **Problem / solution:** covered by TOOLKITCS-049 as it now reads: `Tokens` stays the JavaScript array literal (it has the same type as every public token constant in `SteamUiProbeJs`, and every value is compile-time code, not untrusted input); `SteamPagePatch.Create` validates each probe name as an identifier and refuses duplicate names with `ArgumentException`, since a duplicate key silently overwrites a count in the probe's JSON and a bad name breaks the probe. The six WSGM page surfaces use only the static `SteamPageProbe` members and need no edit. Escaping against injected script is not pursued (security hardening dropped, DECISIONS.md).
- **Plan v2:** B056.
- **Related:** TOOLKITCS-049, U03A-SUTS-021.

### U03B-SUTS-023: The file picker promise may never settle when the modal is closed another way

- **Severity:** low
- **Where:** `Source/file-picker.ts:19-31, 200-205`.
- **Problem / solution:** covered by TOOLKITJS-021 (an unmount effect settles null once; `settle` is idempotent).
- **Plan v2:** B059.
- **Related:** TOOLKITJS-021, U03B-SUTS-004.

### U03B-SUTS-024: The controller row disappears when neither an observed nor a selected target is published

- **Severity:** low
- **Where:** `Source/components.ts:1117-1122`.
- **Problem / solution:** covered by TOOLKITJS-002 (the dropdown draws with no selection instead of a note; no row vanishes for missing readback).
- **Plan v2:** B057.
- **Related:** TOOLKITJS-002.

### U03B-SUTS-026: Emitted-asset check fixtures cannot express non-uniqueness, and navigation adoption is never exercised

- **Severity:** low
- **Where:** `toolkit/eng/check-home-carousel.mjs:189-198`, `check-library.mjs:98-110`, `check-navigation-panel.mjs:62-91`, `check-pages.mjs:75-83` (each `findUnique` stub returns a module id for every token set, never `null`); `check-navigation-panel.mjs` defines no `document`, so `reactRootFibers()` returns `[]` and the navigation gate's `createMountedAdoption` and `createSourceAdoption` paths never run. Model to follow: `check-extension-surfaces.mjs:114-129` (`moduleFor` returning null).
- **Problem:** each gate's "was not a unique match" refusal (baseline `navigation.ts:290`, `home-carousel.ts:431`, `library-badge.ts:253` and the later lookups in the same `resolve`, `pages.ts:206`; anchor by the message text) is untested (`check-library.mjs`'s details-stat section already uses a `moduleFor` table, L294; its badge section at L103 does not), so a refactor that inverts or drops one installs a gate against the wrong or a missing module without any check failing. The navigation gate's adoption of the mounted menu and of the main-menu popup host, which is how a claim reaches Big Picture's already-mounted UI, has no offline coverage at all.
- **Best solution:** give each of these checks a token table instead of a catch-all: `moduleFor(tokens)` returns the fixture module id only for the gate's real tokens and `null` otherwise, and `findUnique` returns `null` when `moduleFor` does (the real `findUnique` contract in `module-resolver.ts:53-56`, which answers null for both absent and ambiguous). Add one case per gate where the gate's primary module lookup answers null: install returns `{ok: false}` with the gate's "not a unique match" error and nothing is claimed. Give `check-navigation-panel.mjs` a `document` fixture whose root container holds a fiber tree with one mounted instance of the menu memo and one fiber whose source carries the popup-host tokens, then assert `status()` reports both adopted, a publication asks them to render, and `remove()` hands both back.
- **Tests:** the cases above, run by child `npm run prelude:claims` and parent `npm run steam-assets:claims`.
- **Plan v2:** B051 (it edits these checks for `assertRemoveRetries`).
- **Related:** U03B-SUTS-027, U03B-SUTS-034, U03B-SUTS-006, TOOLKITJS-008.

### U03B-SUTS-027: Failed-release retry is checked only for the Extensions tab

- **Severity:** low
- **Where:** `toolkit/eng/check-extension-surfaces.mjs:463-471` (present); absent from `check-home-carousel.mjs`, `check-library.mjs`, `check-navigation-panel.mjs`, `check-pages.mjs`.
- **Problem / solution:** covered by TOOLKITJS-008 (`assertRemoveRetries(gate, failOnce)` in `check-harness.mjs`, used by every gate check).
- **Plan v2:** B051.
- **Related:** TOOLKITJS-008, U03B-SUTS-001.

### U03B-SUTS-028: Game context menu: one rejection value in C#, one happy path in the emitted check

- **Severity:** low
- **Where:** `tkt/SteamGameContextMenuTests.cs:53-73` (`ActivationCarriesTheExactSteamAppAndRejectsOtherShapes`, only `appId: 0`); `Surfaces/SteamGameContextMenuSurface.cs:111-125` (`TryReadActivation`); `toolkit/eng/check-extension-surfaces.mjs:480-516`; `Source/gates/game-context-menu.ts:33-42` (`appIdFor`), `:104-108` (placement), `:96-98` (route answer).
- **Problem:** the reader refuses strings, negatives, values above uint32, a missing or empty id and surplus fields, but only `appId: 0` is tested, so loosening the exact-shape check would pass. On the page, a multi-app menu (`GetTargetApps().length !== 1` draws no host items), navigation to a route the host answers with, the append fallback when no `AppProperties` item exists, and removal after a failed release are untested.
- **Best solution:** turn the C# test into a theory over the refused payloads: `{"appId":"480","id":"x"}`, `{"appId":-1,"id":"x"}`, `{"appId":4294967296,"id":"x"}`, `{"appId":480}`, `{"appId":480,"id":""}`, `{"appId":480,"id":"x","extra":1}`, each answering "The game context menu activation payload is invalid." with no backend call, following `SteamExtensionsTabTests`' rejection theory. In `check-extension-surfaces.mjs` add: a menu instance whose `GetTargetApps` returns two apps renders Steam's menu unchanged; an activation answered with `{route: "/x"}` calls `navigateSteamRoute("/x")`; a menu without an `AppProperties` item gets the host items appended; `assertRemoveRetries` on the gate (from TOOLKITJS-008).
- **Tests:** toolkit filter `FullyQualifiedName~SteamGameContextMenuTests`; child `npm run prelude:claims`.
- **Plan v2:** B062 (C# theory), B051 (JS check cases).
- **Related:** TOOLKITCS-064, TOOLKITJS-008, U03B-SUTS-040.

## Nit

### U03A-SUTS-022: Patch `Version` is always 1 while fingerprints carry v2/v3

- **Severity:** nit
- **Where:** `Surfaces/SteamQuickAccessRowPatch.cs:104`, `Surfaces/SteamGatePatch.cs:99`, `tk/SteamUiPatchManager.cs:1103`.
- **Problem / solution:** covered by TOOLKITCS-032 (`ISteamUiPatch.Version`, `SteamUiPatchSnapshot.Version` and the `v{n}` log text are deleted; the fingerprint carries the revision).
- **Plan v2:** B054.
- **Related:** TOOLKITCS-032.

### U03A-SUTS-023: PowerMenu `enabled` doc says it gates installation

- **Severity:** nit
- **Where:** `Surfaces/SteamPowerMenuSurface.cs:73`.
- **Problem / solution:** covered by TOOLKITCS-060 (doc fixed to "gates publication").
- **Plan v2:** B056.
- **Related:** TOOLKITCS-060, U03A-SUTS-012.

### U03A-SUTS-024: `SteamNativeSurfaceCommands` has a namespace, exception and timeout nit

- **Severity:** nit
- **Where:** `Surfaces/SteamNativeSurfaceCommands.cs:6` (namespace), `:44` (`TimeSpan.FromSeconds(2)`), `:52-55` (`ArgumentOutOfRangeException(nameof(action))`).
- **Problem / solution:** the namespace is covered by TOOLKITCS-056. Residual exception: `CreateExpression` throws `ArgumentOutOfRangeException(nameof(action))` also for a zero process id with a non-zero app id, which names the wrong argument. Split the guard: an undefined `action` keeps `ArgumentOutOfRangeException(nameof(action))`; `processId == 0 && appId != 0` throws `ArgumentException("An overlay app needs its overlay process id.", nameof(processId))`, naming the argument that is missing. The 2 s timeout stays a local constant: `SteamUiPatchBounds` is deleted in B054 (TOOLKITCS-031/032), so there is no shared bound to take it from.
- **Tests:** `tkt/SteamWindowSurfaceTests.cs`: `CreateExpression(QuickAccess, 0, 480)` throws `ArgumentException` with `ParamName == "processId"`, and an undefined action value still throws `ArgumentOutOfRangeException` naming `action`. Toolkit filter `FullyQualifiedName~SteamWindowSurfaceTests`.
- **Plan v2:** B056.
- **Related:** TOOLKITCS-056, U03A-SUTS-020, U03B-SUTS-029.

### U03A-SUTS-025: Screensaver row-id regex differs between C# and TypeScript

- **Severity:** nit
- **Where:** `Surfaces/SteamScreensaverSurface.cs:94, 189-190`; `Source/gates/screensaver.ts:86`.
- **Problem / solution:** covered by TOOLKITCS-052 (`\z` end anchor). The documented 32-character limit is deleted (TOOLKITCS-031), not enforced.
- **Plan v2:** B049.
- **Related:** TOOLKITCS-052, TOOLKITCS-031, U03A-SUTS-009.

### U03A-SUTS-026: Storage `TryReadId` caps uint32 ids at `int.MaxValue`

- **Severity:** nit
- **Where:** `Surfaces/SteamStorageSurface.cs:294-304`.
- **Problem / solution:** covered by TOOLKITCS-050 (storage ids read as `uint`).
- **Plan v2:** B056.
- **Related:** TOOLKITCS-050, U03A-SUTS-010.

### U03A-SUTS-027: `ReadAppId` uses a culture-sensitive parse

- **Severity:** nit
- **Where:** `Surfaces/SteamPerformanceSurface.cs:505`.
- **Problem / solution:** covered by TOOLKITCS-051 (`NumberStyles.None`, `CultureInfo.InvariantCulture`).
- **Plan v2:** B049.
- **Related:** TOOLKITCS-051, U03A-SUTS-013.

### U03A-SUTS-028: WSGM product name appears in reusable-library docs and comments

- **Severity:** nit
- **Where:** `Surfaces/SteamSettingsRows.cs:86-87`; `Source/settings.ts:193`.
- **Problem / solution:** covered by TOOLKITCS-060 (C# doc) and TOOLKITJS-026 (TS comments, together with the other product names it lists).
- **Plan v2:** B056 (C#), B051 (TS).
- **Related:** TOOLKITCS-060, TOOLKITJS-026.

### U03A-SUTS-029: Formatting and style are inconsistent within the scope

- **Severity:** nit
- **Where:** compressed parameter lists in `Surfaces/SteamCpuBoostRow.cs:47-50, 66-76`, `SteamHybridCoreRow.cs`, `SteamPowerProfileRow.cs`, `SteamPowerPresetRow.cs`, `SteamSettingsQuickAccessRow.cs`, `SteamSoundOverrideSurface.cs`; `Source/gates/sound-overrides.ts` (2-space indentation, `install` answers `installed: true` when already installed at L77); `Surfaces/SteamSoundOverrideSurface.cs:25` (chunk label).
- **Problem / solution:** the chunk label is covered by TOOLKITCS-058 (`steam_ui_sound_overrides_probe_`). The 2-space TypeScript is no change (TOOLKITJS-027: toolkit AGENTS.md asks to respect local style and avoid unrelated formatting). The compressed C# lists are rewritten anyway when B056 moves every `Module` onto the public builder (TOOLKITCS-046); lay the rewritten calls out in the expanded style the other surfaces use, with no separate restyle pass (WSGM's Rider gate excludes `external/`). In `sound-overrides.ts` `install`, the already-installed answer becomes `{ok: true, alreadyInstalled: true}` like every other gate.
- **Tests:** compile toolkit and parent; child `npm run prelude:claims` (add one `check-sound-overrides.mjs` assertion that a second `install()` answers `alreadyInstalled: true`).
- **Plan v2:** B056 (C#), B051 (`sound-overrides.ts`).
- **Related:** TOOLKITCS-058, TOOLKITCS-046, TOOLKITJS-027, U03A-SUTS-031.

### U03A-SUTS-030: The default `SetUnifiedModeAsync` refusal gives a misleading reason

- **Severity:** nit
- **Where:** `Surfaces/SteamPowerLimitSurface.cs:54-57`.
- **Problem / solution:** covered by TOOLKITCS-042 (the default returns "Mode selection is not supported on this device.").
- **Plan v2:** B049.
- **Related:** TOOLKITCS-042.

### U03A-SUTS-031: `SteamSoundOverrideState` exposes a mutable `string[]`

- **Severity:** nit
- **Where:** `Surfaces/SteamSoundOverrideSurface.cs:10`.
- **Problem / solution:** covered by TOOLKITCS-058 (`IReadOnlyList<string>`).
- **Plan v2:** B056.
- **Related:** TOOLKITCS-058, U03A-SUTS-029.

### U03A-SUTS-032: Toolkit `AGENTS.md` check list omits checks in this scope

- **Severity:** nit
- **Where:** `toolkit/AGENTS.md:39-45` (the `eng/check-*.mjs` bullet) versus `toolkit/eng/run-checks.mjs:10-27`.
- **Problem:** the guidance's check map names 12 of the 16 checks `run-checks.mjs` runs on the baseline: `check-power-menu`, `check-sound-overrides`, `check-theme-styles` and `check-ui-kit` are missing (U03B-SUTS-044 is the same item). B051 and B059 add `check-audio`, `check-performance`, `check-network` and `check-file-picker`, so a hand-kept list in the guidance will drift again. TOOLKITJS-012 removes the hand list from `run-checks.mjs` but leaves the guidance text as it is.
- **Best solution:** in the same child commit as TOOLKITJS-012, replace the enumerated list in that AGENTS.md bullet with the rule itself: "`eng/check-*.mjs`: the emitted-asset checks. `run-checks.mjs` runs every `check-*.mjs` except `check-harness.mjs`; each file's header comment says what it covers." Every check file then needs such a header: on the baseline `check-extension-surfaces.mjs` and `check-startup.mjs` start with imports, and `check-sound-overrides.mjs` has a one-line header that does not say what it covers; give those three a header carrying what the AGENTS.md map says about them today (the Extensions tab and game context menu; module resolver, component host, network probe and bridge replay; sound override fixtures over the member-ownership primitives). The toolkit has only `AGENTS.md` (no `CLAUDE.md`); B047's file list does not name it, so add it there. Keeping a list and adding the four names would beat this only if the list carried information the headers do not, and it does not.
- **Tests:** none (guidance). Child `npm run prelude:claims` still runs every check.
- **Plan v2:** B047.
- **Related:** U03B-SUTS-044, TOOLKITJS-012.

### U03B-SUTS-029: Namespace split within one folder

- **Severity:** nit
- **Where:** `Surfaces/SteamGameWindowActivation.cs:7`, `SteamOverlayActivationPatch.cs:5`, `SteamRouteNavigation.cs:7`, `SteamSideMenuSnapshot.cs:8`.
- **Problem / solution:** covered by TOOLKITCS-056 (all move to `SteamUiToolkit`; the WSGM `using` lines go).
- **Plan v2:** B056.
- **Related:** TOOLKITCS-056, U03A-SUTS-024.

### U03B-SUTS-030: A disposed component host refuses with "component is not allowlisted"

- **Severity:** nit
- **Where:** `Source/components.ts:2446-2447`.
- **Problem / solution:** covered by TOOLKITJS-031 ("component host disposed").
- **Plan v2:** B060.
- **Related:** TOOLKITJS-031.

### U03B-SUTS-031: The power-limit error shows "Error: ..." and wraps `String()` in a redundant `normalizeText`

- **Severity:** nit
- **Where:** `Source/components.ts:1556, 1593`.
- **Problem / solution:** covered by TOOLKITJS-031 (`reason?.message ?? String(reason)`).
- **Plan v2:** B060.
- **Related:** TOOLKITJS-031, U03B-SUTS-019.

### U03B-SUTS-032: Probe predicate style differs between gates (`Flag("claimable")` versus `ClaimableOrOurs`)

- **Severity:** nit
- **Where:** `Surfaces/SteamHomeCarouselSurface.cs:178`, `Surfaces/SteamLibraryBadgeSurface.cs:168` (`SteamUiPatchEvaluation.Flag(root, "claimable")`) versus `SteamNavigationPanelSurface.cs:149`, `SteamPageSurface.cs:149`, `SteamExtensionsTabSurface.cs:161` (`ClaimableOrOurs`); `tk/SteamUiPatchEvaluation.cs:200-210`.
- **Problem:** both probes already report `claimed` but their compatibility predicate ignores it. It works today only because the claim primitive keeps the member descriptor writable and configurable, so a member we hold still reads as claimable; if that ever changes, the home carousel and the library badge would declare themselves incompatible against their own claim and tear down, exactly the failure `ClaimableOrOurs` was written to prevent. The toolkit rule is "accept a member that is claimable or already ours".
- **Best solution:** replace `SteamUiPatchEvaluation.Flag(root, "claimable")` with `SteamUiPatchEvaluation.ClaimableOrOurs(root)` in both predicates. Storage's `Flag(root, "claimable")` (`SteamStorageSurface.cs:202`) is left alone: TOOLKITCS-001 drops `claimable` and `claimed` from that probe in B049.
- **Tests:** in `tkt/SteamHomeCarouselTests.cs` and `tkt/SteamLibraryBadgeTests.cs`, a probe answer with `claimable: false, claimed: true` (and the other fields compatible) is compatible; `claimable: false, claimed: false` is not. Toolkit filter `FullyQualifiedName~SteamHomeCarouselTests|FullyQualifiedName~SteamLibraryBadgeTests`.
- **Plan v2:** B056 (edits every surface file).
- **Related:** TOOLKITCS-001, TOOLKITCS-064.

### U03B-SUTS-033: The tab-array transform stringifies component source on every array `useMemo` without a cache

- **Severity:** nit
- **Where:** `Source/components.ts:2375-2381, 2411-2435`.
- **Problem / solution:** covered by TOOLKITJS-V-005 and TOOLKITJS-031 (the matcher uses the cached `sourceMatches`; `transformTabs` becomes one indexed pass returning `value` by identity when nothing matched).
- **Plan v2:** B060.
- **Related:** TOOLKITJS-V-005, TOOLKITJS-031.

### U03B-SUTS-034: `createSourceAdoption.release` retargets without checking the fiber still holds the wrapper

- **Severity:** nit
- **Where:** `Source/gate-helpers.ts:771-795` (`createSourceAdoption`: `adopted.set` at L780, `retargetFiber(fiber, wrapFor(type))` at L781, `release` at L789-792) versus `createMountedAdoption.release` (L717-723, which retargets only when `fiber.type === replacement`).
- **Problem:** `release` writes the stored original type onto every adopted fiber unconditionally. If React or Steam changed the fiber's type after adoption (a hot replacement, or another tool adopting the same host), release overwrites that newer type with a stale one, against the toolkit rule "restore only what was displaced". The sibling helper already guards this case.
- **Best solution:** keep the wrapper per fiber and restore only our own: `const wrapper = wrapFor(type); adopted.set(fiber, {original: type, wrapper, hadParent: fiberAttached(fiber)}); retargetFiber(fiber, wrapper);` and in `release`: `if (fiber.type === wrapper) retargetFiber(fiber, original);` then `adopted.clear()` as today. Same check `createMountedAdoption` uses; no new state beyond the wrapper reference the entry already implies.
- **Tests:** with the navigation fiber fixture from U03B-SUTS-026, set an adopted popup-host fiber's `type` to a different function before `remove()`: after release it keeps that function; an untouched adopted fiber gets Steam's original back.
- **Plan v2:** B051 (it owns `gate-helpers.ts` edits and the navigation check).
- **Related:** U03B-SUTS-026, TOOLKITJS-008.

### U03B-SUTS-035: The UI kit header claims structural selectors but uses a generated CSS-module class name

- **Severity:** nit
- **Where:** `Source/ui-kit.ts:19-27` versus `:32`.
- **Problem / solution:** covered by TOOLKITJS-032 (correct the header comment; the selector stays, so appearance is identical).
- **Plan v2:** B060.
- **Related:** TOOLKITJS-032.

### U03B-SUTS-036: Small robustness gaps in UI kit elements

- **Severity:** nit
- **Where:** `Source/ui-kit.ts:534, 408, 446`; `:264, 338, 367, 388` (no `ui.focusable` fallback).
- **Problem / solution:** the empty tab list and the label and URL keys are covered by TOOLKITJS-032. The focusable fallback needs no change: every caller resolves `focusable` as a required component before drawing (`SteamUiTabbedPageRequired` in `ui-kit.ts:500-515`, the Extensions tab's required list in `gates/extensions-tab.ts:48-52`), so those elements never run without it.
- **Plan v2:** B060.
- **Related:** TOOLKITJS-032.

### U03B-SUTS-037: `renderSteamGlyph` caches elements by path only, across React instances

- **Severity:** nit
- **Where:** `Source/icons.ts:447-466` (domain review: L109-126).
- **Problem / solution:** covered by TOOLKITJS-032 (a `WeakMap<react, Map<string, element>>`, as `ui-kit.ts:134-151` does).
- **Plan v2:** B051. TOOLKITJS-032 assigns the per-React glyph cache to B051, whose spec carries it ("per-React glyph cache") because B051 edits `icons.ts` for TOOLKITJS-005; B060 does not touch it again.
- **Related:** TOOLKITJS-032.

### U03B-SUTS-038: Library details `resolve()` does not check that React resolved

- **Severity:** nit
- **Where:** `Source/gates/library-badge.ts:467`.
- **Problem / solution:** covered by TOOLKITJS-017 (library badge and screensaver use `resolveReact` and refuse with "React unavailable" on null).
- **Plan v2:** B050.
- **Related:** TOOLKITJS-017.

### U03B-SUTS-040: Uniqueness of game-context-menu item ids is documented but not enforced

- **Severity:** nit
- **Where:** `Surfaces/SteamGameContextMenuSurface.cs:10` ("unique in one publication"); `Source/gates/game-context-menu.ts:85` (React key `steam-ui-game-context-menu-${item.id}`); WSGM producers `src/WSGM/Shell/SteamGameContextMenuBackend.cs:93-108`, `src/WSGM/Shell/CommonPluginSteamUiSource.cs:198-207` (ids come from a dictionary, so WSGM never publishes duplicates).
- **Problem:** a host that publishes two items with the same id gets two siblings with the same React key, which React warns about and may reconcile as one, so an item can vanish or be drawn with the wrong label after an update. WSGM is not affected today; another host of the library is.
- **Best solution:** key each item by position and id, `` key: `steam-ui-game-context-menu-${index}-${item.id}` `` (map with the index), the same fix TOOLKITJS-032 applies to chips and thumbnails. Do not deduplicate in the gate: that would drop published content, and activation by id already works for duplicates (both run the same host command).
- **Tests:** `check-extension-surfaces.mjs`: a publication with two items sharing an id renders two items with distinct keys.
- **Plan v2:** B060 (TOOLKITJS-029 already adds `game-context-menu.ts` to B060's list).
- **Related:** TOOLKITJS-032, TOOLKITJS-029, U03B-SUTS-028.

### U03B-SUTS-042: Some C# assertions test constants or trivially absent strings rather than production behaviour

- **Severity:** nit
- **Where:** `tkt/SteamPageTests.cs:59-65`; `SteamNavigationPanelTests.cs:41-42`, `SteamHomeCarouselTests.cs:47-48`, `SteamLibraryBadgeTests.cs:42-46`.
- **Problem / solution:** covered by TOOLKITCS-064 (delete the `DoesNotMatch` against a test-local literal; once the probes run through NodeScript, the history-only `DoesNotContain` checks go with the substring tests they guard) and, for the JS analogue, TOOLKITJS-013.
- **Plan v2:** B062 (JS analogue in B057).
- **Related:** TOOLKITCS-064, TOOLKITJS-013, U03B-SUTS-006.

### U03B-SUTS-043: Brittle deep-index assertions and a hand-maintained class list in the checks

- **Severity:** nit
- **Where:** `toolkit/eng/check-extension-surfaces.mjs:207, 292, 344, 396` (chains such as `section.props.children[0].props.children[0]...`); `toolkit/eng/check-ui-kit.mjs:50-53` (asserts "every class an element uses has a rule" over a hand list of 25 class names that misses classes the kit uses, for example `tool`, `empty`, `hero`, `thumb`, `muted`); `toolkit/eng/check-harness.mjs:106` (`find` helper already exists).
- **Problem:** the deep-index chains break on any wrapper element added or removed without a behaviour change, and when they break they say nothing about what was wrong. The class-list assertion claims a guarantee it does not give: a kit element whose class has no stylesheet rule renders unstyled and the check still passes.
- **Best solution:** replace each chain with the harness `find(react, root, predicate)` by element type, `className` or text, asserting on the found node. For the kit, collect the classes from rendered output instead of a hand list: the check already renders nearly every kit element; walk each rendered tree with `find`, gather every `className` token that starts with `steam-ui-kit-`, and assert each has a rule in `SteamUiKitStyles`. Skip the `className` on the `ui.modalRoot` element itself: `showSteamModal` (`gate-helpers.ts`) hands `steam-ui-kit-modal` to Steam's modal component as a hook, and on the baseline it is the only prefixed class the kit uses without a rule (checked against the source); adding a rule for it would change the CSS and is not wanted. Rendered output is the behaviour that matters, so this beats a regex over the kit's source text.
- **Tests:** the rewritten checks pass on the baseline asset; deleting one kit rule (for example `.steam-ui-kit-empty`) makes `check-ui-kit.mjs` fail.
- **Plan v2:** B047 (check infrastructure, together with TOOLKITJS-011's fragment accessor).
- **Related:** TOOLKITJS-011, U03A-SUTS-018.

### U03B-SUTS-044: Toolkit AGENTS check map omits four checks

- **Severity:** nit
- **Where:** `toolkit/AGENTS.md:39-45` versus `toolkit/eng/run-checks.mjs:10-27`.
- **Problem / solution:** covered by TOOLKITJS-012 (checks discovered instead of listed); the guidance text change that goes with it is written out in U03A-SUTS-032.
- **Plan v2:** B047.
- **Related:** TOOLKITJS-012, U03A-SUTS-032.

### U03B-SUTS-045: `SteamPanelFoldsTests` defines a private `RecordingBackend` that shadows the shared fake

- **Severity:** nit
- **Where:** `tkt/SteamPanelFoldsTests.cs:65-74`; `tkt/Fakes/RecordingBackend.cs`.
- **Problem / solution:** covered by TOOLKITCS-070 (the shared fake implements `ISteamPanelFoldsBackend`; the folds test uses it).
- **Plan v2:** B062.
- **Related:** TOOLKITCS-070.

### U03B-SUTS-046: The home carousel sends its bridge report from inside render

- **Severity:** nit
- **Where:** `Source/gates/home-carousel.ts:252, 263-269`.
- **Problem / solution:** covered by TOOLKITJS-024 (report from an effect of the carousel wrapper keyed by the counts JSON; the dedupe stays).
- **Plan v2:** B060.
- **Related:** TOOLKITJS-024, U03B-SUTS-017.

## Missing bodies

None. All 79 U03A-SUTS and U03B-SUTS ids have full bodies in `claude-findings-raw.json` (`status: saved-final`), and the disposition ledger lists none of them under `unavailable_bodies` or `partial_body`.

## Refuted or no-change

- **U03A-SUTS-011** (low, storage actions acknowledged to Steam before the host decides): no change. WSGM's backend answers only after the operation finishes (`src/WSGM/Shell/SteamStorageBridge.cs` `FormatAsync` and `AdoptAsync` await the whole format, `EjectAsync` the eject), so awaiting that answer inside Steam's `SendMsg` would run into the bridge's 5 s request timeout and report every successful format as failed. The fire-and-forget design is deliberate (`storage.ts:169-178`), refusals are logged with a reason in `wsgm.log`, and the outcome reaches Steam through the next storage publication.
- **U03A-SUTS-012** (low, the audio `getDevices` command serves state while the module's `enabled` predicate is false): no change. WSGM's audio module uses the shared `Enabled()` (`src/WSGM/Shell/SteamUiSessionHost.cs` `Enabled`, `_enabled`, the native Quick Access switch, registered at `:1296`), and when that turns off `SetPatchStates` removes the audio gate, so Steam's `GetDevices` can reach the handler only in the retraction window. In that same window the runtime serves every command of every module (`SteamUiModuleRuntime.cs` gates commands only on `commandsEnabled` and module failure), including audio's own `setVolume` and `setDefaultDevice`, which the finding rightly leaves alone; WSGM's `read` is `audio.Current` and never throws. Refusing only the read there shows nothing different to the user and adds a per-command policy with no failing case behind it.
- **U03A-SUTS-033** (nit, route-table tokens duplicated in TypeScript and C#): no change. `RouteTokens` has one TypeScript user (`gates/screensaver.ts:32, 294`); every token family has the same C#/TS pair by design (`FieldTokens`/`NativeFieldTokens`, `SettingsTokens`/`SettingsStoreTokens`) because probes and gates are separate languages, and B062's executed probes plus the gate checks catch drift.
- **U03B-SUTS-021** (low, Extensions tab panel re-renders on every publication): no change, as TOOLKITJS-033 records. C# already skips identical envelopes (`SteamUiBridge.cs` around L517-526), so unchanged publications never arrive; the panel keeps its own subscription because it also covers the mount-time replay.
- **U03B-SUTS-022** (low hypothesis, portal windows created after install stay unstyled until the next publication change): no change until a live unstyled late portal is shown (TOOLKITJS-023 and its verifier, PV09-032). No notification store or polling is added without that evidence.
- **U03B-SUTS-025** (low hypothesis, adoption placeholder props visible to other fiber readers): no change. The only readers of `memoizedProps` (`home-carousel.ts:399` `findHome` and its C# probe in `SteamHomeCarouselSurface.cs:125`) look for the Fragment or route-switch fiber that carries the route list; no gate adopts that fiber (`pages.ts` swaps the switch's `type` without touching its props), the adopted fibers are memo instances and popup hosts, and React writes the real props back on their next render.
- **U03B-SUTS-039** (nit, file picker drawn with inline styles instead of the UI kit): no change. TOOLKITJS-021 keeps the inline styles so the picker looks identical; moving it onto kit classes would be a visual change the maintainer has not asked for.
- **U03B-SUTS-041** (nit, kit, picker and tab chrome strings are fixed English): accepted no-change (domain review R3). The strings are generic, not product policy, and a host label hook would change nothing visible while adding mechanism; `localizedOr` stays for Steam tokens where Steam has them.
