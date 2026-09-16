# WSGM Plugin SDK

The MIT-licensed common contracts for WSGM integrations that are not a device: identity, category
strings, host-owned slot policy, strict manifests, resident lifecycle, configuration and state
publications. It depends on nothing, not the Device SDK, not the UI, not Windows Device Control.

The Device SDK and the device runtime still work as before. A compatibility adapter in WSGM maps
this common lifecycle onto that runtime through the resident Shell host.

`eng/new-plugin.ps1` creates a common project, and `eng/package-plugin.ps1` builds an archive. See
`docs/plugin-system.md` for installation, explicit update and reload, and the provider fixture.

## Categories and slots

Device is the `wsgm.device` category, with zero or one active instance. Every other category is an
open string and the host decides how many instances it allows. A desktop with no device plugin is
perfectly valid.

Manifest permissions declare what a plugin requires. They do not grant privileges and they do not
sandbox in-process code.

## Lifecycle

`IPlugin` starts with a host-owned instance and generation context, receives Desktop and Game
transitions plus suspend and resume, then stops and disposes.

A timeout is not proof that work stopped. A failed stop must never be reported as released, and the
host discards publications that arrive from a retired generation.

## Manifests

`PluginManifestReader` accepts bounded camel-case JSON, rejects unknown members, checks common API
compatibility and numeric dependency ranges, and admits only a DLL filename at the package root.
Resolving those dependencies, deciding trust, containing the filesystem and loading code all belong
to the host.

## Configuration and state

These are two different things and they never cross.

`IConfigurablePlugin` declares preferences and confirms the host-owned revision it was asked for.
WSGM saves only explicit edits, before dispatch, so initialization defaults and failed delivery
cannot overwrite them.

`IPluginHost.PublishState` publishes effective observations with a generation, a sequence and an
origin. It never changes saved preferences.

Boolean, finite numeric and bounded text primitives are shared through `PluginValue`, and schema
validation lives in `PluginConfigurationRules`.

## Actions and UI

`IPluginActions` declares named operations for the UI and for Core automation. Results tell
dispatched commands, independently verified effects, rejections and uncertain outcomes apart.

`IPluginUi` links bounded action forms to those declarations. Text and numeric arguments can be
edited through the host's controller keyboard before an explicit dispatch, and defaults only
initialize the draft rather than firing a command. Other host-rendered controls link actions and
effective state keys. No plugin UI code is ever injected, and an action result cannot change saved
preferences.

`IPluginUi.Widgets` can declare up to 32 compact `PluginWidget` groups. Each one has a stable widget
ID and references one to eight existing contribution IDs. The optional icon, secondary state,
boolean visibility and enabled keys, and owning navigation category are all data, so again no plugin
UI code is loaded. The host combines widget IDs with the plugin instance identity and captures the
declarations immutably.

## Where the host puts all this

Discovery and explicit per-instance activation are hosted independently of Device Integration.
Settings exposes activation and is where you author session automation: four ordered lists of named
action steps that run when entering Game Mode, leaving it, and at desktop startup and wake. The
overlay's Tools page renders declared status, action, toggle and slider controls, and declared
widgets can be pinned to Quick Access.

A collectible non-device fixture validates common loading and the whole contract path.
