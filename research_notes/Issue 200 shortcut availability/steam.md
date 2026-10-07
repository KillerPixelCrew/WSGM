# Steam presentation and launch feasibility for Issue 200

## Can Steam represent a shortcut as natively uninstalled?

### Takeaway

No supported shortcut install-state setter was established. A narrow native semantic override is
technically plausible, but changing `installed` alone cannot provide the requested behavior; an
explicit toolkit availability model is the lower-risk first implementation.

### Cited Findings

- Scope is runtime availability for retained WSGM-managed shortcuts, including equivalent
  unavailable presentation if native uninstalled representation is absent; this is explicitly a
  future feature outside 2.0. The original request concerns library-associated shortcut storage, not
  an existing API. Sources: [Issue 200](https://github.com/KillerPixelCrew/WSGM/issues/200),
  [Valve issue 8575](https://github.com/ValveSoftware/steam-for-linux/issues/8575).
- Research baseline supplied by coordinator: WSGM `57a3dafdc7b4871eeb073d15ceb92dc5356abd6f`,
  toolkit `d8902b71c92a162f1221df4ebc8f1e642603290d`. Current source reads distinguish shortcuts by
  `BIsShortcut` and include them from `allAppsCollection`, because `type-games` excludes them; ids
  are normalized unsigned. Source:
  [SteamLibraryData.cs:54](/D:/Coding/WSGM/external/steam-ui-toolkit/src/SteamUiToolkit/Client/SteamLibraryData.cs:54).
- Current installed Steam source was read and parsed offline on 2026-10-06; no Steam JavaScript was
  executed. Inspected main chunk is 14,382,868 bytes, modified 2026-09-06, SHA256
  `f9606b111203c9140dcdd6fc016a0ee00e4c8406aeb786187c42873f08d071fb`. Its `installed` getter returns
  `most_available_per_client_data.installed`; local data selects client id `"0"`. JavaScript
  character offsets 10,213,957 and 10,214,628 identify the getters. Source:
  [installed chunk, line 1](</C:/Program Files (x86)/Steam/steamui/chunk~2dcc5aaf7.js:1>).
- Native primary-action selection switches on the chosen per-client `display_status`, then
  `app_type`; several ready statuses return launch for shortcuts. An `installed` check occurs in one
  particular status branch, not as a universal launch gate. Primary-action resolver starts at
  character offset 11,530,637. Source:
  [installed chunk, line 1](</C:/Program Files (x86)/Steam/steamui/chunk~2dcc5aaf7.js:1>).
- Native `localGamesCollection` first filters `local_per_client_data.installed`, then removes
  shortcuts while GamepadUI is active. This is an offline native-client fact, separate from WSGM's
  Home carousel policy described below. Source:
  [installed chunk, line 1](</C:/Program Files (x86)/Steam/steamui/chunk~2dcc5aaf7.js:1>), search
  literal `get localGamesCollection()`.
- Valve's documented Steamworks `BIsAppInstalled` is a read for base applications; it supplies no
  shortcut availability setter. Community frontend typings expose shortcut properties and `RunGame`,
  but do not establish Valve-supported API stability. Sources:
  [Valve ISteamApps](https://partner.steamgames.com/doc/api/ISteamApps#BIsAppInstalled),
  [Decky frontend Apps source](https://github.com/SteamDeckHomebrew/decky-frontend-lib/blob/main/src/globals/steam-client/App.ts).
- Current toolkit writes shortcut exe, start directory and launch options through Steam's client;
  its client layer has no availability setter. `SetShortcutInstalled` does not appear in the
  inspected installed main chunk. Absence is limited to inspected source, not proof no private API
  exists anywhere. Sources:
  [SteamApps.cs:244](/D:/Coding/WSGM/external/steam-ui-toolkit/src/SteamUiToolkit/Client/SteamApps.cs:244),
  [installed chunk, line 1](</C:/Program Files (x86)/Steam/steamui/chunk~2dcc5aaf7.js:1>).

### Inferences

- Avoid falsifying `app_type` or `BIsShortcut`: identity, native collections and shortcut-specific
  properties should remain intact. Keep the entry in Steam and represent host-known availability
  separately.
- Overriding native per-client `installed` and `display_status` would require proving enum meanings,
  observable updates, selected versus most-available client behavior, filtering, primary actions and
  exact restoration. A synthetic uninstalled status may offer native Install/streaming affordances
  that cannot install a ROM. This has more client coupling than the issue's permitted equivalent
  unavailable state.
- Small reusable model: `{ appId, available, locationLabel, reason }`, plus monotonic revision;
  `appId` is Steam's confirmed unsigned shortcut id. Managed membership comes from WSGM, not from
  every high-bit id. Storage detection and metadata remain host policy.

### Gaps

- No runtime descriptor/observable inspection, semantic override, actual native filter rendering or
  controller test was performed. Offline source cannot prove that any getter can be safely claimed
  or that a modified store triggers all consumers.
- Whether native desktop-only library surfaces all share the same exported tile as Big Picture still
  needs an attended surface check. Existing toolkit claims grid and Home coverage, not independently
  proven current-client rendering everywhere.

## What existing presentation and refresh hooks should be reused?

### Takeaway

Extend the existing library tile/badge integration with per-shortcut availability and explicit
refresh. Do not create another badge system, claim the same tile through a competing wrapper, or
treat disconnected-library exclusion as unavailable presentation.

### Cited Findings

- The badge surface already claims the exported tile memo's `type`, preserves original identity
  through a cached wrapper, and identifies Valve's controller badge by element identity. It
  documents grid/Home coverage and reads Steam's `library_home_big_art` setting; tile-relative
  placement works in either Home layout. Sources:
  [SteamLibraryBadgeSurface.cs:43](/D:/Coding/WSGM/external/steam-ui-toolkit/src/SteamUiToolkit/Surfaces/SteamLibraryBadgeSurface.cs:43),
  [library-badge.ts:219](/D:/Coding/WSGM/external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/gates/library-badge.ts:219).
- Current offline bundle has exactly one factory matching both `ControllerSupportIcon` and
  `appportrait_`, the existing tile fingerprint. This confirms source uniqueness only; exported
  shape and rendered behavior remain live questions. Source:
  [installed chunk, line 1](</C:/Program Files (x86)/Steam/steamui/chunk~2dcc5aaf7.js:1>); parsing
  procedure follows
  [check-steam-fingerprints.mjs:44](/D:/Coding/WSGM/eng/check-steam-fingerprints.mjs:44).
- Badge state holds libraries `{name, connected, appIds}`. Badge color prefers `overview.installed`,
  using `connected` only when the overview has no boolean installed value. Consequently
  disconnected-library publication alone cannot reliably grey a shortcut. Sources:
  [SteamLibraryBadgeSurface.cs:9](/D:/Coding/WSGM/external/steam-ui-toolkit/src/SteamUiToolkit/Surfaces/SteamLibraryBadgeSurface.cs:9),
  [library-badge.ts:54](/D:/Coding/WSGM/external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/gates/library-badge.ts:54).
- WSGM retains absent cards in `LibraryBadges`; their remembered app ids and names still feed the
  badge. The present model builds app associations from tracked card libraries and Steam
  registration metadata. Source:
  [LibraryBadges.cs:129](/D:/Coding/WSGM/src/WSGM/Shell/LibraryBadges.cs:129).
- Badge publication presently replaces its map but does not actively rerender existing tiles; its
  comment explicitly waits for focus, scrolling or Home rebuilding. Source:
  [library-badge.ts:327](/D:/Coding/WSGM/external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/gates/library-badge.ts:327).
- Toolkit already has bounded mounted adoption with cached instance tracking and
  `rerender`/`release`, avoiding a whole-tree scan on every publication. Home already uses this
  primitive and an external-store revision signal. Sources:
  [gate-helpers.ts:735](/D:/Coding/WSGM/external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/gate-helpers.ts:735),
  [home-carousel.ts:350](/D:/Coding/WSGM/external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/gates/home-carousel.ts:350),
  [home-carousel.ts:487](/D:/Coding/WSGM/external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/gates/home-carousel.ts:487).
- WSGM Home explicitly treats every shortcut as installed through
  `local_per_client_data.installed || BIsShortcut()`. Independently, `eligible` rejects every
  `disconnectedAppId`. Adding managed shortcuts to that disconnected list would hide them from this
  carousel. This is WSGM/toolkit policy, not proof of a native Steam installed-only filter. Sources:
  [home-carousel.ts:128](/D:/Coding/WSGM/external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/gates/home-carousel.ts:128),
  [HomeCarousel.cs:28](/D:/Coding/WSGM/src/WSGM/Shell/HomeCarousel.cs:28).
- Home already generates grayscale rules for unavailable native games, scoped to its carousel's
  `[data-id]` artwork, and preserves an always-present container so state changes do not remount the
  carousel. Sources:
  [home-carousel.ts:233](/D:/Coding/WSGM/external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/gates/home-carousel.ts:233),
  [home-carousel.ts:306](/D:/Coding/WSGM/external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/gates/home-carousel.ts:306).
- The existing Game Library import page already has source selection, source-grouped preview,
  artwork review and controller-native Steam components, with a typed state/command surface.
  Sources:
  [library-import.ts:1](/D:/Coding/WSGM/src/WSGM/Core/SteamUiAssets/Source/library-import.ts:1),
  [SteamLibraryImportSurface.cs:49](/D:/Coding/WSGM/src/WSGM/Shell/SteamLibraryImportSurface.cs:49).

### Inferences

- Smallest presentation extension: add an optional per-app availability map to the existing library
  publication or one adjacent typed availability surface, and make the existing tile owner consume
  it. The badge's location projection should merge managed-source app membership and names with
  existing library associations, including disconnected sources. Per-file availability must override
  Steam's shortcut installed assumption for badge color; volume connectivity alone cannot
  distinguish a missing ROM on a present card.
- Draw the existing logical-storage badge and grey only artwork; preserve title legibility, focus
  outline, element keys, memo identity and Valve's navigation props. A semantic host availability
  map drives presentation and action logic; CSS is only the artwork rendering of that state.
- Update already-mounted cards and details immediately on revision using the existing bounded
  adoption/rerender pattern. Do not add new hooks directly into an already-mounted Valve wrapper;
  use the established separate reactive child component pattern where a subscription is required.
- Keep managed missing shortcuts in their existing all-apps, non-Steam and user collections. Do not
  send them through Home's disconnected-library exclusion. If Home is enabled, retain them in its
  existing candidate/list positions and add unavailable state to its grey logic; do not couple this
  to the optional show-uninstalled-native-games toggle.
- Use feature enablement independent of Card Manager if availability is enabled. Reuse its badge
  infrastructure without requiring unrelated card controls or duplicate transports.
- The user's clarified scope includes the full ROM importer together with availability. Extend the
  existing importer preview/sync screen with the ROM source, logical location and authoritative
  backing-path information; saving/synchronizing confirmed entries feeds the same availability model
  and source badges. This is part of the requested delivery, not a deferred importer prerequisite.
  Parser and sync details belong to the parallel importer lane.

### Gaps

- Current mounted tile refresh scheduling, focus retention during removal/reinsertion and Big Art
  transitions need real client proof.
- Native user-selected installed-only filters may intentionally omit unavailable titles if a future
  native state override is adopted. Define expected filter semantics explicitly; retaining a library
  record is different from overriding every user-selected filter.

## How should launching be blocked, and what is the first useful proof?

### Takeaway

Use a friendly unavailable affordance and best-effort CEF launch interception, backed by an
authoritative managed launcher check. First prove one retained shortcut can change state and refresh
on screen without restarting Steam or losing controller focus.

### Cited Findings

- Current installed main chunk has two direct `SteamClient.Apps.RunGame` calls: one selected-game
  list action and the shared game action path. Shared action also updates
  recently-launched/running-app state. Source:
  [installed chunk, line 1](</C:/Program Files (x86)/Steam/steamui/chunk~2dcc5aaf7.js:1>), shared
  call at character offset 11,535,327; search literal `SteamClient.Apps.RunGame`.
- RunGame's first argument is a GameID string, not necessarily the numeric shortcut app id; current
  native calls use `overview.GetGameID()`. Community typings also expose its void return. Sources:
  [installed chunk, line 1](</C:/Program Files (x86)/Steam/steamui/chunk~2dcc5aaf7.js:1>),
  [Decky frontend Apps source](https://github.com/SteamDeckHomebrew/decky-frontend-lib/blob/main/src/globals/steam-client/App.ts).
- The context-menu gate already locates the app through its `GetTargetApps`, wraps its render,
  borrows Steam's emitted item type and routes bounded `{appId,id}` commands. It currently adds host
  items; replacing the primary launch item is an extension rather than existing delivered
  functionality. Source:
  [game-context-menu.ts:33](/D:/Coding/WSGM/external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/gates/game-context-menu.ts:33).
- Toolkit ownership supplies narrow claim/release operations; patch lifecycle requires unique probe,
  apply, verify and exact owned removal, with stock Valve behavior restored on incompatibility.
  Source: [toolkit AGENTS.md:117](/D:/Coding/WSGM/external/steam-ui-toolkit/AGENTS.md:117).

### Inferences

1. Publish only confirmed managed shortcut ids, their actual GameID strings, availability, library
   label and reason through the existing session host/bridge. Build the GameID-to-shortcut map from
   Steam's actual overview, rather than guessing a numeric conversion.
2. Extend the tile owner's rendering and badge read to use this semantic availability. Extend
   primary play/context-menu affordances to offer "Storage unavailable" or "Reconnect SD Card -
   Retro", with an explanation and explicit Recheck action. Borrow known Steam components and
   preserve controller navigation. Details-page Play needs its own proven transform; a menu-only
   addition is insufficient.
3. Consider a narrow `SteamClient.Apps.RunGame` claim for unavailable managed GameIDs, passing every
   unmanaged/available call and all arguments unchanged to the original. Refuse the missing launch
   and show the same explanation. Prefer blocking before Valve updates recently-launched/running-app
   state through the primary action hook; a final RunGame guard covers other CEF call sites but can
   leave surrounding UI bookkeeping to reconcile.
4. This CEF guard is best effort. Steam protocol launches, external shortcuts, another native
   window/process or an incompatible client can bypass it. The managed launcher must validate actual
   backing content and expected source identity immediately before starting, then display the same
   friendly message. It also closes the race where media disappears after the UI last checked.
   Coordinate the launcher metadata interface with the other lane.
5. First attended proof after implementation authorization: one disposable managed shortcut,
   retained original id and collection membership; publish available/unavailable/available while
   grid and Home remain open, toggle Big Art, retain selection/focus, attempt Play, context-menu
   Play and protocol launch, then disable/remove the patch and verify exact stock restoration. No
   storage hardware is needed for this first CEF proof; removable-media and drive-letter acceptance
   remain separate later checks.

### Gaps

- The actual RunGame property descriptor, UI modal routing, details Play element shape and removal
  behavior were not inspected live. The hook is a candidate, not a verified integration.
- No build, test suite, deployment, application launch, live Steam attachment or mutation occurred.
  This research establishes source mechanisms and concrete risks; visual/controller/runtime
  acceptance remains open.
