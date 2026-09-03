# Driving Steam through its CEF front-end

What WSGM has learned by driving the Steam client's Chromium front-end on a live Windows machine:
the rules each feature rests on, the device findings behind them, and the approaches that were tried
and disproven. It covers library registration, custom tabs, the card badge and artwork, launch
configuration, download sorting, glyph delivery and the revived native Quick Access Menu. The
mechanism itself (transport gate, session host, patches, gates, rows, logging, tooling) is in
`docs\steam-cef-system.md`; the toolkit it is built on is in
`external\steam-ui-toolkit\docs\reference.md`.

Related:

- `docs\steam-cef-system.md` — the mechanism, including the transport gate.
- `docs\sd-cards.md` — the card manager and format UI that call into library registration.
- `docs\elevation.md` — the launch wrapper and the non-Steam shortcut rules.

## Steam Input handheld glyphs

### Physical glyphs are CSS, like CSSLoader's Handheld Controller Glyphs theme

The reference theme (victor-borges/handheld-controller-glyphs, run by SDH-CssLoader) already
replaces controller artwork correctly on Decky and CSSLoader-Desktop and covers the MSI Claw. WSGM
copies its mechanism rather than inventing one. Nothing patches Steam's data model; every override
is a stylesheet rule:

- Glyph replacement is `content:` on the image. Valve renders each glyph as
  `<img src="/steaminputglyphs/<name>.svg">`; the rule is
  `img[src="/steaminputglyphs/shared_color_button_a.svg"] { content: url(<asset>); }`. The Valve
  basename is the stable key, and several names map onto one physical control (`ps_button_x`,
  `shared_button_a` and `shared_color_button_a` are all the south face button).
- Inline Valve SVG (the Steam logo in particular) is keyed by the path's `d` attribute:
  `:has(svg path[d="M21.8011 11.5C…"])` hides the inner `svg` and paints the replacement as a
  `background` on the container.
- Capability hiding is `display: none` on the row that carries the absent control's glyph, plus the
  long structural selectors for the configurator and layout screens. Hiding rules sit inside
  `@container style(--hiding-enabled: 1)` against an `@property --hiding-enabled`, which makes
  hiding switchable without a second stylesheet.

The device-specific half of the theme is custom properties only (`--controller-image`,
`--button-guide-image`, `--button-l4-image` and so on); every selector lives in the shared sheet.

### WSGM owns the selectors, the plugin owns the artwork

`Core\SteamGlyphCss.cs` emits the stylesheet and `Core\SteamInputGlyphStylePatch.cs` installs it as
one owned `<style>` element. WSGM owns the Valve resource names, the selectors, the stylesheet shape
and the injection; the device plugin owns the glyphs. Every image in the sheet comes from the active
plugin's imported profile as a hash-checked data URI. A plugin never supplies a selector, a URL,
stylesheet text or a script, and WSGM ships no handheld artwork and no per-device stylesheet.

Injection follows CSSLoader exactly, because that is what makes coexistence work: append a `<style>`
to `document.head` with WSGM's own marker class `wsgm-glyph-style`, remove only nodes with that
class, and never touch a `.css-loader-style` node. A user running both is the normal case.

Two selectors are Steam-generated class names (`SteamGlyphCss.InlineLogoContainerClass` and
`ControlRowClass`) and are coupled to a Steam build, as the reference theme is. The probe checks
both before installing anything; a Steam rebuild that renames them disables glyph delivery and keeps
Valve's rendering.

### Probe the stylesheets, not the DOM

At the moment of the live check the two classes matched zero elements and there were zero
`/steaminputglyphs/` images on screen, because those nodes exist only while a controller settings or
configurator view is open (Claw, 2026-08-29). A probe that looked for live elements would report the
patch incompatible almost always. WSGM's probe reads the parsed rules in `document.styleSheets`
instead, so it gives the same answer whatever the user is looking at.

Verified on the reference Claw against the running client (Chrome/126, 2026-08-29): both
build-coupled classes present in the parsed stylesheets, all rules parsed including both `:has()`
selectors with the full Steam-logo `d` attribute, the controller-image property resolved to its data
URI through `getComputedStyle`, and removal left no owned node and touched no `.css-loader-style`
node. Visual acceptance with a real plugin profile on a controller settings screen is still the
attended item.

## Registering a library with a running Steam

### Add a library through Steam's front-end, never its internals

A library is added to a running Steam by evaluating
`SteamClient.InstallFolder.AddInstallFolder("<path>")` in `SharedJSContext`, so Steam adds,
persists, mounts and scans on its own thread with no restart. Repository-owned one-shot operations
borrow the one session transport through `SteamUiTransportSession`; they cannot discover a target or
open a second socket stack. The port only opens when Steam starts with the
`<SteamDir>\.cef-enable-remote-debugging` flag present, which is written before a cold Big Picture
launch.

Tried and disproven: calling `CApplicationManager::AddLibraryFolder` in-process from the injected
thread. It clears and rebuilds the library array without Steam's lock and destroyed the library list
(dropped D:/E: and persisted the loss to config).

Rules that follow:

- JSON-encode the path into the JavaScript (`JsonEncodedText`). A raw path drops its backslashes and
  Steam rejects it as `NotWritableFolder`.
- Steam enforces one library per drive; `DriveAlreadyHasLibrary` means already present, not an
  error.
- When Steam is closed or the port is unreachable, `SdFormatManager` splices
  `config\libraryfolders.vdf` instead; Steam reads it on its next start.
- Before formatting a card that already carries a library marker, read that marker's `contentid`,
  remove the matching registered or live library first, then erase the disk. Never identify the old
  library by its reused drive letter or path.

### The CEF port: accepted security posture

Steam's CEF port is unauthenticated (a platform limitation) but loopback-only on `127.0.0.1`.
Driving the front-end is the only way to build the live-add, library-tab and artwork features, and
every comparable tool (CSSLoader-Desktop, Millennium, Decky-on-Windows) uses the same flag and port.
The residual is a loopback port any same-user process can drive; that is inherent and rated medium.

WSGM's hardening is against a local squatter: port 8080 is refused unless the listening PID is
`steamwebhelper` or `steam` (native TCP table, loopback listener preferred over a wildcard one), and
the returned `webSocketDebuggerUrl` is rejected unless it is `ws`/`wss` on `127.0.0.1` or
`localhost` port 8080, so a spoofed `/json/list` cannot redirect the client. The checks live in the
toolkit's `SteamCef`.

**The `.cef-enable-remote-debugging` flag is never deleted, on uninstall or anywhere else.** It is
Steam-wide state that CSSLoader-Desktop and Millennium also set and depend on. WSGM writes it only
when absent and cannot know who created it. Tried and reverted: deleting it on uninstall.

### Steam never dedupes same-path registrations

**Steam keys install folders by path and allows several at one path (live Steam client,
2026-08-20).** This is the cause of "the new card shows the previous card's games but the right
capacity". A card pulled from the reader leaves its registration behind: `bIsMounted:false`, still
carrying its own `contentid`, app list and last-seen capacity. `AddInstallFolder` on the same path
does not adopt or replace it; it appends a second entry, and `libraryfolders.vdf` is written with
two blocks at one path. Ejecting does not clear the phantom and `RefreshFolders()` does not dedupe.
Only `RemoveInstallFolder(index)` drops it. A Steam restart rebuilds the list from disk, which is
why the bug appears to fix itself after a reboot.

Two more measured facts. When a registration at the path is mounted, a second add is refused with
`NotWritableFolder` (not `DriveAlreadyHasLibrary`) even though the folder is writable, so that code
means "already registered" here. A registration stays `bIsMounted:true` with `nCapacity:0` when its
folder is deleted while the volume is present, so mounted does not prove a registration is current.

The rules in `Core\SteamCdp.cs` follow from this:

- The add expression purges same-path registrations before adding. `replaceExisting: true` from the
  format flow purges even a mounted one, because a just-formatted card makes every prior
  registration there stale.
- The remove expression removes every match, not the first.
- The relabel expression prefers the mounted match, because a phantom sorts first and `find` would
  relabel it.
- The closed-Steam path calls `SteamLibraryVdf.TryRemovePath` before splicing, because dedup there
  is by content id and cannot see a registration the previous card left under its own id.
- `SteamLibraryVdf.NormalizePath` and `SteamCdp.NormalizePathJs` stay equivalent; a mismatch
  silently skips the purge.

### nFolderIndex is a stable id, not an array position

Removing an install folder does not renumber the ones after it: removing index 2 of `[0,1,2,3]` left
`0,1,3`, `libraryfolders.vdf` persisted the non-contiguous keys, and removing an index that is
already gone is a harmless no-op (live Steam client, 2026-08-23). Steam's own store agrees:
`GetInstallFolder(e){return this.m_InstallFolders.find(t=>t.nFolderIndex==e)}` while array position
is exposed separately through `findIndex`.

The purge and remove loops therefore iterate one `GetInstallFolders()` snapshot and remove each
match in order. Tried and disproven: a descending sort or a re-fetch between removals to guard
against index shift. There is no shift.

### Card swaps are reconciled on the volume notification, not by polling

`Shell\CardVolumeMonitor.cs` subscribes with `RegisterDeviceNotification` to
`GUID_DEVINTERFACE_VOLUME` on the process message-only window. Two alternatives are wrong: the
broadcast `DBT_DEVTYP_VOLUME` message goes only to top-level windows, so a `HWND_MESSAGE` window
never receives it; WMI (`Win32_DiskDrive` plus a model string) matches one reader only and gives no
volume-arrival identity.

The notification arrives before the volume is mounted and lettered, so the reaction settles 3 s and
rescans all drives rather than resolving the reported device path. The decision compares the card's
own `contentid` (the identity that travels with the card) against the ids registered for that path
in `libraryfolders.vdf`; Steam's live folder API exposes no content id, so the file is the source.
Reconciliation is gated on the CEF master switch and off in `--overlay-test`.

The monitor must start for both ways game mode becomes active: the initial boot and a later
desktop-to-game transition. Initial boot does not raise `SessionModes.GameModeEntered`, and relying
on that event alone left the monitor absent for a whole boot session (Claw, 2026-08-22: Safe Eject
succeeded with no card-volume notification or reconcile).

Notification and scanning start immediately so a present card and removals are not missed, but the
live add and remove are deferred until Steam's Big Picture window exists. A running Steam process
and a reachable `SharedJSContext` are not proof that a cold-starting client may be touched; see the
transport gate in `docs\steam-cef-system.md`. CEF unreachability saves the desired configuration and
fails open with a retryable warning; it never replaces the last successfully injected definitions.

## Custom library tabs

### Custom tabs are injected into the tab strip, not collections

Collections render under the "Collections" tab and never as top-strip tabs; that model was wrong and
is removed. `SteamCollections` survives only as the read/filter bridge and a one-time cleanup for
collection ids created by older builds. New tabs never create collections.

`Core\SteamLibraryTabs.cs` injects a resident script into `SharedJSContext` that replicates
TabMaster without Decky. It pushes a chunk to `window.webpackChunksteamui` to capture
`__webpack_require__`, finds React by loading candidates through `req(id)`, then installs a getter
on React's current dispatcher slot
(`__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE.H`) so every `useMemo` result
passes through `patchTabs`. That rewrites the library tab array (found by a tab with
`id==='AllGames'`) to append WSGM's tabs as fake in-memory collections rendered by Steam's own grid
(found by the `Library_FilteredByHeader` source marker).

**The captured require's `req.c` cache is empty (live Steam client), so React can only be found by
loading candidates through `req(id)`.** Tried and disproven: a cache-only exports scan. A review
once made that "safer" swap and broke all tab injection until the next device test.

WSGM supplies only `window.__wsgm.tabs = [{id,title,appids}]`, `tabOrder` (the full strip order as
tab keys, native ids like `AllGames` mixed with `wsgm-…` ids; unlisted tabs keep natural order after
the listed ones) and `hiddenTabs` (native ids to omit; hiding is omission from the returned array,
and the tab reappears untouched when unhidden). `patchTabs` records `W.nativeTabs`, persisted as
`AppConfig.KnownNativeTabs` so the order UI shows real localized titles. App ids come from
`Core\LibraryFilter.cs`, a persisted `FilterNode` tree compiled to a pure JavaScript predicate over
`appStore`; keep it Steam-free and unit-tested. Card and genre tabs use the same injection.

Sync is reactive: `LibraryTabManager.SyncAllAsync` re-injects after every builder change, and
reordering uses the cheap `PushOrderAsync` (order and hidden set only). The boot sync waits for Big
Picture plus `webpackChunksteamui`, `collectionStore` and `appStore`. A reachable but failed filter
evaluation retries the full tab sync even if the independent badge push succeeded; treating the
badge success as completion was why custom tabs only appeared after opening WSGM's sidebar (Claw,
2026-08-22).

The accepted fragility is the two things that move on a major Steam UI update: the dispatcher slot
name and the `Library_FilteredByHeader` marker. Kill switch: `window.__wsgm.disableTabs()`; a Steam
restart also recovers. Prototype any change against live Steam with
`tools\WsgmLibTest\run-file.mjs tabs-prod.js` before editing the C#.

## The Steam-page bridge on the visible window

### The card badge runs in the visible window

`SharedJSContext` is headless: empty DOM, no images, only stores and React.
`Core\SteamPageBridge.cs` therefore injects the "On: <card>" badge into the visible Big Picture
window through `EvaluateOnVisibleWindowAsync`. The window is selected by shape, not localized title:
a `page` whose URL has `createflags` and lacks `openerid` and `browserviewpopup`.

The current game is the appid of the largest wide visible `assets/<appid>/...` image, the hero
banner, matched by `width>=600 && width>height`. That skips the portrait grid capsules and clears
the badge when leaving a game, and it holds across art naming: some games serve `library_hero`,
others a hashed `assets/<id>/<hash>`, and both put the appid in the path. Matching the
`library_hero` filename alone fails for many games.

`CurrentAppIdJs` resolves to `{id,src}`, one source string shared by the C# reader and the resident
badge so their rules cannot drift. `src` names the signal that matched: `focus` (the focused
element's React fiber, tried first) or `hero image`. The log prints the signal, so a detection that
shifts from one signal to the other is visible in a pasted `wsgm.log`. Bump `BadgeScriptVersion`
whenever the resident script text changes, and re-probe both branches against a live Steam
(`tools\WsgmLibTest`) before shipping.

### Artwork: data on SharedJSContext, DOM on the visible window

Artwork apply uses `SteamClient.Apps.ClearCustomArtworkForApp` then
`SetCustomArtworkForApp(appid, base64, ext, assetType)` on `SharedJSContext` (grid=0, hero=1,
logo=2, wide=3, icon=4), with about 500 ms between clear and set. Icons alone need filesystem writes
and are refused.

### Header Wi-Fi indicator

Big Picture's header Wi-Fi icon is empty on Windows because Steam's backend sends device reports
with an empty `wireless.aps` list, so `SystemNetworkStore` never sees a connected access point. WSGM
injects a synthetic access point (real SSID and signal from `WindowsRadio.GetWifiStatus`) through
the store's own `SetDeviceInfo` ingestion (plain protobuf-toObject shape; `estate` 5 = connected,
`estrength` 0-4 = filled arcs).

Tried and disproven: wrapping `OnNetworkDevicesChanged`. The backend holds the bound callback
registered at init, so a property wrap never fires. Instead the synthetic instance gets a no-op
`MarkAsNotPresent`, which pins it across the backend's periodic reports. Removal deletes the map
entry and calls `SteamClient.System.Network.ForceRefresh()`. The indicator is owned by the toolkit's
network gate today.

### CSSLoader-Desktop coexistence

Steam's CEF allows concurrent CDP clients, and CSSLoader only appends and removes `<style>` nodes in
`document.head`. WSGM namespaces everything under `window.__wsgm`, gives injected nodes a unique
class (`wsgm-badge`, `wsgm-glyph-style`; never `css-loader-style`, which CSSLoader bulk-removes),
never clears `document.head`, and never disables the debug flag or port.

## Writing a game's launch configuration

### Launch options are written through Steam's own API, verbatim

The Tools tab's per-game launch fixes (`Core\SteamLaunchConfig.cs`) configure the running client
over `SharedJSContext` instead of handing the user a command to paste; with `Cef.Enabled` off they
fall back to the clipboard. A real title takes `SteamClient.Apps.SetAppLaunchOptions(appid, str)`; a
non-Steam shortcut takes `SetShortcutExe` plus `SetShortcutLaunchOptions`, because a shortcut
ignores an exe-replacement launch option (see `docs\elevation.md`).

Steam stores every value verbatim: no quotes added or stripped, backslashes untouched. Its own
shortcut `Exe` is stored quoted with single backslashes (`"C:\Games\…\game.exe"`), so WSGM supplies
the quotes itself. Never use Decky's `JSON.stringify(path)` form; it doubles backslashes and is only
correct on Linux.

Reads go through `RegisterForAppDetails` wrapped in a promise with a timeout and `unregister()` on
both paths; it is a subscription, not a getter, and re-fires after a write. `GetLaunchOptionsForApp`
is the launch-menu list, not the options string. Writes persist to `shortcuts.vdf` and
`localconfig.vdf` immediately with no Steam restart; those files are never hand-written. `StartDir`
is never written, so the game's folder stays the working directory.

A real title's existing launch options are composed, never replaced, because `%command%` expands to
the game's own command and overwritten options would silently stop applying. Plain options move
after the placeholder; a user value that positions `%command%` itself keeps its prefix and suffix;
re-applying reads them back through `LaunchWrapperCommand.OriginalLaunchOptions`. `%command%` is for
real titles only.

Configuring a shortcut destroys its original Target, so the pre-change values are snapshotted into
`AppConfig.LaunchWrappers` before the write. `SteamLaunchConfig.OriginalsFrom` unwraps an
already-wrapped game (the command may have been pasted by hand or the config reset) so the snapshot
never records WSGM's own wrapper as the original, and re-applying keeps the first snapshot.

### A user prefix ahead of %command% is preserved

A prefix before `%command%` runs at Steam's own integrity level, in front of the wrapper. That is
accepted: it runs there whether or not WSGM applies a fix, and applying one strictly reduces the
elevated surface by moving the game itself to medium. The prefix is never stripped, reordered,
escaped or refused; doing so breaks `-dx11`, `-nolauncher` and profiler/RTSS shims. It is only
reported: `LaunchWrapperCommand.PreservedPrefix` strips control characters and caps the length for
one `Log.Info` before the write, because a launch option is user text and `Log` interpolates raw.
The string handed to `SetAppLaunchOptions` is byte-identical either way.

### The custom launch action uses Steam-native syntax

The Tools tab's custom launch action uses no WSGM wrapper and replaces the active launch fields. A
real title gets `"selected.exe" [arguments] %command%`; CMD/BAT and PS1 selections prefix the
placeholder with an explicit `cmd.exe` or Windows PowerShell invocation. A non-Steam shortcut gets
the selected EXE (or script host) in `Exe` and only the script plus custom arguments in Launch
Arguments; `%command%` is never written there. The first pre-change snapshot is retained across
edits so Restore returns every field verbatim.

## Download-queue sorting

Name/Size/Type buttons injected into the header of Big Picture's "Up Next" download section reorder
the queue through `SteamClient.Downloads.SetQueueIndex(appid, index, remoteClientId)`
(`Core\SteamDownloadSort.cs`, live Steam client, 2026-08-12). Three findings were each a real
failure first.

### The buttons must be built from Steam's own Focusable component

A plain DOM injection renders and clicks fine but is invisible to Big Picture's gamepad focus tree
("not navigable with controller"). With `Focusable` the controller reaches them and the footer shows
the select hint.

### The injection point is the JSX runtime, not the component

The section header rebuilds its own `children` array after spreading rest props, so it can only be
wrapped. The download-list section is a MobX observer whose `render` is a non-configurable,
non-writable own property on every instance, so it cannot be patched, deleted or shadowed by a
prototype accessor. What is left is wrapping `jsx`/`jsxs` and intercepting the header element at
creation; the hot-path cost is one reference comparison. Some runtime modules re-export the same
binding, so a wrapper is skipped when it already carries the guard property; wrapping a wrapper
renders the bar twice.

### The Focusable lookup must stay tight

Matching "flow-children" plus "onActivate" also hits three chat/friends class components, and the
registry hands a text-area component back first, which rendered a textbox into the download header.
The lookup requires a plain function under 1500 characters that destructures the quoted
`"flow-children"` key together with `onActivate:`, `focusClassName` and `focusWithinClassName`; that
leaves exactly one match. Webpack's ES exports are accessor properties, so a value-only scan
(`getOwnPropertyDescriptor(...).value`) finds neither React nor `Focusable`.

### A sort re-queues the whole pending list

Scope is `QueuedTransfers` + `UnqueuedTransfers` + `ScheduledTransfers`, minus completed, renumbered
from index 0. Index 0 is included: the item Steam is working on is part of the queue, excluding it
made a sort look broken, and moving another app to index 0 only switches which one Steam works on
while per-app progress is retained.

Including the scheduled entries queues them (their `queue_index` is -1 until a sort assigns one),
exactly as dragging them into the queue does in Steam's own UI, so a sort empties the "Scheduled"
section. That is the point: when Wi-Fi drops mid-download Steam kicks the whole queue out to
unqueued/scheduled, and one tap on a sort button is how fifty entries go back in. Do not sort each
section separately or preserve `deferred_time`. Never seed the renumbering from
`items[0].queue_index`, which can be -1. The apply loop is one `SetQueueIndex` per item at 120 ms,
so a fifty-entry re-queue takes about 6 s with the buttons dimmed; the list is not capped.

Size means bytes left to download (`bytes_total - bytes_in_progress`). A freshly restarted client
reports `bytes_total == 0` for queued-but-not-yet-planned apps; that is unknown, not smallest, and
ranking it as zero was the "only works on the second tap" bug. Unknown sizes are parked at the end
in both directions, which is why each comparator takes the direction as an argument. The displayed
size is Steam's own formula, the sum of `progress[k_EAppUpdateProgress_Download].bytes_total` across
every content type; a max over the progress array does not match the rows. `buildid == 0` is
Install, otherwise Update. The queued section is identified by the locale-independent
`#Downloads_Section_Current` token plus a `count`+`labelId` shape check.

A sort resumes a paused queue (paused → `Downloading`, even when the order is unchanged) because
Steam reacts to a `SetQueueIndex` at the head. That is what dragging an item to the top does in
Steam's own UI and is accepted; WSGM never calls `EnableAllDownloads` and does not re-pause.
Re-probe with `tools\WsgmLibTest\run-prod-sort.mjs`, which extracts the script verbatim from the C#,
before shipping a change.

## Retract, don't just stop

Turning a CEF feature off must remove what it injected. Tabs, the badge, the synthetic access point
and the sort buttons are resident in Steam's session and survive until Steam restarts. The master
switch fails every evaluation closed, including WSGM's own removal calls, so `ShellSession` awaits
removal before closing the choke point. Wi-Fi and download sorting live in the patch lifecycle and
are retracted by it; the remaining legacy residents, tabs and the badge, keep explicit removal until
their attended migrations land.

## Persistent host and native Quick Access

WSGM owns exactly one CEF transport, the toolkit's `PersistentSteamUiTransport`, attached through
`SteamUiTransportSession` so one-shot callers and a settings reload share the same choke point.
`SteamCef` keeps only the remote-debugging opt-in and pure endpoint/JavaScript validation.
`Shell\SteamUiSessionHost.cs` is the one owner of state publication and command dispatch: its
projections are a publication table and its `(patchId, command)` dispatch is a handler table, which
keeps every refusal on one diagnostic path. `SteamUiPatchManager` is the only patch scheduler; one
incompatible patch does not disable another, and a `SharedJSContext` generation change cancels
commands authorized against the replaced document.

The bootstrap patch fingerprints four literal modules (TDP availability gate, TDP component,
performance actions, read-only profile projection), each found exactly once (live Steam client,
2026-08-28). Module build ids are not selectors. It installs only a versioned Runtime binding and
namespace; it never spoofs SteamOS or Steam Deck identity or touches unrelated gates.

Injected code can request only the compiled patch/command vocabulary. The bridge validates schema,
patch id, command, payload size, monotonic request and action generations, current execution context
and document, and replay. There is no generic evaluation, filesystem, shell, device or plugin
endpoint. `Cef.NativeQuickAccess` is an independent kill switch under the CEF master.

### Valve's "no game" is 769, never 0

The header, the per-game toggle's availability and the app-name lookup compare game ids against the
client's own pseudo-app 769 (live Steam client, 2026-09-02): `active_profile_game_id == 769` is the
"Default settings" branch, anything else renders "Use profile from <name>". Publishing 0 made the
header take the game branch and look up game id 0, a blank name while a game ran. The projection
publishes 769 wherever it used to say 0, and a delta carrying 769 is read as "global". The toggle
`#QuickAccess_Tab_Perf_ToggleGameSettings` is a separate export from the header on the current
client and is mounted as its own row.

### The per-application header is driven by Steam's AppID, not RTSS discovery

A live screenshot showed Valve's complete Performance tab with a blank "Use profile from" header
while WSGM had already observed AppID 220 (Claw, 2026-08-31): the projection had discarded Steam's
identity-only state because no executable profile existed yet. `PerformanceService` keeps the AppID
separately from its optional RTSS profile. `current_game_id` carries the AppID as soon as Steam
names a game; `active_profile_game_id` matches it only when that game's profile is enabled;
foreground observation later supplies the executable without changing the AppID. A delta naming a
different AppID is refused as stale.

### Controller-target ids keep their PascalCase

Controller-target ids are the projected `ManagedControllerTarget` names (`SteamDeckComposite`,
`Xbox360`, `DualShock4`) from state through Valve's dropdown and back. A lowercase-only payload
reader let the row render and the dropdown select normally but rejected every valid command; only
the live log exposed it. The reader accepts ASCII uppercase while keeping its length, character and
exact-shape checks.

### Device lighting uses Valve's primitives, not Valve's LED wrapper

Charge limit and lighting are WSGM-owned rows selected by SDK semantic role, not by a Claw package
id. Literal module `30519` holds Steam's generic HSV implementation closed over but not exported;
its exported controller-LED wrapper calls `SteamClient.Input.PreviewControllerLEDColor`, a Steam
Input side effect unrelated to the plugin capability (live Steam client, 2026-08-31). WSGM builds
the same HSV interaction from the resolved Valve slider, dropdown, row and localization primitives.
Hue, saturation and value stay local while dragging; a plugin write is requested only from
`onChangeComplete`. The overlay follows the same rule with one explicit Apply.

### Verified independence and the harness

The frame-limit and RTSS own-statistics components register and replay retained state independently,
emit only their exact request payload, survive removal of their peer, and restore React's original
`useMemo` only after the last component is removed (live Steam client, 2026-08-28).
`tools\WsgmLibTest\qam-harness.mjs` plays host for the shipped asset without running WSGM; it
reconstructs the bridge vocabulary from the session host's declarations, because after the
repository split the harness once rendered a fixture the installed bridge would have rejected. A
managed test now inspects the emitted bridge configuration. The `screenshot` command captures the
main window through `Page.captureScreenshot` and does not operate the client.

The card badge and library tabs stay on their verified legacy resident scripts. A read-only probe
can show their primitives exist but cannot prove resident installation, SPA survival, current-game
clearing, CSSLoader coexistence, native-tab hiding or rollback; moving them without those attended
checks trades device-verified behavior for a source cleanup.

## Valve's surfaces are present on Windows; only their backends are absent

**The QAM, Quick Settings, Internet, Bluetooth and audio components are not gated off; they are
wired to nothing (live Steam client, 2026-08-30).** The performance store is the clearest case.
`window.SystemPerfStore` is one MobX observable whose entire state is `m_msgState`, and its
constructor reads:

```js
(SteamClient.System.Perf?.RegisterForDiagnosticInfoChanges(this.OnDiagnosticInfoChanged),
  SteamClient.System.Perf?.RegisterForStateChanges(this.OnStateChanged));
```

`SteamClient.System` has no `Perf` namespace on Windows, so the optional chaining no-ops, the state
stays `{}`, and every control renders `null`. Writes are the same: each setter builds a protobuf
delta and hands it to `SteamClient.System.Perf?.UpdateSettings(...)`.

Availability is read from that same state, so hiding is free: the VRR hook is
`[limits?.is_vrr_supported ?? false, per_app?.is_vrr_enabled ?? false, SetVRREnabled]`, and omitting
a `limits` field makes Valve's wrapper render nothing. Two layers are still needed, because some
hooks hardcode `available: true` (both scaling ones do); the first layer is not mounting a component
at all.

### Filling a store is not enough: every revived item has a second gate

Supplying a backend satisfies the data gate, not the render gate above it, and the two are
independent. This caught the audio work.

- The store may cache availability at construction. The audio store computes
  `m_bAvailable = null != SteamClient.System.Audio` once, in its constructor, which ran at client
  start when the namespace did not exist. With the namespace installed afterwards the singleton
  still reported `bAvailable: false` (live Steam client, 2026-08-30). The running store is written
  directly: `m_bAvailable` is writable and `RegisterOrUpdateDevice` is its own ingestion path.
- A component may sit behind a platform constant no data can reach: night mode is `IN_GAMESCOPE`,
  several performance rows are behind a gamescope feature gate, the Quick Settings audio section is
  `!IN_VR && bAvailable`. Such a row is a hide, not a backend.
- A wrapper may gate on `available` passed as a prop from the state WSGM supplies. That one is
  reachable, and is why omitting a field hides a row for free.

Each item needs three answers before it is done: what supplies its data, whether the store caches
the availability derived from it, and whether anything above it gates on a platform constant.

### A platform-constant gate is sometimes final

Where the constant is read through a store getter (`networkManagementAvailable` returning
`TS.IS_STEAMOS`), the getter is on a prototype, configurable, and can be overridden narrowly. Where
it is read through a module export it may not be: night mode's support hook is
`function(){return TS.IN_GAMESCOPE}` and its export descriptor is non-configurable (live Steam
client, 2026-08-30). Then the only route is the global constant, which decision D16 forbids, and the
feature has to be a WSGM-owned control rather than Valve's.

### Four gates, and the one that is never touched

| Gate                     | Example                                   | Response                   |
| ------------------------ | ----------------------------------------- | -------------------------- |
| Absent JS namespace      | `SteamClient.System.Perf`, `System.Audio` | Supply it                  |
| Absent RPC response      | `SteamOSService/State/Manager`            | Supply it                  |
| RPC stub with no backend | `BluetoothManagerService`                 | Replace the stub's methods |
| Deck-only store getter   | `networkManagementAvailable`              | Override that one getter   |
| Global platform constant | `TS.IS_STEAMOS`                           | Never (the D16 spoof)      |

`networkManagementAvailable` is literally `get networkManagementAvailable(){return TS.IS_STEAMOS}`,
so overriding the getter and setting the constant produce the same Wi-Fi row; one touches a single
store and the other changes unrelated client behaviour everywhere.

### Three performance backend families, one reachable

- The perf store (`CMsgSystemPerfState`, modules `74514`/`83571`): per-app profiles,
  `fps_limit_options`, frame limit, overlay level, refresh, VRR, basic/advanced, reset. Backed by
  the absent `SteamClient.System.Perf`. This is the reachable one. Its message shape is
  `{limits, settings:{global, per_app}, current_game_id, active_profile_game_id}`; a per-game
  profile is `current_game_id == active_profile_game_id` plus
  `per_app.is_game_perf_profile_enabled`, with 769 as "no game".
- The SteamOS Manager family (`steamos_tdp_limit*`, `steamos_manual_gpu_clock*`): client settings
  whose availability comes from a WebUI transport RPC. TDP and charge limit live here; there is no
  TDP component in the perf store.
- The gamescope family (`gamescope_app_target_framerate` and friends): behind a gamescope feature
  gate and not reachable on Windows.

### Bluetooth facts

`BluetoothManagerService` is its own service and does not share the SteamOS Manager seam. Its
`GetState` round-trips on Windows and returns
`{is_service_available:false, adapters:[], devices:[]}`: transport and message shapes present,
backend missing. Its `*Handler` exports are message descriptors (`{name, request, response}`), not
registration hooks, so the stub's methods are replaced instead.

The full Bluetooth settings page ships in the Windows client and opens (screenshot-confirmed, live
Steam client, 2026-08-30). Three facts not to re-derive wrongly; two earlier theories failed because
they tested invented token shapes:

- The page's strings are the `#QuickAccess_Tab_Bluetooth_*` family in module `18931`; the settings
  page reuses the QAM panel. There is no `#Settings_Bluetooth_Title`, and no sidebar token is shaped
  `#Settings_X_Title`, so absence of such a token proves nothing.
- The nav gate is `is_service_available` read through
  `useQuery({queryKey:["BluetoothManagerService","State"], staleTime: 1/0})` (module `25467`; the
  query client is export `L` of `21371`). Replacing the stub changes nothing until that key is
  invalidated.
- The chain that opens it (replace stub methods, publish `available:true`, invalidate the key) is
  what the bootstrap does. The earlier "page missing" failures were a self-incompatibility teardown
  loop killing the bridge before a Bluetooth publication landed.

### Wi-Fi and audio facts

Steam's Windows backend does push real `CMsgNetworkDevicesData` reports (`hasWirelessDevice` and
`isWifiEnabled` are genuinely true), but every report carries an empty `wireless.aps`, so it never
enumerates networks. Any access point in a live probe may be WSGM's synthetic one and is not
evidence to the contrary. Audio is the cheapest gate: the store's flag is
`m_bAvailable = null != SteamClient.System.Audio`, so supplying that namespace is the whole of it,
subject to the cached-availability rule above.

### A probe must name the modules it touches

**Never iterate `webpackChunksteamui`'s module registry, and never call `new` on an export you have
not identified first.** A probe looking for three protobuf classes walked every id in `runtime.m`,
called `runtime(id)` on each (which executes the factory) and then `new value()` on every exported
function. It found nothing, restarted the machine and signed Steam out (2026-08-30); a single
`SignOutAndRestart` from the power menu is the probable path. The bundle contains power, login,
transport and storage classes whose constructors and factories have real side effects. Forcing every
module to evaluate is not a read-only operation.

A probe is read-only only when every module it resolves is a literal and every value it constructs
is one whose source it has already read. `tools\WsgmLibTest\probe-perf-components.js` is the shape
to copy: it reads the named factory as text and constructs nothing. When a class cannot be reached
that way, read its factory source (`String(runtime.m[id])`) and stop.

### Do not set force_deck_perf_tab

It is Valve's own gate override (`U(e) = e || force_deck_perf_tab`) and a persisted client setting.
It force-shows every row, including the ones WSGM cannot back.
