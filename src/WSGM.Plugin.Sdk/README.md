# WSGM Plugin SDK

The MIT-licensed common contracts for independent WSGM integrations: identity, open category strings,
host-owned slot policy, strict manifests, resident lifecycle, configuration and state publications.
It has no Device SDK, UI or Windows Device Control dependency.

The existing Device SDK and device runtime remain operational. A compatibility adapter in WSGM
maps the common lifecycle onto that runtime through the resident Shell host. Configuration and state
events use separate revision/origin contracts. Named actions and declarative UI links are validated
by the host. External loading, packaging tooling and a real
non-device consumer follow sequentially; this assembly alone does not claim a completed plugin host.

Device is the selected `wsgm.device` category with zero or one active instance. Other categories are
open strings, and the host decides multiplicity. A desktop with no Device Plugin remains valid.
Manifest permissions declare requirements; they do not grant privileges or sandbox in-process code.

`IPlugin` starts with a host-owned instance/generation context, receives Desktop/Game and suspend/resume transitions,
then stops and disposes. Timeout is not proof that work stopped. A failed stop must not be reported
as released, and publications from retired generations must be discarded by the host.

`PluginManifestReader` accepts bounded camel-case JSON, rejects unknown members, checks common API
compatibility and numeric dependency ranges, and admits only a DLL filename at the package root.
Actual dependency resolution, trust, filesystem containment and code loading belong to the host.

`IConfigurablePlugin` declares preferences and confirms host-owned requested revisions. WSGM saves
only explicit edits before dispatch; initialization defaults and failed delivery cannot replace them.
`IPluginHost.PublishState` publishes effective observations with generation, sequence and origin.
It never changes saved preferences. Boolean, finite numeric and bounded text primitives are shared
through `PluginValue`; schema validation is available in `PluginConfigurationRules`.

`IPluginActions` declares named operations for UI and Core automation. Results distinguish dispatched
commands, independently verified effects, rejection and uncertain outcomes. `IPluginUi` links bounded
host-rendered control descriptions to those actions and effective state keys. No plugin UI code is
injected, and action results cannot mutate saved preferences.
