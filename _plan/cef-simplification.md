# CEF inventory and the road to one system

Written 2026-08-30, at the close of the QAM/Steam-UI revival push, while everything below is fresh
and live-verified on the reference Claw. Every claim here was either read out of the running client,
proven by a device log line, or paid for as a bug this session. This is the plan for collapsing two
generations of CEF integration into one — analysis first; no code moves with this document.

## 1. What exists today: two generations

### Generation 1 — per-feature, one-shot, self-managed (2025 → mid-2026)

Built feature by feature before a shared system existed. Each module owns its own connection
attempt, its own residency trick, and its own retry story. Transport is `SteamCef` — enable the
debug port, find SharedJSContext, open a socket, evaluate one expression, close.

| Module | Lines | Target | Mechanism | Residency | Config flag |
| --- | --- | --- | --- | --- | --- |
| `Core\SteamCef.cs` | 440 | SharedJSContext | one-shot `Runtime.evaluate` transport + port enable | n/a | `Cef.Enabled` |
| `Core\SteamCdp.cs` | 435 | SharedJSContext | library folder add/remove (SD format's CEF budget) | one-shot | `Cef.SdFormat` |
| `Core\SteamCollections.cs` | 315 | SharedJSContext | library reads + legacy collection-id cleanup | one-shot | — |
| `Core\SteamLibraryTabs.cs` | 285 | SharedJSContext | injected in-memory library tabs (TabMaster mechanism) | resident script, own version sentinel | `Cef.LibraryTabs` |
| `Core\LibraryFilter.cs` | 653 | (pure) | filter trees evaluated against appStore | n/a | `Cef.LibraryTabs` |
| `Shell\LibraryTabManager.cs` | 1120 | both | sync orchestration, boot-wait, own retry policy | own retry loop | `Cef.LibraryTabs`/`CardManager` |
| `Core\SteamPageBridge.cs` | 280 | **MainWindow** | current-game detection + card badge (MutationObserver) | resident, `window.__wsgm` sentinel, version 4 | `Cef.CardManager` |
| `Core\SteamDownloadSort.cs` | 328 | **MainWindow** | JSX-runtime injection of queue sort buttons | resident, `dlSortVer` sentinel | `Cef.DownloadQueueSort` |
| `Core\SteamDownloads.cs` | 120 | SharedJSContext | download overview snapshot (keep-awake) | one-shot | `Cef.DownloadKeepAwake` |
| `Core\SteamNetworkIndicator.cs` | 288 | SharedJSContext | synthetic AP into `SystemNetworkStore` | resident, `netVer` sentinel, 10 s re-push | `Cef.WifiIndicator` |
| `Core\SteamArtwork.cs` | 202 | SharedJSContext | `SetCustomArtworkForApp` one-shots | one-shot | `Cef.Artwork` |
| `Core\SteamLaunchConfig.cs` | 391 | SharedJSContext | launch options / shortcut read+write | one-shot | — |

Lifecycle is scattered: `ShellSession` calls four `DisableAsync`s in three separate places
(desktop-mode entry, CEF-config-off, dispose), `LibraryTabManager` waits for Big Picture on its own,
and each resident script defends itself with a hand-rolled version constant that must be bumped in
two places when its behavior changes.

### Generation 2 — one transport, one asset, one protocol (mid-2026, the QAM push)

| Piece | Lines | Owns |
| --- | --- | --- |
| `Core\SteamUiEndpointDiscovery.cs` | 210 | allowlisted target discovery (reuses `SteamCef` port plumbing) |
| `Core\SteamUiCdpConnection.cs` | 407 | one bounded CDP socket |
| `Core\PersistentSteamUiTransport.cs` | 501 | persistent per-target connections + generation tracking |
| `Core\SteamUiPatchManager.cs` | 519 | probe → apply → verify → poll state machine, resource keys |
| `Core\SteamUiBridge.cs` | 595 | envelope schema, **command allowlist**, sequence/action-generation validation |
| `Core\SteamUiAssetCatalog.cs` | 37 | hash-pinned built asset |
| `SteamUiAssets\Source\NativeQamBootstrap.ts` | 3160 | the injected side: bridge + every gate + native rows + panel wrap |
| `Shell\SteamUiSessionHost.cs` | 1676 | registration, publish loop, request router, backlight poll |
| patch classes (12) | ~2100 | per-surface probe/apply/verify/remove |

Everything revived this season — Performance tab, TDP RPC seam, VRR, audio, Bluetooth, network gate,
brightness backend, resolution/refresh in Quick Settings — runs on generation 2. It has the
properties generation 1 lacks: a persistent connection with generation change events, a validated
message schema, an allowlist, per-patch verified state, and one build-time-hashed injected asset
that replaces itself when it changes.

## 2. The verified-primitive catalog

The whole point of unifying **now** is that the primitives are finally known. Each entry below is
device-verified; the taxonomy is what `docs\steam-cef.md` records in detail.

**Gate kinds** (all in the bootstrap today):

1. **Supply an absent namespace** — Audio (`SteamClient.System.Audio`), Perf
   (`SteamClient.System.Perf`). The store's availability is literally `null != namespace`.
2. **Override one store getter/flag** — network (`networkManagementAvailable`), brightness
   (`is_display_brightness_available`).
3. **Overlay one RPC answer** — SteamOS Manager `GetState` (TDP range), merged into the client's
   own reply, never replaced.
4. **Replace a stub service's methods + invalidate its react-query** — Bluetooth
   (`staleTime: Infinity` means replacement alone changes nothing).
5. **Mount Valve's components** — selected by localization token or structure, never by minified
   export name; panel wrap via patched `useMemo`; Quick Settings matched by source.
6. **Watch client settings** — Valve's TDP rows write `steamos_tdp_limit*` and call nobody; the
   gate reads `window.settingsStore.clientSettings` (the store Valve's own hooks read).
7. **Feed a store's own ingestion path** — audio `RegisterOrUpdateDevice` +
   `OnAudioDeviceVolumeChanged`, brightness `m_flDisplayBrightness.Set`, network indicator
   `SetDeviceInfo`.

**Hard rules, each learned as a production failure:**

- **Ownership markers on everything injected.** The self-incompatibility teardown loop (probe
  requires the pre-patch condition its own apply invalidates) has now happened **three times**:
  audio namespace, network getter, brightness flag. Overlays must carry the replaced original on
  themselves so a bridge replaced in place unwinds instead of stacking (Manager `GetState`,
  brightness `SetBrightness`).
- **The second gate.** Filling a store proves nothing until the render gate opens:
  constructor-cached `m_bAvailable`, `staleTime: Infinity` queries, prototype getters.
- **Protobuf at every namespace boundary.** `UpdateSettings` receives `serializeBase64String()`,
  `SetSetting` sends one; decode with the message class's own `deserializeBinary`, taken from an
  instance the client builds.
- **Argument order is read, never assumed.** The audio direction enum (`Input=0, Output=1`, module
  74362) was assumed backwards and produced three distinct bugs; `SetDeviceVolume` is 3-ary.
- **Bridge envelopes need `actionGeneration > 0`** — five gates passed 0 and every one of their
  requests was silently rejected.
- **Every refusal logs host-side** (`did nothing:` lines) — the reason otherwise returns to a JS
  side with nowhere to put it.
- **Observable feeds write only on their own change** (the volume/brightness echo rule), or they
  fight the user's drag and spam the OSD.
- **Never iterate the module registry constructing exports** (restarted the machine once); resolve
  ids as literals, match by token, read `String(factory)`.

## 3. The debt, named

**Cross-generation:**

- Two transports. Gen-1 reconnects per call; gen-2 holds generations. `SteamUiEndpointDiscovery`
  already leans on `SteamCef` internals, so the seam exists.
- Three hand-rolled residency sentinels (`netVer`, `dlSortVer`, `__wsgm` v4) doing what the patch
  manager's asset hash + generation + verify-poll already does better.
- Gen-1 lifecycle is smeared across `ShellSession` (three separate disable sites); gen-2 lifecycle
  is `SetPatchEnabled`.
- `SteamNetworkIndicator` (gen-1) and `SteamNetworkGatePatch` (gen-2) **both operate on the same
  `SystemNetworkStore`** from different systems with different reconnect stories.
- `LibraryTabManager` (1120 lines) duplicates boot-wait/retry that the patch manager owns.

**Inside the bootstrap (3160 lines):**

- Six copies of the webpack runtime tap.
- Five marker styles for one concept (`__wsgmOwnedNamespace`, `__wsgmOwnedGetter`,
  `__wsgmOwnedGetState` + original field, `__wsgmBrightnessRevealed`, `__wsgmOwnedSetBrightness` +
  original field).
- Two action-generation counters (`actionGenerations` for rows, `gateActionGenerations` in
  `request()`).
- The echo/only-on-own-change rule implemented separately for volume and brightness.
- The append chain: ten near-identical `if (wants(kind)) push(row)` blocks; the row order — which
  the maintainer set deliberately — is legible only by reading all ten.
- Triple bookkeeping per kind: `definitions`, `quickSettingsKinds`, `registrations`.
- Retired-but-present rollback rows: `valveFrameLimit` and `valveVrr` mount code and patch classes
  remain, unregistered, after WSGM's own rows replaced them.

**Inside the host (1676 lines):**

- The request router is one ~300-line else-if chain.
- The publish loop hand-lists eight serialize+publish stanzas.
- `TryReadIntegerPayload` requires exactly one property — already cost one real bug (the TDP
  payload) and remains a trap for the next two-field command.

## 4. Target architecture

One sentence: **generation 2 becomes the only system; generation 1 modules become patches, bridge
commands, or plain one-shot calls on the shared transport; the bootstrap becomes modular TS with one
gate contract; the host becomes tables.**

- **One transport.** `PersistentSteamUiTransport` serves everything. `SteamCef` shrinks to the two
  primitives discovery already borrows (port enable, target listing) and stops being a public eval
  surface. One-shot callers (artwork, launch config, library folders, collections, download
  overview) become `EvaluateAsync` calls on the transport — they don't need patches, just the
  shared socket and its generation awareness.
- **One resident system.** Everything that must survive inside Steam is a patch with a probe,
  ownership marker, verify, and remove: badge, download-sort buttons, network indicator, library
  tabs. Version sentinels die; the asset hash and patch generations replace them.
- **One injected asset, many source files.** `Source\` splits into modules (`bridge.ts`,
  `gates\*.ts`, `rows\*.ts`, `panels.ts`, `lib\runtime.ts`) compiled by the existing build into the
  same single hashed asset. The 3160-line file is the single biggest reviewability cost in the tree.
- **One gate contract.** A shared `createGate({ id, probeOwn, apply, unwind, status })` factory
  providing: the runtime tap, the ownership-marker convention (`__wsgmOwned<X>` + stashed
  original), install-accepts-ours, remove-unwinds, status with `lastError`. The teardown trap
  becomes structurally impossible instead of individually remembered.
- **One row table.** `[{ kind, key, control, placement }]` in display order; `appendControls`
  walks it. The order becomes one visible list.
- **One router.** Host-side `(patchId, command) → handler` dictionary; payload readers per shape
  (single int, int+flag, id+flag) instead of one reader with a hidden arity rule.
- **Lifecycle in one place.** `SetPatchEnabled` driven from config; the three scattered gen-1
  disable sites disappear with the modules that needed them.

Config flags keep their exact meanings — `Cef.LibraryTabs`, `CardManager`, `DownloadQueueSort`,
`WifiIndicator` simply gate patch enablement instead of module calls. No user-visible change.

## 5. The road, phased by risk

Every phase ends deployable by file swap and independently committable. Device checks listed are
the full gate for that phase — nothing merges on "compiles".

**Phase 0 — freeze the knowledge (no code).** This document; `docs\steam-cef.md` updated with this
season's findings (direction enum, base64 boundaries, DisplayManager gate and the `_external`
consequence, `\\.\LCD` backend, action-generation rule, teardown-trap rule). *Done with this
commit.*

**Phase 1 — bootstrap-internal, behavior-identical.** Runtime-tap helper (6→1), one
action-generation counter, marker convention + gate factory adopted by the existing six gates, the
row table, shared echo-rule helper, then the module split into TS source files. Verify: asset hash
changes, `qam-harness install/status` green, one full QAM/Settings pass on device.
*Risk: low — pure structure; the harness exists precisely for this.*

**Phase 2 — host-internal.** Router table, publish-loop table, payload readers, split
`SteamUiSessionHost` (publication, routing, observation). Verify: unit suite (the bridge tests
already cover envelope validation) + device pass. *Risk: low-medium.*

**Phase 3 — migrate gen-1 residents, one per commit, easiest first.**

1. `SteamDownloadSort` → MainWindow patch (glyph-style patch proves the target works). Verify:
   buttons present after Steam restart and after library reload, gamepad focus reaches them.
2. `SteamNetworkIndicator` → **merge into the network gate** (same store, same gate file); its 10 s
   re-push becomes bridge state publication. Verify: header bars track SSID/strength, survive
   Steam restart, no double-write with the scan gate.
3. `SteamPageBridge` badge + current-game → MainWindow patch + bridge state (badge map) and a
   bridge query (current game). Verify: badge on card games, survives SPA navigation, CSSLoader
   coexistence re-checked.
4. Library tabs: `SteamLibraryTabs` script → patched asset; `LibraryTabManager` keeps *policy*
   (what tabs exist) and sheds *transport/retry* (the patch manager's). Verify: the full tab
   matrix — boot sync, card insert/eject, filter tabs, native-tab hiding, badge sync — this is the
   riskiest migration and goes last.

**Phase 4 — retire.** `SteamCef` public eval surface deleted; one-shot callers on the transport;
`valveFrameLimit`/`valveVrr` rollback rows and patch classes deleted after Phase 3 has soaked one
release. Verify: grep-clean, full attended pass, `eng\verify.ps1`, installer build.

**Explicitly not in scope:** multi-launcher anything; `SteamClient.System.DisplayManager` supply
(its own future seam — it would restore Valve's VRR row and retire the `_external` twins, but it is
new capability, not simplification); mic-volume backend; per-app audio.

## 6. Standing risks

- Steam client updates re-minify: everything selected by token/structure survives; anything that
  still matches an export name does not. Phase 1's gate factory should assert selection style.
- The MainWindow target only exists in game mode; patches targeting it must tolerate long absence
  (glyph patch already models this).
- Library tabs are the one surface where a failed migration is instantly user-visible on every
  boot; hence last, behind its config flag, with the old module kept one release as rollback.
