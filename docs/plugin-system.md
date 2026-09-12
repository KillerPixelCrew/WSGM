# Common plugin contracts

`src/WSGM.Plugin.Ir` is the first hardware-backed independent integration under development (#52).
It owns its endpoint protocol, command library and companion firmware, and reaches the endpoint over
USB serial or, after USB-only pairing with a per-endpoint token, over the local network. Its
declarative Tools contributions use the existing common host. Command selection, naming, relearning,
timing, scene management and Wi-Fi pairing are available through host-rendered action forms.
Hardware acceptance passed on the reference XIAO with a real HDMI switch remote. See its README for
the implemented boundary and remaining limitations.

`src/WSGM.Plugin.Sdk` is the MIT, dependency-free common contract assembly. `WSGM.Device.Sdk`
continues to define hardware detection, controllers, capabilities and Device Lab integration. The
resident Shell session owns the common host. Its Device coordinator admits the existing Device
runtime through an adapter, preserving the Device SDK hardware contracts.

Categories are stable strings. The host owns category policy: Device permits zero or one selected
active instance, while independent categories can permit multiple instances. No Device Plugin is
required on a desktop. Plugin manifests cannot grant themselves multiplicity or privileges.

The common manifest names the assembly, entry type, numeric package version, accepted API range,
dependencies and declared access requirements. Parsing is bounded and rejects unknown members. The
host must copy admitted metadata before asynchronous use, validate paths and dependencies, and keep
plugin instances tied to their admitted identity and generation.

The lifecycle is Start, resident Desktop/Game transitions, Suspend/Resume, Stop, Dispose.
Publications carry instance and generation; the host rejects stale publications. Timeout only
cancels waiting and requests cooperative unwind. It does not establish that plugin code stopped or a
hardware write was undone.

`DevicePluginCompatibilityAdapter` wraps the existing device runtime for this lifecycle. It retains
device command and hardware ownership, maps device health, advances the runtime generation on resume
and preserves an unconfirmed stop result across repeated requests. The common host serializes each
instance's lifecycle separately. The Device coordinator keeps controller neutralization and release
before plugin stop. The runtime retains its admitted private state directory; the adapter does not
relocate device state. A collectible fixture exercises the full host/adapter/runtime lifecycle.

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

## Configuration and state

`IConfigurablePlugin` declares bounded boolean, numeric or text preferences for plugin behavior. The
host snapshots and validates the schema before startup. It restores saved preferences with unsaved
declaration fallbacks, then delivers a complete immutable `PluginConfiguration` snapshot.
External-state controls belong to action/capability surfaces, not this preferences contract.

An explicit edit includes the revision the UI read. `CommonPluginSettings` validates the change,
persists only those changed keys through `ConfigStore.Mutate`, then dispatches the complete
requested configuration. A stale revision or failed save prevents dispatch. Defaults are not saved
implicitly. Application failure does not erase desired preferences; a mismatched confirmation
remains unconfirmed. There is no automatic configuration retry. Existing Device settings retain
their current adapter path.

`PluginStatePublication` carries instance, lifecycle generation, increasing sequence, origin and
optional configuration/action correlation. It describes effective state only and cannot reach the
configuration store. The host accepts bounded primitive values, retains at most 128 state keys per
instance, rejects reordered/stale observations and checks queued UI events again before dispatch.
These are ordinary UI/status events; high-rate controller samples retain their specialized path.

## Named actions and UI contributions

`IPluginActions` declares stable operation names and primitive argument schemas. The host snapshots
them before startup and gives each invocation a fresh operation identity, current generation, origin
and deadline. Stale generations and invalid arguments cannot dispatch. A missing, failed or
mismatched reply remains unconfirmed; there is no automatic retry. `Dispatched` means a command was
sent, whereas `AppliedVerified` requires independent evidence of the declared effect. Route
orchestration must not treat an IR endpoint acknowledgment as proof that a television changed input.

`IPluginUi` supplies bounded status, button, toggle and slider descriptions. Admission checks every
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

`CommonPluginPackage` reads bounded `plugin.wsgm.json` metadata and loads a public parameterless
`IPlugin` entry type with a matching ID. It rejects Device-category packages, which retain their
selected installation slot and adapter. Package roots and entry/manifest files cannot be reparse
points. Package constructors must not acquire external resources.

The common loader reuses the existing `PluginLoadContext`, including host-owned Device/common SDK
type identity, shared WinRT process state, host-first dependencies and collectible package-local
fallbacks. A failed plugin disposal does not explicitly unload its context. The caller must retain
ownership of loading tasks that ignore cancellation.

`CommonPluginDependencyPlan` orders enabled packages before their consumers. Missing, duplicate,
incompatible and cyclic dependencies reject affected packages while preserving independent ones.
Dotted numeric versions compare with omitted build/revision components treated as zero.

A temporary non-device package fixture exercises actual collectible loading, configuration, a named
action with file readback, declarative contributions, resident mode changes and cleanup.

`CommonPluginCatalog` reads `%ProgramFiles%\WSGM\Plugins\<plugin-id>` without executing code. The
directory name must match the manifest ID and the entry assembly must exist. Discovery is
independent of Device Integration and does not enable a package. `AppConfig.PluginInstances`
contains explicit `PluginId`, `InstanceId` and `Enabled` choices; its default is empty.

`CommonPluginManager` starts selected instances in dependency order and retains them across resident
mode changes. Config reload applies only a newly saved preference revision; an unconfirmed revision
is not automatically retried. Disable and shutdown cancel startup, await actual completion and
dispose in reverse admission order. Failed cleanup retains ownership and prevents replacement.
Suspend/resume is deduplicated per instance independently from the Device coordinator. Instance
state directories use a hash of the instance ID to avoid path aliases.

Activation requests carry increasing host revisions. Disabling an instance cancels its pending load
immediately; an obsolete enable cannot start it later. A rapid explicit re-enable waits for
confirmed cleanup before creating the replacement.

Install or replace trusted packages only while their instances are stopped. Settings' Plugin tab
discovers metadata without loading code and exposes activation for installed and configured
instances. Save merges only edited instance choices into a fresh configuration. A package with no
configured instances offers a disabled `default` instance. Additional stable instance IDs can be
configured in `PluginInstances`; every configured instance appears separately. Missing packages
remain visible so their activation can be disabled without discarding preferences.

Loading inherits the application's current authority; this host neither elevates itself nor grants
access based on manifest declarations.

The initial execution model remains trusted in-process code. Collectible load contexts isolate
dependencies, not security or crashes. A process boundary would require separately designed and
validated transport, permission and recovery contracts. The SDK neither resurrects the retired
DeviceHost protocol nor describes declared permissions as enforced isolation.

## Authoring and packaging

From a source checkout, create a new output directory with a harmless common plugin example:

```powershell
.\eng\new-plugin.ps1 -Id example.counter -Output C:\work\CounterPlugin
.\eng\package-plugin.ps1 -Project C:\work\CounterPlugin\Plugin.csproj -Archive C:\work\counter-0.1.0.zip
```

The template references this checkout's MIT common SDK and demonstrates lifecycle, effective state,
a named action and declarative status/button contributions. `-Category wsgm.infrared` or another
stable category changes metadata without introducing a Core specialization. Device packages keep the
existing Device Lab scaffold, validation and hardware harness.

Packaging runs the project's build, checks essential manifest/output fields and creates a new ZIP.
It does not execute the plugin entry type, install, enable or replace a package. Full common
manifest, dependency and UI/action validation remains authoritative in the host. Trust build inputs
before publishing; MSBuild is executable code. Extract an approved archive into the protected
`%ProgramFiles%\WSGM\Plugins\<plugin-id>` directory while the instance is stopped, then enable it in
Settings. Updating follows explicit disable, confirmed cleanup, replacement and re-enable.

The existing `CommonPluginPackageTests` fixture is an offline example harness covering
configuration, actions, state and lifecycle without external hardware. Use the same contract pattern
for provider fakes. Runtime health and action outcomes appear on Tools; a failed or unconfirmed
operation is never an automatic retry request. Common preferences use `PluginConfigurations` with
explicit increasing revisions; provider schema validation occurs before delivery. Their generic
Settings editor is not part of this initial surface; the Device settings editor remains available.

The migration follows common contracts, Device compatibility adapter,
lifecycle/configuration/events, action/UI contributions, then an independent non-device consumer.
Delivery status lives only in `_plan/implementation-todo.md`; existing device behavior stays the
baseline throughout.

### Widget declarations

IPluginUi.Widgets is an optional additive declaration surface. The host admits at most 32 widgets,
validates stable IDs and one to eight distinct existing contribution links, and copies plugin-owned
lists. Navigation categories must exist. State predicates reference normal effective-state keys;
missing predicate state means unavailable.

Widget pin persistence stores plugin ID, configured instance ID and widget ID separately in
AppConfig.PluginWidgetPins. Order follows the list. Normalization removes malformed/duplicate
entries, bounds the list to 64 and retains unavailable providers. Reset order sorts by plugin,
instance and widget identity without removing pins. UI-facing mutations use the normal atomic
ConfigStore path.

The plugin source panel now exposes Pin widget and Unpin widget actions for each validated widget
declaration. These edit preferences only on an explicit click and report persistence failures. The
Device page exposes the same pin controls in its Quick Access widgets expander, without duplicating
capability editors.

Pinned common widgets now render below the front-page quick-access cards. The existing contribution
renderer supplies live state and named actions; widget predicates disable unavailable controls.
Missing plugin instances retain identity-labelled placeholders. Each card offers move up/down and
unpin, with a reset-order action below the list. The IR package declares a selected-command/send
widget.

Pinned widgets with NavigationCategory now offer Open plugin controls. The Overlay selects Tools,
then scrolls and focuses the owning plugin-instance/category anchor using stable identities.

Widget rendering consumes ICommonPluginOverlaySource for observations and explicit actions, without
owning package lifecycle. The missing-provider headless test verifies a retained visible placeholder
and no action dispatch.

The rendering source returns detached PluginOverlayInstance and PluginOverlayControls records. Views
no longer retain PluginRegistration or acquire lifecycle ownership. Current source observations
control action availability; generation-bound action routing remains with the source adapter.

DeviceWidgetSource now projects readable Device capabilities through the common widget vocabulary.
Numeric and boolean edits use the Device coordinator with captured cycle/descriptor generations; no
second Device lifecycle is created. Stable widget keys encode capability and instance identity. The
combined source includes Device widgets even without common packages. Choice widgets show readback
and an explicit action with the currently declared options. Selection alone does not dispatch.
Device-page pin controls use this same source and persistence path.

Widget action clicks re-read provider availability, generation and widget predicates before
dispatch, including changes between timer refreshes. Reloaded providers rebuild the retained pin
using the new generation. Focused headless tests cover unload/recovery and stale-click refusal.

Pinned widgets use a vertical list in the Quick Access scroll surface. Reordering restores focus to
the same widget action by stable identity. Unpinning focuses the neighboring widget, or the list
itself when empty; resetting order retains the reset control. The preference operations are supplied
by the Shell source, allowing focused UI checks without reading or writing live configuration.

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

Settings > Display lists the saved steps read-only with their arguments and deadlines, and says when
a named plugin is not running. Settings owns no plugin host, so it never resolves a step against a
live instance.

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

Compensation before the boundary runs the leave actions whenever an entry step was dispatched or
left uncertain, re-applies the return layout and clears the pending record. A step that was
_rejected_ changed nothing, so it earns no compensation: an IR burst there would move a switch the
user never asked to move.

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

Leave restores the configured Desktop profile before invoking the external entertainment-route
action, after Explorer recovery succeeds. Startup and wake route work runs only on Desktop, awaits
initial plugin admission and resume completion, and rechecks mode before dispatch. Notifications are
coalesced while work runs and for five seconds from admission. One session semaphore serializes
route work with mode transitions, so queued Desktop work cannot switch away during Game Mode entry.

DisplayRouteBackend uses PluginHost with SessionAutomation origin and the currently admitted
instance generation. Core contains no IR protocol or HDMI-device logic. Dispatched permits the next
step without claiming external hardware readback; Unconfirmed and Rejected stop the sequence.
Display waits and profile calls run off the UI thread. Deadline/cancellation failures name the stage
and preserve a handle to still-running work, refusing subsequent routes until it settles. Errors and
lifecycle reasons are logged; transition and Desktop route failures use the existing warning
surface. The Desktop splash button cancels entry, including the wait for Steam's target window.

Validation uses fake providers, source-generated config round trips, ordered route tests, admission
checks and headless editor interactions. It does not represent a physical HDMI-switch, live logon,
Modern Standby or Steam-window placement pass. Those remain hardware review scenarios, separate from
#52's carrier measurement and real-remote acceptance.
