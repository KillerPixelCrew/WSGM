# SteamUiToolkit JavaScript and TypeScript findings

Scope: the injected Steam UI script in the SteamUiToolkit child (`SteamUiAssets/Source/**`: bridge, ownership,
module resolver, gate helpers, component host, settings renderer, UI kit, icons, file picker, all 17 gates), the
toolkit's `eng/*.mjs` build and check scripts, its `package*.json` and CI, and the WSGM side that composes and checks
the shipped asset (`eng/build-steam-assets.mjs`, root `package.json`, `NativeQamBootstrap.js`,
`tests/WSGM.Tests/Core/SteamUiAssetTests.cs`, the WSGM fragments `wsgm-settings.ts` and `wsgm-graphics.ts`).
Baseline: toolkit `main` 388dd1b, parent `master` 1329813f.

Path shorthand used below: `Source/...` means `external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/...`,
`toolkit/...` means `external/steam-ui-toolkit/...`. Every other path is relative to the repository root. Line
numbers are the reviewers' (verifier-corrected where noted); anchor every edit by symbol, not by line.

Counts: 47 ids. 43 are findings below (0 critical, 0 high, 8 medium, 25 low, 10 nit); 4 are refuted or no-change
(listed at the end). Plan v2 batches covering this area: B032 (asset size bound), B047 (check infrastructure), B049
(storage resolution), B050 (bridge and module resolution), B051 (gate lifecycle, content caps, missing checks), B055
(component host split), B057 (host-supplied Quick Access presentation; D13 decided: the badge shows the library's name), B058 (drafts and refusals),
B059 (file picker), B060 (theme cache, carousel and small items), B061 (fingerprints, attended), B062 (probe
execution coverage, no TOOLKITJS id), B177 (docs). Every toolkit batch commits and pushes the child on `main` first,
then one parent commit carries the gitlink, the regenerated `NativeQamBootstrap.js`, its hash in
`src/WSGM/Core/SteamUiAssetCatalog.cs` and any consumer edits. Toolkit validation for every batch is
`npm run prelude:claims` in `external/steam-ui-toolkit`, then `npm run steam-assets:build`,
`npm run steam-assets:check` and `npm run steam-assets:claims` in the parent.

Review refinements that plan v2 and the critic dropped, so no batch implements them: bridge `hostId` and
per-bootstrap nonce (review R17 and its batch B7; a restarted WSGM would be refused as foreign by its own previous
bridge; critic conflict 6, plan v2 section 1; DECISIONS.md drops all same-user hardening as security theater), the cross-gate `steamDocuments` notification store for late portals
(R20; no live evidence of an unstyled late portal yet), file-picker paging with a "Load More" control (R2; the picker
renders the whole list today; critic conflict 7; DECISIONS.md: no file picker paging), a fold-id migration (none needed, R1, critic conflict 8), a host
label hook for kit chrome strings (R3; U03B-SUTS-041 accepted no-change) and a toolkit-wide Prettier pass
(TOOLKITJS-027).

## Medium

### TOOLKITJS-001: Interleaved multi-part deliveries cancel each other

- **Severity:** medium
- **Where:** `Source/bridge.ts:37-39` (`let assembling` single slot), `Source/bridge.ts:206-239` (`deliverPart`);
  C# `toolkit/src/SteamUiToolkit/SteamUiBridge.cs:587-620` (`DeliverAsync`, called from `RespondAsync` L434-461 and
  `PublishStateAsync` L506-556 with no shared gate), `toolkit/src/SteamUiToolkit/SteamUiModuleRuntime.cs:78` and
  `:168` (publication and request answers run on separate tasks).
- **Problem:** Any envelope over 256 K characters is sent as numbered parts. The page keeps exactly one reassembly
  slot: part 0 of a new delivery replaces it, and a part with any other id resets it to null and is refused. A large
  response (file-picker listing, Extensions tab answer) in flight while a large state (theme styles, sound overrides,
  settings pages) is published makes both fail. A refused state is republished next round, but a refused response is
  lost: the page waits out the 5 s request timeout and rejects with "Steam UI bridge request timed out".
- **Best solution:** Key reassembly by delivery id. Replace `assembling` with
  `const assembling = new Map<number, {count: number; parts: string[]}>()`. In `deliverPart`, after the existing
  shape validation: on `index === 0` set a fresh entry for `part.id`; otherwise read the entry for `part.id`; if it
  is missing, its `count` differs or `parts.length !== part.index`, delete that id's entry and return false (other ids
  are untouched); push the text; when complete, delete the entry, then `deliver(JSON.parse(text))` as today. `dispose`
  calls `assembling.clear()`. No cap on entries: C# ids come from `Interlocked.Increment`, and an entry abandoned by
  a failed C# delivery dies with the document. This beats serializing `DeliverAsync` in C#, which would queue every
  small response behind multi-megabyte publications.
- **Tests:** add a `toolkit/eng/check-startup.mjs` case that feeds two part sets interleaved (A0, B0, A1, B1, ...)
  and asserts both envelopes are delivered, plus a case where a wrong index for one id leaves the other id's
  reassembly intact. Run `npm run prelude:claims` in the child and
  `dotnet test external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj --filter "FullyQualifiedName~SteamUiBridgeHostTests"`.
- **Plan v2:** B050.
- **Related:** NEW (no ledger id). Interacts with TOOLKITJS-V-008 (whole file-picker listings make large responses
  more common).

### TOOLKITJS-003: WSGM product policy inside the generic component host and settings renderer

- **Severity:** medium
- **Where:** `Source/components.ts:1898-1905, 2094-2096, 2216-2223` (Valve FPS rows hidden whenever host perf rows
  exist, motivated by RTSS); `Source/components.ts:296-309, 1738-1744` and `Source/settings.ts:193-204` ("Game
  override" text and `#1a9fff`); `Source/components.ts:996-998, 1036, 1044` (`"custom"` preset sentinel);
  `Source/components.ts:1133` ("Restart the application to rebind."); `Source/components.ts:2019-2063, 2149,
  2196-2202` (section titles, icons, grouping and order keyed by English titles); `Source/components.ts:2084-2085`
  and `Source/gate-helpers.ts:247-253` (fold ids are the titles); `Source/components.ts:1017` ("Manual selection").
  Consumers: `src/WSGM/Shell/NativeQamSemanticServices.cs`, `src/WSGM/Shell/NativeQamPowerPresetService.cs:43-45`,
  `src/WSGM/Shell/DevicePowerAssignments.cs:78-79, 234`, `src/WSGM/Shell/SteamUiSessionHost.cs`, the graphics and
  settings page row builders that feed `wsgm-graphics.ts` and `wsgm-settings.ts`.
- **Problem:** The generic library hard-codes WSGM's Quick Access layout, its per-game-profile vocabulary, its RTSS
  policy and a magic preset id. Any other consumer gets WSGM's sections and labels, and WSGM cannot change its own
  presentation without a toolkit release.
- **Best solution:** Move only policy, keep generic row labels ("Display resolution", "Battery charge limit", ...)
  as toolkit defaults. One commit pair (toolkit child, then parent with WSGM consumers, B5a and B5b merged so no
  parent gitlink lands between them):
  1. Layout publication `steam-ui.quick-access-layout`: `{perf: Section[], quickSettings: Section[],
     hideValveFpsRows: boolean}` with `Section = {id, title, icon, folds, kinds[]}` plus Reset placement. C# adds one
     `SteamQuickAccessLayout` record and a publisher registered with the row modules (in the single module builder
     B056 leaves). `qam-sections.ts` composes sections only from this data; `SectionIcons`, `RowGroups`, the order
     arrays and the "Reset"/"Profile scope" special cases are deleted from the toolkit. WSGM publishes today's exact
     strings as section ids ("Profile scope", "Power profiles", "Display and frame rate", "Power limits",
     "Controller", "Display", "Audio", "Charging", "RGB lighting", `settings.<pageId>`, `extensions:<id>[:<key>]`),
     so `QuickAccessFolds` keeps working with no migration. Put the constant layout in a new
     `src/WSGM/Shell/NativeQamLayout.cs` (pure data) and register its publisher in `SteamUiSessionHost`.
  2. `hideValveFpsRows` (default false; WSGM sends true) gates `createValveFpsFilter()`; with false the Valve FPS rows
     render untouched.
  3. `accent: boolean` plus a host-built description replaces every `overrideId` field
     (`ModeOverrideId`, `AcOverrideId`, `BatteryOverrideId`) and `SteamSettingsRow.Override`. WSGM writes
     "Game override · <status>" (or "Game override") itself; the toolkit renders an accented description through
     one `steamAccentDescription(react, text, accent)` in `gate-helpers.ts` using one shared accent constant
     (`#1a9fff`, also used by `settings.ts`). `normalizeOverrideId`, `OverrideColor`, `overrideDescription` and
     `SteamSettingOverrideColor` are deleted. The lighting Edit-color description is TOOLKITJS-V-007.
  4. `selectable: false` on an option replaces the `"custom"` sentinel, with the semantics in TOOLKITJS-V-006.
  5. The controller restart sentence is deleted from the toolkit; WSGM appends it to the controller row's
     `statusText` when a restart is required.
  6. "Manual selection" stays a generic toolkit default label (not policy).
  Toolkit package version goes to 0.2.0 with B056.
- **Tests:** a golden WSGM test that the published layout equals today's titles, ids, icons, order, membership and
  folds; toolkit checks feed WSGM's exact layout as fixture data and assert the rendered sections; `hideValveFpsRows:
  false` leaves Valve FPS rows; accented descriptions render in the accent colour. Run
  `dotnet test external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj --filter "FullyQualifiedName~SteamPerformanceTests|FullyQualifiedName~SteamChoiceRowTests|FullyQualifiedName~SteamQuickAccessRowPatchTests|FullyQualifiedName~SteamPanelFoldsTests|FullyQualifiedName~SteamSettingsRowsTests|FullyQualifiedName~SteamLibraryBadgeTests"`
  and `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~NativeQam|FullyQualifiedName~SteamUiAssetTests|FullyQualifiedName~QuickAccessFolds"`.
  Manual M01-25 (Quick Access appearance identical).
- **Plan v2:** B057 (after B055 and B056).
- **Related:** U03B-SUTS-002, U03A-SUTS-005, U03B-SUTS-008, U03A-SUTS-015, U03A-SUTS-028, TOOLKITCS-047, DEVICE-025
  (library side), critic conflict 8 (no fold migration), critic conflict 21 (B5a/B5b merged); depends on
  TOOLKITJS-004; includes TOOLKITJS-V-006, TOOLKITJS-V-007; TOOLKITJS-014 rides in the same batch.

### TOOLKITJS-004: `createNativeComponentHost` is one 2,490-line closure

- **Severity:** medium
- **Where:** `Source/components.ts:1-2490` (mutable bindings L2-42, L1906-1910, L2092; validators, rows, hooks,
  colour math, native-row filter, section composer, memo interception and diagnostics in one function);
  `toolkit/eng/build-prelude.mjs:40-52` and `eng/build-steam-assets.mjs:76-120` (fragment discovery).
- **Problem:** Every Quick Access change edits one closure with shared mutable state, which is why policy leaked in
  (TOOLKITJS-003) and why checks slice it by text. Both builders only discover `Source/*.ts` and
  `Source/gates/*.ts`, so a `Source/qam/` folder would be silently left out of the asset.
- **Best solution:** Pure move, behaviour and literals identical, into new top-level fragments (they sort after
  `module-resolver.ts`/`page-gate.ts` and before `settings.ts`, `ui-kit.ts`, `gates/` and the always-last
  `components.ts`):
  - `qam-validators.ts`: `normalizeText`, `validEnum`, every `normalize*State`/`normalize*Range` function and
    `isBusy`, as pure top-level functions.
  - `qam-hooks.ts`: `useEchoedValue`, `useTrailingCommit`, `localizeOr`, `rgbToHsv`, `hsvToRgb`, `rgbCss`,
    `currentRefreshNotch`.
  - `qam-rows.ts`: `QamRowDefinitions` (frozen data, today's `definitions` including `panelFolds`) and every
    `create*Control(host, controlRuntime)` factory plus `withIcon`. `createCpuBoostControl` merges into
    `createChoiceControl`, which takes a description function; the five copies of the pending-dropdown pattern use
    one `sendPending` helper (TOOLKITJS-016).
  - `qam-sections.ts`: the section composer (`sectionIcon`, `sectionSummary`, `hostSection`, `describe`,
    `appendControls`) and `createValveFpsFilter()` holding `NativeFpsTokens`, `filteredNative`, `nativeFpsLabels`,
    `lastHidden`, `descendCache`, `hideNativeRows`, `withNativeRowsHidden` as factory state. Keep today's
    `SectionIcons`/`RowGroups`/order literals here until B057 replaces them with the layout publication.
  - `gate-helpers.ts`: `uniqueFunction` becomes `uniqueSteamFunction`.
  - `components.ts` keeps only owner state: registrations, listeners, `notify`, `subscribeHost`, runtime and roots,
    one `controls` map keyed by kind filled in `resolveControls`, diagnostics (`appendDiagnostics`,
    `renderOutcomes`, `summaries`, `drawnKinds`, `note`, `drew`), `sendCommand`/`toggleCommand`, folds
    (`panelFolds`, `isFolded`, `setFolded`), `acceptedStates`/`useSemanticState`, `createControlRuntime`,
    `controlRows`, `ensurePatched`, `SteamUiPerformanceRoot`, `wrappers`, `transformTabs`, install, remove, status,
    `disposeHostResources` and `registerGate("nativeComponents", ...)`. It passes a `host` context object to rows.
  Constraints: the new fragments hold only function declarations and frozen literals, never a top-level `const`
  that reads a kit name at evaluation time (they precede `settings.ts`/`ui-kit.ts`, so that would be a TDZ error).
  New top-level names carry a `qam`/`Qam` prefix so they cannot collide with WSGM fragments, and every consumer name
  in TOOLKITJS-028 is kept. Do not add "clear host state on dispose" (TOOLKITJS-030 is refuted). Confirm
  `eng/check-steam-fingerprints.mjs` still finds every fingerprint conjunction after the move (read-only; needs a
  Steam install on disk).
- **Tests:** `toolkit/eng/check-startup.mjs` builds the host from the new fragments plus `components.ts` (through the
  B047 fragment accessor); `check-power-profile.mjs` reads fragments instead of slices. The emitted asset diff must
  be limited to fragment markers and moved order. Child `npm run prelude:claims`, parent steam-assets build, check
  and claims.
- **Plan v2:** B055 (after B051).
- **Related:** U03B-SUTS-003; prerequisite for TOOLKITJS-003, TOOLKITJS-016; uses TOOLKITJS-011's markers.

### TOOLKITJS-005: Arbitrary caps that drop or refuse valid content

- **Severity:** medium (verifier corrected the locations and removed one row)
- **Where:**

  | Site | Cap and effect |
  | --- | --- |
  | `Source/components.ts:347` | more than 8 controller targets: whole state null, controller row disappears |
  | `Source/components.ts:358, 867` | target/option id longer than 64: whole state null |
  | `Source/components.ts:329-335` | AutoTDP watts above 200 dropped |
  | `Source/components.ts:1486-1489` | power range above 200 W: range null (duplicated in C# `SteamPowerLimitSurface.cs:151` and WSGM `NativeQamSemanticServices.cs:891`) |
  | `Source/components.ts:583-591` | FPS above 1000: whole frame-limit state null |
  | `Source/module-resolver.ts:36-44` | over 32,768 registered modules throws for every surface; fingerprints over 16 tokens or tokens over 512 chars throw |
  | `Source/icons.ts:447` | host glyph path over 4,096 chars: glyph dropped |
  | `Source/gates/pages.ts:114`, `Source/gates/home-carousel.ts:401` | route list with 512 or more routes not recognised: pages and Home never claimed |
  | `Source/gates/power-menu.ts:21, 93` | menu with more than 48 children: entry never drawn |
  | `Source/gates/library-badge.ts:92, 220, 406, 451` | tile subtree over 64 children or stats section over 32: badge or stat skipped |
  | `Source/gates/sound-overrides.ts:32, 45, 53, 58` | 128 resources (set refused), 16 variants (name skipped silently), 1.4 MB per URL, 24 MB total (throws, every sound dropped) |

- **Problem:** Each cap silently removes real content or a whole control. The rule is type checks only; payloads
  are already chunked. `tests/WSGM.Tests/Core/SteamUiAssetTests.cs:28-29` pins two of the sound caps.
- **Best solution:** Delete each cap and keep only the type or shape check beside it:
  - Controller targets: no count limit; ids match `/^[A-Za-z0-9._-]+$/` (character set kept, length dropped) for
    both targets and options. In the same child commit drop `<= 64` from C# `SteamUiPayload.TryReadTarget` (keep
    `Length >= 1` and `ValidTargetId`) and its "1–64 characters" XML remark, so JS and C# accept the same ids
    (TOOLKITCS-V-004 rule). B051's file list names only `gates/*.ts` and `gate-helpers.ts`: add `components.ts`,
    `icons.ts` and `Surfaces/SteamUiPayload.cs` to it.
  - AutoTDP watts: any positive integer, else null.
  - Frame limit: drop the two `> 1000` comparisons; keep integer and non-negative.
  - `module-resolver.ts`: fingerprint must be a non-empty array of non-empty strings; delete the 16/512/32,768 limits.
    Keep the file a single plain-JS function (TOOLKITJS-V-002).
  - `icons.ts`: `SteamGlyphPattern` becomes `/^[MmLlHhVvCcSsQqTtAaZz0-9.,\-\s]+$/u`.
  - Route lists: one `isSteamRouteList(react, value, knownRoute)` in `gate-helpers.ts` (array, length above 2, some
    valid element whose `props.path === knownRoute`), used by `pages.ts` and by `home-carousel.ts`'s walk. This also
    removes the duplicated detection noted in TOOLKITJS-024.
  - `power-menu.ts`: delete `MaximumChildren`; recognition is `children.some(isPowerEntry)`.
  - `library-badge.ts`: call `mapChildren` without a maximum and drop the stats `MaximumChildren` test; the
    `MaximumDescent` depth bound stays.
  - `sound-overrides.ts`: delete the 128, 16, 1.4 MB and 24 MB checks; keep the file-name regex and the data-URL
    validator, which run before `fetch`. Remove the `url.length > 1400000` and `total > 24000000` assertions from
    `SteamUiAssetTests.cs` and keep the decoder and validator-order assertions. Fix the
    `SteamSoundOverrideSurface` docs that describe the caps.
  - The 200 W power range is removed in B057 together with its C# and WSGM duplicates (one positive-integer type
    check remains), because it crosses into C# and WSGM.
  Keep, because they are not content caps: the percent device range `maximum > 100` (`components.ts:486`; charge
  limit and lighting brightness are percentages, so 0-100 is the value's type), bridge `maximumPending`
  backpressure, the C# delivery part size, per-gate `MaximumDescent` depth bounds, `MaximumMountedNodes` (60,000)
  and home-carousel's 250,000 walk bound. Do not unify the two walk bounds downward (Home would not be found on a
  window with a whole library mounted); leave both.
- **Tests:** toolkit checks for 9 and 20 controller targets, a 65-character id, a 1,200 FPS cap, a 600-route list, a
  60-child power menu, a 5,000-character glyph path, and a sound set of 200 resources with 20 variants that loads.
  Add a `SteamUiPayloadTests` case: a 65-character target id is accepted, an empty one and one with a space are
  refused. Run child `npm run prelude:claims`, then
  `dotnet test external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj --filter "FullyQualifiedName~SteamStorageTests|FullyQualifiedName~SteamLibraryBadgeTests|FullyQualifiedName~SteamHomeCarouselTests|FullyQualifiedName~SteamNavigationPanelTests|FullyQualifiedName~SteamScreensaverTests|FullyQualifiedName~SteamUiPayloadTests|FullyQualifiedName~SteamChoiceRowTests"`
  and `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamUiAssetTests"`.
- **Plan v2:** B051 (module-resolver caps in B050; the 200 W triple in B057).
- **Related:** U03A-SUTS-019, U03A-SUTS-009, TOOLKITCS-V-004, CRIT-002 (the C# sound-pack caps, owned elsewhere),
  STEAMHOST-V-009, TOOLKITJS-024.

### TOOLKITJS-006: Fingerprints and comments written against minified output

- **Severity:** medium (verifier corrected the recommendation)
- **Where:** fingerprints `Source/gate-helpers.ts:383-388` (`isLocalizer`: `"void 0"`, `"!0)"`, `"!=null"`),
  `Source/settings.ts:41` (`valueField`: `"focusable:!0"`), `Source/gate-helpers.ts:168` (tabs: `"(function()"`),
  `Source/gate-helpers.ts:127, 130` (`'"DialogButton","_DialogLayout","Secondary"'`), `Source/settings.ts:44`
  (`'"DialogButton _DialogLayout Small"'`, the joined form); fixture `toolkit/eng/check-startup.mjs:149` (minified
  localizer body). Comments describing minified identifiers: `Source/gates/navigation.ts:5-19`,
  `Source/gates/pages.ts:6-12`, `Source/gates/library-badge.ts:7-16`.
- **Problem:** Toolkit AGENTS.md requires fingerprints to name tokens an author typed, never the identifiers or
  spacing a minifier chose. The localizer is load-bearing: when it last broke, every Quick Access row refused
  (`gate-helpers.ts:390-393`). The next client minifier change breaks it again.
- **Best solution:** Select by author-typed tokens only, never by invoking candidate exports (calling a candidate
  with a probe token executes unknown functions, which toolkit AGENTS.md and the parent CEF rule forbid, and which
  TOOLKITJS-018 removes from storage). Localizer: the unique function export of the localization module whose source
  contains `.LocalizeString(` and not `createElement`, distinguished from its siblings by author-level differences
  confirmed on the live client (declared arity via `Function.length`, the module's own literal strings), with
  ambiguity refused as today. Value field: `inlineWrap`, `"shift-children-below"` and `focusable` as separate tokens
  (today's `inlineWrap:"shift-children-below"` also bakes in the minifier's missing space), no `!0`. Dialog
  buttons: the author's class names as separate string tokens (`"DialogButton"`, `"_DialogLayout"`, `"Secondary"`,
  `"Primary"`, `"Small"`), no joined or minifier-spaced form; the existing uniqueness refusal handles a component
  that names both variants. Tabs: inside the module `findUnique([".TabRowTabs", "activeTab:"])` already identifies,
  the unique export whose `type` is a function (`typeof value?.type === "function"`), replacing the
  `"(function()"` text test; two fits are refused as today. Update the
  `check-startup.mjs` fixture to an authored-shape body. Rewrite the three comments to describe behaviour without
  minified names (the comment part lands in B051).
- **Tests:** child `npm run prelude:claims`; parent steam-assets build, check and claims; attended: the maintainer
  confirms localizer, value field, buttons and tabs resolve on the current client (never during a Steam cold start).
- **Plan v2:** B061 (fingerprints, attended), comment part in B051.
- **Related:** NEW; TOOLKITJS-018 (same no-invocation rule).

### TOOLKITJS-008: Gates forget ownership before a fallible release

- **Severity:** medium
- **Where:** `Source/gates/home-carousel.ts:502-518`, `Source/gates/library-badge.ts:340-350` and `:517-526`,
  `Source/gates/navigation.ts:376-388`, `Source/gates/bluetooth.ts:318-335`, `Source/gates/audio.ts:347-379`,
  `Source/gates/performance.ts:118-146`, `Source/gates/screensaver.ts:346-367`, `Source/gates/brightness.ts:129-138,
  174-191` (release result ignored), `Source/gates/storage.ts:317-326`.
- **Problem:** Ten gates set `installed = false` (and drop subscriptions) before the release that can fail. A failed
  release leaves the wrapper installed while every later `remove()` answers `{absent: true}`, so cleanup can never be
  retried. Worst case is storage: the `SendMsg` wrapper stays on the transport that carries every Steam service call
  and keeps answering `StorageDeviceManager.*` because it never checks `installed` (L236-244).
- **Best solution:** Fix the order in place in each gate, mirroring `pages.ts` `remove()` (L326-357): run every
  fallible release first (`releaseMember`, `withdrawNamespace`, `releaseAccessor`, `releaseValue`); on any failure
  set `lastError` and return `{ok: false, error}` with `installed` still true and nothing forgotten; only after all
  releases succeed clear `installed`, end subscriptions and reset cached state. Brightness makes `restoreSetter`
  return its release result and checks it beside the value release. The primitives already answer ok for a member
  that is no longer claimed, so a retry after a partial success releases only what is left. No extra `installed`
  test inside storage's `SendMsg` wrapper: with this order the wrapper is on the transport only while the gate is
  installed. No shared lifecycle framework.
- **Tests:** add `assertRemoveRetries(gate, failOnce)` to `toolkit/eng/check-harness.mjs`: it makes the gate's
  release fail once, asserts the first `remove()` reports failure and the second retries and succeeds. Use it in every
  gate check (with the new audio, performance and network checks from TOOLKITJS-V-003). Filters as TOOLKITJS-005.
- **Plan v2:** B051.
- **Related:** U03B-SUTS-001, U03A-SUTS-001, U03A-SUTS-002, U03A-SUTS-007, U03B-SUTS-027; TOOLKITJS-V-003.

### TOOLKITJS-009: WSGM checks its shipped asset with 5 of 16 toolkit checks

- **Severity:** medium
- **Where:** root `package.json:8` (`steam-assets:claims`); `toolkit/eng/run-checks.mjs:33-40` (asset-path branch).
- **Problem:** WSGM runs only ownership-claims, sound-overrides, power-profile, startup and service-gates on
  `NativeQamBootstrap.js`. Navigation panel, settings fields, pages, storage, library, home carousel, screensaver,
  extension surfaces, power menu, theme styles and UI kit are never checked against the asset WSGM ships, which is
  Prettier-formatted unlike the prelude.
- **Best solution:** `"steam-assets:claims": "node external/steam-ui-toolkit/eng/run-checks.mjs
  src/WSGM/Core/SteamUiAssets/NativeQamBootstrap.js && node eng/check-steam-module-discovery.mjs"`. `run-checks.mjs`
  already checks a given asset without building. Fix any latent failure the full suite exposes in the same batch; do
  not narrow the suite.
- **Tests:** `npm run steam-assets:claims` passes all checks;
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamUiAssetTests"`.
- **Plan v2:** B047.
- **Related:** NEW; requirement 16 (independent child validation), TOOLKITJS-012.

### TOOLKITJS-V-001: Cached resolvers make a cold-start module failure permanent

- **Severity:** medium (verifier-found)
- **Where:** `Source/module-resolver.ts:13, 24-34` (`failed` Set, "previously failed"); per-gate caches
  `Source/gates/audio.ts:107-118`, `Source/gates/brightness.ts:43-54`, `Source/gates/elements.ts:10-11`;
  `Source/bridge.ts:46` (`getWebpackRuntime`).
- **Problem:** A module whose factory throws once (a dependency chunk not ready during Steam's cold start) goes into
  `failed`, and every later resolution through that resolver throws "Steam module resolution previously failed" for
  the life of the bridge, even when the exports Steam's loader kept for that module are usable. The audio, brightness and elements gates already never recover from that; memoizing one
  resolver per bridge (TOOLKITJS-017) would extend it to every gate, including the Quick Access host, which recovers
  today only because it builds a fresh resolver on each `ensurePatched`.
- **Best solution:** Delete the `failed` Set from `module-resolver.ts`: `requirePresent` calls `runtime(id)` every
  time and wraps a throw in "Steam module resolution failed: <id>: <error>". Steam's loader does not re-run a factory
  that threw: it stores the module record before calling the factory (the loader shape `check-startup.mjs:24-41`
  records from a live session), so a later `runtime(id)` returns whatever exports that factory set, the same object
  Steam's own code now uses. The resolver then hands that object to the existing shape tests (`exported`,
  `uniqueSteamExport`, `resolveReact`), which accept a complete export or refuse with "export absent", instead of
  refusing for the bridge's life with "previously failed". Memoize the resolver in `bridge.ts` (TOOLKITJS-017); the
  per-resolver `sources` WeakMap stays and becomes shared, which saves repeated `toString` work.
- **Tests:** replace the `check-startup.mjs:74-79` "previously failed" pin, using the existing fixture loader: a
  factory sets `exports.ready = true` and then throws; the first resolution throws "resolution failed"; the next
  resolution through the same resolver returns exports with `ready === true` and the factory ran once. Run child
  `npm run prelude:claims` and
  `dotnet test external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj --filter "FullyQualifiedName~SteamUiBridgeHostTests|FullyQualifiedName~SteamCefTests|FullyQualifiedName~Probe"`
  (no test name contains `ModuleResolver`; the resolver-resource test is
  `SteamCefTests.ResolverResourceEncodesScopeAndContainsSharedDiscoveryBoundary`).
- **Plan v2:** B050.
- **Related:** TOOLKITJS-017, TOOLKITJS-V-002, U02A-SUTC-005.

## Low

### TOOLKITJS-002: Toolkit rows vanish without readback

- **Severity:** low (verifier lowered from medium: no WSGM row vanishes today because WSGM fills the value itself)
- **Where:** `Source/components.ts:1478-1509` (`normalizePowerLimitRange` requires an in-range, on-step
  `observedWatts`, else the range is null and the slider removed at L1576); `:473-513` and `:1655-1660, 1682, 1706`
  (device ranges refuse off-step or out-of-range desired/observed and hide the slider when both are null);
  `:1117-1122` (controller row needs an observed or selected target among the options); `:1313` (frame-limit row
  notes "no observed or desired fps" and draws nothing); pinned by `toolkit/eng/check-power-profile.mjs:561-563`.
  WSGM compensates in `src/WSGM/Shell/NativeQamSemanticServices.cs:903-906` and `:1184-1185`
  (`observed ?? desired ?? maximum`) and `:1144-1145` (`?? 0xFFFFFF`).
- **Problem:** The library gates a control on readback, against the rule "never gate a write or a control on
  readback". Any consumer that publishes an honest null or an off-step reading loses the control, and the only way to
  correct the value disappears with it (the frame-limit validator already had this incident and now stretches its
  bookends instead).
- **Best solution:** One display rule for every range row in `qam-validators.ts`/`qam-rows.ts`: a valid descriptor
  (integer min below max, positive step) draws the control. The shown value is the host's value clamped into
  `[min, max]`; an off-step value is shown as is and the next user move snaps to the step. A null value draws the
  slider at the minimum with its value label hidden and sends nothing until the user moves it. Applies to
  `normalizePowerLimitRange` (observed may be null or off-step), `normalizeDeviceRange` (desired and observed),
  the frame-limit row and the controller row (no matching target: draw the dropdown with no selection instead of a
  note). WSGM keeps its own display fallback as host policy; the OSD projection keeps raw observed and desired watts
  and only the slider uses the ceiling fallback.
- **Tests:** invert `check-power-profile.mjs:561-563`: a null boost reading keeps two sliders, an off-step reading
  keeps the slider; add device-range and controller cases. Filters as TOOLKITJS-003.
- **Plan v2:** B057.
- **Related:** U03B-SUTS-024 (controller row), STEAMHOST-V-008 (finished in B087), R11.

### TOOLKITJS-007: Webpack module ids written in comments

- **Severity:** low
- **Where:** `Source/rpc.ts:33`, `Source/gates/audio.ts:42, 97`, `Source/gates/bluetooth.ts:177`,
  `Source/gates/brightness.ts:35`.
- **Problem:** Module ids and export names ("module 74362", "module 1409, export F5") are renumbered by every client
  build, so the comments are wrong after the next update and invite id-based lookups.
- **Best solution:** Delete the ids and export names; keep the sentence explaining that modules are renumbered and
  resolved by fingerprint.
- **Tests:** none beyond child `npm run prelude:claims`.
- **Plan v2:** B051 (`rpc.ts` in B050 with its comment fixes).
- **Related:** U02A-SUTC-058.

### TOOLKITJS-010: Two builders own fragment order; stale comments

- **Severity:** low
- **Where:** `toolkit/eng/build-prelude.mjs:39-52, 86-90`; `eng/build-steam-assets.mjs:28-32, 50-54, 55-120`.
- **Problem:** The toolkit prelude builder and WSGM's asset builder each encode the fragment order, so a change to
  one silently diverges from the other. WSGM's comments are stale ("there are none today" with nine WSGM fragments;
  "bridge.ts can call them before they appear textually" although gates self-register). `build-prelude.mjs` spawns
  `node` from PATH.
- **Best solution:** New side-effect-free module `toolkit/eng/steam-ui-fragments.mjs` exporting
  `steamUiFragments(extraDirectories = [])`, which returns the ordered absolute paths: `types.ts`, `bridge.ts`,
  `ownership.ts`, `rpc.ts`, the remaining top-level `Source/*.ts` sorted (excluding `components.ts` and
  `epilogue.ts`), `Source/gates/*.ts` sorted, `components.ts`, each extra directory's `*.ts` sorted, then
  `epilogue.ts`; and `fragmentMarker(path)` returning `// @fragment <label>` (toolkit files labelled by their path
  relative to `Source/`, extra-directory files as `consumer/<file>`). Markers go on every fragment after `bridge.ts`
  (`types.ts` is erased and `bridge.ts` starts the asset at `// @steam-ui-bundle-start`, so `fragment(asset,
  "bridge.ts")` is the text before the first marker). tsc drops a comment that leads an erased `type` declaration
  (checked with the pinned TypeScript: a marker followed by `type X = ...` vanishes from the output), and
  `ownership.ts`, `icons.ts` and `page-gate.ts` open with one, so move each file's leading `type` aliases below its
  first runtime declaration (types are order-free; add the three files to B047's file list), and make both builders
  throw when an inserted marker is missing from the compiled text, which also catches a later fragment (the B055
  `qam-*` files included) that opens with a type. `build-prelude.mjs` becomes a thin CLI that
  imports it (it runs tsc on import today, so WSGM cannot import it directly) and uses `process.execPath` instead of
  `node`. WSGM's `build-steam-assets.mjs` imports the module, passes its own `Source` directory, and drops its own
  ordering code and the stale comments. The plugin-directory discovery and 768 KiB bound are already gone in B032.
- **Tests:** child `npm run prelude:claims`; parent `npm run steam-assets:build`, `npm run steam-assets:check`,
  `npm run steam-assets:claims`. The emitted asset changes only by the markers (say so in the commit).
- **Plan v2:** B047.
- **Related:** U02B-SUTC-022, U02B-SUTC-027, TOOLKITJS-011, BUILD-015.

### TOOLKITJS-011: Checks depend on emitted-text adjacency

- **Severity:** low
- **Where:** `toolkit/eng/check-harness.mjs:47` (`sharedFragments` from `"const defineHidden"` to
  `"const SteamUiIconShapes ="`); `check-ui-kit.mjs:41`, `check-settings-fields.mjs:90`,
  `check-extension-surfaces.mjs:148` (slice up to `function createAudioNamespace`, true only because `audio.ts` sorts
  first in `gates/`); `check-power-profile.mjs:17, 23, 53, 83, 239, 313, 374, 380, 494-495` (slices between adjacent
  declarations or ending at a comment); `check-startup.mjs:13-18, 197-199` (regex-scrape C#); parent
  `eng/check-steam-module-discovery.mjs:2` imports `sharedFragments`.
- **Problem:** Renaming, adding or reordering a fragment breaks checks or makes them test the wrong text, and the
  B055 split cannot land without rewriting every slice anyway.
- **Best solution:** With TOOLKITJS-010's markers, the harness exposes `fragment(asset, label)` (text from that
  marker to the next marker) and `fragments(asset, labels)`. Every check instantiates whole fragments by label; no
  slice by neighbouring declarations remains. `sharedFragments` is redefined as the explicit label list it means
  (ownership, rpc, module resolver, gate helpers, icons) so `check-steam-module-discovery.mjs` keeps working. Markers
  survive tsc (`removeComments: false`) and Prettier as long as none leads an erased declaration (TOOLKITJS-010 moves
  the three leading type aliases and makes the builders assert every marker). `fragment()` matches the marker at
  any indentation, because Prettier indents the IIFE body in WSGM's asset. The C# regex scrape in
  `check-startup.mjs` is deleted in B062 once NodeScript runs the C# probes; until then it is the one check that
  evaluates the raw `module-resolver.ts` the way the C# probes embed it (TOOLKITJS-V-002), so it stays.
- **Tests:** all checks pass against `dist/prelude.js` and `NativeQamBootstrap.js`.
- **Plan v2:** B047 (C# scrape removal in B062).
- **Related:** U02B-SUTC-022, U03A-SUTS-018, TOOLKITJS-010.

### TOOLKITJS-012: `run-checks.mjs` holds a hand list

- **Severity:** low (verifier corrected the line range)
- **Where:** `toolkit/eng/run-checks.mjs:10-27`.
- **Problem:** A new `check-*.mjs` that is not added to the list never runs, and the AGENTS.md check map is already
  stale.
- **Best solution:** Discover the checks: `readdirSync(join(repositoryRoot, "eng"))` filtered by
  `/^check-.+\.mjs$/` excluding `check-harness.mjs`, sorted. The list is deleted; the summary line prints the
  discovered count.
- **Tests:** `npm run prelude:claims` runs every check file present (including the new ones from TOOLKITJS-V-003).
- **Plan v2:** B047.
- **Related:** U03B-SUTS-044, U02B-SUTC-028.

### TOOLKITJS-013: Tests pin WSGM labels, policy and toolkit internals

- **Severity:** low (verifier corrected two parts)
- **Where:** about 36 assertions in toolkit checks name WSGM English labels (for example
  `toolkit/eng/check-power-profile.mjs:98, 134-136, 144, 148-165`); `check-power-profile.mjs:203-218` pins the
  `custom` sentinel; `check-power-profile.mjs:47` (`!asset.includes("useGlobal")`); `check-power-profile.mjs:561-563`
  pins readback gating; `tests/WSGM.Tests/Core/SteamUiAssetTests.cs:28-29, 67` pin sound caps and
  `persistence: "automatic"`.
- **Problem:** The generic library's tests encode WSGM's product policy, so moving policy to WSGM (TOOLKITJS-003)
  either breaks them or keeps the policy alive in tests.
- **Best solution:** After B057, toolkit checks take labels and layout as fixture data (WSGM's exact layout) and
  assert rendering of that data. Rewrite the `custom` pin as the `selectable: false` behaviour (TOOLKITJS-V-006) and
  invert the readback pins (TOOLKITJS-002). Move the "no row may offer a Use global control" assertion into
  `SteamUiAssetTests.cs`: it is WSGM's decided per-game-profile rule, not history. Delete the
  `persistence: "automatic"` assertion (TOOLKITJS-014) and the sound-cap assertions (TOOLKITJS-005). Keep every
  authority and platform invariant in `SteamUiAssetTests.cs`: no `eval`, no `fetch` outside the sound decoder, no
  `WebSocket`, no filesystem access, no `force_deck_perf_tab`, `IS_STEAMOS =`, `PLATFORM =` or
  `SteamClient.SteamOSManager`.
- **Tests:** `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamUiAssetTests"`;
  child `npm run prelude:claims`.
- **Plan v2:** B057 (sound-cap assertions in B051).
- **Related:** U03B-SUTS-042.

### TOOLKITJS-014: Dead `persistence` field on the frame-limit wire

- **Severity:** low
- **Where:** `Source/components.ts:1316-1319`; C# `toolkit/src/SteamUiToolkit/Surfaces/SteamSurfaceModule.cs:11`
  (`SteamSettingPersistence`), `:114-140` (`TryReadValueWrite`), `Surfaces/SteamFrameLimitRow.cs:89`; WSGM
  `src/WSGM/Shell/NativeQamSemanticServices.cs:230-241`.
- **Problem:** JS always sends `persistence: "automatic"`, C# requires the field and maps three values, and WSGM
  ignores it. `global` and `application` are never sent. Dead contract surface carrying WSGM's per-game-profile
  vocabulary.
- **Best solution:** The frame-limit commands send `{value}` only. Delete `SteamSettingPersistence`, the persistence
  branch of the value-write reader (wherever B056 leaves it after deleting `SteamSurfaceModule.cs`), the parameter
  from the `SteamFrameLimitRow` handler and from WSGM's `SetFrameLimitAsync`. Delete the
  `persistence: "automatic"` assertion in `SteamUiAssetTests.cs`.
- **Tests:** frame-limit row command tests (`FullyQualifiedName~SteamPerformanceTests|FullyQualifiedName~SteamChoiceRowTests`
  in the toolkit) and `FullyQualifiedName~NativeQam|FullyQualifiedName~SteamUiAssetTests` in WSGM.
- **Plan v2:** B057.
- **Related:** NEW; TOOLKITJS-003, TOOLKITJS-013.

### TOOLKITJS-015: Draft and confirm logic implemented several times

- **Severity:** low
- **Where:** drafts `Source/settings.ts:386-398` (`SteamSettingsView`, never drops a draft on refusal),
  `Source/components.ts:1194-1201` (QAM settings sections), `Source/gates/extensions-tab.ts:253-303`; WSGM
  workaround `src/WSGM/Core/SteamUiAssets/Source/wsgm-settings.ts:16-25` and `wsgm-graphics.ts` (~L19-27); confirms
  `Source/settings.ts:81-95` and `Source/ui-kit.ts:595-625`; duplicate accent `#1a9fff` in `components.ts:299` and
  `settings.ts:196`.
- **Problem:** Three draft implementations behave differently. `SteamSettingsView` keeps a refused draft on screen,
  so WSGM fakes a new revision on every refusal, which also wipes every other row's in-flight draft.
- **Best solution:** One `useSteamSettingDrafts(react)` hook in `settings.ts`, used by `SteamSettingsView`, the QAM
  settings sections and the Extensions tab. A draft is `{value, base, committed}` keyed by row key, where `base` is
  the row's published value when the draft was made. Rendering shows a draft only while the row's published value
  still equals `base`. `onChange(row, value)` returns the request promise: on rejection the hook drops that row's
  draft and shows the refusal text in that row's description (not truncated); on success it marks the draft
  committed, and committed drafts drop on the next revision. Uncommitted text drafts survive unrelated revisions
  (TOOLKITJS-V-009). Delete the `useEffect(() => setDrafts({}), [revision])` resets and the WSGM `refusals` counter;
  WSGM's `onChange` returns `request(...)`. Both confirm implementations stay (different Steam components, both
  shipped). The accent constant is shared (TOOLKITJS-003).
- **Tests:** `toolkit/eng/check-settings-fields.mjs`: a rejected change restores the published value and shows the
  refusal without touching another row's draft; `check-extension-surfaces.mjs` and `check-power-profile.mjs` updated.
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamUiAssetTests"`.
- **Plan v2:** B058 (after B057; WSGM fragment edits land in the parent only after B057).
- **Related:** U03A-SUTS-014, U03B-SUTS-019, TOOLKITJS-V-009, TOOLKITJS-016.

### TOOLKITJS-016: Row component duplication

- **Severity:** low
- **Where:** `Source/components.ts:949-988` (`createCpuBoostControl`) versus `:887-925` (`createChoiceControl`); the
  pending-dropdown pattern at `:891-922, 952-986, 1014-1052, 1219-1247`.
- **Problem:** The CPU boost row duplicates the choice row except for its description, and the
  `pending`/`setPending`/`sendCommand(...).catch().finally()` pattern is copied five times, so refusal handling
  (TOOLKITJS-015, U03B-SUTS-019) would have to be fixed five times.
- **Best solution:** `createChoiceControl` takes a `describe(state)` function; the CPU boost row becomes a call to it.
  One `sendPending(setPending, promise)` helper in `qam-rows.ts` sets pending, awaits, clears pending and reports a
  refusal through the row's description path. The merge happens in B055 (pure move) and the refusal reporting in
  B058.
- **Tests:** `check-power-profile.mjs` CPU boost and choice row cases unchanged in output.
- **Plan v2:** B058 (structural merge already done in B055).
- **Related:** TOOLKITJS-004, TOOLKITJS-015.

### TOOLKITJS-017: Module runtime resolution repeated and inconsistent

- **Severity:** low
- **Where:** `Source/bridge.ts:46` (`getWebpackRuntime` builds a new resolver and pushes a chunk on every call);
  `Source/components.ts:2251` (every failed install), `Source/page-gate.ts:245`, most gates; per-gate caches in
  `gates/audio.ts:107-112`, `gates/brightness.ts:43-48`, `gates/elements.ts:10-11`; throwing `runtime.resolve` for
  React in `gates/screensaver.ts:276` and `gates/library-badge.ts:467` versus `resolveReact` returning null
  (`gate-helpers.ts:45-48`); `gates/screensaver.ts:281-292` re-implements dropdown resolution.
- **Problem:** Every resolution pushes a new webpack chunk and re-reads every module source; React resolution
  fails differently per gate.
- **Best solution:** In `bridge.ts`: `let webpackResolver; const getWebpackRuntime = (scope) => (webpackResolver ??=
  createSteamUiModuleResolver(scope));` (the factory throws when the runtime is unavailable, so nothing is cached
  until a capture succeeds). Only safe together with TOOLKITJS-V-001 (no sticky `failed` set). Delete the per-gate
  caches in audio, brightness and elements (call `getWebpackRuntime` directly). Screensaver and library badge use
  `resolveReact` and refuse with "React unavailable" on null; screensaver's dropdown comes from
  `resolveSteamFieldComponents`. `module-resolver.ts` itself is unchanged in shape (TOOLKITJS-V-002). Add the five
  gate files to B050's file list.
- **Tests:** as TOOLKITJS-V-001; the screensaver and library checks still pass.
- **Plan v2:** B050.
- **Related:** U02A-SUTC-005, U03B-SUTS-038, TOOLKITJS-V-001, TOOLKITJS-V-002; plan L137 "monotonic IDs" dropped
  (R7).

### TOOLKITJS-018: Storage resolution calls every function export

- **Severity:** low
- **Where:** `Source/gates/storage.ts:196-209`; the C# probe does the same (`toolkit/src/SteamUiToolkit/Surfaces/SteamStorageSurface.cs:174-184`).
- **Problem:** The gate calls every function export of the transport module with no arguments until one returns an
  object with `GetDefaultTransport`. Constructing unknown exports is forbidden by the toolkit's probe rule (it has
  restarted a machine and signed Steam out) and by the parent CEF rule.
- **Best solution:** Identify the provider without invoking candidates: among the module's function exports, keep
  only those whose own source (`sourceMatches(fn, tokens)`, which reads `Function.prototype.toString` and never calls
  the function) carries the provider accessor's author tokens; exactly one must remain, else refuse with "transport
  provider export not identified" and the count. Call only that one export, once, at install. `GetDefaultTransport`
  alone is not those tokens: an accessor that returns a module-level instance does not name it (the
  `check-storage.mjs` fixture's provider is `OI: () => provider`), while a class export that defines it does and
  must not be called. Take the tokens from the accessor's source as read on the current client (the maintainer reads
  the known transport module by literal id, never during a Steam cold start, as TOOLKITCS-001 requires) and make the
  `check-storage.mjs` fixture's accessor source carry them. The C# probe uses the same tokens in the same child
  commit, so probe and gate cannot disagree (TOOLKITCS-001).
- **Tests:** a Node fixture whose other exports (including a class that defines `GetDefaultTransport`) throw if
  invoked; the check asserts none was called and that two matching accessors are refused.
  `dotnet test external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj --filter "FullyQualifiedName~SteamStorageTests"`.
- **Plan v2:** B049.
- **Related:** U03A-SUTS-003, TOOLKITCS-001.

### TOOLKITJS-019: Bridge disposal is silent and incomplete

- **Severity:** low
- **Where:** `Source/bridge.ts:249-261` (gate `remove`/`dispose` failures swallowed), `:292` (`gates` not cleared),
  `:121-203` (`deliver`, `deliverPart`, `subscribe` accept work after dispose).
- **Problem:** A gate that fails to unwind when its bridge is replaced leaves no trace anywhere, and a disposed bridge
  still accepts deliveries and subscriptions into dead closures.
- **Best solution:** `dispose` collects the names of gates whose `remove` or `dispose` threw or returned
  `{ok: false}` and returns them as an array. The new bridge's `prior.dispose(...)` call keeps that array and the
  install result becomes `{ok, reused, version, priorDisposeFailures}` (omitted when empty). In the same child
  commit `SteamUiBridge`'s bootstrap, after a positive acknowledgement, writes one
  `SteamUiLog.Warn("Steam UI bridge: the previous bridge could not remove gates: <names>")` when that array is
  present (add `SteamUiBridge.cs` to B050's file list), so the failure reaches wsgm.log. `dispose` clears `gates`.
  `deliver` and `deliverPart` return false when `disposed`. `subscribe` and `subscribeRefusal` after dispose return
  a no-op unsubscribe without registering; they do not throw, because the old bridge's components can still run an
  effect before Steam unmounts them, and a throw there would take down the React tree it sits in. No new state.
- **Tests:** `check-startup.mjs`: a gate whose remove throws is named in the next install result; a delivery after
  dispose returns false; a subscription after dispose returns a function and its callback is never called. A
  `SteamUiBridgeHostTests` case: an install result carrying `priorDisposeFailures` is still ready and logs the names.
  `FullyQualifiedName~SteamUiBridgeHostTests`.
- **Plan v2:** B050.
- **Related:** U02A-SUTC-056.

### TOOLKITJS-020: Ownership release edge cases report false success

- **Severity:** low
- **Where:** `Source/ownership.ts:384-394` (`releaseAccessor` returns ok when no original is stored, leaving the
  claimed getter installed), `:212` (`releaseValue` casts the stored original without `isPropertySnapshot`),
  `:440-447` (`createSharedClaim.release` with a null host drops the transform but keeps `wrappers`).
- **Problem:** A release that restored nothing reports success, so the gate forgets a claim that is still in place.
- **Best solution:** `releaseAccessor`: when the descriptor is ours but no original is stored, return
  `{ok: false, error: "accessor original missing"}`. `releaseValue`: if the stored original fails
  `isPropertySnapshot`, return `{ok: false, error: "stored original invalid"}` without touching the host.
  `createSharedClaim.release`: delete the transform; if transforms remain return ok; if `host` is null and
  `wrappers` is non-null return `{ok: false, error: "host unavailable"}` so the caller retries with a host; clear
  `wrappers` only after every member is restored.
- **Tests:** `toolkit/eng/check-ownership-claims.mjs` cases for each, written after that file is converted to
  `node:assert/strict` in the same batch (TOOLKITJS-036 lands here, because B050 is the first batch to edit the
  file).
- **Plan v2:** B050.
- **Related:** U02A-SUTC-025.

### TOOLKITJS-021: File picker lifetime and rendering

- **Severity:** low
- **Where:** `Source/file-picker.ts:19-31, 200-205` (settles only on finish, cancel or `!shown`), `:80-91`
  (`listPlaces` not ticketed), `:170` (every entry rendered), `:187` (`ui.dialogButtonPrimary` without fallback;
  the kit uses `?? ui.dialogButton`, `ui-kit.ts:613`).
- **Problem:** If the modal unmounts any other way (navigation, Steam closing it), the caller's promise never
  settles. A late `listPlaces` answer can open the start folder after the user navigated or the picker closed. A
  client without a primary dialog button renders `undefined` as a component.
- **Best solution:** Add an unmount effect in `Picker`: `react.useEffect(() => () => { requested.current = -1;
  settle(null); }, [])`; `settle` is already idempotent, so finish followed by close settles once. The `listPlaces`
  answer is applied only while `requested.current === 0` (no folder opened yet, picker still mounted). Use
  `ui.dialogButtonPrimary ?? ui.dialogButton`. Keep rendering the whole list (no paging, no More control, critic
  conflict 7) and keep the inline styles (appearance identical). The C# side of the batch moves enumeration to a
  worker with caller cancellation, typed outcomes and path normalization (TOOLKITCS-055).
- **Tests:** new `toolkit/eng/check-file-picker.mjs`: 450-entry listing renders all rows; enter two directories then
  Back, a stale listing is ignored; cancel and unmount each settle null exactly once; a late `listPlaces` after a
  folder was opened does nothing. `FullyQualifiedName~SteamFilePicker`. Manual M01-31 (rewritten) and M01-32.
- **Plan v2:** B059.
- **Related:** U03B-SUTS-023, U03B-SUTS-004 (paging dropped), U03B-SUTS-039 (inline styles kept), TOOLKITCS-055,
  TOOLKITJS-V-008, LIBRARY-035, critic conflict 7.

### TOOLKITJS-022: Colour editor turns unknown colours into white

- **Severity:** low
- **Where:** `Source/settings.ts:99-111` (`parseSteamColor`), `:265-281` (the colour row).
- **Problem:** Anything other than `#hex` or comma-separated `hsl(a)` parses to white. A theme colour written as
  `rgb(...)`, a named colour or space-separated `hsl()` opens as white, and Save writes white over it.
- **Best solution:** `parseSteamColor` also parses `rgb(a)` (comma or space separated, converted to HSL) and
  space-separated `hsl(a)`; for anything else it returns null. The colour row renders the existing text-field
  fallback (the branch used when no modal exists) whenever the value does not parse, so the value stays editable as
  text and is never replaced.
- **Tests:** `check-settings-fields.mjs`: `rgb(10, 20, 30)` opens with the right swatch; `rebeccapurple` and
  `var(--x)` render a text field holding the original text.
- **Plan v2:** B060.
- **Related:** NEW.

### TOOLKITJS-023: Theme styles pattern cache never cleared; stale comment

- **Severity:** low (verifier corrected: `status()` does not walk the tree, and `windows()` is check-only)
- **Where:** `Source/gates/theme-styles.ts:55` (pattern cache), `:295-309` (`remove`), `:172` (stale "2 s passes"
  comment).
- **Problem:** Compiled CSSLoader patterns from removed themes stay in memory for the bridge's life; the comment
  describes polling that no longer exists.
- **Best solution:** `remove` clears the pattern cache after its releases succeed (TOOLKITJS-008 order). Rewrite the
  comment to describe the current trigger. No byte bound on theme CSS (the gate installs every theme however large),
  and no late-portal notification store until a live unstyled late portal is shown (U03B-SUTS-022 stays a
  hypothesis).
- **Tests:** `check-theme-styles.mjs`: after remove, the cache is empty;
  `FullyQualifiedName~SteamThemeStyleTests`. Manual M01-27.
- **Plan v2:** B060.
- **Related:** U03B-SUTS-015, U03B-SUTS-013, U03B-SUTS-022 (deferred), R9.

### TOOLKITJS-024: Home carousel costs and duplication

- **Severity:** low
- **Where:** `Source/gates/home-carousel.ts:538` (`status` runs `staleFibers` over up to 250,000 fibers on every
  verify), `:252, 263-269` (`report` called from render), `:55, 401` versus `pages.ts:52, 111-115` (duplicated
  route-list detection), `:383` (`isHome` reads the claim marker directly), `:341, 381` and `library-badge.ts:260`
  (`Symbol.for("react.memo")` repeated), `:444` and `screensaver.ts:313` (redundant `useObserver = null;`).
- **Problem:** Each host verification pays a full tree walk on Steam's UI thread; a request is issued from render.
- **Best solution:** Compute the stale count when the adoption walk already runs and store it in `lastAdoption`;
  `status` returns cached values only (C# does not read `stale`). Move `report(counts)` into an effect of the
  carousel wrapper keyed by the counts JSON (the existing JSON dedupe stays, it saves a bridge round trip per change).
  Route-list detection uses the shared `isSteamRouteList` (TOOLKITJS-005, landed in B051). `isHome` uses
  `unclaimedValue`. One `ReactMemoType = Symbol.for("react.memo")` constant in `gate-helpers.ts`. Delete the
  redundant assignments. The ordering policy (L35-39, L150-254) stays: it is library-owned data projection.
- **Tests:** `check-home-carousel.mjs` (`status().mounted` still reports `stale`);
  `FullyQualifiedName~SteamHomeCarouselTests`.
- **Plan v2:** B060 (route-list helper in B051).
- **Related:** U03B-SUTS-017, U03B-SUTS-046.

### TOOLKITJS-025: Library badge invents a label

- **Severity:** low
- **Where:** `Source/gates/library-badge.ts:54-55` (`"Internal"` default), comment `:26`; C#
  `toolkit/src/SteamUiToolkit/Surfaces/SteamLibraryBadgeSurface.cs:27` (`InternalLabel = "Internal"` default); WSGM
  `src/WSGM/Shell/LibraryBadges.cs:73`.
- **Problem:** A product label lives in the generic library and is drawn before any publication. It is also wrong
  on a machine with more than one internal library (common on desktop): every game outside a tracked card reads
  "Internal", whichever library holds it.
- **Best solution:** Per D13 (decided): the badge shows the name of the library that holds the game, and the host
  supplies every name. Toolkit: delete `internalLabel` from `readLibraryBadgeState` and from `libraryForOverview`
  (`library-badge.ts`), so a game no published library holds gets no badge and no stat; delete the
  `InternalLabel` parameter from the C# `SteamLibraryBadgeState` record and its XML doc; rewrite the gate's header
  comment (L24-29) to say the host names every library, without the maintainer-choice sentence. WSGM:
  `LibraryBadges.Build` takes the `libraryfolders.vdf` text (read through `Steam.TryReadLibraryFolders` by both
  `Update` callers, `LibraryTabManager` and `ShellSession`) and, besides the tracked cards, publishes one
  `SteamLibraryBadgeLibrary` per registration that is not a tracked card's content id: its name is the
  registration's Steam `label`, or its drive root (for example `D:`) when the label is empty; `Connected` is true;
  `AppIds` are the keys of its `apps` block. Extend `SteamLibraryVdf.ReadEntries` (or add one sibling reader) so
  label and app ids come from each entry's own block, never from zipped `ValuesOf` lists. A card that is also
  registered keeps its card name and is not listed twice. Visible change: an installed game on an internal library
  shows that library's name instead of "Internal", and between the gate's install and WSGM's first badge
  publication (sent at session start and replayed by the bridge) no badge is drawn.
- **Tests:** `check-library.mjs`: the `internalLabel` fixtures go; an app no published library holds draws no badge
  or stat; two published internal libraries draw their own names. WSGM `LibraryBadgesTests`: a VDF with a labelled
  and an unlabelled non-card registration yields "Games" and "D:" with their app ids, a registered card keeps its
  card name once, and a missing VDF yields the card libraries only. Run
  `dotnet test external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj --filter "FullyQualifiedName~SteamLibraryBadgeTests"`
  and `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~LibraryBadges|FullyQualifiedName~SteamLibraryVdf"`.
- **Plan v2:** B057; decided: D13, the badge shows the library's name supplied by WSGM.
- **Related:** U03B-SUTS-016.

### TOOLKITJS-V-002: `module-resolver.ts` is also a C# embedded expression

- **Severity:** low (verifier-found)
- **Where:** `toolkit/src/SteamUiToolkit/SteamUiToolkit.csproj:37` (embeds `module-resolver.ts` as
  `SteamUiToolkit.ModuleResolver.js`); `toolkit/src/SteamUiToolkit/SteamUiModuleResolver.cs:24`
  (`({Source})(scope)`).
- **Problem:** A second top-level statement, a TypeScript annotation or a module-level cache added to that file
  makes every standalone C# probe expression a syntax error. The emitted asset hides this (tsc strips annotations);
  only `check-startup.mjs:13-18,44-46`, which evaluates the raw file inside the scraped C# probe preamble, notices,
  and the C# probe tests are text checks that never execute it.
- **Best solution:** Keep `module-resolver.ts` a single plain-JS function declaration; memoization lives in
  `bridge.ts` (TOOLKITJS-017) and cap removal stays inside the function. Keep the raw-file case in
  `check-startup.mjs` until B062's NodeScript executes the C# probes, and run the C# resolver and probe tests in every
  batch that edits the file.
- **Tests:** child `npm run prelude:claims` (the raw-file case) and
  `dotnet test external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj --filter "FullyQualifiedName~SteamCefTests|FullyQualifiedName~Probe"`
  (the plan's `~ModuleResolver` filter matches no test; the resolver-resource test lives in `SteamCefTests`).
- **Plan v2:** B050.
- **Related:** TOOLKITJS-017, TOOLKITJS-V-001, TOOLKITJS-005.

### TOOLKITJS-V-003: Several gates and helpers have no emitted-asset check

- **Severity:** low (verifier-found)
- **Where:** no check instantiates `createAudioNamespace` (`Source/gates/audio.ts`), `createPerfNamespace`
  (`Source/gates/performance.ts`), `createNetworkGate` (`Source/gates/network.ts`; `check-startup.mjs:197-214` runs
  only the C# probe), `showSteamFilePicker` (`Source/file-picker.ts`) or `registerSteamPage` (`Source/page-gate.ts`).
- **Problem:** The release-order fix (TOOLKITJS-008) and the retry helper have nothing to land in for these gates,
  and regressions in volume direction mapping (`audio.ts:159-176, 306-320`) or performance state writes ship
  untested.
- **Best solution:** Add `toolkit/eng/check-audio.mjs`, `check-performance.mjs` and `check-network.mjs`: small
  fixtures over the shared fragments plus the gate, covering install, a state publication, the main command path
  (volume up/down direction, perf settings write, scan start/stop) and `assertRemoveRetries`. Add a
  `registerSteamPage` case to `check-pages.mjs`. The file-picker check is TOOLKITJS-021 (B059). `run-checks.mjs`
  discovers them (TOOLKITJS-012).
- **Tests:** the new checks themselves via `npm run prelude:claims` and `npm run steam-assets:claims`.
- **Plan v2:** B051.
- **Related:** U03A-SUTS-006 (critic 2.8), TOOLKITJS-008.

### TOOLKITJS-V-004: Perf gate removal writes invented values instead of what it displaced

- **Severity:** low (verifier-found)
- **Where:** `Source/gates/performance.ts:45-63` (`onState` overwrites `m_msgState.limits`, `settings`,
  `current_game_id`, `active_profile_game_id` without a snapshot), `:123-135` (`remove` writes `undefined` to all
  four).
- **Problem:** Toolkit AGENTS.md: "Removal restores exactly what was displaced ... Never restore an invented platform
  value." If Steam's store held anything in those fields before the first publication, removal leaves `undefined`.
- **Best solution:** On the first `onState` after install, snapshot the four fields once (presence via `Object.hasOwn`
  and value), as `audio.ts:198-202` does for its store; `remove` restores the snapshot (deleting fields that were
  absent) after `withdrawNamespace` succeeds, then clears the snapshot. Same batch, same file: the settings-update
  decoder that returns `{}` on a decode failure (around L37-43) refuses the update instead of applying an empty delta
  (U03A-SUTS-004).
- **Tests:** `check-performance.mjs` (TOOLKITJS-V-003): a pre-seeded `current_game_id` survives install, publish and
  remove; an undecodable update is refused.
- **Plan v2:** B051.
- **Related:** U03A-SUTS-004, TOOLKITJS-008, TOOLKITJS-V-003.

### TOOLKITJS-V-005: Tab transform allocates on every object-array `useMemo` in the client

- **Severity:** low (verifier-found)
- **Where:** `Source/components.ts:2411-2436` (`transformTabs`), run through the shared `useMemo` claim
  (`Source/ownership.ts:471-481`) for every `useMemo` result Steam computes; the quick-settings matcher stringifies
  sources uncached (`components.ts:2377`).
- **Problem:** Every memoized array whose first item is an object pays two `filter` passes and up to two `map`
  copies on every render (library grids, lists), not only the Quick Access tab list.
- **Best solution:** Rewrite `transformTabs` as one indexed pass per wrapper that counts matches and remembers the
  index, with no intermediate arrays; return `value` unchanged unless some wrapper matched exactly once, and only then
  `slice()` once and replace the matched items. The quick-settings matcher uses `sourceMatches(type, [...])`
  (WeakMap-cached) instead of `String(type)`. Both shared claims iterate a frozen array snapshot (TOOLKITJS-029).
- **Tests:** `check-startup.mjs`/`check-power-profile.mjs` tab cases unchanged; add a case asserting a non-tab object
  array is returned by identity.
- **Plan v2:** B060.
- **Related:** TOOLKITJS-029, U03B-SUTS-033 (in TOOLKITJS-031).

### TOOLKITJS-V-006: Power-preset "custom" semantics the presentation contract must keep

- **Severity:** low (verifier-found)
- **Where:** `Source/components.ts:996-999` (rejects the whole state when a `custom` option exists but neither
  assignment is `custom`), `:1036` (shows `custom` only in the dropdown whose current value it is), `:1044` (never
  sends it); WSGM `src/WSGM/Shell/NativeQamPowerPresetService.cs:43-45`; Device SDK
  `src/WSGM.Device.Sdk/DevicePowerPresetReference.cs:33`, `DevicePowerPreset.cs:80`.
- **Problem:** A naive `selectable: false` port would show the option in both dropdowns (visible UI change) and a
  straight port of L996-999 would make the generic flag delete the whole row.
- **Best solution:** `selectable: false` means: listed only in a dropdown whose current value is that option, never
  sent as a command. No whole-state rejection. C# `SteamPowerPresetRow` (L90) drops its `custom` special case and
  carries `Selectable`; WSGM marks its custom option `Selectable = false` in `NativeQamPowerPresetService` and
  `DevicePowerAssignments`. The SDK's reserved id is untouched.
- **Tests:** `check-power-profile.mjs`: a non-selectable option appears only in the dropdown whose value it is,
  selecting it sends nothing, and a state whose assignments do not use it still renders.
  `FullyQualifiedName~SteamChoiceRowTests` and WSGM `FullyQualifiedName~NativeQam`.
- **Plan v2:** B057.
- **Related:** TOOLKITCS-047, DEVICE-025, TOOLKITJS-003.

### TOOLKITJS-V-007: Accent contract misses the Edit-color toggle description

- **Severity:** low (verifier-found)
- **Where:** `Source/components.ts:1734-1746` builds "Game override · <zone labels>" for the Edit color toggle from
  every zone's `overrideId`.
- **Problem:** Replacing `overrideId` with host-built descriptions (TOOLKITJS-003) leaves no field for this composed
  line, so the toolkit either keeps WSGM vocabulary or the line disappears.
- **Best solution:** Add `editDescription: string` (the name B057's spec uses) to the device-controls (lighting)
  state. WSGM builds today's
  exact text ("Game override · " plus the overridden zone labels joined by ", ", or empty) in its projection; the
  toolkit renders it as the toggle's plain description, as today (it is not accented today, so no accent).
- **Tests:** `check-power-profile.mjs` (device controls): the toggle shows the published text; WSGM NativeQam
  projection test for the composed string.
- **Plan v2:** B057.
- **Related:** TOOLKITJS-003, R5.

### TOOLKITJS-V-008: A whole-listing response meets the 32 M delivery cap and the 5 s request timeout

- **Severity:** low (verifier-found)
- **Where:** `toolkit/src/SteamUiToolkit/SteamUiBridge.cs:225` (`MaximumDeliveryCharacters = 32 * 1024 * 1024`,
  oversized answers replaced by "The answer was too large to deliver." at L452-457), `:233`
  (`RequestTimeoutMilliseconds = 5000`); `Source/bridge.ts:103-110` (the JS timer covers the whole parted delivery).
- **Problem:** Without paging, a very large folder listing would be refused outright past 32 M characters.
- **Best solution:** Delete the 32 M delivery cap in C# (TOOLKITCS-031 in B056); the listing returns
  whole through the existing chunked delivery. The existing bridge request timeout stays the only wait bound (no
  second clock): a listing that cannot be produced and delivered in time ends as the `TimedOut` outcome with its
  fixed message, and the user retries by opening the folder again. No paging, spool, worker caps or capacity
  outcomes.
- **Tests:** B059 fixtures: 450-entry tree order, a timeout through an internal blocking enumerator seam;
  `FullyQualifiedName~SteamFilePicker`.
- **Plan v2:** B059 (cap deletion in B056).
- **Related:** TOOLKITJS-021, TOOLKITJS-001, TOOLKITCS-055, critic conflict 7, critic over-engineering table.

## Nit

### TOOLKITJS-026: WSGM and device names in generic comments

- **Severity:** nit
- **Where:** `Source/components.ts:394, 399, 580, 758, 785, 1898, 2096, 2187`; `Source/bridge.ts:61`;
  `Source/gates/home-carousel.ts:314`; `Source/gates/theme-styles.ts:27`; `Source/settings.ts:193`.
- **Problem:** Generic library comments name WSGM, RTSS, the Claw and other product context, which reads as product
  coupling and goes stale.
- **Best solution:** Reword each to describe the general behaviour (for example "a host whose limiter overlay
  replaces Valve's FPS rows" instead of RTSS). Dates and incident descriptions may stay when they explain a rule.
  Comment-only edits; `components.ts`, `bridge.ts` and `settings.ts` join B051's file list for this.
- **Tests:** none beyond child `npm run prelude:claims`.
- **Plan v2:** B051.
- **Related:** U03A-SUTS-028.

### TOOLKITJS-028: Undeclared consumer-facing script API

- **Severity:** nit
- **Where:** all fragments share one IIFE scope; WSGM fragments (`src/WSGM/Core/SteamUiAssets/Source/*.ts`) use
  about 30 toolkit top-level names (`registerSteamPage`, `renderSteamSettings`, `renderSteamSettingRow`,
  `renderSteamUi*`, `showSteamUi*`, `showSteamModal`, `showSteamFilePicker`, `createSteamCapsule`,
  `resolveSteamLibraryClasses`, `renderSteamDropdown`, `steamCheckbox`, `onSteamTriggers`, `navigateSteamRoute`,
  `request`, `subscribe`, `registerGate`, `interceptMemo`, `getWebpackRuntime`, `invalidateQuery`, `renderSteamGlyph`,
  `SteamUiTabbedPageRequired`, `SteamSettingsRequired`, `resolveSteamSettingsComponents`, `resolveSteamUiComponents`).
- **Problem:** Nothing lists the script API a consumer may use; a rename is caught only by WSGM's tsc compile.
- **Best solution:** One "Script API for consumer fragments" section in `toolkit/docs/reference.md` listing each name
  with a retain, internalize or remove decision (critic 2.6). No new mechanism. The B055 split keeps every listed
  name.
- **Tests:** none (docs).
- **Plan v2:** B177.
- **Related:** critic 2.6.

### TOOLKITJS-029: Element-transform hot path

- **Severity:** nit
- **Where:** `Source/ownership.ts:509-519` (a `Map` iterator per `jsx`/`jsxs` call once any element transform is
  registered) and `:471-481` (same for `useMemo`); `Source/gates/game-context-menu.ts:163-173` (capture transform
  kept after capturing the class).
- **Problem:** Every element Steam creates allocates an iterator while power menu, library details or game context
  menu are installed, and one transform stays registered after it has done its job.
- **Best solution:** `createSharedClaim` keeps `let active: readonly T[] = Object.freeze([])`, rebuilt from
  `transforms` in `intercept` and `release`; `wrap` receives a getter for `active`, and both wrappers loop with an
  index over it. `game-context-menu.ts` releases its capture transform right after `claimMenuRender` succeeds (add
  that file to B060's list).
- **Tests:** `check-ownership-claims.mjs` (transform order and failure isolation unchanged); game context menu check
  shows the capture transform is gone after capture.
- **Plan v2:** B060.
- **Related:** TOOLKITJS-V-005.

### TOOLKITJS-031: Confirmed small component-host items

- **Severity:** nit
- **Where:** `Source/components.ts:1924-1979` (`nativeRowsHidden` undercount, `lastHidden` mutated in render),
  `:1190-1196` (settings-sections control returns null without a `note`), `:229, 919-921, 983-985, 1049-1051, 1131,
  1169, 1244-1246, 1317-1319, 1647-1648` (refusals swallowed except power limit), `:2446` (disposed host refuses as
  "component is not allowlisted"), `:1556, 1593` (`String(reason)` shows "Error: ..."), `:2377` (`transformTabs`
  matcher stringifies uncached).
- **Problem:** Diagnostics are wrong or missing, and refusals are invisible to the user.
- **Best solution:** In `qam-sections.ts`'s `createValveFpsFilter`, count hidden rows per wrapper render into a local
  and publish it after render instead of mutating `lastHidden` mid-render. The settings-sections control calls
  `note("settingsSections", "no sections")` before returning null. A disposed host refuses with "component host
  disposed". Power-limit errors use `reason?.message ?? String(reason)`. The matcher uses `sourceMatches`
  (TOOLKITJS-V-005). Visible refusal text for every row is delivered by TOOLKITJS-015/016 in B058. After B055 the
  filter and the settings-sections control live in `qam-sections.ts` and `qam-rows.ts`; B060's file list names only
  `components.ts`, so add both.
- **Tests:** `check-power-profile.mjs` status assertions for each diagnostic.
- **Plan v2:** B060 (refusal display in B058).
- **Related:** U03B-SUTS-007, U03B-SUTS-018, U03B-SUTS-019, U03B-SUTS-030, U03B-SUTS-031, U03B-SUTS-033.

### TOOLKITJS-032: Confirmed UI-kit items

- **Severity:** nit
- **Where:** `Source/ui-kit.ts` header versus `:32` (`gamepadtabbedpage_TabHeaderRowWrapper` class-substring
  selector), `:534` (`props.tabs[0].id` on an empty list), `:408` (chips keyed by label), `:445` (thumbnails keyed by
  URL); `Source/icons.ts:109-126` (glyph cache shared across React instances).
- **Problem:** The header comment claims structural selectors only; an empty tab list throws; duplicate labels or
  URLs collide as React keys; a cached glyph element from one React instance is reused in another.
- **Best solution:** Correct the header comment to name the one class-substring selector (keep the selector:
  appearance stays identical). `const active = ... : props.tabs[0]?.id ?? ""`. Chips keyed by `` `${index}:${label}` ``,
  thumbnails by `` `${at}:${url}` ``. `renderSteamGlyph` caches per React instance through a
  `WeakMap<react, Map<string, element>>`, as `ui-kit.ts:134-151` does.
- **Tests:** `check-ui-kit.mjs`: empty tabs render, duplicate chip labels render both; glyph cache test with two React
  fixtures.
- **Plan v2:** B060, except the per-React glyph cache, which B051's spec already carries (B051 edits `icons.ts` for
  TOOLKITJS-005); B060 does not touch it again.
- **Related:** U03B-SUTS-035, U03B-SUTS-036, U03B-SUTS-037.

### TOOLKITJS-035: `isNavigableRoute` accepts control characters

- **Severity:** nit
- **Where:** `Source/gate-helpers.ts:361`.
- **Problem:** Docs and the C# side (`SteamRouteNavigation.cs:29-34`) say control characters are refused; the JS
  accepts them.
- **Best solution:** `const isNavigableRoute = (route) => typeof route === "string" && route.startsWith("/") &&
  route !== "/" && !/[\u0000-\u001f\u007f]/u.test(route);` No length limit.
- **Tests:** `check-navigation-panel.mjs`: `"/a\nb"` is refused, a long route is accepted.
- **Plan v2:** B057.
- **Related:** U03B-SUTS-009.

### TOOLKITJS-036: Check style inconsistency

- **Severity:** nit
- **Where:** `toolkit/eng/check-ownership-claims.mjs:25-34, 275-276` (own `check()` and `process.exit`).
- **Problem:** One check uses a private assertion helper while every other uses `node:assert`.
- **Best solution:** Convert it to `node:assert/strict` with the same cases; exit status comes from the thrown
  assertion.
- **Tests:** child `npm run prelude:claims`.
- **Plan v2:** B050, with TOOLKITJS-020's new cases in the same file (B050 lists `check-ownership-claims.mjs`); B051's
  spec names 036, so B051 only confirms the file is already converted.
- **Related:** TOOLKITJS-020.

### TOOLKITJS-037: Asset size cap raised by hand

- **Severity:** nit
- **Where:** `eng/build-steam-assets.mjs:128-134` (`maximumAssetBytes = 768 * 1024`).
- **Problem:** A "sanity bound" with no transport behind it, raised twice already; the asset is about 646 KB.
- **Best solution:** Delete the bound; keep the non-empty, UTF-8, no-BOM and single-file checks.
- **Tests:** `npm run steam-assets:check`.
- **Plan v2:** B032.
- **Related:** STEAMHOST-V-003.

### TOOLKITJS-038: Lockfile name and unpinned CI actions

- **Severity:** nit
- **Where:** `toolkit/package.json` (no `name`) versus `toolkit/package-lock.json:2` (`"name": "toolkit"`);
  `toolkit/.github/workflows/ci.yml:18, 21, 26` (`actions/checkout@v5`, `setup-dotnet@v5`, `setup-node@v5`).
- **Problem:** The lockfile name is stale; floating major tags let CI change underneath the repository.
- **Best solution:** Add `"name": "steam-ui-toolkit"` to `package.json` and regenerate the lock with
  `npm install --package-lock-only`. Pin the three actions to full commit SHAs with the version as a trailing
  comment, as B032 does for `claude.yml`. Add `ci.yml` to B047's file list.
- **Tests:** `npm ci` and `npm run prelude:claims` in the child.
- **Plan v2:** B047.
- **Related:** U02B-SUTC-026, U02B-SUTC-023.

### TOOLKITJS-V-009: Uncommitted text drafts are wiped by any republish

- **Severity:** nit (verifier-found)
- **Where:** `Source/settings.ts:393` (`useEffect(() => setDrafts({}), [revision])`), `Source/components.ts:1195`
  (same pattern); text and secret rows keep typed text as an uncommitted draft until blur (`settings.ts:311-319`).
- **Problem:** A publication that bumps the page revision for any reason clears what the user is typing in an
  unrelated text field before it is sent.
- **Best solution:** Folded into TOOLKITJS-015's `useSteamSettingDrafts`: a draft is dropped only when its row's
  published value changed from the draft's `base`, when its write completed (committed, then the next revision), or
  when its write was refused. The revision-reset effects are deleted.
- **Tests:** `check-settings-fields.mjs`: typing in one text row survives a publication that changes another row.
- **Plan v2:** B058.
- **Related:** TOOLKITJS-015.

## Refuted or no-change

- **TOOLKITJS-027** (nit, two coding styles, no formatter): no change. A toolkit-wide Prettier pass contradicts
  toolkit AGENTS.md ("respect the established local TypeScript style; avoid unrelated formatting") and the shipped
  WSGM asset is already Prettier-formatted.
- **TOOLKITJS-030** (nit, component host state survives disposal): refuted. Disposal is terminal (`install` refuses
  after `disposedHost`), a new bridge re-evaluates the IIFE and builds a new host, and within one bridge the
  `latestStates` replay re-delivers current state, so stale state is never drawn.
- **TOOLKITJS-033** (nit, Extensions tab double render): no change. The panel keeps its own `subscribe(patchId,
  redraw)`, because nothing shows the gate's adopted rerender reaches the panel through every memo boundary, and the
  panel's subscription also covers the mount-time replay.
- **TOOLKITJS-034** (nit, redundant dedupe and polling): no change. Storage maps the shape before comparing
  (`gates/storage.ts:304-306`), the carousel's JSON dedupe saves a bridge round trip per change
  (`gates/home-carousel.ts:263-269`, moved into an effect by TOOLKITJS-024), and the screensaver's first-report poll
  (`gates/screensaver.ts:39-40, 138-142`) is bounded and owned by the gate. B060's spec records it as reviewed; no
  code change.
