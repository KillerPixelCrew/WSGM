# Common plugin contracts

The MIT common plugin SDK, the resident host that admits its packages, and everything WSGM builds on
them: configuration and state, named actions and declared UI, widgets, Steam placements, session
automation and the Game Mode entry transaction. The Device runtime keeps its own contracts and is
admitted through an adapter; its mechanism is in [device plugin system](device-plugin-system.md).

## The contract and the host

`src/WSGM.Plugin.Sdk` is the MIT, dependency-free common contract assembly. `WSGM.Device.Sdk`
continues to define hardware detection, controllers, capabilities and Device Lab integration. The
resident Shell session owns the common host. Its Device coordinator admits the existing Device
runtime through an adapter, preserving the Device SDK hardware contracts.

Categories are stable strings. The host owns category policy: Device permits zero or one selected
active instance, while independent categories, `wsgm.gpu` among them, can permit multiple instances.
No Device Plugin is required on a desktop. Plugin manifests cannot grant themselves multiplicity or
privileges.

The common manifest names the assembly, entry type, numeric package version, accepted API range,
dependencies and declared access requirements. Parsing is bounded and rejects unknown members. The
host must copy admitted metadata before asynchronous use, validate paths and dependencies, and keep
plugin instances tied to their admitted identity and generation.

`src/WSGM.Plugin.Ir` is the first hardware-backed independent integration under development (#52).
It owns its endpoint protocol, command library and companion firmware, and reaches the endpoint over
USB serial or, after USB-only pairing with a per-endpoint token, over the local network. Its
declarative Tools contributions use the existing common host. Command selection, naming, relearning,
timing, scene management and Wi-Fi pairing are available through host-rendered action forms.
Hardware acceptance passed on the reference XIAO with a real HDMI switch remote. See its README for
the implemented boundary and remaining limitations.

## Lifecycle

The lifecycle is Start, resident Desktop/Game transitions, Suspend/Resume, Stop, Dispose.
Publications carry instance and generation; the host rejects stale publications. Timeout only
cancels waiting and requests cooperative unwind. It does not establish that plugin code stopped or a
hardware write was undone.

Admission reserves both instance identity and category capacity until confirmed stop and successful
disposal. Failed or uncertain release keeps the slot reserved. A timed-out in-process call retains
its lifecycle lane, cancellation budget and instance until its task actually ends; disposal cannot
overtake it. No failed stop or disposal is automatically retried. Trusted code that ignores
cancellation can therefore require process exit to recover its slot.

Health callbacks are checked against their owning registration and generation, then dispatched to
the UI with another generation check. Desktop/Game intent uses increasing revisions, cancels
obsolete cooperative mode work and leaves the Device integration resident. Independent fake
instances validate coexistence without a Device Plugin. Installed packages use the catalog and
instance manager described below.

## Device lifecycle

The common host refuses the Device category. `DeviceCoordinator` drives the sole
`DevicePluginRuntime` directly and keeps controller neutralization and release before plugin stop.
The runtime retains its private state directory and advances its generation on resume. The common
host serializes each independent common instance's lifecycle separately. Device ownership and
recovery are described in [device-plugin-system.md](device-plugin-system.md).

## Configuration and state

`IConfigurablePlugin` declares bounded boolean, numeric or text preferences for plugin behavior. The
host snapshots and validates the schema before startup. It restores saved preferences with unsaved
declaration fallbacks, then delivers a complete immutable `PluginConfiguration` snapshot.
External-state controls belong to action/capability surfaces, not this preferences contract.

An explicit edit includes the revision the UI read. `CommonPluginSettings` validates the change,
persists only those changed keys through `ConfigStore.Update`, then dispatches the complete
requested configuration. A stale revision or failed save prevents dispatch. Defaults are not saved
implicitly. Application failure does not erase desired preferences; a mismatched confirmation
remains unconfirmed. There is no automatic configuration retry. Device settings use their own
runtime's settings manifest and apply path.

`PluginStatePublication` carries instance, lifecycle generation, increasing sequence, origin and
optional configuration/action correlation. It describes effective state only and cannot reach the
configuration store. The host accepts primitive values, keeps every state key an instance publishes,
rejects reordered/stale observations and checks queued UI events again before dispatch. These are
ordinary UI/status events; high-rate controller samples retain their specialized path.

## Named actions and UI contributions

`IPluginActions` declares stable operation names and primitive argument schemas. The host snapshots
them before startup and gives each invocation a fresh operation identity, current generation, origin
and deadline. Stale generations and invalid arguments cannot dispatch. A missing, failed or
mismatched reply remains unconfirmed; there is no automatic retry. `Dispatched` means a command was
sent, whereas `AppliedVerified` requires independent evidence of the declared effect. Session
automation must not treat an IR endpoint acknowledgment as proof that a television changed input.

`IPluginUi` supplies status, button, toggle and slider descriptions. Admission checks every
action/argument link and requires numeric bounds for sliders. WSGM owns actual controls and
placement; plugins cannot inject UI code. The overlay Tools page renders common contributions,
grouped by instance and contribution category. Status readback is separate from an editable draft;
toggles and sliders require an explicit Apply press. Refresh never invokes an action. Declared
widgets can be pinned to Quick Access. Action contributions expose their declared arguments in
collapsible forms. Text and numeric fields use press-to-edit buttons and the Overlay keyboard, so
controller navigation never depends on focusing a bare TextBox. Drafts remain separate from readback
and are sent only on explicit invocation.

Stop closes action admission immediately and cooperatively cancels the active lifecycle/action call.
The stop and disposal operations still wait behind that call's actual completion, so cancellation
cannot unload code that is still using external resources. Stop tolerates partially completed
startup.

## Package loading and dependencies

`Shell\PluginLoader` loads a common package's public parameterless `IPlugin` entry type with a
matching ID, after checking that the reopened `plugin.wsgm.json` still equals the admitted one. It
rejects Device-category packages, which retain their selected installation slot and adapter. Package
roots and entry/manifest files cannot be reparse points. Package constructors must not acquire
external resources.

The device package and common packages share that loader and its `PluginLoadContext`, including
host-owned Device/common SDK type identity, shared WinRT process state, host-first dependencies and
collectible package-local fallbacks. The host works on the plugin instance itself, so a plugin that
does not implement `IConfigurablePlugin` has no settings and is never sent a configuration. The
plugin is disposed once by its current owner, and its code is unloaded only after that disposal
completed: a failed or unfinished disposal keeps the context loaded and the instance reserved. The
caller must retain ownership of loading tasks that ignore cancellation.

`CommonPluginDependencyPlan` orders enabled packages before their consumers. Missing, duplicate,
incompatible and cyclic dependencies reject affected packages while preserving independent ones.
Dotted numeric versions compare with omitted build/revision components treated as zero.

A temporary non-device package fixture exercises actual collectible loading, configuration, a named
action with file readback, declarative contributions, resident mode changes and cleanup.

`PluginPackageCatalog` reads the `.wsgmpkg` files in `%ProgramFiles%\WSGM\Plugins` without executing
code; a manifest with a `category` member is a common package. The entry assembly must exist in the
package, and for one id the highest version wins while the others are reported as superseded. The
package format, budgets and loading are in `device-plugin-system.md` §2–§7. Discovery is independent
of Device Integration and does not by itself enable a package. `AppConfig.PluginInstances` contains
explicit `PluginId`, `InstanceId` and `Enabled` choices; its default is empty. The one implicit
enable is a graphics package on a machine with its adapter (below).

`CommonPluginManager` starts selected instances in dependency order and retains them across resident
mode changes. Config reload applies only a newly saved preference revision; an unconfirmed revision
is not automatically retried. Disable and shutdown cancel startup, await actual completion and
dispose in reverse admission order. Failed cleanup retains ownership and prevents replacement.
Suspend/resume is deduplicated per instance independently from the Device coordinator. Instance
state directories use a hash of the instance ID to avoid path aliases.

Activation requests carry increasing host revisions. Disabling an instance cancels its pending load
immediately; an obsolete enable cannot start it later. A rapid explicit re-enable waits for
confirmed cleanup before creating the replacement.

Install or replace trusted packages only while WSGM is closed: a loaded package file is held open,
and a change applies at the next start. Settings' Plugin tab discovers metadata without loading code
and exposes activation for installed and configured instances. Save merges only edited instance
choices into a fresh configuration. A package with no configured instances offers a disabled
`default` instance, or an enabled one for a graphics package that serves this machine. Additional
stable instance IDs can be configured in `PluginInstances`; every configured instance appears
separately. Missing packages remain visible so their activation can be disabled without discarding
preferences.

Loading inherits the application's current authority; this host neither elevates itself nor grants
access based on manifest declarations.

## Graphics packages (`wsgm.gpu`)

The bundled vendor packages are [Intel](../src/WSGM.Plugin.IntelGpu/README.md),
[NVIDIA](../src/WSGM.Plugin.NvidiaGpu/README.md) and [AMD](../src/WSGM.Plugin.AmdGpu/README.md).
NVIDIA uses DRS-native application values; AMD's documented ADLX 3D APIs use the host's Switched
scope. Their package provenance records API sources and the remaining driver acceptance. Curated
`blind` status does not claim hardware validation.

A graphics package exposes a vendor driver's controls (variable refresh, sharpening, colour, latency
and the like) as Device SDK capabilities. Several run at once, one per vendor, beside the device
package. They are common packages, so they run whether device integration is on or off and take no
part in the machine-wide device owner.

**Enablement.** `Core\CommonPluginEnablement` decides which instances run. A graphics package runs
as its `default` instance on a machine where `DisplayAdapterInventory.Collect()` reports a PCI
adapter matching one of its `displayAdapters`, until `PluginInstances` names an instance of it: an
explicit `Enabled = false` switches it off. On a machine without such an adapter it never runs,
whatever the configuration says. Setup offers the package by the same match and writes no enable
entry. The adapter list is read once per process and logged.

**Admission.** The loader refuses a graphics entry type that does not implement `ICapabilityPlugin`.
Before admission `CommonPluginManager` asks `GpuCoordinator` to open a `PluginCapabilityChannel` for
the instance, and `PluginHost.Admit` requires that channel for the category and refuses one for any
other. The plugin reaches it as `IPluginHost.Capabilities`. The registration begins a new capability
cycle generation before `StartAsync` and before each `ResumeAsync`, and closes command admission
before a suspend or a stop, so what the plugin publishes from inside those calls is already current.
The channel refuses a stale descriptor or state generation and a descriptor whose role the
manifest's `capabilities` list does not declare.

**Tracing.** Every common plugin, not only a graphics one, logs through `IPluginHost.Trace` and
`IPluginHost.TraceChange`. WSGM writes the lines into `wsgm.log` as
`plugin/<plugin id>/<scope>: ...`, and `TraceChange` writes only when that key's value changed.

**Ownership.** `Shell\GpuCoordinator` owns one `DeviceCapabilityRouter` per publisher. It is never
merged with the device router: consumers that select the power limit, a fan or VRR by role expect
exactly one match within a publisher. The router reads its publisher through `ICapabilityPublisher`,
the same small interface the device runtime implements. Values are stored under `gpu:<plugin id>`,
each descriptor's profile scope is honoured, and native per-application values are handed to the
plugin through `SyncApplicationProfilesAsync`; `docs\profiles.md` has those rules.

The UI reaches graphics capabilities only through the coordinator:

- `Publishers()` lists each running publisher with its name, profile key and health.
- `Snapshot(pluginId)` returns its sections and each capability's view, with the projection (desired
  and Global values, profile scope, apply timing), the last result and the override id.
- `ExecuteAsync(pluginId, capabilityId, instanceId, value, origin)` writes one value and, for the
  user, saves it by its scope.
- `UseGlobalAsync(overrideId)` clears exactly that publisher's game value.
- `Changed` fires on the UI dispatcher.

The overlay and Steam draw them through `GraphicsOverlayBridge`: the overlay's Device > GPU section
([overlay and input](overlay-and-input.md#graphics-sections)) and Steam Settings > Display
([Steam CEF system](steam-cef-system.md#native-steam-settings)). Steam Quick Access also shows every
published category in Performance through `SteamSettingsQuickAccessRow`, using the same rows and
command backend. Steam Display groups the sections by adapter, display and declared category.

Variable refresh is published by a graphics package, per display. The Quick Access switch, the
Device page's Power and thermals row and the per-application restore all use
`VariableRefreshCapabilities`, which picks the only VRR capability when there is one, otherwise the
built-in panel's (an instance id starting with `internal`), then the device package's.

The initial execution model remains trusted in-process code. Collectible load contexts isolate
dependencies, not security or crashes. A process boundary would require separately designed and
validated transport, permission and recovery contracts. The SDK neither resurrects the retired
DeviceHost protocol nor describes declared permissions as enforced isolation.

## Authoring and packaging

From a source checkout, create a new output directory with a harmless common plugin example:

```powershell
.\eng\new-plugin.ps1 -Id example.counter -Output C:\work\CounterPlugin
.\eng\package-plugin.ps1 -Project C:\work\CounterPlugin\Plugin.csproj -Archive C:\work\example.counter-0.1.0.wsgmpkg
```

The template references this checkout's MIT common SDK, takes its API version from it, and
demonstrates lifecycle, effective state, a named action and declarative status/button contributions.
The common and graphics examples live in `eng/templates` and are compiled by the Plugin SDK test
project. The generator copies those sources and sets the matching `ExamplePlugin.Common.Plugin` or
`ExamplePlugin.Gpu.Plugin` entry type. `-Category wsgm.infrared` or another stable category changes
metadata without introducing a Core specialization. Device packages keep the existing Device Lab
scaffold, validation and hardware harness.

A graphics driver package uses `-Category wsgm.gpu -PciVendorId 8086` (or `10DE`, `1002`). Its
manifest adds two lists that only this category may carry, and it must carry both:

```json
"displayAdapters": [{ "pciVendorId": "8086" }],
"capabilities": ["VariableRefreshRate", "GenericToggle"]
```

`displayAdapters` holds distinct PCI vendor ids of four hexadecimal digits, read back uppercase;
setup offers the package, and WSGM runs it, only where a present adapter matches. `capabilities`
holds distinct `CapabilityRole` names the package may publish. The controller, motion, haptic and
OEM roles belong to the device package and are refused, so a graphics package never brings VIIPER,
USB/IP or HidHide. `eng/build-bundle.ps1` copies both lists into the package's `bundle.json` entry,
where setup and the Plugins page read them without loading code. A first-party graphics package is
bundled like any other through its `plugins/curated` file.

Packaging publishes the project for `win-x64`, validates the manifest with this checkout's
`PluginManifestReader` through `eng/plugin-manifest.cs`, checks the entry file, creates a new
`.wsgmpkg` and validates it with the Device SDK's `PluginPackageLayout`, the rules WSGM applies when
it opens the package: the byte bounds, safe entry names and no native images. It does not execute
the plugin entry type, install, enable or replace a package. Full common manifest, dependency and
UI/action validation remains authoritative in the host. Trust build inputs before publishing;
MSBuild is executable code. Copy an approved package file into the protected
`%ProgramFiles%\WSGM\Plugins` folder while WSGM is closed, then enable it in Settings. To update,
close WSGM and replace the file.

The existing `PluginLoaderTests` fixture is an offline example harness covering configuration,
actions, state and lifecycle without external hardware. Use the same contract pattern for provider
fakes. Runtime health and action outcomes appear on Tools; a failed or unconfirmed operation is
never an automatic retry request. Common preferences use `PluginConfigurations` with explicit
increasing revisions; provider schema validation occurs before delivery. Their generic Settings
editor is not part of this initial surface; the Device settings editor remains available.

Delivery status lives in `_plan/implementation-todo.md`. Device and common plugins have separate
lifecycle owners and retain the existing user workflows.

### Widget declarations

IPluginUi.Widgets is an optional additive declaration surface. The host validates stable widget IDs
and at least one distinct existing contribution link per widget, and copies plugin-owned lists.
Navigation categories must exist. State predicates reference normal effective-state keys; missing
predicate state means unavailable.

Widget pin persistence stores plugin ID, configured instance ID and widget ID separately in
AppConfig.PluginWidgetPins. Order follows the list. Normalization removes malformed/duplicate
entries, bounds the list to 64 and retains unavailable providers. Reset order sorts by plugin,
instance and widget identity without removing pins. UI-facing mutations use the normal atomic
ConfigStore path.

The plugin source panel exposes one pin toggle for each validated widget declaration, initialized
from the saved preference. These edit preferences only on an explicit click and report persistence
failures. The Device page exposes the same pin controls in its Quick Access widgets expander,
without duplicating capability editors. The session's plugin overlay source caches one pin snapshot
for all of these readers, refreshes it after pin mutations, and replaces it when the existing config
watcher applies a save from Settings or another process.

Pinned common widgets render below the front-page quick-access cards. The existing contribution
renderer supplies live state and named actions; widget predicates disable unavailable controls.
Missing plugin instances retain unavailable placeholders. Each card offers move up/down and unpin
under Arrange widget, with reset order under Widget order below the list. The IR package declares a
selected-command/send widget.

Pinned widgets with NavigationCategory offer Open plugin controls. The Overlay selects Tools, then
scrolls and focuses the owning plugin-instance/category anchor using stable identities.

Widget rendering consumes ICommonPluginOverlaySource for observations and explicit actions, without
owning package lifecycle. The missing-provider headless test verifies a retained visible placeholder
and no action dispatch. The rendering source returns detached PluginOverlayInstance and
PluginOverlayControls records. Views do not retain PluginRegistration or acquire lifecycle
ownership. Current source observations control action availability; generation-bound action routing
remains with the source adapter.

DeviceWidgetSource projects readable Device capabilities through the common widget vocabulary.
Numeric and boolean edits use the Device coordinator with captured cycle/descriptor generations; no
second Device lifecycle is created. Stable widget keys encode capability and instance identity. The
combined source includes Device widgets even without common packages. Choice widgets show readback
and an explicit action with the currently declared options. Selection alone does not dispatch.

`IPluginSteamUi` adds host-rendered Steam placements for an admitted common package: actions and
declared primitive settings in the shared Quick Access Extensions tab, selected-game context-menu
commands, declared custom routes, and typed toolkit modules for plugin-owned pages. A game-menu
declaration names exactly one numeric action argument; WSGM supplies the app ID from the menu that
the user opened. An action may return a validated Steam route for the host surface to navigate to.
Steam receives opaque contribution IDs, and WSGM resolves each request against the current
registration, configuration revision and generation before dispatch. Secret settings are write-only
in Steam and are never published back. This declarative path does not supply React/webpack or raw
evaluation handles; its page presentation is a compiled package-owned fragment using the toolkit's
generic renderer registration and typed bridge. The current projection exposes only Ready,
non-stopping, non-quarantined registrations. IDs belong to the captured registration and generation,
so a replacement with the same plugin identity cannot receive an old menu action. `SteamUiChanged`
invalidates plugin module state without coupling the session host to a particular plugin. A
package's Steam UI modules are registered when it becomes ready and removed when it stops, without
rebuilding the session host; a module whose patch, state or command collides with one already
registered is dropped with one log line, and the other surfaces stay.

The Extensions tab maps `PluginSettingKind.OrderedChoices` to native move-up and move-down controls.
Its text value is a comma-separated permutation of the declared choices; validation rejects
duplicates, omissions, unknown values and choice names containing commas before a plugin sees the
new configuration.

Custom routes are declared, not registered. The session host publishes one route list for the whole
session, because the page host keys its patch and its publication by a single id: two owners
publishing it would alternately clobber each other's routes, and two owners registering the patch is
refused by the module set, which would take every Steam surface down rather than just the package's
own. A plugin therefore contributes pages through `SteamPages` and the host merges them after its
own. The host's own pages are the artwork browser and the Game Library. A route already served is
dropped and named in the log, a package cannot override one of Valve's, and a plugin module
declaring a host-owned patch id is refused at projection.

### Unrestricted Steam CEF frontends, issue 119

A common package declares `steamCef: true` and a `frontendModules` list in `plugin.wsgm.json`. Each
entry names a package-local id, a UTF-8 JavaScript file and optionally a CSS file. Package paths
remain inside the archive. The content is unrestricted session code: there is no sandbox, content
review or per-capability permission system. The declarative placements above remain useful, but do
not restrict frontend bundles. Contributor rules for diagnosing live Steam remain separate from what
an opted-in product plugin can execute.

Settings > Plugins shows the initial trust warning, per-instance Steam CEF opt-in, package CEF
badges and persisted module failures. Installing a CEF package through the package cards requires
acknowledging its warning. Signing establishes origin and integrity, not review. Frontends run only
when the package declares access, the initial warning is acknowledged, its instance is enabled and
its CEF opt-in is saved. Existing packages gain no access automatically.

Bundles run as function bodies with `api`; return a teardown callback or a promise that resolves to
one. They can access JavaScript, Steam, React and the toolkit directly. `api.registerPage`,
`registerMenuEntry`, `registerQuickAccessTab`, `registerQuickAccessRow`, `registerLibraryAddition`,
`registerGamePageAddition` and `registerPatch` reuse the toolkit's existing Valve adapters. Each
registration has its own identity, readiness check, render boundary and teardown. `addStyle`
supplies the theme-loading primitive, updating Steam popup documents as they appear. Bundle code as
an IIFE/function body; Decky API compatibility is not promised.

`IPluginSteamFrontend` is an optional backend. `api.call(method, payload)` sends arbitrary JSON to
its current-generation instance; `ReadFrontendState` and `FrontendChanged` publish backend state to
`api.subscribe`. A frontend-only package needs just the ordinary `IPlugin` lifecycle. The bridge
retains generation/replay checks and registered command identities; these are transport correctness,
not a security boundary.

A load, readiness, render, registered callback, asynchronous error, backend or verification failure
disables the complete owning instance. Its modules and UI registrations retract, its backend stops,
and its module/reason are saved. Other packages and WSGM's own surfaces remain active. Reload is an
explicit Settings action followed by Save; ordinary saves preserve a failure that appeared after the
editor opened. Updating an installed package's version/hash retires the old registration before
admitting its replacement. Backend calls drain before package unload.

Every bundle has a distinct `steam-ui-plugin://` source URL. Window `error` and `unhandledrejection`
handlers attribute uncaught async failures through that URL; unrelated errors disable nothing. React
boundaries isolate component errors. Toolkit-owned callbacks use guards; timer/listener helpers
automatically clean up, and plugins register cleanup for their own external side effects. CSS and
error listeners are removed before plugin teardown. CDP disables Debugger and pause-on-exceptions
before a connection becomes ready. Endless loops and renderer crashes cannot be caught here: start
WSGM with `--shell --desktop-resident --cef-plugins-off` to recover without injecting package
frontends. If Steam's renderer is already stuck, close and restart Steam with WSGM running in this
recovery mode.

The independently buildable example is in
[examples/SteamCefPlugin](../examples/SteamCefPlugin/README.md).

Artwork was briefly a bundled package and is now part of WSGM again, so nothing ships in `Plugins\`
by default. `src/WSGM.Plugin.Ir` remains an independent plugin, and an installed third-party package
still reaches Steam through exactly the placements above.

Widget action clicks re-read provider availability, generation and widget predicates before
dispatch, including changes between timer refreshes. Reloaded providers rebuild the retained pin
using the new generation. Focused headless tests cover unload/recovery and stale-click refusal.

Pinned widgets use a vertical list in the Quick Access scroll surface. Reordering restores focus to
the same widget action by stable identity, or its Unpin action when the requested move reaches an
end. Unpinning focuses the neighboring widget, or the list itself when empty; resetting order
retains the reset control. The preference operations are supplied by the Shell source, allowing
focused UI checks without reading or writing live configuration.

Widget icons use host keys: power, fan, battery, lighting, controller, display, settings and action.
They render with the shared WSGM geometry and foreground color. Unknown keys omit the icon; plugin
strings are never parsed as geometry or markup. The IR command widget uses the action icon.

Controller confirmation opens widget editors and choice popups. While a selector is open, shared
navigation keeps D-pad selection with its owning ComboBox despite popup-item focus. Confirmation
closes the popup and restores selector focus; the separate Apply action dispatches the draft.

### Session automation

`AppConfig.GameModeLaunch` holds four ordered lists of plugin action steps, run at four session
events: entering Game Mode, leaving it, desktop startup and desktop wake. A step names a plugin
instance, a declared action, its primitive arguments and a 1–120 second deadline. They run with
origin `SessionAutomation`, and the step's generation is resolved at the moment it runs, so a plugin
that restarted between two steps is never addressed with a stale one.

`Shell\PluginActionSequence.cs` runs them. Entry stops at the first step that did not succeed;
sending the rest after the TV failed to come on only makes the failure harder to read. The other
three run every step and report what failed, because each one is independently worth attempting and
there is nothing to abort. **Nothing is ever retried**, in either mode: a `Dispatched` or
`Unconfirmed` outcome means the command may already be on the wire.

Settings > Display authors the four lists: add a step from a running plugin's declared actions,
remove it, move it earlier or later, and edit its arguments and deadline. Order is the point, not a
detail: the HDMI switch has to select this PC before the television is told to turn on, or the
television comes up showing the wrong input. Argument values are checked against the same declared
ranges and choices the host enforces, and a value outside them blocks the save rather than being
sent and refused.

Which actions can be added depends on where Settings was opened. Opened from the WSGM tray icon, the
overlay or a desktop Settings launch it is the shell session's one Settings window
(`Shell\SettingsSurface`), and the session hands it its own reader of the running instances; a
standalone `--settings` process has no plugin host, so it lists nothing to add. A saved step whose
plugin is not running keeps its values, is shown but not editable, and is still removable: dropping
a step you no longer want must not require starting a plugin. Its argument schema is captured by the
host before the plugin starts, so for one that is not there, there is nothing truthful to render.

Desktop startup and wake are coalesced by `Shell\DesktopActionAdmission.cs`: one run at a time, a
five-second cooldown, and never while Game Mode is active or a transition is in flight. The four
notifications around a sleep overlap, and an action list must not fire twice for one wake.

### Game Mode entry

`Shell\GameModeEntryTransaction.cs` owns the order and the compensation; the backend owns the
effects. Everything before Explorer leaves is undoable, so the splash offers Cancel and a failure
puts the desktop back exactly as it was. Once Explorer is gone there is no cheap desktop to return
to, so that exit is the boundary: after it the splash button becomes "Switch to desktop" and later
failures compensate forwards.

The order is: record what to return to, run the entry actions, wait for every required display,
persist the pending return layout, prepare the Explorer anchor, re-check the displays, exit
Explorer, apply the layout, request Big Picture, commit.

Big Picture is requested **after** Explorer leaves and after the layout is applied. The desktop-only
shell did the opposite as a latency optimisation, which was worth having when Steam was not already
running. Here Steam is already up on the desktop, the splash covers the whole transaction, and a Big
Picture window created before the layout would be built on the wrong display at the wrong scaling.

Entry recovery restores the desktop before running leave actions whenever an entry step was
dispatched or left uncertain, re-applies the return layout and clears the pending record. A step
that was _rejected_ changed nothing, so it earns no compensation: an IR burst there would move a
switch the user never asked to move.

The display wait has **no deadline**, only cancellation. The reference setup puts a TV behind an
HDMI switch, so how long the display takes is up to a person and a piece of consumer hardware; a
timeout would only ever fire on the honest case. `Shell\DisplayArrivalWaiter.cs` requires two
identical observations a settle apart before it believes a display has arrived, because a monitor
coming up behind a switch enumerates, disappears and re-enumerates while the sink negotiates.
`Interop\DisplayChangeWindow.cs` supplies the hint: a hidden top-level window, because
`WM_DISPLAYCHANGE` is broadcast to top-level windows only and the message-only window never hears
it. The hint only shortens a wait; a five-second backstop poll is what makes the wait correct when
no broadcast arrives.

Desktop residency is its own setting (Settings > System, "Start WSGM at sign-in" plus "Start in"); a
Desktop start runs `--shell --desktop-resident` and needs the installed logon service.

Leaving Game Mode restores the layout the session owes the desktop before Explorer starts, and runs
the leave actions only after Explorer recovery succeeds. Startup and wake actions run only on
Desktop, await initial plugin admission and resume completion, and recheck the mode before dispatch.
One session semaphore serializes action work with mode transitions, so queued Desktop work cannot
switch away during Game Mode entry.

The transaction reaches plugins only through `PluginHost`, with origin `SessionAutomation` and the
currently admitted instance generation; WSGM itself contains no IR protocol or HDMI-device logic.
Display waits and layout calls run off the UI thread. A deadline or cancellation failure names the
stage, and `SessionModes.TransitionInProgress` refuses another transition until the transaction has
settled on any outcome. Transition and Desktop action failures use the existing warning surface.

Normal-level diagnostics record entry stages, required display identities and wait completion. Each
layout operation logs its requested values, elapsed time, native result code, warnings, rollback
outcome and observed arrangement under one operation identifier. A failed diagnostic readback cannot
change the apply result or dispatch another write.

Validation uses fake providers, source-generated config round trips, ordered transaction tests,
admission checks and headless editor interactions. It does not represent a physical HDMI-switch,
live logon, Modern Standby or Steam-window placement pass. Those remain hardware review scenarios,
separate from #52's carrier measurement and real-remote acceptance.
