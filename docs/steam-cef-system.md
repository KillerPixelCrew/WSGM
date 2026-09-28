# Steam CEF system

How WSGM drives Steam's Chromium front-end: how Steam is found and launched, when the one CDP
transport may be open, what the session host injects and patches, how the native Quick Access Menu
is rebuilt on Windows, how the library features and WSGM's own pages in Steam are wired, and how it
is configured, logged and tested. It is a mechanism reference. The device findings and the reasoning
behind each rule are in [driving Steam through its CEF front-end](steam-cef.md), and the toolkit's
own contract is in `external\steam-ui-toolkit\docs\reference.md`.

Related:

- [steam-cef.md](steam-cef.md): findings and disproven approaches.
- `external\steam-ui-toolkit\docs\reference.md`: transport, patch lifecycle, bridge, surfaces.
- [boot-and-shell.md](boot-and-shell.md): the desktop/game transition sequence that calls the
  transport gate.
- [power-and-display.md](power-and-display.md): Windows power-profile selection through the native
  Performance dropdown.
- [device-plugin-system.md](device-plugin-system.md) §15: glyph data on the device side.
- [decisions.md](decisions.md): the standing product decisions.

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
 Core\SteamLibraryTabs.cs                       legacy resident script: library tabs
 Shell\LibraryBadges.cs, HomeCarousel.cs        card readings behind the library badge and Home carousel
 toolkit Client\                                one-shot client calls through the session transport:
                                                app details and launch writes, artwork, install
                                                folders, downloads, library data, current game page,
                                                running apps
 Core\SteamCdp.cs, SteamLaunchConfig.cs                  WSGM's policy over generic calls
 Core\Artwork\                                  the artwork feature and its providers
 Core\Library\, Shell\GameLibrary*             the Game Library: sources, planning, choices,
 Shell\SteamLibraryImportSurface.cs             shortcut writing; its Steam page
 Core\Themes\, Shell\ThemeService.cs            the Steam themes: CSSLoader-compatible loader,
 Shell\SteamThemesSurface.cs                    DeckThemes client, installer; its Steam page
 tools\WsgmLibTest\                             live probes and the QAM harness
```

Ownership follows decision D16. The toolkit owns how to find, own and remove a thing safely, and
every revived Valve surface: the gates, the Quick Access rows, the library badge and the Home
carousel, the module ids and localization tokens they name, and the wire shape of each state and
command. WSGM owns the data behind them, its managers, RTSS, the device plugin and the card model,
adapted onto the toolkit's `ISteam*Backend` interfaces, and the policy about which patches are on
when. Its own features, library tabs, download sorting and glyph delivery, stay WSGM's. Reading and
driving the client itself is the toolkit's `Client` layer: app details and launch writes, artwork,
install folders, the download overview, library data, the current game page and the running-app
observer. WSGM keeps the policy on top, which slot, which wrapper, which card's library. A plugin
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
into it. Only that path reconciles the Steam Input shim and writes the CEF flag, because the flag
takes effect on a fresh start. With `SteamLaunchUnelevated` and WSGM elevated, Steam starts through
the de-elevated scheduled task; `Steam launch integrity: …` records which path ran. A warm launch
fires the protocol URL. Readiness is `IsRunning && IsBigPictureVisible`.

## 3. The transport gate

Steam's CEF exposes an unauthenticated, loopback-only debug port. The toolkit verifies that port
8080 is owned by Steam and that the debugger URL is loopback before connecting; the accepted
security posture is in [steam-cef.md](steam-cef.md). WSGM adds a second guard of its own.

### A cold-starting Steam must not be touched before its window exists

**A running Steam process and a reachable `SharedJSContext` are not proof that a cold-start UI is
ready.** Steam opens its CEF port seconds before it has a Big Picture window, and a CEF touch in
that gap can keep the window from ever appearing. On a failed boot the Steam Input proxy had
initialized cleanly, CEF accepted the download-sort injection, and the card monitor was replacing a
library before any window existed; that boot never produced one, and manual Steam starts with the
same proxy did (Claw, 2026-08-22). That trace cleared the proxy; the CEF touch was the difference.

A `SharedJSContext` generation is not a readiness signal either. On a desktop-to-game transition
that cold-started Steam, the patch host applied on the first `GenerationChanged`: download sort and
the running-application probe were on CEF at +2.9 s, and the native-QAM bootstrap plus eighteen more
patches were Applied/Verified by +4 s. No `SDL_app` window ever appeared and Steam had to be ended
from Task Manager (Claw, 2026-09-01). The one cold boot in the same log that succeeded had connected
80 ms after `Big Picture window detected`: the same race, won.

The rule: **the transport is closed whenever game mode has no Big Picture window.** The flag is the
one choke point the patch host, the running-application probe and every one-shot evaluator share, so
nothing WSGM does can reach a cold-starting Steam's port before its window exists.

```text
TransportShouldBeOpen(cefMaster, inGameMode, bigPictureRequestPending, bigPictureReady,
                      bigPictureClosePending)
  = cefMaster && !bigPictureClosePending && ((!inGameMode && !pending) || bigPictureReady)
```

The hold is symmetric. Leaving Big Picture rebuilds Steam's front-end exactly as entering it does,
so `bigPictureClosePending` closes the transport before `steam://close/bigpicture` fires and keeps
it closed until the desktop return settles.

Desktop mode permits discovery on the master switch. In both modes WSGM constructs the toolkit
transport with `requireMainWindow: true`: discovery must find exactly one validated, shaped
MainWindow before it attaches to any target, including SharedJSContext. A login popup is not a
MainWindow. This holds desktop cold starts before the network and login services initialize, using
the existing discovery connection and no JavaScript evaluation to decide readiness.

`ShellSession` runs a one-second gate loop that re-decides on every signal (mode change,
`SteamStarted`, `SteamExited`, master switch) and logs the transition under
`Log.Change("steam-ui-transport-gate", …)`:

| Log line                                                                                                                          | Meaning                     |
| --------------------------------------------------------------------------------------------------------------------------------- | --------------------------- |
| `Steam UI transport open: Big Picture window is up.`                                                                              | healthy game mode           |
| `Steam UI transport open: desktop mode.`                                                                                          | healthy desktop mode        |
| `Steam UI transport closed: game mode without a Big Picture window — holding every automatic CEF touch until Steam's UI exists.`  | Steam cold-starting or gone |
| `Steam UI transport closed: Big Picture was requested — holding every automatic CEF touch until Steam's UI exists.`               | transition in flight        |
| `Steam UI transport closed: Big Picture was asked to close — holding every automatic CEF touch until the desktop return settles.` | desktop return in flight    |
| `Steam UI transport closed: Steam CEF integration is off.`                                                                        | master switch off           |

A healthy cold boot shows `Big Picture window detected` before the first `open:` line and before any
`steam.ui.patch.<id>: Applied`. What the gate governs:

- The gate decision is applied before `SteamUiTransportSession.Attach`, because attaching copies the
  flag and an open transport with a subscriber starts discovery at once.
- Overlay-test mode never attaches a transport.
- Card-volume notification and scanning start immediately so a present card and removals are not
  missed; the live library add/remove, tab and manifest sync, and download-state polling wait for
  the window.
- Desktop download polling and overlay-driven operations stay immediate because they do not act on a
  half-built game-mode session; their shared transport still waits for a MainWindow on cold starts.
- The remote-debugging flag uses the configured `Cef.Enabled` value, not the temporary transport
  hold. A first cold start must write the flag while attachment is still prohibited.

### Retract before either Big Picture mode change

Steam rebuilds its front-end for a Big Picture request and bootstraps against whatever
`SteamClient.System.*` says exists, so namespaces WSGM supplied on the desktop would go unanswered
once the gate closes. `PrepareSteamUiForBigPictureAsync` therefore runs before the request, under a
5 s budget: it marks the request pending, disables the session host (which retracts the library
badge and the Home carousel with every other patch) and the library tabs, and closes the transport.
On timeout it logs
`Steam UI retraction did not finish before the Big Picture request; continuing with the transition.`

When the transition settles, the hold is released and the gate is re-checked. Surface restoration
waits for the retraction to finish, including when it outlives the transition's budget, then
re-applies the current configuration on the UI dispatcher. It explicitly restores the device glyph
profile and absent-control hiding, because disabling the host clears that profile. CEF master-switch
re-enabling also restores it after retraction; neither path relies on another device publication.
The transition sequence itself is in [boot and shell](boot-and-shell.md).

`PrepareSteamUiForDesktopAsync` is the mirror image on the way out, under a shorter 2 s budget
because the user is waiting for their desktop. The desktop return retracts and closes before
`steam://close/bigpicture`, then waits up to 3 s for the Big Picture window to disappear. If it is
still there, WSGM posts `WM_CLOSE` to the window itself, which needs neither the shell protocol
handler nor Steam's main thread, waits another 3 s, and logs the window handle and `IsHungAppWindow`
either way before restoring the display scale and restarting Explorer. The whole exit is shorter
than the 20 s Explorer restore budget it precedes, on purpose.

Both holds release through `ReleaseSteamUiBigPictureHold` when the transition settles. Closing the
gate detaches and disposes the channel's CDP connection, so the hold also retires a socket Steam has
stopped servicing. Without the exit half, patch traffic and the running-application probe kept
driving the front-end Steam was rebuilding, and nothing retired the dead connection. On 2026-09-26
the Big Picture entry patch pass stalled on `steam-ui.bridge`, every later evaluation timed out for
three minutes until Steam's own websocket closed and steamwebhelper restarted, the close request
thirteen seconds later was never consumed, and Explorer came up underneath a Big Picture window
Steam was no longer servicing and never answered a liveness probe.

Mode events: `DesktopModeStarting` clears game mode, cancels the tab boot sync and retracts the
tabs; `GameModeEntered` sets game mode, re-checks the gate and starts the tab boot sync. The header
Wi-Fi indicator, download sort, the library badge and game-page stat, the Home carousel and the
Screensaver settings rows are not mode-bound. They follow their own switches in either mode, because
Big Picture on the desktop draws the same surfaces. Game-mode-only, the indicator left the header
empty until Steam's network page started a scan, and the download queue had no sort buttons, after
every restart next to Explorer (Claw, 2026-09-11). With native Quick Access off, any of these keeps
the bootstrap up on its own.

Steam's screensaver timeouts bound the display timeouts only while the Screensaver settings patch is
enabled and applying, applied or verified. After every synchronization pass and on every
SharedJSContext generation change the host drops the report otherwise, so a client without the
screensaver, such as the Stable client of 2026-09-11, leaves nothing bounding the overlay's rows.
`SteamStarted` and `SteamExited` both request a gate check, so a restart's headless context is never
connected before its own window.

### Master switch

Turning `Cef.Enabled` off stops the card volume monitor and the tab boot sync, then under the gate
disables the host and the tabs and closes the transport
(`Steam CEF integration disabled — injected UI retracted.`). Every evaluation then fails closed,
which is why removal is awaited before the choke point closes. Turning it on re-runs the gate
through readiness rather than opening directly.

## 4. The session host

`Shell\SteamUiSessionHost.cs` owns one `SteamUiBridgeHost` over the embedded bootstrap asset, one
`SteamUiPatchManager`, one `SteamUiModuleSet` and one `SteamUiModuleRuntime`. It registers the
bootstrap patch first and every module's patches after it, starts with everything disabled, and
follows the transport's generation events and every service's `StateChanged`. The shell applies four
switches for native Quick Access, surface observation, the network indicator, download sort and
glyph delivery. Surface observation registers the toolkit's bounded overlay-activation callback
while CEF is enabled, independently of custom QAM rows. Generation changes reinstall it; disabling
CEF removes it. Unknown overlay activation after a reload remains unknown until a fresh event.

OEM Steam QAM and Overlay actions borrow the same transport to replay the toolkit's native button
handler on an exact observed process/app identity and CEF generation. The session's controller
handoff owns physical release and restoration; the toolkit neither changes controller ownership nor
retries a command whose execution is uncertain.

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
| screensaver (only with a session timeout owner)                                | `SteamScreensaverSurface`                           | `DisplayTimeouts`                                   |
| panel-folds                                                                    | `SteamPanelFoldsSurface`                            | `SteamPanelFoldsBackend`                            |
| animations                                                                     | `SteamAnimationsSurface` (WSGM)                     | `AnimationService`                                  |

Publications are enabled while native Quick Access is on; the network publication is also enabled
while the header indicator is on.

### Patch inventory

| Patch id                        | Class                                | Target          | Resource key                         | Enabled by                 |
| ------------------------------- | ------------------------------------ | --------------- | ------------------------------------ | -------------------------- |
| `steam-ui.bridge`               | `SteamUiBridgePatch` (toolkit)       | SharedJSContext | `steam-ui.bridge-binding`            | QAM or network indicator   |
| `steam-ui.performance`          | gate `perf`                          | SharedJSContext | `steam-ui.performance-namespace`     | QAM                        |
| `steam-ui.audio`                | gate `audio`                         | SharedJSContext | `steam-ui.audio-namespace`           | QAM, audio manager present |
| `steam-ui.power-limit`          | row `powerLimit`                     | SharedJSContext | `steam-ui.performance-root`          | QAM                        |
| `steam-ui.brightness`           | gate `brightness`                    | SharedJSContext | `steam-ui.brightness-availability`   | QAM                        |
| `steam-ui.bluetooth`            | gate `bluetooth`                     | SharedJSContext | `steam-ui.bluetooth-manager-service` | QAM, radio manager present |
| `steam-ui.network`              | gate `network`                       | SharedJSContext | `steam-ui.network-availability`      | QAM or network indicator   |
| twelve `steam-ui.*` row patches | `SteamQuickAccessRowPatch` (toolkit) | SharedJSContext | `steam-ui.performance-root`          | QAM                        |
| `wsgm.download-sort`            | `SteamDownloadSortPatch`             | SharedJSContext | `steam-ui.jsx-runtime`               | download sort only         |
| `steam-ui.library-details`      | gate `libraryDetails`                | SharedJSContext | `steam-ui.jsx-runtime`               | card manager               |
| `wsgm.steam-input.glyph-style`  | `SteamInputGlyphStylePatch`          | MainWindow      | `wsgm.steam-input.glyph-style`       | glyph delivery only        |
| `steam-ui.screensaver`          | gate `screensaver`                   | SharedJSContext | `steam-ui.settings-pages`            | CEF master switch          |
| `steam-ui.theme-styles`         | gate `themeStyles` (toolkit)         | SharedJSContext | `steam-ui.theme-styles`              | CEF and `Themes.Enabled`   |

### Switching and synchronization

Disabling is two passes. First the components and glyphs come off with the bootstrap still up, so
removals have a bridge to talk to; then the bootstrap and the global switch. The host polls nothing.
Its loop waits on a coalesced signal, runs the patch manager's synchronization, then publishes state
when the QAM or indicator is on, or lets the global switch follow `downloadSort || glyphs` so an
independent patch keeps the manager alive.

A `SharedJSContext` generation change cancels every in-flight semantic request and releases the RTSS
observation. The RTSS observation is held only while native Quick Access is on, the bridge is ready,
and the frame-limit or overlay-level row is `Verified`: RTSS polling exists for rendered rows, not
for the session. Glyph delivery is enabled only when the setting is on and the presentation carries
resources, controller images or absent controls (`Log.Change("steam.ui.glyphs", …)`).

Disposal runs the disable passes, disposes the runtime first so it stops answering, then the patch
manager, the bridge and the services; the shell detaches and disposes the transport afterwards.

### Screensaver settings

Steam's Big Picture screensaver runs natively on Windows on its own idle timeout, and holds no power
request while it does. WSGM leaves it Steam's and adds two rows, "Turn display off after (on
battery)" and "(plugged in)", to the Screensaver section of Steam's Customization settings through
the toolkit's `SteamScreensaverSurface`. They edit the same active-scheme display timeouts as the
overlay's Power page, through the one session owner `Shell\DisplayTimeouts.cs`. Nothing caches the
values; both surfaces read Windows each time.

The gate reports Steam's `system_idle_screensaver_ac_sec` and `system_idle_screensaver_battery_sec`
and whether Steam believes the machine has a battery, when it first reads them, when they change on
the page and whenever the page opens. `Core\DisplayTimeoutPolicy.cs` bounds each display timeout by
the screensaver timeout Steam pairs with it: plugged-in by plugged-in, battery by battery, and both
by the plugged-in one on a machine Steam believes has none, because that is the only timeout it
shows there. A report finding a display timeout below its bound raises it once to the shortest
preset at or above it (`Display timeout … raised from … to …`); a refused write is logged, not
retried. Steam's rows offer only allowed choices and the backend refuses anything else with the
reason; the overlay's cycle skips forbidden presets and names the bound in the row description. Zero
means never for the display and disabled for the screensaver, so it satisfies every bound and
imposes none. The rows follow the CEF master switch and are not declared in overlay-test.

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
| `npm run steam-assets:check`  | stale file, stale hash, a second `.js` beside the asset, a BOM, invalid UTF-8, > 768 KiB |
| `npm run steam-assets:claims` | the toolkit's ownership scenarios against the shipped bytes                              |

Both run in `eng\verify.ps1` and in CI.

### Anatomy

| Region                 | Origin  | Content                                                                                                                  |
| ---------------------- | ------- | ------------------------------------------------------------------------------------------------------------------------ |
| prelude                | toolkit | reuse check, request/subscribe/deliver/dispose, gate registry, ownership primitives, `transportReply`, `invalidateQuery` |
| `gates\audio.ts`       | toolkit | supplies `SteamClient.System.Audio`                                                                                      |
| `gates\bluetooth.ts`   | toolkit | replaces the Bluetooth service stub's methods                                                                            |
| `gates\brightness.ts`  | toolkit | reveals brightness and claims `SetBrightness`                                                                            |
| `gates\network.ts`     | toolkit | overrides `networkManagementAvailable`, feeds the network store                                                          |
| `gates\performance.ts` | toolkit | supplies `SteamClient.System.Perf`                                                                                       |
| `components.ts`        | toolkit | the React component host that mounts rows into Valve's panels                                                            |
| `epilogue.ts`          | toolkit | `return installResult;`                                                                                                  |

Every gate returns `{install, remove, status}` and registers itself under its name. The C# side
reaches a gate through `window[namespace].gate(name)`; a missing gate reads the same as a missing
bridge.

### Gates

| Gate         | Found by                    | What it does                                                                                                                                                                                                                                                                              | Markers                                                                                                 |
| ------------ | --------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------- |
| `perf`       | perf store fingerprint      | `supplyNamespace(SteamClient.System, "Perf")` with `UpdateSettings(base64)` decoded through the store's own message class and forwarded as `updateSettings {delta}`; state written into `SystemPerfStore.m_msgState`                                                                      | `__steamUiOwnedNamespace`                                                                               |
| `audio`      | audio store fingerprint     | supplies `System.Audio` (`GetDevices`, `SetDefaultDeviceOverride`, `SetDeviceVolume(id, direction, volume)`, no-op app volume, eight `RegisterFor*`); state feeds the running store through `RegisterOrUpdateDevice` and sets `m_bAvailable`; dispatches a volume change only above 0.004 | `__steamUiOwnedNamespace`                                                                               |
| `brightness` | display store fingerprint   | `claimValue` on `is_display_brightness_available`, `claimMember` on `SetBrightness` → `setBrightness {percent}`; state sets the slider                                                                                                                                                    | `__steamUiBrightnessRevealed`, `__steamUiOriginalBrightnessAvailability`, `__steamUiOwnedSetBrightness` |
| `network`    | `window.SystemNetworkStore` | `claimAccessor` on the prototype getter `networkManagementAvailable`; wraps start/stop scanning and always calls through; writes up to 24 synthetic access points (ids 990001+) through `SetDeviceInfo`; removal deletes them and calls `ForceRefresh`                                    | `__steamUiOwnedGetter`, `__steamUiOriginalGetterDescriptor`, `__steamUiOwnedNetworkScan`                |
| `bluetooth`  | service stub fingerprint    | replaces eleven methods on the stub; one synthetic adapter; invalidates `["BluetoothManagerService","State"]`                                                                                                                                                                             | `__steamUiOwnedBluetoothService`, `__steamUiOriginalBluetoothServiceMethod`                             |

### The component host

`components.ts` mounts nothing into the DOM and injects no CSS. It resolves Valve's own primitives
by localization token and source shape (React, the slider, dropdown and toggle fields, panel section
and row, the localizer), then wraps `React.useMemo` so that when the Quick Access tab array passes
through a memo, two panels are replaced by wrappers. The Performance panel is found by export
identity through `#QuickAccess_Tab_Perf_Common_Settings`,
`#QuickAccess_Tab_Perf_BatteryTimeRemaining` and `TS.ON_FRAME`; the Quick Settings panel by source
containing `#QuickAccess_Tab_Settings_Section_Other_Title` and
`#QuickAccess_ReorderControllers_Button`.

The wrappers draw WSGM's rows in the toolkit UI kit's groups after Valve's Performance tree: blocks
with a fill and border under a heading that carries the section's glyph. Quick Settings places
Display before the native controls, then Charging and RGB lighting after them, and wraps Valve's own
sections so they get the same block look. Performance groups profile scope, power profiles,
display/frame rate, power limits, controller and reset; Valve's battery line above them is kept at
one line's height. Every group but Profile scope folds and starts folded, and a folded heading
reports what its rows hold (the chosen profile, the frame cap, the watts, the charge limit). Every
Quick Access fold, the Extensions tab's items and each theme's patches included, goes through the
toolkit's one fold surface: the sections the user opened are sent to `SteamPanelFoldsBackend` and
kept in `quick-access-folds.json`, so an open section outlives Steam rebuilding the tab. A section
whose WSGM rows all draw nothing, such as Power limits and Controller without a device, stays
mounted but out of layout. Steam's two FPS-counter rows are hidden only while WSGM has rows to add.
The wrap is one transform on the toolkit's shared `useMemo` claim, which the Screensaver settings
rows use too; `useMemo` is handed back when the last transform on it is removed. RGB brightness
stays visible; Edit color reveals the zone and HSV sliders only when needed.

Rows and section headers carry a glyph from `icons.ts`, drawn by the toolkit on a 24x24 grid rather
than taken from the client, filled with `currentColor` so it inherits the row's colour. A row passes
it as Field's `icon`, which every Valve field forwards; sliders add `iconLocation: "front"` so the
glyph sits with the label instead of beside the track. A header pairs an 18px glyph with its title,
which `PanelSection` accepts because `title` is rendered as-is. No glyph is used twice: a header
never repeats one from a row inside it, and no two rows share one, because the panel is navigated by
shape before the label is read.

Valve's rows take no props. The overlay-level row is rendered and cloned with an icon, which works
because Valve's slider wrapper spreads unknown props into `SliderField`. The per-game toggle returns
a Fragment, the reset row is a button, and the profile header already draws the game's capsule art,
so those three keep Valve's own appearance. The profile a preset row reports is Valve's `LabelField`
with the scope and status as its description, rather than the unstyled divs it used to be.

| Kind                 | Row                                                                                           | Placement      |
| -------------------- | --------------------------------------------------------------------------------------------- | -------------- |
| `valveProfileHeader` | Valve's "Use profile from" header and the per-game toggle                                     | Performance    |
| `powerPreset`        | AC/battery assignments, including source-specific Custom, and read-only active device profile | Performance    |
| `powerProfile`       | Windows power-plan dropdown                                                                   | Performance    |
| `valveOverlayLevel`  | Valve's overlay-level selector                                                                | Performance    |
| `frameLimit`         | WSGM slider with a "Disable frame limit" switch                                               | Performance    |
| `vrr`                | WSGM toggle labelled by `#QuickAccess_Tab_Perf_EnableVRR`                                     | Performance    |
| `powerLimit`         | Sustained power (PL1) and Boost power (PL2), driven by observed device values                 | Performance    |
| `autoTdp`            | WSGM toggle "Automatic TDP"                                                                   | Performance    |
| `controllerTarget`   | Valve dropdown labelled by the controller section title                                       | Performance    |
| `valveReset`         | Valve's reset button                                                                          | Performance    |
| `resolution`         | WSGM dropdown "Display resolution"                                                            | Quick Settings |
| `valveRefreshRate`   | Valve's manual refresh row                                                                    | Quick Settings |
| `deviceControls`     | charge limit, lighting brightness, zone dropdown, colour preview, hue, saturation, value      | Quick Settings |

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

`SteamUiModuleResolver` owns the module boundary for probes, the injected bridge, library tabs and
download sorting. Its single JavaScript source is embedded for standalone expressions and composed
into the asset. Fingerprints are source-only scans; export resolution requires one match. Literal
lookups reject missing factories before entering webpack, whose failed loads can leave empty exports
cached. Features supply fingerprints and interpret exports, but do not scan and execute the
registry. The network gate reads `window.SystemNetworkStore`, which Steam publishes itself, instead
of loading its module or constructing the singleton. Factory presence alone does not prove
dependency readiness, so these checks supplement the attachment gate.

Nothing names a module id or a minified export name. A gate resolves a module by a source
fingerprint that matches it alone and takes the export by its shape with
`exported(tokens, predicate)`. The audio store is the export carrying `m_bAvailable` and
`RegisterOrUpdateDevice` in the module with `SteamClient.System.Audio`. The perf and display stores
are the exported classes whose `Get()` body declares `m_msgState` or `m_flDisplayBrightness`. The
Bluetooth stub is the object with `GetState` and `Pair` in the module naming
`BluetoothManager.GetState#1`. The query client is the one with `invalidateQueries` in the provider
module carrying `ReactQueryDevtools` and `offlineFirst`. Native QAM, Home and keyboard replay and
the side-menu snapshot read Steam's own `window.SteamUIStore`. The September 2026 beta renumbered
every module and refused each gate that had named one; the record is in
[steam-cef.md](steam-cef.md).

| Gate        | Verify                                  | Remove              |
| ----------- | --------------------------------------- | ------------------- |
| perf, audio | `installed && namespacePresent`         | `!namespacePresent` |
| brightness  | `installed && available && setterOwned` | `!available`        |
| bluetooth   | `installed && replaced > 0`             | `!installed`        |
| network     | `installed && available`                | `!available`        |

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
class. The reasoning is in [steam-cef.md](steam-cef.md).

## 7. The native Quick Access Menu

Valve's performance, audio, Bluetooth and network surfaces ship in the Windows client and are inert
only because nothing answers behind them. WSGM supplies the answers through the gates above and
mounts its own rows through the component host. The four gates it may open, and the platform
constant it never touches, are in [steam-cef.md](steam-cef.md).

### Command flow

A row calls `request(patchId, command, payload)`; the bridge allocates a positive action generation,
the host authorizes the envelope, `SteamUiModuleRuntime` routes it to the module's handler, and the
handler reads the payload with a strict reader (exact object shape, bounded strings, ranges).
Results travel back as a response envelope; every refusal is logged once under
`steam.ui.request.<patch>.<command>`. Correlation ids are
`native-qam:<context>:<document>:<sequence>:<action>` and RTSS commands carry origin `native-qam`.

### State flow

The bridge isolates subscriber exceptions during cached replay and later delivery. A failing module
callback cannot interrupt another subscriber or prevent installation from retaining its cleanup
handle. The power-limit rows subscribe to observed PL1/PL2 state, so profile changes refresh both
sliders. Completed user edits send one explicit command for the selected limit. A failed or
uncertain command is shown without an automatic retry. The old SteamOS Manager overlay, saved TDP
setting watcher and single toggle/slider pair are removed.

Every semantic service raises `StateChanged`; the host coalesces one publication round, and the
bridge replays the latest state to new subscribers. A publication or response larger than 256 KiB is
delivered in parts the page reassembles before it renders, up to 32 MiB. What the document sends the
host stays capped at 16 KiB, and a request past that is refused in the page at once rather than
timing out. Until 2026-09-27 both directions shared the 16 KiB cap, and the Game Library's review, a
page's worth of titles and artwork, was refused without a word to the page, which kept showing
"Scanning…" against the last state it had been given. A delivery still refused, past 32 MiB, is
logged once under `steam.ui.publication.<patch>` and reaches the page as a refusal the page shows. A
publication stamped with a revision is not read or serialized again while the revision is unchanged
and the page already has it.

Polling exists only where Windows offers no event: brightness every 2 s, network first after 2 s
then every 10 s with a 400 ms scan debounce. A perf delta field equal to the desired value is
dropped as an echo (`Log.Change("native-qam-echo-<Kind>")`), which ended a 4/0 overlay-level
ping-pong.

### Quarantined modules

`SteamUiModuleRuntime` raises `ModuleFailed` when a module's publication or command callback throws,
and refuses that module's traffic from then on. The session host retracts that module's patches and
records their ids, so the feature switches cannot mount the surface again on the next Quick Access
enable cycle. The runtime is still refusing to answer for it, and a mounted surface with nothing
behind it is worse than an absent one. The quarantine lasts as long as the runtime does.

### Advanced audio

`SteamAudioFormatRow` mounts two dropdowns in Quick Settings: the playback channel layout and
default format, and spatial sound. The published state carries only the current playback endpoint's
supported formats, the current selections, and a reason when the endpoint exposes neither. The
vocabulary is `setFormat` and `setSpatial`, each with one bounded `target` identifier out of the
list that was published. `NativeQamAudioFormatService` validates every payload against the latest
observed capability before it reaches `AudioProfileService`, which is the same service the Overlay
panel writes through, so there is one Windows implementation behind both. A row is not drawn when
the endpoint offers fewer than two choices for it.

### Degradation

Brightness reads and user writes are serialized by `NativeQamBrightnessService`. The resident
session owns its single poll, so Overlay Tools brightness works with CEF disabled, and both surfaces
share confirmed state. Unavailable readback disables the retained Overlay slider, and projection
changes never dispatch a write. A successful read advances the monotonic revision only when the
confirmed percent changes; a successful write returns its verified readback using the same sequence.
The toolkit separates pending requests from confirmed state and rejects older revisions. It applies
matching readback to Steam's observable even when the user requested that same percent, because
dropping that acknowledgement used to leave Steam's initial 100% value intact. Programmatic
observable changes and their matching setter echoes cannot dispatch hardware writes. Failed or
unreadable writes report a reason without retrying. The service tests and the emitted brightness
fixture cover this without hardware; focused controller/touch and reconnect checks remain attended.

Without a device coordinator the TDP, AutoTDP, device-control and controller-target services publish
an unavailable state and refuse writes with the reason. Audio, network, Bluetooth and resolution
modules are not declared at all without their managers. A perf control is hidden by omitting its
field; a component that cannot mount reports why in `renderOutcomes`. The power-profile and
processor-core dropdowns hide when they publish no options, which is how a processor with one kind
of core shows no core row.

`state received but rejected by validation` is the outcome to look for when a row that used to draw
stops drawing. The host published something the injected half refused, so the control returns null
and the row vanishes with no other symptom. It has caused three separate disappearances of the
frame-limit row, all with that one line as the only evidence: enum fields no state carried after a
simplification (2026-09-02), a 12 FPS cap under a 30 FPS bookend (2026-09-03), and a `deferred`
progress term the vocabulary did not list (2026-09-04).

**A validator's closed vocabulary is a contract, and every host outcome has to be in it.** The
frame-limit row's `progress` list mirrors `PerformanceCommandPhase` one term at a time. `Deferred`
was left out when it was written, so a cap saved against a game Steam had named but Windows had not
exposed took the row down the moment the user touched the slider.
`EveryCommandPhaseProjectsToAProgressTermTheInjectedRowAccepts` enumerates the phases and reads the
vocabulary out of the built asset, because a restated copy would agree with itself while disagreeing
with the script that runs. A validator gaining a field or a term needs the same treatment.

### Performance state

The toolkit's `SteamPerformanceState` mirrors Valve's `CMsgSystemPerfState`: `limits`,
`settings.global`, `settings.per_app`, `current_game_id`, `active_profile_game_id`. Every field is
nullable and omitted when null, which is how a Valve control is hidden without CSS. Display fields
carry an `_external` twin because the Claw's built-in panel reports itself as external.
`Core\NativeQamPerfProjection.cs` is WSGM's policy about what to put in it. It never publishes an
fps limit of zero: `fps_limit` is the desired cap or the lowest option and `is_fps_limit_enabled`
says whether it applies.

| Topic         | Rule                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           |
| ------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 769           | Valve's "no game" is pseudo-app 769, not 0. Both ids default to `"769"`; `active_profile_game_id` equals the AppID only when the per-application profile is enabled; a delta carrying 0, 769 or an out-of-range id is read as "global".                                                                                                                                                                                                                                                                                                                        |
| Overlay level | Valve's enum is Hidden 0, Basic 1, Medium 2, Full 3, Minimal 4 while the selector shows OFF, Minimal, Basic, Medium, Full; `SteamOverlayLevelWire` maps both ways.                                                                                                                                                                                                                                                                                                                                                                                             |
| Deltas        | `UpdateSettings` receives a base64 protobuf, decoded with the message's own `deserializeBinary` and read by `SteamPerformanceDeltaReader`. Recognized: `fps_limit`, `is_fps_limit_enabled`, `perf_overlay_level`, `is_vrr_enabled`, `display_refresh_manual_hz`, `is_game_perf_profile_enabled`, `is_advanced_settings_enabled`, `reset_to_default`; anything else is logged as unbacked. A delta naming another AppID is refused as stale. Fields apply in arrival order, echoes skipped, the first failure collected; `AppliedUnverified` counts as success. |
| Frame limit   | `FrameLimitStrategy` is `FrameLimitOnly` (default; the refresh rate stays the user's), `NativeModes` or `FrameDoubling`. Bookends are the lowest and highest option, else RTSS's caps; the manual refresh row exists only under `FrameLimitOnly`; the switch writes zero or the displayed cap.                                                                                                                                                                                                                                                                 |
| Header        | Driven by Steam's AppID as soon as Steam names a game, only later by the RTSS executable.                                                                                                                                                                                                                                                                                                                                                                                                                                                                      |
| TDP           | Selects sustained and boost descriptors by SDK roles `PowerSustainedLimit` and `PowerSlowLimit`; requires readable/writable integer watts with valid observed state, range and step. Independent PL1/PL2 sliders send user-origin commands with a 5 s timeout through the coordinator. Profile readback updates both; Steam's saved TDP setting is never replayed.                                                                                                                                                                                             |

Other rows: the controller-target dropdown offers the intersection of the three managed targets with
what the backend can build, is disabled below two options, and tells the user to restart the
application when a game holds the target. Device controls select capabilities by SDK role, refuse an
ambiguous match, and re-resolve descriptors at execution time. Audio maps endpoints through the
audio manager on the UI thread. Bluetooth maps `pair` and `cancelPair` to scanning because pairing
is prompt-driven, accepts `setTrusted` and `setWakeAllowed` as no-ops, and reads adapter state from
the radio manager. The network gate merges the connected access point from
`WindowsRadio.GetWifiStatus` into the store, which is what gives the header Wi-Fi indicator a signal
on Windows.

The power-limit surface exposes a Unified TDP toggle using the coordinator's persisted manual mode.
Unified mode shows one TDP slider and both observed limits in its description; split mode shows both
independent sliders. Configuration changes publish mode updates, including selections made in
Overlay. Toggle requests persist policy without applying a wattage.

## 8. Library features

The findings behind each of these are in [steam-cef.md](steam-cef.md).

| Feature              | Files                                                                                                                          | Mechanism                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                     | Switch                                                        |
| -------------------- | ------------------------------------------------------------------------------------------------------------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------- |
| Library tabs         | `Core\SteamLibraryTabs.cs`, `Shell\LibraryTabManager.cs`                                                                       | legacy resident in SharedJSContext; wraps `useMemo` through React's dispatcher slot to append fake in-memory collections; inputs `window.__wsgm.tabs`, `tabOrder`, `hiddenTabs`; kill switches `suspendTabs`, `disableTabs`; `PushOrderAsync` debounced 600 ms                                                                                                                                                                                                                                                                                                                                | `Cef.LibraryTabs`                                             |
| Library badge        | `Shell\LibraryBadges.cs`, toolkit `SteamLibraryBadgeSurface`                                                                   | patch lifecycle; claims the library tile memo and draws the library name beside Valve's Steam Input badge, green installed and grey not; fed from the card reading; reports Big Art Mode as `steam.home.layout`                                                                                                                                                                                                                                                                                                                                                                               | `Cef.CardManager`                                             |
| Home carousel        | `Shell\HomeCarousel.cs`, toolkit `SteamHomeCarouselSurface`                                                                    | patch lifecycle; claims Home's memo and adopts a Home already on screen, replaces the carousel's `games` array with the attached libraries' games and clears its whole-list overscan; excludes games on disconnected cards; reports its counts as `steam.home.carousel`                                                                                                                                                                                                                                                                                                                       | `Cef.ConnectedLibraryCarousel`, `Cef.CarouselShowUninstalled` |
| Current game         | toolkit `SteamCurrentPage`                                                                                                     | one-shot read in the visible window: signal `focus`, else `hero image`, else the library route                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                | —                                                             |
| Library data         | toolkit `SteamLibraryData`, `Core\LibraryFilter.cs`                                                                            | read-only: lists collections, games and store tags; WSGM's compiled filter predicates are batched into one evaluation by `LibraryFilter.EvaluateAsync`                                                                                                                                                                                                                                                                                                                                                                                                                                        | —                                                             |
| Downloads            | toolkit `SteamDownloadActivity`, `Core\SteamDownloadSort.cs`                                                                   | overview is a one-shot `RegisterForDownloadOverview` with immediate unregister (keep-awake, screen-off mute); the sort patch transforms the header on the JSX claim, builds buttons from Valve's `Focusable`, renumbers through `SetQueueIndex` every 120 ms                                                                                                                                                                                                                                                                                                                                  | `Cef.DownloadQueueSort`                                       |
| Launch configuration | `Core\SteamLaunchConfig.cs`, `Core\SteamCustomLaunchCommand.cs`, toolkit `SteamApps`                                           | reads through `RegisterForAppDetails` (3 s timeout, unregister); writes `SetAppLaunchOptions` for titles, `SetShortcutExe` + `SetShortcutLaunchOptions` for shortcuts, verbatim, 400 ms settle; clipboard fallback with CEF off                                                                                                                                                                                                                                                                                                                                                               | —                                                             |
| Artwork              | `Core\Artwork\`, `Shell\SteamArtworkBrowser*`, toolkit `SteamApps`, `SteamPageSurface`                                         | the page searches providers in parallel, the Game Library's automatic match asks them in order; SteamGridDB and Screenscraper.fr over HTTPS, each behind its own gate, bounded downloads, format from the image's bytes; native tabbed Steam route; clear/apply through the running client                                                                                                                                                                                                                                                                                                    | `Cef.Enabled`                                                 |
| Game Library         | `Core\Library\`, `Shell\GameLibrary*`, toolkit `SteamApps`, `SteamLibraryData`, `SteamFilePickerSurface`, `library-capsule.ts` | ten sources, each detected on its own and read together; Xbox packages classified by runtime with one Store catalog lookup per package family, remembered for the session; launcher titles as exact command routes; artwork candidates gathered in the background and picked before saving; the library read in one call per scan or run; shortcuts written through the running client one at a time with a settle, a new id confirmed in the toolkit by a before/after library diff which is the authority and its fields read back, an unconfirmed write stops the run and is never retried | `Cef.Enabled`                                                 |
| Libraries            | `Core\SteamCdp.cs`, `Shell\SteamLibraryVdf.cs`, toolkit `SteamInstallFolders`                                                  | `AddInstallFolder` on the running client after purging same-path registrations; removal iterates one snapshot; WSGM resolves a card's content id to one path first and refuses an ambiguous one; `libraryfolders.vdf` splice with Steam closed                                                                                                                                                                                                                                                                                                                                                | `Cef.SdFormat`                                                |

### Host-owned surfaces

The pages, the game menu and the plugin tab are WSGM's own and exist whether or not any plugin is
installed. Each is gated on CEF itself, not on native Quick Access, and each republishes when its
backend raises `Changed`. The host subscribes to both page backends for that reason: their commands
answer at once and finish in the background.

| Patch id                     | Backend                             | Republished on                               | On config reload         |
| ---------------------------- | ----------------------------------- | -------------------------------------------- | ------------------------ |
| `steam-ui.pages`             | `SteamUiSessionHost.ReadPages`      | the host's page set                          | nothing                  |
| `steam-ui.extensions-tab`    | `SteamExtensionsTabBackend`         | plugin changes, `GameLibraryService.Changed` | nothing                  |
| `steam-ui.game-context-menu` | `SteamGameContextMenuBackend`       | plugin changes                               | nothing                  |
| `steam-ui.power-menu`        | `SteamPowerMenuBackend`             | its `Changed`, when the session mode changes | nothing                  |
| `steam-ui.artwork-browser`   | `SteamArtworkBrowserSource`         | its `Changed`                                | `ConfigurationChanged()` |
| `steam-ui.library-import`    | `GameLibraryService`                | its `Changed`, its artwork stage's `Changed` | reads `AppConfig` live   |
| `steam-ui.file-picker`       | toolkit `SteamFilePickerSurface`    | nothing: commands only                       | nothing                  |
| `steam-ui.wsgm-settings`     | `WsgmSteamSettingsService`          | its `Changed`, plugin changes                | `ConfigurationChanged()` |
| `steam-ui.themes`            | `ThemeService`                      | its `Changed`                                | `ConfigurationChanged()` |
| `steam-ui.theme-styles`      | `ThemeService.ReadStyles`           | its `Changed`, under its own styles revision | `ConfigurationChanged()` |
| `steam-ui.navigation-panel`  | `WsgmSteamSettingsService.ReadMenu` | nothing: one fixed row                       | nothing                  |

The main menu's WSGM row, before Power, is drawn by Valve's own route entry and navigates to
`/wsgm/settings` with Valve's own action; the host is never asked. That page is drawn by the
toolkit's settings renderer with Steam's own Settings components; see "WSGM's settings page in
Steam" below.

Steam's Big Picture power menu gets back its own Switch to Desktop while WSGM is in Game Mode. Valve
draws that entry only under gamescope and answers it with SteamOS's session service, so on Windows
it never appears. The toolkit's power menu gate appends it, with Steam's localized label and the
item and separator types the menu already rendered, as the menu's root passes through the shared JSX
claim; selecting it sends `switchToDesktop`, which runs the same desktop transition as the overlay's
Return to Desktop and is refused outside Game Mode or during another transition. The menu was mapped
from the installed client offline on 2026-09-28.

One route change starts on the host side: the overlay's Game Library hands the user to a page in
Steam through the toolkit's `SteamRouteNavigation`, a single bounded push rather than a request left
in published state.

The Game Library page draws its posters with the toolkit's `createSteamCapsule`, which builds the
element Steam's gamepad library draws, from the library item class map the library badge also finds
(`LibraryItemBox` with `Portrait` or `Landscape`, `PortraitImage`, the shine and the overlay areas),
because Steam's own capsule takes an app overview and cannot draw a title Steam does not have yet.
Its sources are ticked with Steam's `DialogCheckbox`, which lives in its own module, found by
`DialogCheckbox_Container`; the page falls back to Steam's toggle when a client lacks it. The
triggers cycle a card's image through Focusable's `onButtonDown` with Steam's gamepad codes 7 and 8,
through the toolkit's `onSteamTriggers`, which stops a handled trigger from also scrolling the page.
All three were mapped from the installed client offline on 2026-09-27. The capsule and the checkbox
were then seen live in Big Picture on the reference Claw the same evening; the triggers have not had
a live pass. The page itself, like the artwork page and WSGM's settings page, is declared with the
toolkit's `registerSteamPage`, which owns its gate, its state and its frame.

The toolkit's `showSteamFilePicker` is a folder and file picker drawn as a Steam modal, because
Steam has none a page can open. It asks the `steam-ui.file-picker` commands for the drives, the
user's folders and one folder's contents; they list names only, skip hidden and system entries, and
answer only while the host's Steam pages are enabled.

The library badge's surface also puts the library on a game's own page, as a stat after Last Played
and Play Time, drawn from the same card reading. That row is built inside mobx observer classes that
no claim can reach, so the stat is a transform on the toolkit's shared JSX-runtime claim. Download
sort registers its queue-header transform on the same claim, through the bridge's `elements` gate,
instead of wrapping `jsx` and `jsxs` itself, so it needs the bridge and keeps it up on its own.

The tab boot sync waits for the Big Picture window plus `webpackChunksteamui`, `collectionStore` and
`appStore` and retries a failed sync in full. It also replaces the card reading the library badge
and the Home carousel publish from, which the session seeds at start so neither waits for a sync.

### WSGM's settings page in Steam

WSGM has a row of its own in Big Picture's main menu, the left flyout with Home, Library, Store and
Power. It is called WSGM, sits just above Power, and opens WSGM's settings page. The page is laid
out like Steam's own Settings, with a sidebar of pages, and B leaves it the way it leaves Settings.

#### What is on it

A limited set of WSGM's global settings, every one of which configures WSGM itself:

| Page              | Settings                                                                                                                                                                                                                                                                     | Takes effect                                                                                                           |
| ----------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------- |
| Steam integration | the CEF master switch; library tabs, the SD-card library manager, SD formatting, formatting from Steam's storage page, the Home carousel and its uninstalled games; the Wi-Fi indicator, the native Quick Access bridge, keep awake during downloads, download queue sorting | at once, through the shell's config reload                                                                             |
| Startup           | start WSGM at sign-in; start in game or desktop mode                                                                                                                                                                                                                         | at the next start; boot.json is rewritten with the setting                                                             |
| Steam Input       | blocking Steam Input while WSGM's panels are open; Steam Input management, with the shim's state                                                                                                                                                                             | the lease at the next panel; management at once, with the same elevation and pending-update behaviour as WSGM Settings |
| Plugins           | each installed plugin's on/off switch and, while it runs, its declared settings; the device plugin's settings, by section                                                                                                                                                    | as in WSGM Settings and Quick Access                                                                                   |

Turning the CEF master switch off asks first, in Steam's own destructive confirm: it removes this
page and every WSGM feature in Steam, which come back only from WSGM's overlay or WSGM Settings.
Turning the native Quick Access bridge off asks too, but the page stays: WSGM's pages follow CEF
itself, not the bridge. A plugin's secret is never sent to Steam. Its row says whether one is set,
takes a new one, and clears it when emptied. Two instances of one plugin are named by instance, and
device plugin settings come only from the plugin installed now, never a declaration a removed one
left in configuration.

Windows and other external state are not here, as they are not in WSGM Settings; they are on the
overlay and in Quick Access. Artwork credentials and the Game Library defaults stay in WSGM
Settings.

#### How a change is saved

Each change is one field, written through the config store's read-modify-write path, so nothing else
in the file is rewritten. The shell's config reload then applies it exactly as a save from WSGM
Settings would, and the page shows the new value straight away rather than after the reload. The
start settings rewrite boot.json in the same transaction, and Steam Input management reconciles the
shim after the save and outside the lock, through the helper Settings uses.

WSGM Settings saves by writing its whole snapshot back. The fields this page writes are the
exception: a Settings window keeps whatever is saved for any of them it did not change itself, so a
change made in Steam while it was open survives its next save.

#### What is drawn with what

Everything is Steam's own UI, found by fingerprints checked against the live client:

- The menu row is Valve's own route entry, taken from the entries the menu renders. It is active on
  the page, and selecting it navigates with Valve's own action.
- The page is the toolkit's settings renderer: the routed sidebar Steam's Settings is built on, its
  settings sections, toggle, dropdown, slider, text and value fields, small buttons, and the generic
  confirm modal.
- WSGM's mark and the sidebar icons are single-colour glyphs drawn the way Valve draws its own.

The reusable parts are in the toolkit, which documents each fingerprint in its reference: the
navigation panel surface and `settings.ts`. WSGM's side is `WsgmSteamSettingsService`, which decides
what is on the page and saves changes, `SteamWsgmSettingsSurface`, and the thin `wsgm-settings.ts`.

### Steam themes

CSSLoader-compatible themes, browsed from DeckThemes, installed into WSGM's own folder and put into
every Big Picture window. `Core\Themes\` mirrors CSS Loader b1bc683 rule for rule: the manifest and
its refusals (`ThemeManifest`), the tab aliases (`ThemeTargets`), the class translations and the two
selector rewrites (`ThemeClassMappings`), a theme's blocks, patches and components (`ThemeInject`,
`ThemePatch`, `ThemePatchComponent`, `InstalledTheme`), and the loader with the cascade order,
dependency semantics and profiles (`ThemeLoader`). `ThemeStoreClient` asks the same feed with the
same query CSS Loader sends, `ThemeInstaller` unpacks a package over the folder and fetches the
dependencies it lacks, and `ThemePaths` links Steam's `steamui\themes_custom` to the folder so a
theme's images resolve, as CSS Loader links its own.

Where CSS Loader opens a debugger session per Steam window and appends a `<style>` per block, WSGM
publishes the whole cascade through the toolkit's `SteamThemeStyleSurface`; its gate reaches every
window's document from SharedJSContext, through Steam's popup manager and through the React portals
Steam draws the other windows with, and keeps each head in step, including a window Steam opens
later. On Windows the Big Picture window is named `SP BPM_uid<n>` with a localized title and a URL
without CSS Loader's markers, so `ThemeTargets` names Big Picture by that window name beside CSS
Loader's own entries, and the gate tries a title pattern against the window's name too; the evidence
is in [steam-cef.md](steam-cef.md#where-the-windows-are). The cascade follows `Themes.Enabled`: off,
the patch is retracted and every owned node leaves every window.

| Surface                     | What                                                                                                                                                                  |
| --------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Quick Access Extensions tab | a collapsible Themes section: Browse and Manage actions opening the page, Update all, Refresh, the profile, and one switch per theme with its patches nested under it |
| `/wsgm/themes` page         | Browse (the store's cards, one theme's screenshots and Install), Installed (the same rows as the section, with Update, Hide and Delete), Profiles, Settings           |
| Overlay, Tools › Themes     | the same service one level at a time, rows rather than cards, with a hand-over to the page                                                                            |

Which themes are on and what their patches are set to live beside each theme in CSS Loader's own
`config_USER.json`, so a themes folder copied from a Deck keeps its state. WSGM's own settings are
`Themes.Enabled`, `Themes.TranslationsBranch` (auto, stable or beta; auto follows Steam's
`package\beta`) and `Themes.HiddenThemes`, the names kept off the Quick Access section. The
translation table is fetched once per run, retried every minute until it succeeds, and kept in the
folder as `css_translations.json`; the update check asks `/themes/ids` after every load. Every Quick
Access section starts folded, this one and each theme's patches under its switch included; the ones
the user opened are kept in `quick-access-folds.json`.

Starring and submissions need a DeckThemes account and are not offered. The class-name table is the
one part of the feature someone else keeps current: without it a theme still loads and names classes
the client no longer has.

### Boot animation

Boot movies from SteamDeckRepo, the repository Animation Changer browses, kept in WSGM's own library
and played by Steam through its override route. The mechanism is Steam's, not a surface of WSGM's:
the client serves `config\uioverrides` at `/uioverrides/…` and HEAD-requests the override of its
startup movie before falling back to its own copy (the `steamui` bundle's overrideable-resource
hook, live on 2026-09-28). Everywhere but SteamOS the movie it asks for is
`/movies/bigpicture_startup.webm`, which `Core\Animations\AnimationOverrides` records with that
evidence.

Only the boot movie is offered. Steam's suspend movies are overridable the same way, but they play
only in Steam's own suspend flow, and nothing on Windows drives it: the power button delivers one
press edge and no release (#116), so WSGM sleeps Windows directly (#21).

`Core\Animations\` keeps content and choice apart, as Animation Changer does: `AnimationLibrary`
holds the downloads under `downloads\{id}.webm` with their listings in `downloads.json`, and any
`.webm` the user brought under `custom\`; `AnimationsConfig` names one library id, or empty for
Steam's own movie, plus the shuffle; `AnimationOverrides` copies the chosen movie to the file the
client asks for and removes it for Steam's own, leaving alone the copy it left last (the same size
and write time); `AnimationShuffle` picks anew from the library; and `AnimationRepoClient` reads
`/api/posts/all`, keeps the `boot_video` posts and streams `/post/download/{id}` to a staging file
the library then adopts, both bounded, so no movie is held in memory or copied under the service's
lock. A copy rather than Animation Changer's symlink: a symbolic link needs a privilege an ordinary
user lacks, and the movies are a few megabytes.

WSGM removes only an override it wrote. A `.wsgm` marker beside the override records the size and
write time of WSGM's copy, and a file that does not match it belongs to someone else. Choosing a
movie sets such a file aside as `.wsgm-original`, and returning to Steam's own puts it back, so a
movie placed by hand or by another tool is never deleted, not even by the empty default choice at
WSGM's start.

`Shell\AnimationService` is the one owner. It starts before Steam, reads the library, shuffles when
`ShuffleOnStart` is on, and writes the override, so what Steam reads at its start is the choice
already made. A choice made while Steam runs is written at once but shows at the next Steam start,
because the client caches its override lookup for the life of the document; the state, the section
and the overlay say so until Steam next starts, which the Steam monitor reports in desktop mode as
well as in game mode. A change made while Steam is not running needs no restart and says none. The
copy runs outside the service's lock, serialized by its own, so the overlay never waits on it.

Steam's own Startup Movie choice on Settings > Customization (a Points Shop or local movie, or its
shuffle) replaces whatever the override lookup answered. While one of WSGM's movies is chosen, the
service therefore sets that choice aside through the toolkit's `SteamStartupMovie`, which writes
`startup_movie_id`, `startup_movie_local_path` and `startup_movie_shuffle` through the settings
store's own setter, as the Customization page does. It runs once Big Picture is ready, at WSGM's
start, at each Steam start and with each choice, and keeps what Steam held in
`Animations.SteamSetAside`. Choosing Steam's own gives it back, unless the user picked something in
Steam since, which then stays. Steam has already played its startup movie by then, so a choice set
aside shows at the next Steam start like any other change.

SteamDeckRepo lists thousands of boot movies (7,665 on 2026-09-28), and a card for each stalled
Steam's renderer, so the Browse tab publishes and draws them a page of 48 at a time with Load More,
keeping the sorted and searched order until the list, the sort or the search changes. The repository
sends likes and downloads as strings of digits, which are read as numbers.

The service publishes the Browse tab's sorts and the stock choice's name with its state, and the
Quick Access section sends the boot choice back by library id with the names as its labels, so two
movies of the same name stay two choices. The section is one `IExtensionsTabSection`, as the themes
are.

| Surface                     | What                                                                                                                                                                              |
| --------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Quick Access Extensions tab | a Boot animation section: Browse and Library actions opening the page, Shuffle, the boot movie chosen from the library, and shuffle-on-start                                      |
| `/wsgm/animations` page     | Browse (the repository's cards, sorted and searched on the page's behalf, one movie's preview and Download), Library (the choice, the cards, Add a video file, Shuffle), Settings |
| Overlay, Tools › Animations | the same service one level at a time, rows rather than cards, with a hand-over to the page                                                                                        |

### Artwork sources

`Core\Artwork\` owns the complete artwork feature. Its `ArtworkSearch` asks every ready provider at
once rather than falling back in order. Fallback would let a slow or empty primary hide a good
secondary result, and the point of a second source is that one failing does not remove the other's
answers, which only holds if the others were asked. Declaration order then decides ties, so
SteamGridDB still leads.

The picker must keep two states apart, which is why the provider contract carries readiness at all:
a source that was never asked and a source that was asked and had nothing both produce an empty
grid. Every refusal is named on screen, a rate limit, a missing credential, a provider switched off,
so an empty result never silently reads as "this game has no artwork".

The providers differ in exactly the ways that shaped the contract. SteamGridDB takes a free personal
key and can be addressed by Steam app id, and without that key it is not ready. Screenscraper issues
developer credentials per application, and WSGM ships a registered pair, so it is on by default and
readiness is only WSGM's own switch on Settings' Steam page. It indexes emulated systems by ROM, so
a Steam app id means nothing to it and it answers only title searches. Its media vocabulary
(`box-2D`, `wheel`, `fanart`, `screenmarquee`) does not line up one-to-one with Steam's slots, so
that mapping and its world-region-first preference live inside the provider. Its documented quota
failures are distinct: HTTP 429 is the thread or minute quota, 430 the daily scrape quota, 431 a
day's worth of lookups for titles it does not hold.

All three are counted against the account an `ssid` names, or against the requesting IP when there
is none, never against the shipped developer pair, which carries no allowance of its own. A user can
therefore only spend their own budget, which is also why the debug mode offers `forceip`. 431 is the
one that binds in practice: a ROM database asked about a Steam library misses most of the time, and
misses count. Nothing walks the library in the background. The provider is reached only from an
artwork page the user opened, and every quota message names the free personal account that raises
the limit and the thread count.

The pair lives XOR-folded in `Core\Artwork\ScreenscraperCredentials.cs` with its key alongside. That
is obfuscation against string scans, not secrecy, and deliberately not a build secret: a public
installer yields the credentials to anyone who unpacks it either way, so injecting them at build
time would only cost local developer builds the feature. It is WSGM's own application identity
rather than a user setting, and Settings does not offer to replace it. The only Screenscraper
credential a user supplies is the free personal account that raises their quota. The developer debug
password is not shipped at all: it is read from `WSGM_SCREENSCRAPER_DEBUG` and compiled out of
Release.

Applying is provider-independent: one toolkit `SetCustomArtworkForApp` call, whichever source
supplied the bytes. Provider settings, search state, the page renderer, local files, logo placement
and manage/reset behavior all live in `Core\Artwork\` and `Shell\SteamArtworkBrowser*`, on the
host-owned page route.

## 9. Configuration

| Key                                                  | Default | Meaning                                                                     |
| ---------------------------------------------------- | ------- | --------------------------------------------------------------------------- |
| `Cef.Enabled`                                        | true    | Master switch. Off means the flag is never written and nothing is injected. |
| `Cef.NativeQuickAccess`                              | true    | The native QAM surfaces through the session host.                           |
| `Cef.WifiIndicator`                                  | true    | The header Wi-Fi indicator through the network gate.                        |
| `Cef.DownloadQueueSort`                              | true    | The download sort patch.                                                    |
| `Cef.LibraryTabs`, `Cef.CardManager`, `Cef.SdFormat` | true    | Tabs and order; card tabs, badge and relabel; format plus register.         |
| `Cef.ConnectedLibraryCarousel`                       | true    | Home's carousel lists the games on the attached libraries.                  |
| `Cef.CarouselShowUninstalled`                        | false   | That carousel also lists owned games that are not installed, greyed.        |
| `Cef.DownloadKeepAwake`                              | true    | Wake lock while a download is polled.                                       |
| `SteamAutoRelaunch`                                  | false   | Relaunch Big Picture 10 s after Steam exits.                                |
| `SteamLaunchUnelevated`                              | false   | De-elevated Steam launch through the scheduled task.                        |
| `Themes.Enabled`                                     | true    | Enabled themes are installed into Steam's windows.                          |
| `Themes.TranslationsBranch`                          | auto    | Which DeckThemes class-translation table is fetched: auto, stable or beta.  |
| `Themes.HiddenThemes`                                | []      | Theme names kept off the Quick Access Themes section.                       |
| `Animations.Boot`                                    | ""      | The library id Big Picture starts with, empty for Steam's own movie.        |
| `Animations.ShuffleOnStart`                          | false   | The boot movie is picked anew from the library each time WSGM starts.       |
| `Animations.SteamSetAside`                           | null    | Steam's own Startup Movie choice, kept while one of WSGM's movies plays.    |
| `LeftEdgeSteamMenu`, `RightEdgeSteamQuickAccess`     | true    | Edge swipes send Ctrl+1 and Ctrl+2.                                         |

Glyph delivery requires `Cef.Enabled`, Device Integration on and a resolved device profile. Native
Artwork provider credentials live in `AppConfig.Artwork` and are edited on Settings' Steam page, as
does the browser's tab layout: which tabs are offered, their order, and which one opens first.
`ConfigStore.NormalizeArtwork` repairs a stored order that is not a permutation of the tabs that
exist, and falls the default back to the first tab still shown.

## 10. Logging

| Area                               | Keys and lines                                                                                                                                                                                                                                                                              |
| ---------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Gate and lifecycle                 | `steam-ui-transport-gate`, `Steam CEF remote-debugging enabled`, `Steam launch integrity`, `Steam exited.`, `Steam started.`, `Starting Steam Big Picture.`, `Steam UI retraction did not finish before the Big Picture request`, `Steam CEF integration disabled — injected UI retracted.` |
| Toolkit (`Core\WsgmSteamUiLog.cs`) | `steam.ui.discovery`, `steam.ui.patch.<id>`, `steam.ui.bridge.rejected`, `steam.ui.request.<patch>.<command>`, `steam.ui.response.<patch>.<command>`, `steam.ui.publication.<patch>`                                                                                                        |
| Host                               | `steam.ui.glyphs`, `steam.ui.append.<id>`, `steam.ui.append.error.<id>`, `Steam UI patch synchronization failed`                                                                                                                                                                            |
| QAM                                | `native-qam-echo-<Kind>`, `Native QAM performance delta refused`, `Native QAM power limit released to the device ceiling`, `Native QAM audio: …`, `Bluetooth: …`, `Native QAM resolution refused`, `display.backlight`                                                                      |
| Library                            | `Library tabs injected`, `Library tabs (boot)`, `steam.home.layout`, `steam.home.carousel`, `Library badge: initial reading failed`, `Steam current app <id> (<signal>)`, `Steam library added to the live client.`                                                                         |
| Themes                             | `Themes: … themes read from …`, `Themes: <link status>`, `themes.translations`, `theme.inject.<id>`, `Themes: unpacking …`, `Themes: the update check could not reach the store`                                                                                                            |
| Animations                         | `Animations: … in the library, boot …, override written`, `animations.repository`, `Animations: Downloaded …`                                                                                                                                                                               |

The Screensaver settings rows log `steam.screensaver` once per change of Steam's reported timeouts,
and each raise or refused raise of a display timeout on its own line.

## 11. Tooling

`tools\WsgmLibTest` attaches to the same debug port and requires Steam started with the flag. It
runs no WSGM code and touches no configuration. The scripts share target lookup and evaluation
through `cdp.mjs`.

| Script                                                                      | Purpose                                                                                                            | Safety                                                                                                                                                   |
| --------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `run-file.mjs <file.js> [--target <title>] [--section <name>]`              | evaluate a file in SharedJSContext or the named target, 20 s; `--section` runs one section of a merged probe       | as safe as the file or section                                                                                                                           |
| `run-file-target.mjs <title> <file.js> [--section <name>]`                  | the same as `run-file.mjs --target <title>`                                                                        | as safe as the file or section                                                                                                                           |
| `qam-harness.mjs status\|install\|publish <json>\|remove\|screenshot [png]` | plays host for the shipped asset: injects, installs eight gates and eleven kinds, publishes fixture state, removes | attended only; every non-screenshot command adds a runtime binding, `install` and `publish` mutate the client, and `remove` does not remove that binding |
| `cdp-eval.mjs raw\|add\|remove\|list`                                       | install-folder operations                                                                                          | `add` and `remove` mutate                                                                                                                                |
| `run-prod-sort.mjs [enable\|disable]`                                       | the download-sort resident extracted from the C#                                                                   | mutating                                                                                                                                                 |
| `art-test.mjs`                                                              | SteamGridDB apply                                                                                                  | mutating, needs `SGDB_KEY`                                                                                                                               |
| `probe-*.js --section <name>`                                               | historical focused experiments, one script per family; without `--section` a script only lists its sections        | mixed; each header lists read-only and mutating sections apart, and several sections click, change settings, install gates, or call obsolete bridge APIs |

The `.mcp.json` server `steam-cef` is `chrome-devtools-mcp` attached to the existing endpoint.
Listing targets and bounded read-only evaluation are observation, and `close_page` closes Steam's
real window. The raw helpers do not all prove that port 8080 belongs to Steam, and `run-file*.mjs`
does not turn JavaScript `exceptionDetails` into a failing exit code. Verify the listener owner and
loopback websocket target before attaching, inspect output rather than trusting exit zero, and do
not treat `qam-harness.mjs status` as pure observation. Neither the harness nor the MCP relaxes the
fingerprint rule. The `probe-*.js` files still name the module ids of the client they were written
against, which no longer exist on the September 2026 beta.

`node eng\check-steam-fingerprints.mjs [<Steam directory>]` answers whether a Steam update moved a
fingerprint without attaching to Steam. It reads every token conjunction out of the toolkit's
surfaces and gates and WSGM's Core and Shell, parses the installed `steamui` bundle into its module
factories without executing them, prints each conjunction's match count and where it is used, and
exits non-zero when one matches no module or several. A unique match on disk is unique in the live
registry, which holds a subset. It is not part of `eng\verify.ps1`, because its answer depends on
the client installed rather than on the change being verified.

## 12. Verification boundary

Unattended tests cover the transport gate truth table, the session host's patch policy (unverified
patches removed, per-phase budgets, generation cancellation, independent kill switches, unique
structural matches), the bridge vocabulary, request routing, the perf projection and delta reader,
every native QAM service's projection and refusal, the download parser, the library VDF dialect and
the glyph stylesheet. Whether a row renders, whether a Steam update moved a token, and whether a
cold boot still produces a window are device questions answered on the reference Claw against the
running client and recorded in [steam-cef.md](steam-cef.md).

## 13. Known gaps

- `tools\WsgmLibTest\tabs-prod.js` and `unpatch.js` sweep the webpack registry calling every module,
  which the repository rules forbid. The mutating probe sections (`click`, `settings-change`,
  `perf-shim`, `tdp-rpc`, `audio-gate`, `audio-install`, `nightmode-gate`, and `register`,
  `register2` and `subscribe` in `probe-register.js`) change live state or call obsolete bridge
  APIs. Read `probe-perf-components.js` or the `token-exists` section of `probe-register.js` for the
  safe shape.
- The QAM harness acknowledges every page request without performing it, so it cannot validate a
  write path. It proves rendering and publication only, and its `remove` command does not remove the
  runtime binding installed when the harness connected.
- Library tabs remain a legacy resident script outside the patch manager until their attended
  migration lands. The library badge made that move for #28 and the Home carousel was built on it.
- The Extensions tab and selected-game context-menu surfaces are host-rendered. WSGM's own entries
  come first and are not conditional on a plugin existing: the tab carries the "Game Library" row
  that opens the importer, and the game menu carries "Change Artwork…", with admitted common plugins
  appended after, unable to claim a reserved id. The tab draws every row with Steam's own panel
  pieces and fields (`PanelSection`, its rows, `DialogButton`, `ToggleField`, the dropdown and text
  field) and has no fallback drawing of its own. The game menu is intercepted before its first
  render through the shared JSX claim. Both host pages are served on host-owned routes:
  `steam-ui.artwork-browser` and `steam-ui.library-import`. Regression checks cover those seams;
  live visual acceptance remains open. Completing a live 1:1 comparison of the artwork page with
  Decky SteamGridDB remains an acceptance task.
