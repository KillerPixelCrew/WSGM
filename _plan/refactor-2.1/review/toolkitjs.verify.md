# TOOLKITJS adversarial verification

Verifier pass over `_plan/refactor-2.1/review/toolkitjs.md` against toolkit `main` 388dd1bb and parent `master` 1329813f.
Read-only. Read in full: `bridge.ts`, `module-resolver.ts`, `ownership.ts`, `rpc.ts`, `gate-helpers.ts`,
`settings.ts`, `file-picker.ts`, `page-gate.ts`, `library-capsule.ts`, `epilogue.ts`, `tsconfig.json`,
`components.ts` (all 2,492 lines), `gates/audio.ts`, `performance.ts`, `network.ts`, `storage.ts`, `pages.ts`,
`brightness.ts`, `elements.ts`, `screensaver.ts`, `sound-overrides.ts`, `theme-styles.ts`, `home-carousel.ts`
(L46-547), `library-badge.ts` (L1-130); toolkit `eng/build-prelude.mjs`, `run-checks.mjs`, `check-harness.mjs`,
`check-startup.mjs` (L1-130), toolkit `package.json`, `AGENTS.md`; parent `eng/build-steam-assets.mjs`,
root `package.json`, `tests/WSGM.Tests/Core/SteamUiAssetTests.cs`, `wsgm-settings.ts`; C# `SteamUiBridge.cs`
(L217-700), `SteamUiModuleRuntime.cs` (L50-220), `SteamUiModuleResolver.cs`, `SteamFilePickerSurface.cs`
(listing contract), `NativeQamSemanticServices.cs` (L225-241, L870-920, L1100-1190).

## Refuted

- **TOOLKITJS-030** (component host state survives disposal). Disposal is terminal: `disposeHostResources`
  sets `disposedHost` (components.ts L2483) and `install` refuses every kind afterwards (L2446). A new bridge
  re-evaluates the IIFE and builds a new host, so `acceptedStates`/`renderOutcomes`/`summaries` can never be
  drawn by "a re-install" after dispose. The only carry-over is last-`remove` then `install` inside the same
  bridge, where the bridge's own `latestStates` replay (bridge.ts L129-134) immediately re-delivers the current
  state. No defect; clearing them adds code for nothing. Drop it from B4 ("clear host state on dispose").

## Corrected

- **TOOLKITJS-002** severity medium -> low. The toolkit validators do require readback (power-limit range
  `observedWatts` L1486-1497, device ranges `observed ?? desired` L1655-1660/L1682/L1706, controller target
  L1117-1122), but no WSGM row vanishes today: WSGM publishes `observed ?? desired ?? maximum` for TDP
  (`NativeQamSemanticServices.cs` L905) and device ranges (L1184-1185) and `?? 0xFFFFFF` for zone colours
  (L1144-1145). It is a contract-hardening item for other consumers, not a user-visible readback gate. The
  recommendation (descriptor draws the row, value clamped) still stands.
- **TOOLKITJS-005** line numbers and one row. `module-resolver.ts` caps are at L36-44, not L128-136 (the file is
  87 lines). `sound-overrides.ts` caps are at L32 (128 resources), L45 (16 variants), L53 (1.4 MB URL) and L58
  (24 MB total, throws and drops every sound), not L78/91/99/104. `icons.ts` 4,096-char glyph cap is L447, not
  L108. The `components.ts` L486 "device range maximum above 100" row is not an arbitrary cap: the device range
  is a percentage (charge limit and lighting brightness, `valueSuffix: "%"` L1692/L1716), so 0-100 is the value's
  type; remove that row from the removal list. Walk-bound unification: `home-carousel.ts` L62-64 justifies 250,000
  ("a window with a whole library mounted"); unifying down to `MaximumMountedNodes` 60,000 risks Home not being
  found on large libraries. Unify upward or leave both.
- **TOOLKITJS-006** recommendation. Selecting the localizer by "calling it with an unknown token" executes
  candidate exports, which toolkit AGENTS.md L144-147 ("Never iterate the webpack registry while constructing
  arbitrary exports") and the parent CLAUDE.md CEF rule forbid, and which this same review flags as a defect in
  `storage.ts` (TOOLKITJS-018). Select by author tokens only (for example the `LocalizeString(` call plus the
  module's own literal strings), never by invoking candidates. The finding itself stands: `isLocalizer`
  (gate-helpers.ts L383-388) and `valueField` (settings.ts L41) key on `void 0`, `!0`, `!=null`, `focusable:!0`.
- **TOOLKITJS-013** two parts. (a) `check-power-profile.mjs` L47 `!asset.includes("useGlobal")` is not a
  "historical name" assertion: its message is "no row may offer a Use global control", the maintainer's decided
  per-game profile rule (no Use-global control, Reset is the way back; also `settings.ts` L193-195). It is WSGM
  policy, so move it to `SteamUiAssetTests.cs`, do not delete it. (b) The WSGM asset test does not hold only
  eval/fetch/WebSocket authority checks: L71-74 also assert no `force_deck_perf_tab`, `IS_STEAMOS =`,
  `PLATFORM =`, `SteamClient.SteamOSManager`, and L42-44 no filesystem access. Those are the "never spoof the
  platform" invariant (toolkit AGENTS.md L142-143) and must be kept. Line drift: readback pins are L561-563.
- **TOOLKITJS-017 / R7** (and plan L137 "cache module runtime/source per document"). Memoizing one resolver per
  bridge is not behaviour-neutral: the resolver keeps a `failed` Set (module-resolver.ts L13, L27, L31) and
  throws "previously failed" for every later resolution of a module whose factory threw once
  (`check-startup.mjs` L74-79 pins this). Today most gates build a fresh resolver per install attempt, so a module
  that throws during Steam's cold start is retried on the next poll; a bridge-wide resolver makes that failure
  permanent until the document is replaced. See TOOLKITJS-V-001. Memoize only the runtime capture (the chunk
  push), not the `failed` set.
- **TOOLKITJS-023** "`status()` L320-323 walks the mounted tree" is wrong. `status()` calls
  `reactRootFibers().length` (gate-helpers.ts L554-571), which reads `document.body` children only, no fiber
  walk. `windows()` (L312-318) does walk, but nothing in production calls it: the only caller is
  `check-theme-styles.mjs` L155. The remaining items (pattern cache never cleared L55/L295-309, stale "2 s passes"
  comment L172, late portals) are confirmed.
- **TOOLKITJS-033** recommendation. Dropping the panel's own `subscribe(patchId, redraw)` (extensions-tab.ts L255)
  relies on `mounted.rerender()` (L502, L507) reaching `ExtensionsTabPanel` through every memo boundary between
  the adopted Quick Access fiber and the panel. That is not shown, and the gate's subscription only rerenders on a
  changed publication (L500) while the panel's also covers the mount-time replay. Keep it unless a fixture proves
  the adopted rerender reaches the panel; a double render on a real change is not a defect worth that risk.
- **C9** (`native-qam` only in three C# sites). It is in about twenty C# surfaces as probe fingerprint strings
  (`SteamPowerLimitSurface.cs` L89, `SteamPerformanceSurface.cs` L561-625, every `Steam*Row.cs`). Conclusion
  "not in SteamUiAssets" holds.
- **C20 / TOOLKITJS-009 / TOOLKITJS-012** line numbers. `run-checks.mjs` is 48 lines: the hand list is L10-27, the
  asset-path branch L33-40. Content of all three findings is confirmed.

## Confirmed (ids only)

TOOLKITJS-001, TOOLKITJS-003, TOOLKITJS-004, TOOLKITJS-007, TOOLKITJS-008, TOOLKITJS-009, TOOLKITJS-010,
TOOLKITJS-011, TOOLKITJS-012, TOOLKITJS-014, TOOLKITJS-015, TOOLKITJS-016, TOOLKITJS-018, TOOLKITJS-019,
TOOLKITJS-020, TOOLKITJS-021, TOOLKITJS-022, TOOLKITJS-024, TOOLKITJS-025, TOOLKITJS-026, TOOLKITJS-028,
TOOLKITJS-029, TOOLKITJS-031, TOOLKITJS-032, TOOLKITJS-034, TOOLKITJS-035, TOOLKITJS-036, TOOLKITJS-037,
TOOLKITJS-038; plan-claim verdicts C3, C4, C5, C6 (defect part), C11, C12, C14 (current-state part), C15, C16,
C17, C19, C21, C22, C23, C24, C25.

Notes on the medium ones: TOOLKITJS-001 holds because `SteamUiModuleRuntime` answers each request on its own task
(L168) while the publication loop runs on another (L78), `DeliverAsync` (SteamUiBridge.cs L587-620) awaits each
part separately with no shared gate, and `deliverPart` resets the single `assembling` slot on any foreign id
(bridge.ts L220-228), so two interleaved multi-part sets both fail; a lost response surfaces as the 5 s
`RequestTimeoutMilliseconds` (L233) timeout. TOOLKITJS-008's new `storage.ts` instance is real (L317-326; the
wrapper never checks `installed`, L236-244).

## Missed findings

### TOOLKITJS-V-001 (medium) Cached resolvers make a cold-start module failure permanent

- Where: `module-resolver.ts` L13, L24-34 (`failed` Set, "previously failed"); already cached per gate in
  `gates/audio.ts` L107-118, `gates/brightness.ts` L43-54, `gates/elements.ts` L10-11; proposed bridge-wide in
  review R7/B2 and plan L137.
- Failure: a store or JSX-runtime module whose factory throws once (dependency chunk not ready during Steam's
  cold start) is added to `failed`; every later `exported`/`resolve` through that cached resolver throws
  "Steam module resolution previously failed" for the life of the bridge. The audio, brightness and elements
  gates already never retry such a module; R7 would extend this to every gate, including the Quick Access host,
  which today recovers on the next `ensurePatched` because it builds a fresh resolver (components.ts L2251).
- Recommendation: cache the captured webpack runtime once per bridge in `bridge.ts`, and drop the sticky `failed`
  Set (webpack keeps successful exports itself) or scope it to one resolution call. Add a check-startup case:
  factory throws, is fixed, next resolution through the cached runtime succeeds.

### TOOLKITJS-V-002 (low) `module-resolver.ts` is also a C# embedded expression

- Where: `SteamUiToolkit.csproj` L37 embeds `module-resolver.ts` as `SteamUiToolkit.ModuleResolver.js`;
  `SteamUiModuleResolver.cs` L24 builds `({Source})(scope)`.
- Failure: any second top-level statement, a TypeScript annotation or a `const` cache added to that file (B2
  "memoized getWebpackRuntime", "remove registry/token caps") yields a syntax error in every C# probe expression,
  and the emitted-asset checks would not notice. B2's test list names only `SteamUiBridgeHostTests`.
- Recommendation: B2 keeps `module-resolver.ts` a single plain-JS function declaration (memoization lives in
  `bridge.ts`, as the review already places it) and runs the toolkit C# probe/resolver tests
  (`FullyQualifiedName~ModuleResolver|FullyQualifiedName~Probe`) as well.

### TOOLKITJS-V-003 (low) Several gates and helpers have no emitted-asset check at all

- Where: no `check-*.mjs` instantiates `createAudioNamespace` (gates/audio.ts), `createPerfNamespace`
  (gates/performance.ts), `createNetworkGate` (gates/network.ts; check-startup L197-214 runs only the C# probe),
  `showSteamFilePicker` (file-picker.ts) or `registerSteamPage` (page-gate.ts). `createAudioNamespace` is used
  only as a slice end marker.
- Failure: B3 "harness `assertRemoveRetries` used by every gate check" and the release-order fix in audio and
  performance (TOOLKITJS-008) have no check to land in; regressions in volume direction mapping (audio.ts
  L159-176, L306-320) or perf state writes ship untested.
- Recommendation: B3 adds `check-audio.mjs`, `check-performance.mjs`, `check-network.mjs` (small fixtures over
  `sharedFragments` plus the gate), and B8 adds the file-picker check it already names; count them in B3's size.

### TOOLKITJS-V-004 (low) Perf gate removal writes invented values instead of what it displaced

- Where: `gates/performance.ts` L45-63 overwrites `m_msgState.limits/settings/current_game_id/
  active_profile_game_id` without a snapshot; `remove` L123-135 writes `undefined` to all four.
- Rule: toolkit AGENTS.md L141-142 "Removal restores exactly what was displaced ... Never restore an invented
  platform value."
- Failure: if Steam's store held anything in those fields before the first publication (a client build that
  seeds `current_game_id`, or a future Windows backend that half-populates the message), removal leaves
  `undefined` instead of the original. Low on today's client, where the comment says the fields start empty.
- Recommendation: capture the four fields once on the first `onState` (as audio.ts L198-202 does for its store)
  and restore them in `remove`. No new mechanism beyond that snapshot.

### TOOLKITJS-V-005 (low) Tab transform allocates on every object-array `useMemo` in the client

- Where: `components.ts` L2411-2436 `transformTabs` runs inside the shared `useMemo` claim (ownership.ts
  L471-481, which also builds a Map iterator per call) for every `useMemo` result in Steam's UI. Every array
  whose first item is an object pays two `filter` passes and up to two `map` copies.
- Failure: render-time cost and garbage on every Steam render that memoizes an object array (library grids,
  lists), not only the Quick Access tab list. Same class as TOOLKITJS-029 for `jsx`.
- Recommendation: a cheap shape gate first (`value.some(item => item?.panel && isValidElement(item.panel))`
  without allocation, or test the first item's keys), then the existing logic; iterate a frozen transform array
  in `createSharedClaim` as TOOLKITJS-029 proposes for both claims.

### TOOLKITJS-V-006 (low) Power-preset "custom" semantics the B5a contract must keep

- Where: `components.ts` L996-999 rejects the whole power-preset state when a `custom` option exists but neither
  assignment is `custom`; L1036 shows `custom` only in the dropdown whose current value it is; L1044 never sends
  it. WSGM adds the option only when one side is custom (`NativeQamPowerPresetService.cs` L43-45), and the Device
  SDK reserves the id (`DevicePowerPresetReference.cs` L33, `DevicePowerPreset.cs` L80).
- Failure: B5a's test "`selectable:false` option shown, never sent" would show the option in both dropdowns,
  changing the UI (requirement: appearance identical), and a straight port of L996-999 would make the generic
  flag delete the whole row.
- Recommendation: `selectable:false` means "listed only in a dropdown whose current value it is, never sent";
  drop the whole-state rejection. Pin both in the B5a check.

### TOOLKITJS-V-007 (low) R5 accent contract misses the Edit-color toggle description

- Where: `components.ts` L1738-1744 builds "Game override · <zone labels>" for the Edit color toggle from every
  zone's `overrideId`, separately from the per-row `overrideDescription` sites.
- Failure: replacing `overrideId` with host-built accented descriptions (R5) leaves no field for this composed
  line, so either the toolkit keeps WSGM vocabulary or the line disappears.
- Recommendation: add one host-supplied `editDescription` (text + accent) to the lighting state in the Section 4
  contract table and B5b's WSGM projection.

### TOOLKITJS-V-008 (low) R2's whole-listing response meets the 32 M delivery cap and the 5 s request timeout

- Where: `SteamUiBridge.cs` L225 `MaximumDeliveryCharacters = 32 * 1024 * 1024` (oversized answers are replaced
  by "The answer was too large to deliver." L452-457), L233 `RequestTimeoutMilliseconds = 5000`, and the JS
  request timer covers the whole parted delivery (bridge.ts L103-110).
- Failure: R2 drops the plan's continuation paging and sends the entire sorted listing in one response. A large
  file-mode folder is refused outright past 32 M characters, and a slow share plus a many-part delivery times out
  at 5 s with no partial result. It also makes TOOLKITJS-001 interleaving more likely.
- Recommendation: keep R2's removal of the spool, worker caps and capacity outcomes, but keep offset-based
  continuation (it is the chunking the no-limits rule asks for), or state the 32 M cap and 5 s bound as accepted.
  The 32 M cap itself is a C#-domain content cap to list with TOOLKITCS.

### TOOLKITJS-V-009 (nit) Uncommitted text drafts are wiped by any republish

- Where: `settings.ts` L393 `useEffect(() => setDrafts({}), [revision])`; `components.ts` L1195 same pattern;
  text and secret rows keep typed text as an uncommitted draft until blur (settings.ts L311-319).
- Failure: a publication that bumps the page revision for any reason (another row changed, an external change)
  clears what the user is typing in an unrelated text field before it is sent.
- Recommendation: fold into B6's `useSteamSettingDrafts`: on a new revision drop only drafts whose row's published
  value changed or whose write completed, keep in-progress uncommitted text.

## Batch problems

- **B1**: `build-prelude.mjs` is a top-level-await script that runs tsc and writes `dist/` on import (L61-118).
  WSGM's builder cannot `import { steamUiFragments }` from it without triggering a prelude build; split it into a
  side-effect-free module exporting the ordered list and a thin CLI. Inserting `// @fragment` markers changes the
  emitted asset and its hash (expected; say so in the commit).
- **B2**: R7 memoization must not keep the resolver's sticky `failed` Set (V-001); `module-resolver.ts` must stay a
  single plain-JS function expression for the C# embedding, and B2 must run the C# probe/resolver tests (V-002).
- **B3**: "assertRemoveRetries used by every gate check" assumes checks that do not exist for audio, performance
  and network (V-003); add them in B3 or narrow the claim. The walk-bound unification must not lower
  home-carousel's 250,000 bound. The percent-range "cap" must not be removed (TOOLKITJS-005 correction).
- **B4**: drop "clear host state on dispose" (TOOLKITJS-030 refuted). New `qam-*.ts` fragments sort before
  `settings.ts`/`ui-kit.ts`; any top-level `const` in them that reads a kit name at evaluation time hits a TDZ
  error, so they must hold functions and frozen literals only (the review's "pure" rule, stated as a constraint).
  `check-startup.mjs` L102-122 builds the host from `gateSource(createNativeComponentHost)` plus fixed slices; it
  must also include the new fragments.
- **B5a/B5b**: keep "custom" semantics as V-006 describes and add the Edit-color description field (V-007), or
  appearance changes. Because B5a breaks the presentation contract on toolkit `main`, no other batch may advance
  the parent gitlink between B5a and B5b; B6 also edits WSGM fragments (`wsgm-settings.ts`, `wsgm-graphics.ts`)
  and therefore lands in the parent only after B5b.
- **B6**: preserve uncommitted text drafts across unrelated revisions (V-009); otherwise sound.
- **B7**: `hostId` must be stable across WSGM restarts (an installation or consumer identity, not per process),
  or a restarted WSGM is refused as "foreign" by its own previous bridge until Steam restarts. State what clears a
  foreign owner that crashed (document replacement), so the refusal cannot strand the session.
- **B8**: R2 vs the 32 M delivery cap and 5 s request timeout (V-008); maintainer question Q1 should include it.
- **B9**: R20 adds a cross-gate notification store that couples `components.ts` and `navigation.ts` to the theme
  gate for U03B-SUTS-022, which the ledger records as a hypothesis (PV09-032). Under the simplify rule this needs
  live evidence of an unstyled late portal first; until then B9 is cache clearing and the stale comment only.
- **B10**: the localizer replacement must not invoke candidate exports (TOOLKITJS-006 correction); select by
  author-typed tokens.
- **B12**: a toolkit-wide Prettier pass contradicts toolkit AGENTS.md L223 ("Respect ... the established local
  TypeScript style. Avoid unrelated formatting") and needs the maintainer's explicit sign-off as a guidance
  change; WSGM's shipped asset is Prettier-formatted already, so the pass buys nothing for the product asset.
