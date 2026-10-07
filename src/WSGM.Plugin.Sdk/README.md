# WSGM Plugin SDK

The MIT-licensed common contracts for WSGM integrations that are not a device: identity, category
strings, host-owned slot policy, strict manifests, resident lifecycle, configuration, state
publications and diagnostic tracing. The current contract is `PluginApi.Version` 4.

It references two libraries, and the types it uses from them are part of the contract:

- the Device SDK, for the active-time `Deadline`, the capability descriptor, state and command
  model, `PlainText` and `DeviceTraceLevel`;
- SteamUiToolkit, for `ISteamUiModule` and the toolkit types reachable from it. Every plugin shares
  the host's copy of the toolkit, so API 4 covers that closure too, and a plugin built against an
  older toolkit is refused at manifest read rather than failing at load or first call.

It does not reference Avalonia or Windows Device Control. The device package uses the Device SDK's
own lifecycle and runtime, driven directly by `DeviceCoordinator`; the common host rejects its
category. Nothing maps one lifecycle onto the other.

GPU packages use `PluginCategories.Gpu` (`wsgm.gpu`). They are independent common plugins, may
coexist across vendors and adapters, and do not consume the sole Device slot. Their typed
capabilities appear on the overlay's pages and in Steam's Quick Access, including with Device
Integration disabled. Vendor APIs, capability discovery and adapter/display identity belong to each
package. The category does not provide a driver API or invent global/per-game/inherit semantics for
a driver that has not declared them.

`eng/new-plugin.ps1` creates a common project, and `eng/package-plugin.ps1` builds an archive. See
`docs/plugin-system.md` for installation, explicit update and reload, and the provider fixture.

[The API reference](docs/reference.md) maps every public contract file, lifecycle call, optional
interface and result to its host behavior.

## Categories and slots

Device is the `wsgm.device` category, with zero or one active instance. Every other category is an
open string and the host decides how many instances it allows. A desktop with no device plugin is
perfectly valid.

Manifest `permissions` are metadata. WSGM validates and records them but never grants or enforces
them, and they do not sandbox in-process code.

## Lifecycle

`IPlugin` starts with a host-owned instance and generation context, receives Desktop and Game
transitions plus suspend and resume, then stops and disposes.

A timeout is not proof that work stopped. A failed stop must never be reported as released, and the
host discards publications that arrive from a retired generation.

`IPluginHost.Trace` and `IPluginHost.TraceChange` write into wsgm.log as
`plugin/<pluginId>/<scope>: <message>`. `TraceChange` writes only when that key's value changed, so
polled state can go through it without repeating.

## Manifests

`PluginManifestReader` accepts camel-case JSON of at most 256 KiB and nesting depth 16, rejects
unknown members, checks common API compatibility and numeric dependency ranges, and admits only a
DLL filename at the package root. A `PluginManifest` is immutable once read. Resolving those
dependencies, deciding trust, containing the filesystem and loading code all belong to the host.

## Configuration and state

These are two different things and they never cross.

`IConfigurablePlugin` declares preferences and confirms the host-owned revision it was asked for.
WSGM saves only explicit edits, before dispatch, so initialization defaults and failed delivery
cannot overwrite them.

`IPluginHost.PublishState` publishes effective observations with a generation, a sequence and an
origin. It never changes saved preferences.

Boolean, finite numeric and plain text primitives are shared through `PluginValue`, and schema
validation lives in `PluginConfigurationRules`.

## Actions and UI

`IPluginActions` declares named operations for the UI and for Core automation. Results tell
dispatched commands, independently verified effects, rejections and uncertain outcomes apart.

`IPluginUi` links bounded action forms to those declarations. Text and numeric arguments can be
edited through the host's controller keyboard before an explicit dispatch, and defaults only
initialize the draft rather than firing a command. Other host-rendered controls link actions and
effective state keys. No plugin UI code is ever injected, and an action result cannot change saved
preferences.

`IPluginUi.Widgets` can declare compact `PluginWidget` groups. Each one has a stable widget ID and
references one or more existing contribution IDs. The optional icon, secondary state, boolean
visibility and enabled keys, and owning navigation category are all data, so again no plugin UI code
is loaded. The host combines widget IDs with the plugin instance identity and captures the
declarations immutably.

`IPluginSteamUi` is the Steam counterpart for an explicitly enabled common package. It can declare
actions in the shared Quick Access Extensions tab, a command in a selected game's menu, and typed
`SteamUiModules` built on SteamUiToolkit. The selected-game contribution names one declared numeric
action argument for the exact Steam app id. A successful action can return `SteamRoute` to open a
plugin-owned page, and `SteamUiChanged` asks the host to republish module state. The modules are
registered when the plugin becomes ready and removed when it stops. WSGM renders the generic host
surfaces, keeps opaque command IDs and routes requests through the current plugin generation. A
declarative path does not pass Steam's React objects or evaluation handles. Packages may separately
declare unrestricted Steam CEF frontend bundles, as described below.

### JavaScript/CSS frontends

Declare `steamCef: true` and `frontendModules: [{ id, script, style? }]` in the common manifest. The
host loads these package-relative UTF-8 files through the toolkit's normal patch lifecycle after the
user's initial acknowledgement and per-package opt-in. There is no sandbox or content policy. Code
receives `api`, including Steam's React, surface registration, CSS, teardown and backend calls.
Optional `IPluginSteamFrontend` handles JSON requests and state publications; omit it for a
frontend-only package. A module failure disables the whole instance until manual reload. See
[the full host contract](../../docs/plugin-system.md#unrestricted-steam-cef-frontends-issue-119) and
[the buildable example](../../examples/SteamCefPlugin/README.md).

Every ready configurable package also appears in the Extensions tab. Boolean, number, text, secret
and ordered-choice settings use the existing revisioned configuration contract. Ordered choices
store a comma-separated permutation of the declared values and render with native move controls;
secrets are obscured while editing and are not sent back to Steam as current values.

## Where the host puts all this

Discovery and explicit per-instance activation are hosted independently of Device Integration.
Settings exposes activation and is where you author session automation: four ordered lists of named
action steps that run when entering Game Mode, leaving it, and at desktop startup and wake. The
overlay's Tools page renders declared status, action, toggle and slider controls, and declared
widgets can be pinned to Quick Access.

A collectible non-device fixture validates common loading and the whole contract path.
