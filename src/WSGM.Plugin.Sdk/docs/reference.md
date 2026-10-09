# WSGM.Plugin.Sdk reference

This is the public common-plugin contract, reviewed against API 5. The XML comments on the linked
source define individual members; this guide describes how the types fit together. Host admission,
package loading, UI projection and session automation are covered in
[the host guide](../../../docs/plugin-system.md).

## Identity and manifest

[PluginManifest.cs](../PluginManifest.cs) defines `PluginApi`, `PluginCategories`,
`PluginCategoryPolicy`, `PluginDependency`, `PluginManifest`, `DisplayAdapterMatch` and
`PluginFrontendModule`. API compatibility is independent of the package's version. A manifest's
`minimumApiVersion` / `maximumApiVersion` must contain the host API version and have a minimum of 5.
API 5 is the first shared assembly identity; future additive API versions may remain inside a
compatible declared range. Device packages are no longer admitted; WSGM integrates LibHandheld
directly.

The manifest identifies the package, its category, root DLL and public entry type, optional numeric
dependency ranges and WSGM release, declared permissions, GPU adapter rules and capability roles,
and optional Steam CEF frontend bundles. Permissions describe access; they grant and enforce
nothing. All admitted code runs in WSGM's process with its authority.

[PluginManifestReader.cs](../PluginManifestReader.cs) reads camel-case JSON, rejects unknown
members, bounds the document to 256 KiB and depth 16, and validates identifiers, canonical versions,
entry names, dependencies and frontend paths. Common identity and entry-point rules come from shared
`ManifestRules`. Parsing does not load code, resolve dependencies or prove a package safe. The host
validates its archive and admitted metadata again when opening the entry assembly.

The SDK references only `SteamUiToolkit`. Shared semantic records live in this assembly under
`Shared`, retaining their `WSGM.Device.Sdk.*` source namespaces. API 5 requires rebuilt plugins for
the changed assembly type identity. There is no Device SDK assembly or forwarding layer. All plugin
contracts must retain the host's assembly identity. It references neither Avalonia nor Windows
Device Control. Package constructors and declaration getters must not acquire external resources.

## Lifecycle and host callbacks

[PluginContracts.cs](../PluginContracts.cs) contains every resident lifecycle type:

| Type                                       | Contract                                                                                                      |
| ------------------------------------------ | ------------------------------------------------------------------------------------------------------------- |
| `PluginInstanceIdentity`                   | Package ID plus a host-assigned instance ID.                                                                  |
| `PluginContext`                            | Instance, positive lifecycle generation, Desktop/Game mode, active-time deadline and private state directory. |
| `PluginSessionMode`                        | `Desktop` or `Game`; these transitions do not unload the package.                                             |
| `PluginHealth` / `PluginHealthPublication` | `Ready`, `Unavailable` or `Failed`, tied to the current instance and generation.                              |
| `IPlugin`                                  | Required identity, start, session-change, stop and async disposal; suspend/resume have no-op defaults.        |
| `IPluginHost`                              | Health and effective-state publications, diagnostic tracing, and an optional capability channel.              |

`StartAsync` receives the host and context and returns health after acquisition. Startup failure
must unwind what it acquired. `SessionChangedAsync` handles current mode intent. Suspend quiesces
work; resume receives a new lifecycle generation and revalidates resources. `StopAsync` must
tolerate partial startup and returns true only after confirmed resource release. `DisposeAsync`
releases the instance's remaining managed/native resources after lifecycle work ends.

A timeout cancels waiting and requests cooperative cancellation; it is not proof that an operation
stopped. The host retains the lifecycle lane, instance reservation and load context until unfinished
work ends. Failed stop/disposal is not automatically repeated. Publications from retired generations
are discarded, including UI callbacks queued before the generation changed.

`Trace` records bounded events and `TraceChange` suppresses unchanged polled values. The host prefix
is `plugin/<plugin id>/<scope>`. Avoid per-sample logging. State-directory ownership is a
persistence boundary between instances, not a sandbox or permission boundary.

## Preferences and observations

[PluginState.cs](../PluginState.cs) defines `PluginValue`: exactly one boolean, finite number or
text value. `IsValid` checks that union and number finiteness; declaration/host validation also
checks the relevant text and schema rules.

`PluginStatePublication` identifies the instance, generation, strictly increasing sequence, state
key, value and `PluginStateOrigin`, optionally correlating a configuration revision or action ID.
Origins are Initialization, Configuration, Action and HardwareReadback. Every one is observation
only; none authorizes persisting a preference.

[PluginConfiguration.cs](../PluginConfiguration.cs) owns the preference contract:

| Type                                                       | Contract                                                                                                      |
| ---------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------- |
| `PluginSetting` / `PluginSettingKind`                      | Static key, label, default, optional numeric bounds/choices; Boolean, Number, Text, Secret or OrderedChoices. |
| `PluginConfigurationRules`                                 | Validates declarations, defaults, values and ordered-choice encodings.                                        |
| `PluginConfiguration`                                      | Complete immutable values with a host-owned revision and User/Restore origin.                                 |
| `PluginConfigurationResult` / `PluginConfigurationOutcome` | Exact requested revision plus Applied, Rejected or Unconfirmed; cannot change desired values.                 |
| `IConfigurablePlugin`                                      | Static `Settings` and `ConfigureAsync`; absent means no configuration delivery.                               |

The host captures declarations before startup and delivers configuration afterwards. Defaults fill
missing preferences without becoming saved edits. Explicit edits persist before delivery. A stale
revision, failed save or mismatched result cannot silently replace the user's intent; an unconfirmed
application is not automatically retried. Secret fields obscure their editor and are not sent to
Steam as current values; the type is not an encrypted-storage guarantee. OrderedChoices stores a
comma-separated permutation of the declaration's choices.

## Actions and host-rendered UI

[PluginActions.cs](../PluginActions.cs) contains `IPluginActions`, `PluginAction`,
`PluginActionRequest`, `PluginActionResult`, `PluginActionOrigin` and `PluginActionOutcome`.
Declarations name stable operations and primitive argument schemas. An invocation receives its own
operation ID, complete arguments and User, SessionAutomation or ProfileRestore origin; its context
supplies current generation and deadline. Return the exact operation ID.

| Result            | Meaning                                                                   |
| ----------------- | ------------------------------------------------------------------------- |
| `Rejected`        | No external dispatch.                                                     |
| `Dispatched`      | A command was sent; the external effect is not independently established. |
| `AppliedVerified` | Independent evidence confirms the action's declared effect.               |
| `Unconfirmed`     | External outcome is unknown; no automatic retry.                          |

The optional `SteamRoute` requests navigation after a successful user action. Host admission still
checks route ownership. A device acknowledging an IR send does not establish that a TV reacted.

The same file defines `IPluginUi`, `PluginUiContribution`, `PluginUiKind` and `PluginWidget`.
Contributions are Status, Action, Toggle or Slider records linked to declared actions and state. The
host renders and validates them. Widgets group existing contributions and may declare an icon,
secondary state, boolean visibility/enabled keys and owning navigation category. Missing predicate
state makes a widget unavailable; a pinned unavailable widget retains a placeholder. Rendering,
refresh and pinning never dispatch actions; editable drafts require explicit submission.

## Capabilities and native application profiles

[PluginCapabilities.cs](../PluginCapabilities.cs) supplies `ICapabilityHost` and
`ICapabilityPlugin`. WSGM currently admits this channel only for `wsgm.gpu`; it is required for that
category. The channel carries shared SDK descriptors, observations and commands, without physical
controller, OEM or haptic publications. Each publisher has its own router and profile key.

Publish complete descriptor replacements before their observations. Roles must appear in the
manifest. The owning publisher serializes updates and commands; receiving a publication does not
prove that its consumer accepted it. Commands revalidate current identity, bounds, availability and
deadline immediately before dispatch. Retired capabilities reject commands rather than using stale
metadata. Hardware input and device lifecycle do not pass through this common capability channel.
The [shared contract reference](shared-reference.md) defines capability roles, value shapes, command
outcomes, profile scope, apply timing and written-versus-verified state. A supported write can
succeed without readback; uncertain writes are never blindly retried.

`SyncApplicationProfilesAsync` receives a complete `ApplicationProfileSync` (revision, capability
cycle and profiles). Each `ApplicationCapabilityProfile` holds a WSGM profile ID, display name,
plain executable filenames and `ApplicationCapabilityValue` overrides. The plugin owns its record of
prior native changes and removes only its own obsolete values. The host sends the set after
start/resume, relevant edits and game start. `ApplicationProfileSyncResult` reports written and
removed counts plus `ApplicationProfileFailure` details; a later sync is a fresh full set, not
permission to repeat an uncertain native write. Empty sets permit removal of prior owned values.

## Steam surfaces and frontend bundles

[PluginSteamUi.cs](../PluginSteamUi.cs) defines `IPluginSteamUi`, `PluginSteamUiContribution` and
`PluginSteamUiPlacement`. Static contributions target the Extensions tab or a selected game's
context menu. A game-menu contribution names a declared numeric argument for that exact Steam AppID.
Optional `SteamUiModules` are typed toolkit modules; optional `SteamPages` declare owned routes. The
host registers modules when ready, removes them on stop and owns shared patch/page surfaces.
`SteamUiChanged` requests fresh publication. Duplicate patches/routes and Valve-route overrides are
refused.

[PluginSteamFrontend.cs](../PluginSteamFrontend.cs) defines the separate, optional
`IPluginSteamFrontend` backend for manifest `frontendModules`. `InvokeFrontendAsync` receives the
admitted module ID, package-defined method and JSON payload; `ReadFrontendState` returns detached
JSON state and `FrontendChanged` requests a fresh publication. Frontend-only packages omit the
interface. Default state/event implementations require no backend state.

Unrestricted JavaScript/CSS bundles require the Steam CEF declaration and user opt-in described in
[the host guide](../../../docs/plugin-system.md#unrestricted-steam-cef-frontends-issue-119). They
are executable frontend code with Steam UI access. The safety properties of declarative
contributions do not turn that separate path into a sandbox. The
[Steam CEF example](../../../examples/SteamCefPlugin/README.md) shows both frontend and backend.

## Version history

| API | Change                                                                                                                    |
| --- | ------------------------------------------------------------------------------------------------------------------------- |
| 1   | Initial common identity, manifest and resident lifecycle boundary.                                                        |
| 2   | Active-time `Deadline` in `PluginContext`.                                                                                |
| 3   | GPU category, adapter rules, capability declarations and capability/profile-sync interfaces.                              |
| 4   | Immutable manifests, tracing on every common host, Device SDK `PlainText`, and shared public SteamUiToolkit type closure. |
| 5   | Shared semantic types moved into the Plugin SDK assembly; Device SDK assembly dependency removed. Rebuild required.       |

The source project generates XML documentation and treats missing public-member/parameter comments
as errors. Test sources cover contract and manifest behavior with fakes; they do not establish live
GPU, IR, Steam or device behavior. Follow the repository's manual-first policy before running them.
