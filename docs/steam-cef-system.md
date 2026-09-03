# Steam CEF system

How WSGM drives Steam's Chromium front-end: how Steam is found and launched, when the one CDP
transport may be open, what the session host injects and patches, how the native Quick Access Menu
is rebuilt on Windows, how the library features are wired, and how it is configured, logged and
tested. It is a mechanism reference; the device findings and the reasoning behind each rule are in
`docs\steam-cef.md`, and the toolkit's own contract is in
`external\steam-ui-toolkit\docs\reference.md`.

Related:

- `docs\steam-cef.md` — findings and disproven approaches.
- `external\steam-ui-toolkit\docs\reference.md` — transport, patch lifecycle, bridge, surfaces.
- `docs\boot-and-shell.md` — the desktop/game transition sequence that calls the transport gate.
- `docs\device-plugin-system.md` §15 — glyph data on the device side.
- `_plan\2.0-decisions.md` D16, D17, D18 — the standing product decisions.

## 1. Components and ownership

```text
 Core\Steam.cs                registry discovery, Big Picture launch, shortcuts, update stop
 Shell\SteamMonitor.cs        5 s alive/dead poll → SteamStarted / SteamExited
 Shell\SteamUiReadiness.cs    "may the transport be open?" and RunWhenReady
 ShellSession                 the transport gate loop, retract-before-Big-Picture, master switch
   └─ PersistentSteamUiTransport (toolkit)   one CDP connection per role, attached to SteamUiTransportSession
      └─ Shell\SteamUiSessionHost.cs         the one patch/bridge/module owner
           ├─ SteamUiBridgeHost + NativeQamBootstrap.js (Core\SteamUiAssets, composed from the toolkit)
           ├─ SteamUiPatchManager: bridge, 6 gate patches, 11 row patches (toolkit), download sort, glyph style
           ├─ SteamUiModuleRuntime: publications down, commands up
           └─ NativeQam*Service (Shell\)     the backends: TDP, AutoTDP, frame limit, VRR, controller
                                             target, device controls, audio, network, Bluetooth,
                                             brightness, resolution — each feeds one toolkit surface
 Core\SteamLibraryTabs.cs, SteamPageBridge.cs   legacy resident scripts: tabs and the card badge
 Core\SteamCdp.cs, SteamLaunchConfig.cs, SteamArtwork.cs, SteamCollections.cs, SteamDownloads.cs
                                                one-shot evaluations through the session transport
 tools\WsgmLibTest\                             live probes and the QAM harness
```

Ownership follows decision D16. The toolkit owns how to find, own and remove a thing safely, and
every revived Valve surface: the six gates, the eleven Quick Access rows, the module ids and
localization tokens they name, and the wire shape of each state and command. WSGM owns the data
behind them (its managers, RTSS, the device plugin) adapted onto the toolkit's `ISteam*Backend`
interfaces by the `NativeQam*Service` classes, and the policy about which patches are on when. Its
own features (library tabs, the card badge, download sorting, glyph delivery) stay WSGM's. A plugin
owns nothing here; device state reaches the QAM only through WSGM's backend services.

## 2. Finding and driving Steam

`Core\Steam.cs` is static; WSGM is Steam-exclusive and there is no path setting. The executable is
resolved from `HKCU\Software\Valve\Steam\SteamExe`, then
`HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath` plus `steam.exe`, re-validated on every read.
`InstallDirectory` is the one accessor for everything WSGM writes beside Steam: the CEF flag and the
Steam Input proxy.

| Fact                | Value                                                                                                   |
| ------------------- | ------------------------------------------------------------------------------------------------------- |
| Process names       | `steam`, `steamwebhelper`; only `steam.exe` services `steam://` URLs                                    |
| Big Picture window  | class `SDL_app` owned by a Steam process; `IsBigPictureVisible` is stronger than `IsRunning` on purpose |
| URLs                | `steam://open/bigpicture`, `steam://close/bigpicture`, `steam://exit`                                   |
| Shortcuts           | Ctrl+1 opens the Steam menu, Ctrl+2 the Quick Access Menu, sent without a foreground gate               |
| Update stop         | 10 s budget, 5 s graceful `steam://exit` window, never kills Steam                                      |
| Monitor poll        | every 5 s at background priority; `SteamStarted` only after Steam was seen dead                         |
| Auto relaunch       | 10 s after an exit, when `SteamAutoRelaunch` is set                                                     |
| `RunWhenReadyAsync` | up to 30 attempts, 3 s then 5 s apart; logs `<op>: waiting for the Big Picture window.` once            |

A cold `LaunchBigPicture` passes the Big Picture URL on the command line so Steam boots straight
into it; only that path reconciles the Steam Input shim and writes the CEF flag, because the flag
takes effect on a fresh start. With `SteamLaunchUnelevated` and WSGM elevated, Steam starts through
the de-elevated scheduled task; `Steam launch integrity: …` records which path ran. A warm launch
fires the protocol URL. Readiness is `IsRunning && IsBigPictureVisible`.

## 3. The transport gate

Steam's CEF exposes an unauthenticated, loopback-only debug port. The toolkit verifies that port
8080 is owned by Steam and that the debugger URL is loopback before connecting; the accepted
security posture is in `docs\steam-cef.md`. WSGM adds a second guard of its own.

### A cold-starting Steam must not be touched before its window exists

**A running Steam process and a reachable `SharedJSContext` are not proof that a cold-start UI is
ready.** Steam opens its CEF port seconds before it has a Big Picture window. On a failed boot the
Steam Input proxy had initialized cleanly, CEF accepted the download-sort injection, and the card
monitor was replacing a library before any window existed; that boot never produced one, and manual
Steam starts with the same proxy did (Claw, 2026-08-22). The proxy was cleared by that trace; the
CEF touch was the difference.

A `SharedJSContext` generation is not a readiness signal either. On a desktop-to-game transition
that cold-started Steam, the patch host applied on the first `GenerationChanged`: download sort and
the running-application probe were on CEF at +2.9 s and the native-QAM bootstrap plus eighteen more
patches were Applied/Verified by +4 s. No `SDL_app` window ever appeared and Steam had to be ended
from Task Manager (Claw, 2026-09-01). The one cold boot in the same log that succeeded had connected
80 ms after `Big Picture window detected`: the same race, won.

The rule: **the transport is closed whenever game mode has no Big Picture window.** The flag is the
one choke point the patch host, the running-application probe and every one-shot evaluator share, so
nothing WSGM does can reach a cold-starting Steam's port before its window exists.

```text
TransportShouldBeOpen(cefMaster, inGameMode, bigPictureRequestPending, bigPictureReady)
  = cefMaster && ((!inGameMode && !pending) || bigPictureReady)
```

Desktop mode opens on the master switch alone. `ShellSession` runs a one-second gate loop that
re-decides on every signal (mode change, `SteamStarted`, `SteamExited`, master switch) and logs the
transition under `Log.Change("steam-ui-transport-gate", …)`:

| Log line                                                                                                                         | Meaning                     |
| -------------------------------------------------------------------------------------------------------------------------------- | --------------------------- |
| `Steam UI transport open: Big Picture window is up.`                                                                             | healthy game mode           |
| `Steam UI transport open: desktop mode.`                                                                                         | healthy desktop mode        |
| `Steam UI transport closed: game mode without a Big Picture window — holding every automatic CEF touch until Steam's UI exists.` | Steam cold-starting or gone |
| `Steam UI transport closed: Big Picture was requested — holding every automatic CEF touch until Steam's UI exists.`              | transition in flight        |
| `Steam UI transport closed: Steam CEF integration is off.`                                                                       | master switch off           |

A healthy cold boot shows `Big Picture window detected` before the first `open:` line and before any
`steam.ui.patch.<id>: Applied`. The gate decision is applied before
`SteamUiTransportSession.Attach`, because attaching copies the flag and an open transport with a
subscriber starts discovery at once. Overlay-test mode never attaches a transport. Card-volume
notification and scanning still start immediately so a present card and removals are not missed; the
live library add/remove, tab and manifest sync, and download-state polling wait for the window.
Desktop download polling and overlay-driven operations stay immediate because they do not act on a
half-built game-mode session.

### Retract before Big Picture

Steam rebuilds its front-end for a Big Picture request and bootstraps against whatever
`SteamClient.System.*` says exists, so namespaces WSGM supplied on the desktop would go unanswered
once the gate closes. `PrepareSteamUiForBigPictureAsync` marks the request pending, disables the
session host, the card badge and the library tabs, and closes the transport, under a 5 s budget; on
timeout it logs
`Steam UI retraction did not finish before the Big Picture request; continuing with the transition.`
When the transition settles, the hold is released, the gate is re-checked and the surfaces are
re-applied. The transition sequence itself is in `docs\boot-and-shell.md`.

Mode events: `DesktopModeStarting` clears game mode, cancels the tab boot sync, turns the indicator
and download sort off and retracts badge and tabs; `GameModeEntered` sets game mode, re-checks the
gate, turns them on and starts the tab boot sync. `SteamStarted` and `SteamExited` both request a
gate check so a restart's headless context is never connected before its own window.

### Master switch

Turning `Cef.Enabled` off stops the card volume monitor and the tab boot sync, then under the gate
disables the host, badge and tabs and closes the transport
(`Steam CEF integration disabled — injected UI retracted.`). Every evaluation then fails closed,
which is why removal is awaited before the choke point closes. Turning it on re-runs the gate
through readiness rather than opening directly.

## 4. The session host

`Shell\SteamUiSessionHost.cs` owns one `SteamUiBridgeHost` over the embedded bootstrap asset, one
`SteamUiPatchManager`, one `SteamUiModuleSet` and one `SteamUiModuleRuntime`. It registers the
bootstrap patch first and every module's patches after it, starts with everything disabled, and
follows the transport's generation events and every service's `StateChanged`. The shell applies four
switches in order: native Quick Access, the network indicator, download sort, glyph delivery.

### Modules and their commands

Every module but `shell` is a toolkit surface's `Module(enabled, read, backend)`; the patch id and
command vocabulary are the surface's constants, and WSGM contributes the state reading and the
backend (toolkit reference §15).

| Module                                                                         | Toolkit surface                                     | WSGM backend                                        |
| ------------------------------------------------------------------------------ | --------------------------------------------------- | --------------------------------------------------- |
| shell                                                                          | none (`wsgm.native-qam.shell`, `toggleQuickAccess`) | the overlay toggle                                  |
| tdp                                                                            | `SteamPowerLimitSurface`                            | `DeviceCoordinatorNativeQamTdpService`              |
| auto-tdp                                                                       | `SteamAutoTdpRow`                                   | `DeviceCoordinatorNativeQamAutoTdpService`          |
| frame-limit                                                                    | `SteamFrameLimitRow`                                | `PerformanceServiceNativeQamAdapter`                |
| controller-target                                                              | `SteamControllerTargetRow`                          | `DeviceCoordinatorNativeQamControllerTargetService` |
| vrr                                                                            | `SteamVariableRefreshRow`                           | `PerformanceServiceNativeQamAdapter`                |
| perf (with Valve's header, toggle, reset, overlay-level and refresh-rate rows) | `SteamPerformanceSurface`                           | `PerformanceServiceNativeQamAdapter`                |
| brightness                                                                     | `SteamBrightnessSurface`                            | `NativeQamBrightnessService`                        |
| device-controls                                                                | `SteamDeviceControlsRow`                            | `DeviceCoordinatorNativeQamDeviceControlsService`   |
| resolution (only with a display service)                                       | `SteamResolutionRow`                                | `NativeQamResolutionService`                        |
| audio (only with an audio manager)                                             | `SteamAudioSurface`                                 | `AudioManagerNativeQamAudioService`                 |
| network (only with a radio manager)                                            | `SteamNetworkSurface`                               | `NativeQamNetworkService`                           |
| bluetooth (only with a radio manager)                                          | `SteamBluetoothSurface`                             | `NativeQamBluetoothService`                         |

Publications are enabled while native Quick Access is on; the network publication is also enabled
while the header indicator is on.

### Patch inventory

| Patch id                        | Class                                | Target          | Resource key                         | Enabled by                 |
| ------------------------------- | ------------------------------------ | --------------- | ------------------------------------ | -------------------------- |
| `steam-ui.bridge`               | `SteamUiBridgePatch` (toolkit)       | SharedJSContext | `steam-ui.bridge-binding`            | QAM or network indicator   |
| `steam-ui.performance`          | gate `perf`                          | SharedJSContext | `steam-ui.performance-namespace`     | QAM                        |
| `steam-ui.audio`                | gate `audio`                         | SharedJSContext | `steam-ui.audio-namespace`           | QAM, audio manager present |
| `steam-ui.power-limit`          | gate `steamOsManager`                | SharedJSContext | `steam-ui.steamos-manager-state`     | QAM                        |
| `steam-ui.brightness`           | gate `brightness`                    | SharedJSContext | `steam-ui.brightness-availability`   | QAM                        |
| `steam-ui.bluetooth`            | gate `bluetooth`                     | SharedJSContext | `steam-ui.bluetooth-manager-service` | QAM, radio manager present |
| `steam-ui.network`              | gate `network`                       | SharedJSContext | `steam-ui.network-availability`      | QAM or network indicator   |
| eleven `steam-ui.*` row patches | `SteamQuickAccessRowPatch` (toolkit) | SharedJSContext | `steam-ui.performance-root`          | QAM                        |
| `wsgm.download-sort`            | `SteamDownloadSortPatch`             | SharedJSContext | `steam.downloads.jsx-runtime`        | download sort only         |
| `wsgm.steam-input.glyph-style`  | `SteamInputGlyphStylePatch`          | MainWindow      | `wsgm.steam-input.glyph-style`       | glyph delivery only        |

### Switching and synchronization

Disabling is two passes: first the components and glyphs come off with the bootstrap still up, so
removals have a bridge to talk to, then the bootstrap and the global switch. The host polls nothing;
its loop waits on a coalesced signal, runs the patch manager's synchronization, then publishes state
when the QAM or indicator is on, or lets the global switch follow `downloadSort || glyphs` so an
independent patch keeps the manager alive.

A `SharedJSContext` generation change cancels every in-flight semantic request and releases the RTSS
observation. The RTSS observation is held only while native Quick Access is on, the bridge is ready,
and the frame-limit or overlay-level row is `Verified`: RTSS polling exists for rendered rows, not
for the session. Glyph delivery is enabled only when the setting is on and the presentation carries
resources, controller images or absent controls (`Log.Change("steam.ui.glyphs", …)`).

Disposal runs the disable passes, disposes the runtime first so it stops answering, then the patch
manager, the bridge and the services; the shell detaches and disposes the transport afterwards.

## 5. The injected asset

### Build

`eng\build-steam-assets.mjs` compiles one script from the toolkit's fragments (`types.ts`,
`bridge.ts`, `ownership.ts`, `rpc.ts`, `gates\*.ts` sorted, then `components.ts`), any WSGM-only
fragments under `Core\SteamUiAssets\Source` (none today), and the toolkit's `epilogue.ts`, and
closes the IIFE itself. Fragments are discovered by directory: adding a gate is a new file in the
toolkit's `gates\` plus its `Steam*Surface` class, and nothing else. The program is type-checked
with TypeScript 7, type-stripped, cut at `// @steam-ui-bundle-start`, formatted with Prettier, and
written as `Core\SteamUiAssets\NativeQamBootstrap.js`; its SHA-256 is rewritten into
`Core\SteamUiAssetCatalog.cs`. At runtime the catalog re-hashes the embedded resource and throws on
a mismatch, so a hand edit cannot ship.

| Check                         | Fails on                                                                                 |
| ----------------------------- | ---------------------------------------------------------------------------------------- |
| `npm run steam-assets:check`  | stale file, stale hash, a second `.js` beside the asset, a BOM, invalid UTF-8, > 256 KiB |
| `npm run steam-assets:claims` | the toolkit's ownership scenarios against the shipped bytes                              |

Both run in `eng\verify.ps1` and in CI.

### Anatomy

| Region                      | Origin  | Content                                                                                                                  |
| --------------------------- | ------- | ------------------------------------------------------------------------------------------------------------------------ |
| prelude                     | toolkit | reuse check, request/subscribe/deliver/dispose, gate registry, ownership primitives, `transportReply`, `invalidateQuery` |
| `gates\audio.ts`            | toolkit | supplies `SteamClient.System.Audio`                                                                                      |
| `gates\bluetooth.ts`        | toolkit | replaces the Bluetooth service stub's methods                                                                            |
| `gates\brightness.ts`       | toolkit | reveals brightness and claims `SetBrightness`                                                                            |
| `gates\network.ts`          | toolkit | overrides `networkManagementAvailable`, feeds the network store                                                          |
| `gates\performance.ts`      | toolkit | supplies `SteamClient.System.Perf`                                                                                       |
| `gates\steam-os-manager.ts` | toolkit | overlays the SteamOS manager `GetState` and watches the TDP settings                                                     |
| `components.ts`             | toolkit | the React component host that mounts rows into Valve's panels                                                            |
| `epilogue.ts`               | toolkit | `return installResult;`                                                                                                  |

Every gate returns `{install, remove, status}` and registers itself under its name. The C# side
reaches a gate through `window[namespace].gate(name)`; a missing gate reads the same as a missing
bridge.

### Gates

| Gate             | Literal module                            | What it does                                                                                                                                                                                                                                                                                                           | Markers                                                                                                 |
| ---------------- | ----------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------- |
| `perf`           | `74514` (perf store holder)               | `supplyNamespace(SteamClient.System, "Perf")` with `UpdateSettings(base64)` decoded through the store's own message class and forwarded as `updateSettings {delta}`; state written into `SystemPerfStore.m_msgState`                                                                                                   | `__steamUiOwnedNamespace`                                                                               |
| `audio`          | `1409` (audio store)                      | supplies `System.Audio` (`GetDevices`, `SetDefaultDeviceOverride`, `SetDeviceVolume(id, direction, volume)`, no-op app volume, eight `RegisterFor*`); state feeds the running store through `RegisterOrUpdateDevice` and sets `m_bAvailable`; dispatches a volume change only above 0.004                              | `__steamUiOwnedNamespace`                                                                               |
| `steamOsManager` | `90389` (manager), `21371` (query client) | claims `GetState` and merges `is_tdp_limit_available`, `tdp_limit_min`, `tdp_limit_max` into the real reply; invalidates `["SteamOSService","State","Manager"]`; watches `steamos_tdp_limit` and `steamos_tdp_limit_enabled` (settings change plus a 1 s timer) and sends `setPrimaryLimit {watts, enabled}` on change | `__steamUiOwnedGetState`, `__steamUiOriginalGetState`                                                   |
| `brightness`     | `59547` (display settings)                | `claimValue` on `is_display_brightness_available`, `claimMember` on `SetBrightness` → `setBrightness {percent}`; state sets the slider                                                                                                                                                                                 | `__steamUiBrightnessRevealed`, `__steamUiOriginalBrightnessAvailability`, `__steamUiOwnedSetBrightness` |
| `network`        | `77347` (network store)                   | `claimAccessor` on the prototype getter `networkManagementAvailable`; wraps start/stop scanning and always calls through; writes up to 24 synthetic access points (ids 990001+) through `SetDeviceInfo`; removal deletes them and calls `ForceRefresh`                                                                 | `__steamUiOwnedGetter`, `__steamUiOriginalGetterDescriptor`, `__steamUiOwnedNetworkScan`                |
| `bluetooth`      | `60517` (service stub), `21371`           | replaces eleven methods on the stub; one synthetic adapter; invalidates `["BluetoothManagerService","State"]`                                                                                                                                                                                                          | `__steamUiOwnedBluetoothService`, `__steamUiOriginalBluetoothServiceMethod`                             |

### The component host

`components.ts` mounts nothing into the DOM and injects no CSS. It resolves Valve's own primitives
by localization token and source shape (React, the slider, dropdown and toggle fields, panel section
and row, the localizer), then wraps `React.useMemo` so that when the Quick Access tab array passes
through a memo, two panels are replaced by wrappers. The Performance panel is found by export
identity through `#QuickAccess_Tab_Perf_Common_Settings`,
`#QuickAccess_Tab_Perf_BatteryTimeRemaining` and `TS.ON_FRAME`; the Quick Settings panel by source
containing `#QuickAccess_Tab_Settings_Section_Other_Title` and
`#QuickAccess_ReorderControllers_Button`. The wrappers append a WSGM-owned `PanelSection` after
Valve's Performance tree and before the Quick Settings tree. Steam's two FPS-counter rows are hidden
only while WSGM has rows to add. `useMemo` is restored when the last kind is removed.

| Kind                 | Row                                                                                      | Placement      |
| -------------------- | ---------------------------------------------------------------------------------------- | -------------- |
| `valveProfileHeader` | Valve's "Use profile from" header and the per-game toggle                                | Performance    |
| `valveOverlayLevel`  | Valve's overlay-level selector                                                           | Performance    |
| `frameLimit`         | WSGM slider with a "Disable frame limit" switch                                          | Performance    |
| `vrr`                | WSGM toggle labelled by `#QuickAccess_Tab_Perf_EnableVRR`                                | Performance    |
| `valveTdp`           | Valve's TDP toggle and slider                                                            | Performance    |
| `autoTdp`            | WSGM toggle "Automatic TDP"                                                              | Performance    |
| `controllerTarget`   | Valve dropdown labelled by the controller section title                                  | Performance    |
| `valveReset`         | Valve's reset button                                                                     | Performance    |
| `resolution`         | WSGM dropdown "Display resolution"                                                       | Quick Settings |
| `valveRefreshRate`   | Valve's manual refresh row                                                               | Quick Settings |
| `deviceControls`     | charge limit, lighting brightness, zone dropdown, colour preview, hue, saturation, value | Quick Settings |

| Bound              | Value                                                   |
| ------------------ | ------------------------------------------------------- |
| Payload limits     | 8 controller targets, 64 resolutions, 16 lighting zones |
| Value ranges       | 1000 fps, 200 W, 240 characters of text                 |
| Slider drag        | echoed locally until `onChangeComplete`                 |
| Colour edit commit | 350 ms after the last change                            |

`status(kind)` reports `registered`, `hostVersion`, `performanceRootWrapped`, render outcomes and
the last error; the C# verify reads it and logs under `steam.ui.append.<id>`.

## 6. Gate patches on the C# side

The toolkit's `SteamGatePatch` is data-driven: id, resource key, gate name, fingerprint, a probe
expression, a compatibility predicate over the probe JSON, and `verifyOk` / `removeOk` predicates
over the gate's `status()`; each surface class declares its instance as `Patch`. The probe captures
the webpack runtime by pushing an empty chunk and counts factories whose source contains every token
in a conjunction, naming each module literally. Every probe accepts "absent or already ours" through
the markers above.

| Gate           | Verify                                  | Remove              |
| -------------- | --------------------------------------- | ------------------- |
| perf, audio    | `installed && namespacePresent`         | `!namespacePresent` |
| steamOsManager | `installed && getStateOverlaid`         | `!getStateOverlaid` |
| brightness     | `installed && available && setterOwned` | `!available`        |
| bluetooth      | `installed && replaced > 0`             | `!installed`        |
| network        | `installed && available`                | `!available`        |

`SteamUiBridgePatch` probes four token conjunctions that must each match exactly one module (TDP
availability, TDP component, performance actions, read-only profile projection) and never retains a
module id in C#. Each `SteamQuickAccessRowPatch` shares those conjunctions plus five structural
ones, applies `install(kind)`, verifies `registered && hostVersion === 1 && performanceRootWrapped`,
and removes with `remove(kind)`. All eleven share one resource key so they serialize. Every command
payload is read by the surface's module with `SteamUiPayload` before WSGM's backend sees a typed
value.

`Core\SteamInputGlyphStylePatch.cs` targets the main window (8 s, 2 MiB, 2048 bounds), probes the
parsed stylesheets for the two build-coupled classes rather than the DOM, installs one
`<style id="wsgm-handheld-glyphs" class="wsgm-glyph-style">`, and removes only nodes with that
class. The reasoning is in `docs\steam-cef.md`.

## 7. The native Quick Access Menu

Valve's performance, audio, Bluetooth and network surfaces ship in the Windows client and are inert
only because nothing answers behind them. WSGM supplies the answers through the gates above and
mounts its own rows through the component host. The four gates it may open, and the platform
constant it never touches, are in decision D16 and `docs\steam-cef.md`.

### Command flow

A row calls `request(patchId, command, payload)`; the bridge allocates a positive action generation,
the host authorizes the envelope, `SteamUiModuleRuntime` routes it to the module's handler, and the
handler reads the payload with a strict reader (exact object shape, bounded strings, ranges).
Results travel back as a response envelope; every refusal is logged once under
`steam.ui.request.<patch>.<command>`. Correlation ids are
`native-qam:<context>:<document>:<sequence>:<action>` and RTSS commands carry origin `native-qam`.

### State flow

Every semantic service raises `StateChanged`; the host coalesces one publication round, and the
bridge replays the latest state to new subscribers. Polling exists only where Windows offers no
event: brightness every 2 s, network first after 2 s then every 10 s with a 400 ms scan debounce. A
perf delta field equal to the desired value is dropped as an echo
(`Log.Change("native-qam-echo-<Kind>")`), which ended a 4/0 overlay-level ping-pong.

### Degradation

Without a device coordinator the TDP, AutoTDP, device-control and controller-target services publish
an unavailable state and refuse writes with the reason; audio, network, Bluetooth and resolution
modules are not declared at all without their managers. A perf control is hidden by omitting its
field; a component that cannot mount reports why in `renderOutcomes`.

`state received but rejected by validation` is the outcome to look for when a row that used to draw
stops drawing: the host published something the injected half refused, so the control returns null
and the row vanishes with no other symptom. It is what a 12 FPS frame cap under a 30 FPS bookend
produced (Claw, 2026-09-03) before that row learned to stretch its bookends instead of rejecting the
value — see `docs\rtss.md`.

### Performance state

The toolkit's `SteamPerformanceState` mirrors Valve's `CMsgSystemPerfState`: `limits`,
`settings.global`, `settings.per_app`, `current_game_id`, `active_profile_game_id`. Every field is
nullable and omitted when null, which is how a Valve control is hidden without CSS. Display fields
carry an `_external` twin because the Claw's built-in panel reports itself as external.
`Core\NativeQamPerfProjection.cs` is WSGM's policy about what to put in it; it never publishes an
fps limit of zero: `fps_limit` is the desired cap or the lowest option and `is_fps_limit_enabled`
says whether it applies.

| Topic         | Rule                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           |
| ------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 769           | Valve's "no game" is pseudo-app 769, not 0. Both ids default to `"769"`; `active_profile_game_id` equals the AppID only when the per-application profile is enabled; a delta carrying 0, 769 or an out-of-range id is read as "global".                                                                                                                                                                                                                                                                                                                        |
| Overlay level | Valve's enum is Hidden 0, Basic 1, Medium 2, Full 3, Minimal 4 while the selector shows OFF, Minimal, Basic, Medium, Full; `SteamOverlayLevelWire` maps both ways.                                                                                                                                                                                                                                                                                                                                                                                             |
| Deltas        | `UpdateSettings` receives a base64 protobuf, decoded with the message's own `deserializeBinary` and read by `SteamPerformanceDeltaReader`. Recognized: `fps_limit`, `is_fps_limit_enabled`, `perf_overlay_level`, `is_vrr_enabled`, `display_refresh_manual_hz`, `is_game_perf_profile_enabled`, `is_advanced_settings_enabled`, `reset_to_default`; anything else is logged as unbacked. A delta naming another AppID is refused as stale. Fields apply in arrival order, echoes skipped, the first failure collected; `AppliedUnverified` counts as success. |
| Frame limit   | `FrameLimitStrategy` is `FrameLimitOnly` (default; the refresh rate stays the user's), `NativeModes` or `FrameDoubling`. Bookends are the lowest and highest option, else RTSS's caps; the manual refresh row exists only under `FrameLimitOnly`; the switch writes zero or the displayed cap.                                                                                                                                                                                                                                                                 |
| Header        | Driven by Steam's AppID as soon as Steam names a game, only later by the RTSS executable.                                                                                                                                                                                                                                                                                                                                                                                                                                                                      |
| TDP           | Selects `power.primary-limit` once; requires a `PowerSustainedLimit` integer descriptor in watts with `1 ≤ min < max ≤ 200`; executes with `CapabilityCommandOrigin.User` and a 5 s timeout so AutoTDP steps aside. Toggle off releases the limit to the device ceiling.                                                                                                                                                                                                                                                                                       |

Other rows: the controller-target dropdown offers the intersection of the three managed targets with
what the backend can build, is disabled below two options, and tells the user to restart the
application when a game holds the target. Device controls select capabilities by SDK role, refuse an
ambiguous match, and re-resolve descriptors at execution time. Audio maps endpoints through the
audio manager on the UI thread. Bluetooth maps `pair` and `cancelPair` to scanning because pairing
is prompt-driven, accepts `setTrusted` and `setWakeAllowed` as no-ops, and reads adapter state from
the radio manager. The network gate merges the connected access point from
`WindowsRadio.GetWifiStatus` into the store, which is what gives the header Wi-Fi indicator a signal
on Windows.

## 8. Library features

The findings behind each of these are in `docs\steam-cef.md`.

| Feature              | Files                                                           | Mechanism                                                                                                                                                                                                                                                      | Switch                  |
| -------------------- | --------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------- |
| Library tabs         | `Core\SteamLibraryTabs.cs`, `Shell\LibraryTabManager.cs`        | legacy resident in SharedJSContext; wraps `useMemo` through React's dispatcher slot to append fake in-memory collections; inputs `window.__wsgm.tabs`, `tabOrder`, `hiddenTabs`; kill switches `suspendTabs`, `disableTabs`; `PushOrderAsync` debounced 600 ms | `Cef.LibraryTabs`       |
| Card badge           | `Core\SteamPageBridge.cs`                                       | legacy resident in the visible window; signal `focus`, else `hero image`, else the library route; fixed pill with a mutation observer and a 2 s interval, versioned; class `wsgm-badge` under `window.__wsgm`                                                  | `Cef.CardManager`       |
| Collections          | `Core\SteamCollections.cs`                                      | read-only: lists collections, batches filter predicates into one evaluation, counts store tags; one-time cleanup of ids older builds created                                                                                                                   | —                       |
| Downloads            | `Core\SteamDownloads.cs`, `Core\SteamDownloadSort.cs`           | overview is a one-shot `RegisterForDownloadOverview` with immediate unregister (keep-awake, screen-off mute); the sort patch wraps the JSX runtime's `jsx`/`jsxs`, builds buttons from Valve's `Focusable`, renumbers through `SetQueueIndex` every 120 ms     | `Cef.DownloadQueueSort` |
| Launch configuration | `Core\SteamLaunchConfig.cs`, `Core\SteamCustomLaunchCommand.cs` | reads through `RegisterForAppDetails` (3 s timeout, unregister); writes `SetAppLaunchOptions` for titles, `SetShortcutExe` + `SetShortcutLaunchOptions` for shortcuts, verbatim, 400 ms settle; clipboard fallback with CEF off                                | —                       |
| Artwork              | `Core\SteamArtwork.cs`, `Core\SteamGridDb.cs`                   | SteamGridDB over HTTPS, 20 s timeout, bounded downloads; clear, 500 ms, `SetCustomArtworkForApp`; icons refused                                                                                                                                                | `Cef.Artwork`           |
| Libraries            | `Core\SteamCdp.cs`, `Shell\SteamLibraryVdf.cs`                  | `AddInstallFolder` on the running client after purging same-path registrations; removal iterates one snapshot; `libraryfolders.vdf` splice with Steam closed                                                                                                   | `Cef.SdFormat`          |

The tab boot sync waits for the Big Picture window plus `webpackChunksteamui`, `collectionStore` and
`appStore`, retries a failed sync in full, and retries the badge alone thirty times.

## 9. Configuration

| Key                                                                 | Default | Meaning                                                                              |
| ------------------------------------------------------------------- | ------- | ------------------------------------------------------------------------------------ |
| `Cef.Enabled`                                                       | true    | Master switch. Off means the flag is never written and nothing is injected.          |
| `Cef.NativeQuickAccess`                                             | true    | The native QAM surfaces through the session host.                                    |
| `Cef.WifiIndicator`                                                 | true    | The header Wi-Fi indicator through the network gate.                                 |
| `Cef.DownloadQueueSort`                                             | true    | The download sort patch.                                                             |
| `Cef.LibraryTabs`, `Cef.CardManager`, `Cef.SdFormat`, `Cef.Artwork` | true    | Tabs and order; card tabs, badge and relabel; format plus register; artwork changer. |
| `Cef.DownloadKeepAwake`                                             | true    | Wake lock while a download is polled.                                                |
| `SteamAutoRelaunch`                                                 | false   | Relaunch Big Picture 10 s after Steam exits.                                         |
| `SteamLaunchUnelevated`                                             | false   | De-elevated Steam launch through the scheduled task.                                 |
| `SteamGridDbApiKey`                                                 | empty   | Bearer key for artwork search.                                                       |
| `LeftEdgeSteamMenu`, `RightEdgeSteamQuickAccess`                    | true    | Edge swipes send Ctrl+1 and Ctrl+2.                                                  |

Glyph delivery requires `Cef.Enabled`, Device Integration on and a glyph selection other than native
Steam.

## 10. Logging

| Area                               | Keys and lines                                                                                                                                                                                                                                                                              |
| ---------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Gate and lifecycle                 | `steam-ui-transport-gate`, `Steam CEF remote-debugging enabled`, `Steam launch integrity`, `Steam exited.`, `Steam started.`, `Starting Steam Big Picture.`, `Steam UI retraction did not finish before the Big Picture request`, `Steam CEF integration disabled — injected UI retracted.` |
| Toolkit (`Core\WsgmSteamUiLog.cs`) | `steam.ui.discovery`, `steam.ui.patch.<id>`, `steam.ui.bridge.rejected`, `steam.ui.request.<patch>.<command>`, `steam.ui.response.<patch>.<command>`, `steam.ui.publication.<patch>`                                                                                                        |
| Host                               | `steam.ui.glyphs`, `steam.ui.append.<id>`, `steam.ui.append.error.<id>`, `Steam UI patch synchronization failed`                                                                                                                                                                            |
| QAM                                | `native-qam-echo-<Kind>`, `Native QAM performance delta refused`, `Native QAM power limit released to the device ceiling`, `Native QAM audio: …`, `Bluetooth: …`, `Native QAM resolution refused`, `display.backlight`                                                                      |
| Library                            | `Library tabs injected`, `Library tabs (boot)`, `Card badge install failed`, `Steam current app <id> (<signal>)`, `Steam library added to the live client.`                                                                                                                                 |

## 11. Tooling

`tools\WsgmLibTest` attaches to the same debug port and requires Steam started with the flag. It
runs no WSGM code and touches no configuration.

| Script                                                                      | Purpose                                                                                                          | Safety                                                                                                                            |
| --------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------- |
| `run-file.mjs <file.js>`                                                    | evaluate a file in SharedJSContext, 20 s                                                                         | as safe as the file                                                                                                               |
| `run-file-target.mjs <title> <file.js>`                                     | the same for any target                                                                                          | as safe as the file                                                                                                               |
| `qam-harness.mjs status\|install\|publish <json>\|remove\|screenshot [png]` | plays host for the shipped asset: injects, installs six gates and eleven kinds, publishes fixture state, removes | `install` and `publish` mutate the live client and must be followed by `remove`; page requests are acknowledged but not performed |
| `cdp-eval.mjs raw\|add\|remove\|list`                                       | install-folder operations                                                                                        | `add` and `remove` mutate                                                                                                         |
| `run-prod-sort.mjs [enable\|disable]`                                       | the download-sort resident extracted from the C#                                                                 | mutating                                                                                                                          |
| `art-test.mjs`                                                              | SteamGridDB apply                                                                                                | mutating, needs `SGDB_KEY`                                                                                                        |
| `probe-*.js`                                                                | read-only probes by literal module id                                                                            | read-only                                                                                                                         |

The `.mcp.json` server `steam-cef` is `chrome-devtools-mcp` attached to the existing endpoint;
listing targets and bounded read-only evaluation are observation, and `close_page` closes Steam's
real window. Neither the harness nor the MCP relaxes the literal-module rule.

## 12. Verification boundary

Unattended tests cover the transport gate truth table, the session host's patch policy (unverified
patches removed, per-phase budgets, generation cancellation, independent kill switches, unique
structural matches), the bridge vocabulary, request routing, the perf projection and delta reader,
every native QAM service's projection and refusal, the download parser, the library VDF dialect and
the glyph stylesheet. Whether a row renders, whether a Steam update moved a token, and whether a
cold boot still produces a window are device questions answered on the reference Claw against the
running client and recorded in `docs\steam-cef.md`.

## 13. Known gaps

- `tools\WsgmLibTest\tabs-prod.js`, `unpatch.js` and three `probe-*.js` scripts sweep the webpack
  registry calling every module, which the repository rules forbid; `probe-register*.js` target a
  bridge API shape that no longer exists. Read `probe-token-exists.js` for the safe shape.
- The QAM harness acknowledges every page request without performing it, so it cannot validate a
  write path; it proves rendering and publication only.
- Library tabs and the card badge remain legacy resident scripts outside the patch manager until
  their attended migrations land.
- The Extensions tab the toolkit's host was built for is not mounted yet.
