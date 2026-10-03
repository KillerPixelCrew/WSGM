# SteamUiToolkit JavaScript/TypeScript review (TOOLKITJS)

Scope read in full: `external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/**` (32 files, 11,777
lines: `types.ts`, `bridge.ts`, `ownership.ts`, `rpc.ts`, `epilogue.ts`, `module-resolver.ts`, `gate-helpers.ts`,
`components.ts`, `settings.ts`, `ui-kit.ts`, `icons.ts`, `file-picker.ts`, `library-capsule.ts`, `page-gate.ts`,
`tsconfig.json`, all 17 `gates/*.ts`), the toolkit `eng/*.mjs` build and check scripts (`build-prelude`,
`run-checks`, `check-harness` in full; `check-startup`, `check-power-profile`, `check-ownership-claims`,
`check-ui-kit` in full or near full; the other 12 checks by header, slices and failure-path coverage),
`package.json`, `package-lock.json`, `.github/workflows/ci.yml`, toolkit `AGENTS.md`. Boundary traces: WSGM
`eng/build-steam-assets.mjs`, `eng/check-steam-module-discovery.mjs`, root `package.json`,
`src/WSGM/Core/SteamUiAssets/Source/*.ts` (consumer fragments), `tests/WSGM.Tests/Core/SteamUiAssetTests.cs`,
`src/WSGM/Shell/NativeQamSemanticServices.cs` (TDP/frame-limit projection), toolkit C# `SteamUiBridge.cs`,
`SteamUiModuleRuntime.cs`, `Surfaces/SteamSurfaceModule.cs`, `Surfaces/SteamPowerLimitSurface.cs`,
`tests/.../Fakes/NodeScript.cs`.

Baseline: toolkit `main` 388dd1bb, parent `master` 1329813f. Read-only; nothing built or run.

Ledger cross-reference: the prior Claude units U02A-SUTC, U02B-SUTC, U03A-SUTS and U03B-SUTS already cover most
single-site defects in this domain. They are cited by ID below and confirmed or refuted against current code.
Codex audits A01/A02/A02S01 carry no JS-specific findings (A01 only lists these files in coverage). Of the seven
admitted batches only T01_01 touches the toolkit and it is C#-only (`SteamUiTransportSession.cs`,
`SteamClientScript.cs`); nothing in it affects this domain.

---

## 1. Plan claims check

| # | Claim (source) | Verdict | Evidence and correction |
| --- | --- | --- | --- |
| C1 | "WSGM policy leaks into toolkit `components.ts`, one 2,490-line closure" (plan L45) | Accurate | `components.ts` is 2,492 lines; `createNativeComponentHost` spans L1-L2490, `registerGate` L2492. Policy sites listed in TOOLKITJS-003. |
| C2 | Libraries carry "no WSGM labels, RTSS suppression or profile policy" (plan L73) | Target, currently violated | RTSS-motivated Valve FPS row suppression `components.ts` L1898-1985, L2216-2223; "Game override" profile marker `components.ts` L299-309 and `settings.ts` L193-204; `"custom"` preset sentinel L996-998, L1036, L1044. |
| C3 | "The bridge has only webpack/React/asset preconditions; QAM/TDP matching belongs to its surface" (plan L135) | Partially accurate | The JS bridge (`bridge.ts`) has no QAM precondition. The QAM/TDP fingerprint lives in C# `SteamUiBridgePatch` (U02B-SUTC-003), outside this domain. |
| C4 | "Bridge identity includes caller host ID and per-bootstrap nonce ... Foreign owners are refused, not overwritten" (plan L137) | Target, not current | Reuse check `bridge.ts` L10-24 compares version/assetHash/generations only; L27 unconditionally `prior.dispose(...)` any prior bridge, including one another host installed. Needs config fields from C# (TOOLKITJS-B7). |
| C5 | "Gate removal releases first, clears state only after all owned claims are gone" (plan L137) | Target, partially current | Correct order today in `pages.ts` L329-357, `extensions-tab.ts` L514-535, `game-context-menu.ts` L204-219, `power-menu.ts` L166-180, `sound-overrides.ts` L156-163. Wrong order in ten gates (TOOLKITJS-008). |
| C6 | "Cache module runtime/source per document with monotonic IDs; never sweep exports by executing arbitrary functions" (plan L137) | Partially accurate; over-specified | Only `elements.ts` L10-11, `brightness.ts` L43-49, `audio.ts` L107-113 cache a resolver; every other install pushes a new chunk (`createSteamUiModuleResolver` `module-resolver.ts` L3-11). `storage.ts` L196-209 calls every function export (U03A-SUTS-003). "Monotonic IDs" are unnecessary: memoize one resolver per bridge (R7). |
| C7 | Split native-row state into "pure validators, row-family renderers, a section composer and one component-host owner" (plan L141) | Accurate direction, missing a constraint | Both builders discover only `Source/*.ts` and `Source/gates/*.ts` (`build-prelude.mjs` L40-52, `build-steam-assets.mjs` L76-120). Split files must stay top-level fragments, or both builders change. `components.ts` must stay last because `registerGate("nativeComponents", createNativeComponentHost())` runs at bundle evaluation. |
| C8 | "Host publications provide labels, sections/icons/order, stable fold IDs, selectable options and explicit Valve-FPS suppression. WSGM supplies today's exact data" (plan L141) | Accurate direction, over-broad on labels | See R3-R6: move policy (layout, FPS flag, override marker, selectability), keep generic row labels as toolkit defaults. |
| C9 | "Remove magic `custom`, `native-qam`, WSGM/RTSS policy and fixed product labels from generic code" (plan L141) | Partially accurate for JS | `custom` is in JS (C2). `native-qam` exists only in C# (`SteamUiBridge.cs` L44, `SteamAudioFormatRow.cs` L62, `SteamAudioSurface.cs` L109), not in SteamUiAssets. |
| C10 | "Keep one shared memo claim and live document/fiber adoption" (plan L141) | Accurate | `ownership.ts` L465-485 (`memoClaim`), `gate-helpers.ts` L655-736 (`adoptMountedType`, `createMountedAdoption`). |
| C11 | "Refusals reconcile drafts and produce bounded existing status/description messages" (plan L141) | Partially current; "bounded" conflicts with no-limits rule | QAM settings sections drop the draft on refusal (`components.ts` L1199-1200), the Extensions tab does (`extensions-tab.ts` L293-298), `SteamSettingsView` never does (`settings.ts` L395-398, U03A-SUTS-014); WSGM works around it by bumping a fake revision (`wsgm-settings.ts` L16-25, same in `wsgm-graphics.ts`). Drop "bounded": reuse the description slot without truncation. |
| C12 | "Route length remains unbounded ... both C# and JS reject controls and root-only routes" (plan L141) | Inaccurate as a statement of current JS | `isNavigableRoute` `gate-helpers.ts` L361 rejects non-absolute and `/` but not control characters. Target needs a JS change (one regex), not only docs (U03B-SUTS-009). |
| C13 | "Content labels/rows are shape-validated; impose limits only on actual byte/IO/transport safety boundaries" (plan L141) | Target; current code has many content caps | Enumerated in TOOLKITJS-005. The plan should list them so the implementer removes each. |
| C14 | File picker "injected filesystem backend and 200-entry pages with continuation, cancellation, stale-result rejection and unmount-settles-null" plus B2 worker/spool/capacity rules (plan L143, planning-corrections B2) | Partially current; over-engineered | Stale-result rejection exists (`file-picker.ts` L56-77 ticket). No unmount settle (L19-31, L200-205; U03B-SUTS-023). No render paging (L170). B2's spool, four-worker cap, one-per-root, ProviderBusy/ProviderCapacity have no concrete defect behind them (R2). |
| C15 | "UI kit owns identical styling, labels are host/Steam token supplied, React caches are per React instance" (plan L143) | Partially current; label hook unjustified | Per-React caches exist (`ui-kit.ts` L134-151) except `renderSteamGlyph` (`icons.ts` L109-126, U03B-SUTS-037). Kit chrome strings are generic English, not product policy; a host label hook is new mechanism with no defect (R3). |
| C16 | "Unavailable/null badge state draws nothing; do not invent 'Internal'" (plan L143) | Accurate target | `library-badge.ts` L54-55 defaults to "Internal" (U03B-SUTS-016). |
| C17 | "Preserve existing CSSLoader regex semantics; bound input bytes via existing content validation, clear caches on removal" (plan L143) | Partially accurate; "bound input bytes" contradicts the gate's own contract | `theme-styles.ts` L57-68 explicitly installs every theme "however large their CSS"; the pattern cache L55 is never cleared in `remove` L295-309. Keep cache clearing, drop any new byte bound (R9). |
| C18 | "Late portals reconcile on existing surface/window notifications, with no polling timer" (plan L143) | Accurate target, trigger unspecified | Only `AddPopupCreatedCallback` exists (L274-285); QAM/main-menu portals have no notification (U03B-SUTS-022). The existing QAM and menu wrappers are the natural trigger (B9). |
| C19 | "Replace slice/regex-derived test entry points with explicit emitted fragment markers/manifest and C#-emitted probe fixtures" (plan L145) | Accurate defect, over-specified remedy | Marker slices: `check-harness.mjs` L47, `check-ui-kit.mjs` L41, `check-settings-fields.mjs` L90, `check-extension-surfaces.mjs` L148 end at `function createAudioNamespace` (alphabetical accident); `check-power-profile.mjs` L313 ends at a comment. Builder-inserted per-file markers suffice; no manifest. Probe execution already has a channel: `Fakes/NodeScript.cs`, used by 4 tests (R8). |
| C20 | "The child suite runs independently and against WSGM's composed asset" (plan L145) | Inaccurate today | Root `package.json` L8 runs 5 of 16 checks on `NativeQamBootstrap.js`; `run-checks.mjs` L123, L151-158 already accepts an asset path (TOOLKITJS-009). |
| C21 | "Advance toolkit package to 0.2.0 for breaking APIs" (plan L145) | Accurate, applies here | Contract changes in B5a (row state fields, layout publication, persistence removal) break C# surface records. |
| C22 | "Preserve useful current owners ... the ownership claim helpers" (plan L75) | Accurate | `ownership.ts` primitives are sound; three small release-path defects remain (TOOLKITJS-020). |
| C23 | "old fold titles map to stable section IDs with current English titles still rendered; valid unrecognized fold keys survive" (plan L103, disposition U03B-SUTS-008 "accepted migration") | Over-engineered | Fold ids are opaque strings in `QuickAccessFolds` (`src/WSGM/Core/QuickAccessFolds.cs`, `SteamPanelFoldsBackend.cs` L43-45). If WSGM's layout publishes today's strings ("Power profiles", "Charging", `settings.<page>`, `extensions:<id>`) as the section ids, no migration is needed (R1). |
| C24 | T03 "uses the actual `external/steam-ui-toolkit/src/SteamUiToolkit/Surfaces` subtree" (planning-corrections-r2 R2-1) | Incomplete | T03 also owns `SteamUiAssets/Source/**`, toolkit `eng/*.mjs`, `package*.json`, and parent `eng/build-steam-assets.mjs`, root `package.json` scripts, `NativeQamBootstrap.js`, `SteamUiAssetCatalog.cs` hash, `tests/WSGM.Tests/Core/SteamUiAssetTests.cs` (R14). |
| C25 | "All long-running work is tracked and joined" / "Native callbacks contain exceptions" applied to injected script (plan L115) | Partially current | Timers are owned (`screensaver.ts` L62, L350-353; `components.ts` `useTrailingCommit` L726-752 flushes on unmount). Bridge dispose swallows gate failures silently (TOOLKITJS-019). |
| C26 | "Logging records bounded codes/correlation without raw paths/credentials/payloads" (plan L133) | Not applicable to JS | The injected script logs nothing; `storage.ts` L160 keeps `lastPayload` (drive ids, label) only in `status`. No change. |
| C27 | ledger U02A-SUTC-059 "explicit bounded no broad typing rewrite" | Agree | `types.ts` L17-23 and `tsconfig.json` `noImplicitAny:false` are deliberate at the minified boundary. |

27 claims checked.

---

## 2. Findings

Severity scale: critical / high / medium / low / nit. "Ledger" cites the prior ID; "NEW" means not in the ledger
or Codex audits.

### TOOLKITJS-001 (medium) Interleaved multi-part deliveries cancel each other

- Where: `bridge.ts` L37-39 (`let assembling` single slot), L206-239 (`deliverPart`: index 0 replaces the slot,
  any other id resets it to null). C#: `SteamUiBridge.cs` L434-461 (`RespondAsync`) and L506-556
  (`PublishStateAsync`) both call `DeliverAsync` L587-617 with no shared gate (`_gate` covers install/remove
  only); `SteamUiModuleRuntime.cs` L78 runs publication on its own task while L168 answers requests on others.
- Failure: a response larger than 256 K characters (`DeliveryPartCharacters`, L217), for example a file-picker
  listing or an Extensions tab answer, in flight while a multi-part state (theme styles, sound overrides,
  settings pages) is being delivered. Part 0 of one resets the other's slot; the next part of the other is
  refused, C# returns false. A refused state is retried next round, but a refused response is lost: the page
  waits for `config.timeoutMilliseconds` and rejects with "Steam UI bridge request timed out".
- Ledger: NEW.
- Recommendation: key reassembly by delivery id (`Map<id, {count, parts}>`), delete on completion, mismatch
  and `dispose`. No caps; abandoned entries die with the document. Add a `check-startup.mjs` case delivering
  two interleaved part sets. (Alternative of serializing `DeliverAsync` in C# delays responses behind
  megabyte publications; prefer the JS map.)

### TOOLKITJS-002 (medium) Toolkit rows vanish or are fabricated around missing readback

- Where: `components.ts` `normalizePowerLimitRange` L1478-1509 requires `observedWatts` integer, in range and
  on step, else the range is null and the slider is removed (L1576); device ranges hide the slider when both
  observed and desired are null (L1655-1660, L1682, L1706); controller row needs observed or selected target
  (L1117-1122, ledger U03B-SUTS-024). `check-power-profile.mjs` L555-558 pins "unknown boost readback cannot
  fabricate a value" and "off-step observations are refused". WSGM compensates by inventing a value:
  `NativeQamSemanticServices.cs` L903-906 `observed ?? desired ?? maximum`.
- Rule: "Never gate a write or a control on readback; publish the written value as observed." The frame-limit
  validator already fixed this class after a field incident (`components.ts` L573-608 stretches bookends).
- Ledger: U03B-SUTS-024 covers the controller row only; rest NEW.
- Recommendation: one display rule in the toolkit for every range row: a valid descriptor (min/max/step) draws
  the slider; the shown value is the host's value clamped to the range (no row deletion for off-step or
  out-of-range). WSGM keeps its own display fallback (host policy) and drops the duplicate descriptor checks
  (TOOLKITJS-005). Update the two pinned assertions.

### TOOLKITJS-003 (medium) WSGM product policy inside the generic component host and settings renderer

- Confirmed ledger U03B-SUTS-002, U03A-SUTS-005, U03B-SUTS-008, U03A-SUTS-015, U03A-SUTS-028 against current
  lines: Valve FPS rows hidden whenever host perf rows exist, motivated by RTSS (`components.ts` L1898-1905,
  L2094-2096, L2216-2223); "Game override" text and `#1a9fff` (`components.ts` L298-309, L1739-1743;
  `settings.ts` L193-204); `"custom"` sentinel (L996-998, L1036, L1044); "Restart the application to rebind."
  (L1133); section titles, icons, grouping and order keyed by English titles (L2019-2063, L2149, L2196-2202);
  fold ids are the titles (L2084-2085, `gate-helpers.ts` L247-253); "Manual selection" default (L1017).
- Additional NEW detail: the frame-limit command always sends `persistence: "automatic"` (L1316-1319), a WSGM
  per-game-profile concept; see TOOLKITJS-014.
- Recommendation: R3-R6 (layout publication with today's ids, accent flag, selectable options, FPS flag).

### TOOLKITJS-004 (medium) `createNativeComponentHost` is one closure; split has a builder constraint

- Confirmed ledger U03B-SUTS-003 (mutable bindings L2-42, L1906-1910, L2092; validators, rows, hooks, colour
  math, native-row filter, section composer, memo interception, diagnostics in one function).
- NEW constraint: `build-prelude.mjs` L40-52 and WSGM `build-steam-assets.mjs` L76-120 discover only
  `Source/*.ts` and `Source/gates/*.ts`. A `Source/qam/` folder would be silently omitted by both builders (the
  emitted asset would fail tsc only if a name is referenced). Keep the split as top-level fragments
  (`qam-*.ts`), which sort before `gates/` and before the always-last `components.ts`.
- Recommendation: Section 4 split; old-symbol table included.

### TOOLKITJS-005 (medium) Arbitrary caps that drop or refuse valid content

Each refuses or silently drops real content (rule: type checks only; payloads are chunked):

| Site | Cap and effect |
| --- | --- |
| `components.ts` L347 | more than 8 controller targets: whole state null, controller row disappears |
| `components.ts` L358, L867 | target/option id longer than 64 or outside `[A-Za-z0-9._-]`: whole state null |
| `components.ts` L329-335 | AutoTDP watts outside 1-200: dropped |
| `components.ts` L1486-1489 | power range above 200 W: range null (duplicated in C# `SteamPowerLimitSurface.cs` L151 `TryReadInt(...,1,200)` and WSGM `NativeQamSemanticServices.cs` L891) |
| `components.ts` L583-591 | FPS above 1000: whole frame-limit state null |
| `components.ts` L486 | device range maximum above 100: range null |
| `module-resolver.ts` L128-136 | more than 32,768 registered modules throws "exceeds the discovery bound" for every surface; fingerprints over 16 tokens or 512 chars throw |
| `icons.ts` L108 | host glyph path over 4,096 chars: glyph dropped |
| `pages.ts` L114, `home-carousel.ts` L401 | route list with 512 or more routes is not recognised: pages/Home never claimed |
| `power-menu.ts` L21, L93 | menu with more than 48 children: entry never drawn |
| `library-badge.ts` L92, L220; L406, L451 | tile subtree over 64 children / stats section over 32: badge or stat skipped |
| `sound-overrides.ts` L78, L91, L99, L104 | 128 resources (whole set refused), 16 variants (name dropped silently), 1.4 MB per URL, 24 MB total (everything dropped) |

- Ledger: U03A-SUTS-019 (sound caps silent), U03A-SUTS-009 (doc/limit contradictions); rest NEW.
- Retain (legitimate safety bounds, not content caps): bridge `maximumPending` in-flight backpressure
  (`bridge.ts` L88), delivery part size (C#), and walk/descent bounds that stop cyclic trees
  (`MaximumMountedNodes` L671, `MaximumDescent` per gate). Unify the two walk bounds (60,000 vs
  `home-carousel.ts` L64 250,000) into one constant.
- `tests/WSGM.Tests/Core/SteamUiAssetTests.cs` L28-29 pins `url.length > 1400000` and `total > 24000000`; it
  must change with the removal.

### TOOLKITJS-006 (medium) Fingerprints and comments written against minified output

- AGENTS rule: "A fingerprint names tokens an author typed and says nothing about the identifiers or spacing a
  minifier chose ... Do not describe minified code either."
- Fingerprints: `isLocalizer` `gate-helpers.ts` L383-388 (`"void 0"`, `"!0)"`, `"!=null"`); `valueField`
  `settings.ts` L41 (`"focusable:!0"`); tabs `gate-helpers.ts` L168 (`"(function()"`); dialog buttons
  `gate-helpers.ts` L127, L130 (`'"DialogButton","_DialogLayout","Secondary"'`) and `settings.ts` L44
  (`'"DialogButton _DialogLayout Small"'`, the joined form). The `check-startup.mjs` L149 fixture encodes the
  minified localizer body. The localizer is load-bearing: when it last failed, every Quick Access row refused
  (`gate-helpers.ts` L390-393).
- Descriptions of minified identifiers: `navigation.ts` L5-19 (`v_`, `fe`, `Ie`, `Ae`, `me`, `ve`), `pages.ts`
  L6-12 (`fd`), `library-badge.ts` L7-16 (`TK`, `b.Z`, `d.z`, `Kt`).
- Ledger: NEW.
- Recommendation: select by behaviour and author tokens (localizer: the one non-class export of the
  localization module whose call with an unknown token returns that token and with `#Button_Cancel`-like known
  token returns a string; value field by prop names `inlineWrap`/`focusable` without the literal; buttons by the
  author's class names as separate tokens). Requires a live Steam session per AGENTS; batch B10 is attended.

### TOOLKITJS-007 (low) Webpack module ids written in comments beyond `rpc.ts`

- `rpc.ts` L33 (ledger U02A-SUTC-058, confirmed), plus NEW: `audio.ts` L42 ("module 74362"), L97 ("module 1409,
  export F5"), `bluetooth.ts` L177 ("module 60517, export RF"), `brightness.ts` L35 ("module 59547, export mG").
- Recommendation: delete the ids; keep the "renumbered" rationale.

### TOOLKITJS-008 (medium) Gates forget ownership before a fallible release

- Confirmed ledger U03B-SUTS-001 (`home-carousel.ts` L502-518, `library-badge.ts` L340-350 and L517-526,
  `navigation.ts` L376-388), U03A-SUTS-001 (`bluetooth.ts` L318-335), U03A-SUTS-002 (`audio.ts` L347-379,
  `performance.ts` L118-146, `screensaver.ts` L346-367), U03A-SUTS-007 (`brightness.ts` L129-138 release result
  ignored, L174-191).
- NEW instance: `storage.ts` L317-326 sets `installed=false` before `releaseMember(transport,"SendMsg",...)`. A
  failed release leaves the wrapper on the transport that carries every Steam service call while every later
  `remove()` answers `{absent:true}`, and the wrapper still answers `StorageDeviceManager.*` (it never checks
  `installed`, L236-244).
- Recommendation: fix the order in place in all ten gates (release, then forget), mirroring `pages.ts`
  L326-335. Add one harness helper that makes each gate's release fail once and asserts a second `remove()`
  retries (extends U03B-SUTS-027 beyond the Extensions tab). No shared lifecycle framework (simplify rule).

### TOOLKITJS-009 (medium) WSGM checks its shipped asset with 5 of 16 toolkit checks

- Where: root `package.json` L8 `steam-assets:claims` runs `check-ownership-claims`, `check-sound-overrides`,
  `check-power-profile`, `check-startup`, `check-service-gates` against `NativeQamBootstrap.js`; never
  navigation-panel, settings-fields, pages, storage, library, home-carousel, screensaver, extension-surfaces,
  power-menu, theme-styles, ui-kit. `run-checks.mjs` L123, L151-158 already checks a given asset without
  building. The shipped asset is Prettier-formatted (`build-steam-assets.mjs` L196-210), unlike the prelude.
- Ledger: NEW.
- Recommendation: `steam-assets:claims` becomes `node external/steam-ui-toolkit/eng/run-checks.mjs
  src/WSGM/Core/SteamUiAssets/NativeQamBootstrap.js && node eng/check-steam-module-discovery.mjs`.

### TOOLKITJS-010 (low) Two builders own fragment order; comments drifted

- Toolkit `build-prelude.mjs` L39-52 and WSGM `build-steam-assets.mjs` L55-120 each encode the order. WSGM's
  comments are stale: L28-32 "there are none today" (nine WSGM fragments exist), L50-54 "bridge.ts can call them
  before they appear textually" (gates self-register). WSGM also discovers non-existent
  `src/WSGM.Plugin.*/SteamUiAssets` directories (L93-109). `build-prelude.mjs` L86-90 spawns `node` from PATH
  (ledger U02B-SUTC-027).
- Ledger: U02B-SUTC-022 (alphabetical dependence), U02B-SUTC-027; duplication NEW.
- Recommendation: `build-prelude.mjs` exports `steamUiFragments(extraDirectories)` returning the ordered list
  and inserting per-file markers; WSGM imports it and appends its own directories. One owner of order. Remove
  the plugin-directory discovery unless a plugin fragment exists at implementation time.

### TOOLKITJS-011 (low) Checks depend on emitted-text adjacency

- `check-harness.mjs` L47 `sharedFragments` = `"const defineHidden"` to `"const SteamUiIconShapes ="`;
  `check-ui-kit.mjs` L41, `check-settings-fields.mjs` L90, `check-extension-surfaces.mjs` L148 slice up to
  `function createAudioNamespace` (true only because `audio.ts` sorts first in `gates/`);
  `check-power-profile.mjs` L313 ends a slice at a comment; L17, L23, L53, L83, L239, L374, L380, L494-495 slice
  `components.ts` between adjacent `const` declarations; `check-startup.mjs` L13-18, L197-199 regex-scrape C#.
  WSGM `eng/check-steam-module-discovery.mjs` L2 imports `sharedFragments`.
- Ledger: U02B-SUTC-022, U03A-SUTS-018 (confirmed).
- Recommendation: R8 fragment markers; checks instantiate whole fragments.

### TOOLKITJS-012 (low) `run-checks.mjs` holds a hand list

- `run-checks.mjs` L128-145: a new `check-*.mjs` not added there never runs, and AGENTS' map is already stale
  (ledger U03B-SUTS-044, U02B-SUTC-028).
- Ledger: list-instead-of-discovery NEW.
- Recommendation: discover `eng/check-*.mjs` (excluding the harness), sorted. Removes the list.

### TOOLKITJS-013 (low) Tests pin WSGM labels, policy and toolkit internals

- 36 assertions in the checks name WSGM English labels (for example `check-power-profile.mjs` L98, L134-136,
  L144, L148-165); `check-power-profile.mjs` L203-218 pins the `custom` sentinel; L46 asserts a historical name is
  absent (`!asset.includes("useGlobal")`, same class as U03B-SUTS-042); L555-558 pins readback gating. WSGM
  `SteamUiAssetTests.cs` L28-29, L67 pins sound caps and `persistence: "automatic"` as substrings of the
  composed asset.
- Ledger: U03B-SUTS-042 (C# analogue); JS and consumer instances NEW.
- Recommendation: after B5, labels and layout become fixture data supplied to the host; drop history and
  substring assertions; WSGM's asset test keeps only its authority checks (no `eval`, no `fetch` outside the
  sound decoder, no `WebSocket`).

### TOOLKITJS-014 (low) Dead `persistence` field on the frame-limit wire

- JS always sends `persistence: "automatic"` (`components.ts` L1316-1319) for both the cap and the refresh
  command; C# `SteamSurfaceModule.TryReadValueWrite` L114-140 requires it and maps three values; WSGM ignores the
  parameter (`NativeQamSemanticServices.cs` L230-241). `global`/`application` are never sent.
- Ledger: NEW.
- Recommendation: delete the field, `SteamSettingPersistence` and the reader branch; `{value}` only.

### TOOLKITJS-015 (low) Draft and confirm logic implemented several times

- Drafts: `settings.ts` L386-398 (`SteamSettingsView`, never drops on refusal), `components.ts` L1194-1201 (QAM
  settings sections, drops), `extensions-tab.ts` L253-303 (drops, revision-keyed). WSGM's workaround bumps a fake
  revision (`wsgm-settings.ts` L16-25, `wsgm-graphics.ts` ~L19-27), which discards every other row's in-flight
  draft on any refusal.
- Confirms: `settings.ts` L81-95 (Steam's confirm modal) and `ui-kit.ts` L595-625 (kit confirm).
- Duplicate accent colour `#1a9fff`: `components.ts` L299, `settings.ts` L196.
- Ledger: U03A-SUTS-014 covers one site; duplication NEW.
- Recommendation: one `useSteamSettingDrafts(react, revision)` in `settings.ts`; `onChange` returns the request
  promise and a rejection drops that row's draft. Both confirms stay (different Steam components, both
  shipped), but share nothing new.

### TOOLKITJS-016 (low) Row component duplication

- `createCpuBoostControl` L949-988 duplicates `createChoiceControl` L887-925 except the description; the
  pending-dropdown pattern (`pending`/`setPending`/`sendCommand(...).catch().finally()`) appears five times
  (L891-922, L952-986, L1014-1052, L1219-1247).
- Ledger: NEW.
- Recommendation: `createChoiceControl` takes the description function; one `sendPending` helper.

### TOOLKITJS-017 (low) Module runtime resolution repeated and inconsistent

- Chunk pushed per resolve (ledger U02A-SUTC-005, confirmed: `components.ts` L2251 on every failed install,
  `page-gate.ts` L245, most gates). Inconsistent React resolution: `resolveReact` returns null
  (`gate-helpers.ts` L45-48) while `screensaver.ts` L276 and `library-badge.ts` L467 use throwing
  `runtime.resolve` (ledger U03B-SUTS-038); `screensaver.ts` L281-292 re-implements dropdown resolution that
  `resolveSteamFieldComponents` provides.
- Recommendation: `getWebpackRuntime` in `bridge.ts` L46 memoizes one resolver per bridge; per-gate caches
  deleted; all gates use `resolveReact`/`resolveSteamFieldComponents`.

### TOOLKITJS-018 (low) Storage resolution calls every function export

- Confirmed ledger U03A-SUTS-003 (`storage.ts` L196-209).
- Recommendation: call only the export whose source names `GetDefaultTransport` (`sourceMatches`).

### TOOLKITJS-019 (low) Bridge disposal is silent and incomplete

- `bridge.ts` L249-261 swallows each gate's `remove`/`dispose` failure with no trace; L292 `gates` map is not
  cleared (ledger U02A-SUTC-056, confirmed); `deliver`/`subscribe` (L121-203) still accept work after dispose.
- Recommendation: dispose collects failed gate names and the new bridge returns them in `installResult`
  (`{ok, reused, version, priorDisposeFailures}`) so the C# bootstrap logs them; clear `gates`; refuse
  deliveries after dispose. No new state.

### TOOLKITJS-020 (low) Ownership release edge cases

- Confirmed ledger U02A-SUTC-025: `releaseAccessor` reports success when no original is stored
  (`ownership.ts` L388-391); `releaseValue` casts the stored original without `isPropertySnapshot` (L212).
- NEW nit: `createSharedClaim.release` with a null host drops the transform but keeps `wrappers` (L445-446).
- Recommendation: refuse with an error in both release cases; clear `wrappers` only after members are restored.

### TOOLKITJS-021 (low) File picker lifetime and rendering

- Confirmed ledger U03B-SUTS-023 (no settle on unmount: only `finish`, `onCancel`, `!shown` settle, L26-31,
  L200-205), U03B-SUTS-004 (every entry rendered, L170), U03B-SUTS-039 (inline styles).
- NEW: `ui.dialogButtonPrimary` used without fallback (L187) where the kit uses `?? ui.dialogButton`
  (`ui-kit.ts` L613); `listPlaces` (L80-91) is not ticketed.
- Recommendation: B8. Keep inline styles (appearance must stay identical; no defect).

### TOOLKITJS-022 (low) Colour editor turns unknown colours into white

- `settings.ts` L99-111: anything other than `#hex` or comma `hsl(a)` parses to `{h:0,s:0,l:100,a:1}`. A theme
  colour written as `rgb(...)`, a named colour or space-separated `hsl()` opens as white; Save writes white over
  it.
- Ledger: NEW.
- Recommendation: parse `rgb(a)` too; for anything else fall back to the text field (the same fallback L271-281
  uses when no modal exists) instead of inventing white.

### TOOLKITJS-023 (low) Theme styles: caches, late portals, status cost

- Confirmed ledger U03B-SUTS-015 (pattern cache never cleared, L55, remove L295-309), U03B-SUTS-013 (stale "2 s
  passes" comment L172), U03B-SUTS-022 (late portals). NEW: `status()` L320-323 and `windows()` L312-318 walk the
  mounted tree (up to 60,000 fibers) on each call.
- Recommendation: B9.

### TOOLKITJS-024 (low) Home carousel costs and duplication

- Confirmed ledger U03B-SUTS-017 (`status` walks 250,000 fibers each verify, L538), U03B-SUTS-046 (report from
  render, L252, L263-269).
- NEW nits: route-list detection duplicated with `pages.ts` (`KnownRoute`, `2 < length < 512`: `home-carousel.ts`
  L55, L401 vs `pages.ts` L52, L111-115); `isHome` reads the claim marker directly (L383) instead of
  `unclaimedValue`; `Symbol.for("react.memo")` repeated (L341, L381, `library-badge.ts` L260); redundant
  `useObserver = null;` (L444; also `screensaver.ts` L313).
- Ordering policy (L35-39, L150-254) is library-owned data projection; keep and document (no change).

### TOOLKITJS-025 (low) Library badge invents a label

- Confirmed ledger U03B-SUTS-016 (`library-badge.ts` L54-55 "Internal"); comment L26 records a maintainer product
  choice inside the library.

### TOOLKITJS-026 (nit) WSGM and device names in generic comments

- `components.ts` L394, L399, L580, L758, L785, L1898, L2096, L2187; `bridge.ts` L61; `home-carousel.ts` L314;
  `theme-styles.ts` L27; `settings.ts` L193. Ledger U03A-SUTS-028 covers only `settings.ts` L193 and C#; rest NEW.

### TOOLKITJS-027 (nit) Two coding styles, no formatter

- `components.ts`, `ui-kit.ts`, `pages.ts`, `theme-styles.ts`, `extensions-tab.ts`, `game-context-menu.ts`,
  `sound-overrides.ts` use 2-space Prettier style; the rest 4-space `{a}`. Toolkit `package.json` has no
  formatter; the parent `.prettierignore` excludes `external/`. Ledger U02B-SUTC-036, U03A-SUTS-029 partial.
- Recommendation: add Prettier (pinned like the parent's 3.9.9) to the toolkit with one config and format once
  in the docs/cleanup batch; do not mix with behaviour batches.

### TOOLKITJS-028 (nit) Undeclared consumer-facing script API

- All fragments share one IIFE scope; WSGM fragments use about 30 toolkit top-level names (`registerSteamPage`,
  `renderSteamSettings`, `renderSteamSettingRow`, `renderSteamUi*`, `showSteamUi*`, `showSteamModal`,
  `showSteamFilePicker`, `createSteamCapsule`, `resolveSteamLibraryClasses`, `renderSteamDropdown`,
  `steamCheckbox`, `onSteamTriggers`, `navigateSteamRoute`, `request`, `subscribe`, `registerGate`,
  `interceptMemo`, `getWebpackRuntime`, `invalidateQuery`, `renderSteamGlyph`, `SteamUiTabbedPageRequired`,
  `SteamSettingsRequired`, `resolveSteamSettingsComponents`, `resolveSteamUiComponents`). Nothing lists them; a
  rename is caught only by WSGM's tsc compile.
- Ledger: NEW.
- Recommendation: one "Script API for consumer fragments" list in `docs/reference.md`; splits in B4 keep these
  names. No new mechanism.

### TOOLKITJS-029 (nit) Element-transform hot path

- `ownership.ts` L509-519 creates a `Map` iterator on every `jsx`/`jsxs` call once any element transform is
  registered (power menu, library details, game context menu). `game-context-menu.ts` L163-173 keeps its capture
  transform after capturing the class.
- Recommendation: iterate a frozen array snapshot rebuilt on intercept/release; release the capture transform
  after `claimMenuRender` succeeds.

### TOOLKITJS-030 (nit) Component host state survives disposal

- `components.ts` L666 `acceptedStates`, L65 `renderOutcomes`, L70 `summaries` are not cleared by
  `disposeHostResources` L2482-2488 or by the last `remove`; a re-install first draws the previous install's
  states.
- Recommendation: clear them with the memo release.

### TOOLKITJS-031 (nit) Confirmed small component-host items

- U03B-SUTS-007 `nativeRowsHidden` undercount (L1924-1979, `lastHidden` mutated in render): confirmed.
- U03B-SUTS-018 settings-sections control returns null without `note` (L1190-1196): confirmed.
- U03B-SUTS-019 refusals swallowed except power limit (L229, L919-921, L983-985, L1049-1051, L1131, L1169,
  L1244-1246, L1317-1319, L1647-1648): confirmed.
- U03B-SUTS-030 disposed host refuses as "component is not allowlisted" (L2446): confirmed.
- U03B-SUTS-031 power-limit error `String(reason)` gives "Error: ..." (L1556, L1593): confirmed.
- U03B-SUTS-033 `transformTabs` stringifies sources uncached (L2377): confirmed.

### TOOLKITJS-032 (nit) Confirmed UI-kit items

- U03B-SUTS-035 header claims structural selectors but L32 uses `gamepadtabbedpage_TabHeaderRowWrapper`;
  U03B-SUTS-036 `tabs[0]` on empty list (L534), chips keyed by label (L408), thumbnails by URL (L445); U03B-SUTS-037
  glyph cache across React (`icons.ts` L109-126). All confirmed.

### TOOLKITJS-033 (nit) Extensions tab re-render claim partly stale

- Ledger U03B-SUTS-021 says the panel re-renders on every publication. C# now skips identical envelopes
  (`SteamUiBridge.cs` L517-526), so unchanged publications never arrive. The residual is a double render on a real
  change: the panel's own `subscribe(patchId, redraw)` (L255) plus the gate's `mounted.rerender()` (L502).
- Recommendation: drop the panel's own subscription.

### TOOLKITJS-034 (nit) Redundant dedupe and polling

- `storage.ts` L304-306 and `home-carousel` `report` re-dedupe what C# already dedupes; harmless, keep only
  where the JS derives a different value (storage maps the shape, keep; delete nothing else).
- `screensaver.ts` L39-40, L138-142 polls up to 60 x 2 s for the first report. It is bounded and owned; keep.

### TOOLKITJS-035 (nit) `isNavigableRoute` accepts control characters

- `gate-helpers.ts` L361; docs and C# claim otherwise (ledger U03B-SUTS-009). One regex.

### TOOLKITJS-036 (nit) Check style inconsistency

- `check-ownership-claims.mjs` uses its own `check()` and `process.exit` (L25-34, L275-276); others use
  `node:assert`. Convert when the file moves to fragment accessors.

### TOOLKITJS-037 (nit) Asset size cap raised by hand

- WSGM `build-steam-assets.mjs` L128-134: 768 KiB "sanity bound", raised twice; the asset is 645,950 bytes
  today. It is not a transport limit. Cross-domain (WSGM eng): either tie it to the transport's real expression
  limit or delete it.

### TOOLKITJS-038 (nit) Lockfile and package metadata

- Ledger U02B-SUTC-026 (`package-lock.json` name "toolkit" vs no name) confirmed; CI uses unpinned major
  actions (U02B-SUTC-023) confirmed.

---

## 3. Plan refinements

Additions, changes and removals for this domain. Items marked OVER-ENGINEERED replace plan mechanisms that add
state or limits without a concrete defect.

- R1 OVER-ENGINEERED: remove the fold-id migration (plan L103, U03B-SUTS-008 "accepted migration"). The layout
  publication carries section ids; WSGM publishes today's exact strings ("Profile scope", "Power profiles",
  "Display and frame rate", "Power limits", "Controller", "Display", "Audio", "Charging", "RGB lighting",
  `settings.<pageId>`, `extensions:<id>[:<key>]`). They become opaque ids; `QuickAccessFolds` stays untouched;
  nothing migrates.
- R2 OVER-ENGINEERED: replace B2's on-disk spool, four-worker cap, one-per-provider-root rule,
  ProviderBusy/ProviderCapacity outcomes and retained-worker accounting with: C# enumerates and sorts on a worker
  with the request's cancellation; the whole sorted result returns through the existing parted delivery
  (`DeliverAsync` already splits >256 K); JS renders 200 rows at a time with the kit's existing
  `renderSteamUiMore` (`ui-kit.ts` L283-290); the existing ticket rejects stale listings; an unmount effect
  settles null once. The bridge's own request timeout is the wait bound; no second clock. Keep B2's path policy
  rules (they are the C# domain's) and the offline fixtures that still apply (450 entries, two directories then
  Back, unmount, cancel).
- R3 OVER-ENGINEERED: no host label hook for kit/picker/tab chrome (`ui-kit.ts`, `file-picker.ts`, `page-gate.ts`,
  `extensions-tab.ts` strings). They are generic, not product policy, and changing them would change visible
  text. Record U03B-SUTS-041 as accepted no-change.
- R4: move only policy, not every label. Policy items: section layout (ids, titles, icons, order, membership,
  which sections fold, Reset placement), Valve FPS suppression, the override marker, display-only options, the
  controller restart sentence. Generic row labels ("Display resolution", "Battery charge limit", ...) stay toolkit
  defaults. This shrinks the new contract to one layout record.
- R5: replace every `overrideId` field and `SteamSettingsRow.Override` with a host-built description plus
  `accent: true`. WSGM writes "Game override · <status>" itself; the toolkit only colours an accented
  description. Same output, no WSGM vocabulary in the toolkit, and `OverrideColor` becomes one shared constant.
- R6: `hideValveFpsRows` boolean in the layout publication, default false; WSGM sends true.
- R7 OVER-ENGINEERED: drop "monotonic IDs" for module runtime caching (plan L137). `bridge.ts` memoizes one
  resolver per bridge instance (one document); per-gate resolver caches are deleted.
- R8 OVER-ENGINEERED: no fragment manifest. `build-prelude.mjs` inserts `// @fragment <relative path>` before
  each file (comments survive tsc `removeComments:false` and Prettier); the harness exposes
  `fragment(asset, path)`. Probe execution uses the existing `NodeScript` fake in C# tests rather than a new
  C#-to-file fixture channel.
- R9: no new byte bound on theme CSS or targets (plan L143 "bound input bytes"); clear the pattern cache on
  removal only.
- R10: refusal text is shown in the existing description slot without truncation ("bounded" removed).
- R11 (new): range rows never disappear for missing or off-step readback (TOOLKITJS-002); remove the duplicated
  200 W checks in toolkit JS, toolkit C# and WSGM (one type check remains).
- R12 (new): delete the frame-limit `persistence` field and `SteamSettingPersistence` (TOOLKITJS-014).
- R13 (new): enumerate and remove the content caps in TOOLKITJS-005; keep only in-flight backpressure, delivery
  part size and cycle guards.
- R14: T03 ownership adds `SteamUiAssets/Source/**`, toolkit `eng/*.mjs`, `package*.json`, `.github/workflows/ci.yml`,
  and parent `eng/build-steam-assets.mjs`, `eng/check-steam-module-discovery.mjs`, root `package.json`,
  `src/WSGM/Core/SteamUiAssets/NativeQamBootstrap.js`, `src/WSGM/Core/SteamUiAssetCatalog.cs` (hash),
  `tests/WSGM.Tests/Core/SteamUiAssetTests.cs`, and WSGM fragments `wsgm-settings.ts`, `wsgm-graphics.ts`.
- R15: one fragment-order owner (TOOLKITJS-010).
- R16: WSGM runs the whole toolkit suite on its composed asset (TOOLKITJS-009).
- R17: bridge identity JS side stays minimal: configuration adds `hostId` and `nonce`; a prior bridge with a
  different `hostId` is refused (`{ok:false, foreign:true}`) instead of disposed; same host, different nonce is
  replaced as today. Acknowledgement echoes the nonce.
- R18: gate lifecycle fixed in place (TOOLKITJS-008) plus one harness assertion; no `createClaimedGate` framework.
- R19 (new): parted-delivery reassembly keyed by id (TOOLKITJS-001).
- R20 (new): multi-surface signal for late portals: a `steamDocuments` local store in `gate-helpers.ts`
  (existing `createLocalStore`) that the QAM roots (`components.ts` wrappers) and the navigation popup host notify
  on mount; `theme-styles.ts` subscribes. No timer.

---

## 4. Target design

Owners (all in the toolkit unless stated):

| Owner | File | Responsibility |
| --- | --- | --- |
| Bridge | `bridge.ts` | identity/reuse (hostId, nonce), request/response/state/refusal, keyed reassembly, gate registry, disposal with failure report, one memoized `getWebpackRuntime` |
| Ownership primitives | `ownership.ts` | unchanged API; release edge cases refuse instead of claiming success |
| Steam component resolution and tree helpers | `gate-helpers.ts` | unchanged API plus `uniqueSteamFunction` (moved from components), `steamAccentDescription(react, text, accent)`, `steamDocuments` store; author-token fingerprints |
| QAM validators | NEW `qam-validators.ts` | pure `normalize*` functions and `isBusy`; type and shape checks only; no product caps |
| QAM hooks and colour math | NEW `qam-hooks.ts` | `useEchoedValue`, `useTrailingCommit`, `localizeOr`, `rgbToHsv`, `hsvToRgb`, `rgbCss` |
| QAM rows | NEW `qam-rows.ts` | `QamRowDefinitions` (patch ids, commands) and row factories `create*Control(host, controlRuntime)`; one `createChoiceControl` with a description function; one `sendPending` |
| QAM sections | NEW `qam-sections.ts` | `createQamSectionComposer(host)` driven by the layout publication; `createValveFpsFilter()` used only when `hideValveFpsRows` |
| Component host | `components.ts` (stays last) | owner state (registrations, listeners, accepted states, outcomes, summaries, drawn kinds), `createControlRuntime`, `resolveControls`, `ensurePatched`, tab-array transform, install/remove/status/dispose |
| Settings renderer | `settings.ts` | rows, `useSteamSettingDrafts`, colour parser that never invents white |
| Kit, icons, picker, capsule, page gate | unchanged files | fixes only |

`components.ts` dissolution, old symbol to new owner (every declaration in the closure):

| Old symbol (components.ts) | New owner |
| --- | --- |
| `registrations`, `listeners`, `notify`, `subscribeHost` | `components.ts` host |
| `runtime`, `controlRuntime`, `performanceRoot`, `quickSettingsRoot`, `quickSettingsWrapCache`, `MemoName`, `disposedHost`, `lastPatchError` | `components.ts` host |
| `autoTdpControl` ... `powerLimitControl`, `valveProfileHeaderControl`, `valveProfileToggleControl`, `valveResetControl`, `valveRefreshRateControl`, `valveOverlayLevelControl` | `components.ts` host: one `controls` map keyed by kind, filled in `resolveControls` |
| `appendDiagnostics`, `renderOutcomes`, `summaries`, `summarize`, `drawnKinds`, `layoutQueued`, `setDrawn`, `drew`, `note` | `components.ts` host, passed to rows as `host` context; cleared on dispose/last remove (TOOLKITJS-030) |
| `definitions` | `qam-rows.ts` `QamRowDefinitions` (frozen data; `panelFolds` entry stays) |
| `sendCommand`, `toggleCommand` | `components.ts` host context |
| `panelFolds`, `normalizePanelFoldsState`, `isFolded`, `setFolded` | `components.ts` host context (one `createSteamFolds`) |
| `uniqueFunction` | `gate-helpers.ts` `uniqueSteamFunction` |
| `createControlRuntime` | `components.ts` host |
| `normalizeText`, `validEnum`, `normalizeVrrState`, `normalizeAutoTdpState`, `normalizeControllerState`, `normalizePerformanceCommon`, `normalizeResolutionState`, `normalizeAudioFormatState`, `normalizeDeviceRange`, `normalizeDeviceControlsState`, `normalizeFrameLimitState`, `normalizePowerProfileState`, `normalizeCpuBoostState`, `normalizePowerPresetState`, `normalizePowerLimitRange`, `normalizePowerLimitState`, `isBusy` | `qam-validators.ts` (top-level, pure) |
| `normalizeOverrideId`, `OverrideColor`, `overrideDescription` | deleted; replaced by `accent` + `gate-helpers.ts` `steamAccentDescription` (R5) |
| `acceptedStates`, `useSemanticState` | `components.ts` host context |
| `useEchoedValue`, `useTrailingCommit`, `localizeOr`, `rgbToHsv`, `hsvToRgb`, `rgbCss`, `currentRefreshNotch` | `qam-hooks.ts` |
| `createVrrControl`, `createAutoTdpControl`, `createChoiceControl`, `createPowerProfileControl`, `createHybridCoreControl`, `createCpuBoostControl` (merged), `createPowerPresetControl`, `createControllerControl`, `createResolutionControl`, `createSettingsSectionsControl`, `createAudioFormatControl`, `createFrameLimitControl`, `createPowerLimitControl`, `createDeviceControlsControl`, `withIcon` | `qam-rows.ts` |
| `NativeFpsTokens`, `filteredNative`, `nativeFpsLabels`, `lastHidden`, `descendCache`, `hideNativeRows`, `withNativeRowsHidden` | `qam-sections.ts` `createValveFpsFilter()` (state inside the factory; counted per wrapper render, fixing U03B-SUTS-007) |
| `SectionIcons`, `RowGroups`, section order arrays (L2149, L2196-2202), "Reset"/"Profile scope" special cases | WSGM layout data (published); `qam-sections.ts` reads it |
| `sectionIcon`, `sectionSummary`, `hostSection`, `describe`, `appendControls` | `qam-sections.ts` composer |
| `controlRows` | `components.ts` host (kind to component map; order/placement from layout) |
| `resolveControls`, `ensurePatched`, `SteamUiPerformanceRoot`, `wrappers`, `transformTabs`, `install`, `remove`, `status`, `disposeHostResources`, `registerGate("nativeComponents", ...)` | `components.ts` host |

Name rule: new top-level names carry a `qam`/`Qam` prefix so they cannot collide with WSGM fragments; every name
in TOOLKITJS-028's consumer list is kept.

Public contract changes (toolkit 0.2.0) and consumers that must change:

| Change | Toolkit files | Consumers |
| --- | --- | --- |
| Layout publication `steam-ui.quick-access-layout` {perf: sections[], quickSettings: sections[], hideValveFpsRows}; section {id, title, icon, folds, kinds[]} | JS `qam-sections.ts`, `components.ts`; C# new `SteamQuickAccessLayout` record and publisher module, registration with the row modules | WSGM `NativeQamSemanticServices.cs` (or a small layout provider), `SteamUiSessionHost.cs` module wiring |
| `accent` replaces `overrideId`/`ModeOverrideId`/`AcOverrideId`/`BatteryOverrideId` and `SteamSettingsRow.Override` | JS rows, `settings.ts`; C# `SteamVariableRefreshRow`, `SteamFrameLimitRow`, `SteamControllerTargetRow`, `SteamCpuBoostRow`, `SteamPowerPresetRow`, `SteamPowerLimitSurface`, `SteamDeviceControlsRow`, `SteamSettingsRows` | WSGM projections in `NativeQamSemanticServices.cs`, `NativeQamPowerPresetService.cs`, graphics/settings page row builders feeding `wsgm-graphics.ts`/`wsgm-settings.ts` |
| Option `selectable:false` replaces `"custom"` | JS `normalizePowerPresetState`, preset row; C# `SteamPowerPresetRow` L90 | WSGM `DevicePowerAssignments.cs` L78-79, L234, `NativeQamPowerPresetService.cs` L43-45 |
| Controller restart sentence removed from toolkit | JS L1133 | WSGM appends it to `statusText` |
| `persistence` removed | JS frame-limit row; C# `SteamSurfaceModule.TryReadValueWrite`, `SteamFrameLimitRow`, `SteamSettingPersistence` | WSGM `SetFrameLimitAsync` signature |
| Range value never gates the row; 200 W/1000 FPS/100/8-target/64-char caps removed | JS validators; C# `SteamPowerLimitSurface` L151 and row docs | WSGM `NativeQamSemanticServices.cs` L891 duplicate check |
| Library badge: no default label | JS `library-badge.ts` L54-55; C# `SteamLibraryBadgeSurface` default | WSGM publishes its label |
| `SteamSettingsView` `onChange` returns a promise | `settings.ts` | `wsgm-settings.ts`, `wsgm-graphics.ts` drop the fake-revision workaround |
| Bridge config `hostId`, `nonce`; `installResult.priorDisposeFailures`, `foreign` | `bridge.ts`, `types.ts` | C# `SteamUiBridgeIdentity`/bootstrap (toolkit C# domain) |
| `steamUiFragments()` export | `build-prelude.mjs` | WSGM `build-steam-assets.mjs` |

---

## 5. Implementation batches

Every batch: toolkit commit first, then the parent regenerates `NativeQamBootstrap.js` and its hash
(`npm run steam-assets:build`), runs `npm run steam-assets:check` and `npm run steam-assets:claims`. JS-only
toolkit validation is `npm run prelude:claims` in `external/steam-ui-toolkit`.

### TOOLKITJS-B1: Build and check infrastructure (about 600 lines)

- Files: toolkit `eng/build-prelude.mjs`, `eng/check-harness.mjs`, `eng/run-checks.mjs`, every check that uses
  `sharedFragments` or a cross-file slice (`check-ownership-claims`, `check-sound-overrides`, `check-startup`,
  `check-ui-kit`, `check-settings-fields`, `check-extension-surfaces`, `check-library`), `package.json`,
  `package-lock.json`; parent `eng/build-steam-assets.mjs`, `eng/check-steam-module-discovery.mjs`, root
  `package.json`, regenerated asset and hash.
- Steps: export `steamUiFragments(extra)` with `// @fragment` markers and `process.execPath`; harness
  `fragment(asset, path)`; run-checks discovers `check-*.mjs`; replace cross-file slices; WSGM builder imports the
  list and drops plugin-directory discovery and stale comments; `steam-assets:claims` calls `run-checks.mjs` with
  the composed asset; package name.
- Dependencies: WSGM eng owner agrees on the root script change.
- Tests: all 16 checks against the prelude and against `NativeQamBootstrap.js`;
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamUiAssetTests"`.

### TOOLKITJS-B2: Bridge and core primitives (about 350 lines)

- Files: `bridge.ts`, `module-resolver.ts`, `ownership.ts`, `rpc.ts`, `check-startup.mjs`, `check-ownership-claims.mjs`.
- Steps: keyed reassembly (TOOLKITJS-001); dispose clears `gates`, refuses later deliveries, returns failed gate
  names in `installResult`; memoized `getWebpackRuntime`; remove registry/token caps; release edge cases
  (TOOLKITJS-020); comment fixes; convert ownership check to `node:assert`.
- Dependencies: C# reads `installResult` (adding a field is compatible; logging it is the toolkit C# domain's
  follow-up).
- Tests: interleaved part sets, post-dispose delivery refused, accessor without original refuses;
  `dotnet test external/steam-ui-toolkit/tests/SteamUiToolkit.Tests/SteamUiToolkit.Tests.csproj --filter
  "FullyQualifiedName~SteamUiBridgeHostTests"`.

### TOOLKITJS-B3: Gate lifecycle, caps and comments (about 1,100 lines)

- Files: `gates/home-carousel.ts`, `library-badge.ts`, `navigation.ts`, `storage.ts`, `performance.ts`, `audio.ts`,
  `bluetooth.ts`, `brightness.ts`, `screensaver.ts`, `pages.ts`, `power-menu.ts`, `sound-overrides.ts`,
  `game-context-menu.ts`, `icons.ts`, `ownership.ts` (iterator), matching checks; WSGM `SteamUiAssetTests.cs`;
  toolkit C# `SteamSoundOverrideSurface` docs and `SteamStorageSurface` probe if it also invokes exports.
- Steps: release-then-forget in ten gates; storage selects the provider by source; remove content caps
  (TOOLKITJS-005 rows except QAM validators, which move in B5a); per-React glyph cache; shared route-list helper
  for pages/home; one walk bound; module-id, minified-name and product-name comments removed; harness
  `assertRemoveRetries` used by every gate check.
- Dependencies: none beyond B1.
- Tests: retry-after-failed-release for each gate; sound override set of 200 resources and 20 variants loads;
  `dotnet test ... --filter "FullyQualifiedName~SteamStorageTests|FullyQualifiedName~SteamLibraryBadgeTests|FullyQualifiedName~SteamHomeCarouselTests|FullyQualifiedName~SteamNavigationPanelTests|FullyQualifiedName~SteamScreensaverTests"`;
  WSGM `FullyQualifiedName~SteamUiAssetTests`.

### TOOLKITJS-B4: Component host split, behaviour identical (about 1,400 moved lines, under 200 changed)

- Files: `components.ts`, new `qam-validators.ts`, `qam-hooks.ts`, `qam-rows.ts`, `qam-sections.ts`,
  `gate-helpers.ts` (`uniqueSteamFunction`), `check-power-profile.mjs`, `check-startup.mjs`.
- Steps: pure move per the Section 4 table, keeping today's literals (layout extraction is B5a); merge cpu boost
  into `createChoiceControl`; one `sendPending`; clear host state on dispose; checks instantiate fragments.
- Dependencies: B1. Verify WSGM `eng/check-steam-fingerprints.mjs` still finds every conjunction after the move
  (it extracts them from sources); read-only, needs a Steam install on disk.
- Tests: prelude:claims; WSGM steam-assets checks; emitted asset diff limited to markers and moved order.

### TOOLKITJS-B5a: Policy extraction and readback-free rows, toolkit side (about 1,400 lines; split into two commits if larger)

- Files: `qam-validators.ts`, `qam-rows.ts`, `qam-sections.ts`, `components.ts`, `settings.ts`, `library-badge.ts`,
  `gate-helpers.ts`; C# Surfaces listed in the Section 4 contract table, new layout record/module,
  `SteamSurfaceModule.cs`; toolkit C# tests and checks; version 0.2.0.
- Steps: layout publication drives sections; `hideValveFpsRows`; `accent`; `selectable`; remove persistence,
  caps and the restart literal; range rows never vanish; badge no default label; `isNavigableRoute` rejects
  control characters.
- Dependencies: toolkit C# Surfaces domain (same repo, same batch); parent stays on the old gitlink until B5b.
- Tests: checks use WSGM's exact layout as a fixture and assert today's titles/order/icons; off-step and null
  values keep the row; `selectable:false` option shown, never sent;
  `dotnet test ... --filter "FullyQualifiedName~SteamPerformanceTests|FullyQualifiedName~SteamChoiceRowTests|FullyQualifiedName~SteamQuickAccessRowPatchTests|FullyQualifiedName~SteamPanelFoldsTests|FullyQualifiedName~SteamSettingsRowsTests|FullyQualifiedName~SteamLibraryBadgeTests"`.

### TOOLKITJS-B5b: WSGM consumer of the new presentation contract (about 600 lines)

- Files: `NativeQamSemanticServices.cs`, `NativeQamPowerPresetService.cs`, `DevicePowerAssignments.cs`,
  `SteamUiSessionHost.cs` (layout module), page row builders for graphics/settings, regenerated asset and hash,
  `SteamUiAssetTests.cs`, NativeQam tests.
- Steps: publish today's layout with today's strings as ids; build accented descriptions; `selectable:false`
  for custom; restart sentence in status; drop duplicate 200 W check; keep the display fallback value.
- Dependencies: B5a published as an interim child (plan I01); WSGM Steam UI host domain owns
  `SteamUiSessionHost`.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~NativeQam|FullyQualifiedName~SteamUiAssetTests|FullyQualifiedName~QuickAccessFolds"`;
  manual M01-25 (QAM appearance identical).

### TOOLKITJS-B6: Drafts and row refusals (about 500 lines)

- Files: `settings.ts`, `qam-rows.ts`, `gates/extensions-tab.ts`, `check-settings-fields.mjs`,
  `check-extension-surfaces.mjs`, `check-power-profile.mjs`; WSGM `wsgm-settings.ts`, `wsgm-graphics.ts`.
- Steps: `useSteamSettingDrafts`; `onChange` returns a promise; refusal drops only that row's draft; rows show a
  refusal in the description (power-limit pattern) for U03B-SUTS-019; Extensions panel drops its duplicate
  subscription; WSGM removes the fake-revision workaround.
- Dependencies: B4, B5a/B5b ordering only for file overlap.
- Tests: rejected change restores the published value without touching another row's draft; WSGM
  `FullyQualifiedName~SteamUiAssetTests`.

### TOOLKITJS-B7: Bridge identity (about 150 JS lines)

- Files: `bridge.ts`, `types.ts`, `check-startup.mjs`.
- Steps: R17.
- Dependencies: toolkit C# bridge/transport domain supplies `hostId`, `nonce` in configuration and checks the
  echoed nonce (plan T01).
- Tests: same host replaces, foreign host refused, stale nonce acknowledgement rejected;
  `dotnet test ... --filter "FullyQualifiedName~SteamUiBridgeHostTests|FullyQualifiedName~SteamUiBridgeAuthorizerTests"`.

### TOOLKITJS-B8: File picker (about 400 lines across JS and C#)

- Files: `file-picker.ts`, new `eng/check-file-picker.mjs`; C# `SteamFilePickerSurface.cs` (worker enumeration,
  cancellation, complete sorted result); WSGM path policy only if the C# domain moves it.
- Steps: R2; unmount settles null once; 200-row render pages with `renderSteamUiMore`; primary-button fallback;
  ticket `listPlaces`.
- Dependencies: toolkit C# Surfaces domain and the maintainer answer in Section 6 Q1.
- Tests: 450-entry fixture renders 200/400/450 rows after two "Load More"; enter two directories, Back, stale
  listing ignored, selection pending; cancel and unmount each settle null once;
  `dotnet test ... --filter "FullyQualifiedName~SteamFilePicker"`; manual M01-31/32.

### TOOLKITJS-B9: Themes, portals and status cost (about 350 lines)

- Files: `gates/theme-styles.ts`, `gate-helpers.ts` (`steamDocuments`), `components.ts` (notify on root mount),
  `gates/navigation.ts` (notify on host adoption), `gates/home-carousel.ts` (status via cached adoption, report
  from effect), checks.
- Steps: R20; clear pattern cache on removal; status reads cached counts; comment fixes.
- Dependencies: B4.
- Tests: a QAM portal document created after install receives styles on mount without a publication;
  `dotnet test ... --filter "FullyQualifiedName~SteamThemeStyleTests|FullyQualifiedName~SteamHomeCarouselTests"`;
  manual M01-27.

### TOOLKITJS-B10: Fingerprints without minified tokens (about 150 lines, attended)

- Files: `gate-helpers.ts`, `settings.ts`, `check-startup.mjs` fixture.
- Steps: TOOLKITJS-006 replacements.
- Dependencies: maintainer-run live Steam validation (AGENTS rule); no CEF during Steam cold start.
- Tests: prelude:claims; live probe of localizer, value field, buttons and tabs on the current client.

### TOOLKITJS-B11: Probe execution coverage (about 900 lines, C# tests)

- Files: toolkit `tests/SteamUiToolkit.Tests/*` per surface, shared JS page models.
- Steps: run every surface probe expression through `NodeScript` against absent, ambiguous and already-owned
  models (U03B-SUTS-006, U02B-SUTC-005/006).
- Dependencies: toolkit C# test owner.
- Tests: `dotnet test ... --filter "FullyQualifiedName~Probe"`.

### TOOLKITJS-B12: Docs and formatting (about 300 lines plus mechanical format)

- Files: toolkit `docs/reference.md` (script API list, check map), `README.md`, Prettier config; AGENTS check map
  as a separate guidance proposal (plan requires sign-off).
- Dependencies: last child edit before plan I02.
- Tests: prelude:claims after formatting.

---

## 6. Risks and open questions

Risks:

- Running all 16 checks on WSGM's Prettier-formatted asset (B1) may expose latent failures that the 5-check
  subset hid; fix in B1, do not narrow the suite.
- The B4 split must keep fingerprint conjunctions discoverable by WSGM `eng/check-steam-fingerprints.mjs`.
- B5a/B5b change the presentation contract; WSGM must publish the layout before the new toolkit asset ships or
  the Quick Access sections render empty. Ship B5b in the same parent commit as the gitlink bump.
- B10 changes how every QAM row resolves; it must be validated live by the maintainer on the current client.

Open questions for the maintainer:

1. File picker paging (plan B2, M01-31) adds a visible "Load More" button at 200 entries, while requirement 9
   keeps workflows identical. Accept the paging, or keep one scrolling list and only move enumeration off the
   request thread?
