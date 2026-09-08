# WSGM Plugin SDK

The MIT-licensed common contracts for independent WSGM integrations. This is the first #49 slice:
identity, open category strings, host-owned slot policy, strict manifests and resident lifecycle.
It has no Device SDK, UI or Windows Device Control dependency.

The existing Device SDK and device runtime remain operational. A compatibility adapter in WSGM
maps the common lifecycle onto that runtime. Production host integration, configuration/events,
actions/UI contributions, packaging tooling and a real
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
