# Device runtime boundaries

This is the one-page boundary checklist for device integration: who owns what, which package and
runtime boundaries exist, how imported data is treated, and where automated verification stops. The
decisions and device findings are in `device-integration.md`; the mechanism, with its budgets and
checks, is in `device-plugin-system.md`; package construction and installation are in
`device-plugin-authoring.md`.

## Ownership

- The installed plugin owns exact-device detection, device transports, hardware writes and readback,
  restoration, physical-controller acquisition and device diagnostics.
- WSGM owns session policy, the virtual controller, its own HidHide changes, Steam integration and
  UI.
- Device Lab owns offline diagnosis and one explicitly attended hardware action.

An installed plugin is administrator-selected hardware code running inside WSGM with WSGM's
authority. The collectible load context resolves package-local dependencies and supports unload
after verified cleanup; it does not contain process-fatal plugin failures. Plugins publish through
the semantic SDK and do not supply WSGM UI, Steam patches, URLs or generic shell commands.

## Package boundary

- Production accepts zero or one installed package. More than one package root refuses normal
  startup before plugin code runs.
- Replacement uses the fixed `.staging` and `.previous` siblings while the package-slot mutex is
  held. Path containment, reparse-point, file-identity, manifest, entry-count and byte-size checks
  make the transaction deterministic (`device-plugin-system.md` §5 and §6).
- Package-local dependencies are resolved host-first; the SDK and the WinRT runtime pair always come
  from the host (`device-integration.md`, "Host-first dependency resolution").

## Runtime boundary

- `Global\WSGM.DeviceOwner` serializes the production runtime, package maintenance, setup and
  uninstall, and attended Device Lab hardware access. The package-slot mutex separately keeps
  loading and replacement from observing a half-published package.
- Lifecycle calls, capability commands, state publication, controller samples, haptics, OEM events
  and fault publication are direct in-process calls. Cycle and descriptor generations reject stale
  commands and publications.
- Shutdown closes admission, drains in-flight work, releases controller ownership, stops and
  disposes the plugin, then unloads the context only when cleanup is verified. WSGM still removes
  its own virtual target and HidHide changes after a plugin timeout or failure.
- HidHide changes are recorded in a ledger and reversed newest-first. The two device findings that
  shaped that ledger are in `device-integration.md`, "HidHide findings".

## Imported data and Device Lab

Capture, manifest, package and request parsers accept external files, so they bound sizes and shapes
and require explicit output paths. Shareable capture output is redacted, and the tools never use
live `%LOCALAPPDATA%\WSGM` state. Offline commands do not load plugin code.

The only Device Lab mutation path is a locally attended plugin action. It has no `--yes`, bulk, CI,
remembered-consent or imported-operation route. It validates the package and the live machine,
reserves the production owner object, and retains that reservation through plugin cleanup and
disposal.

## Verification boundary

Automated tests cover package cardinality and containment, lifecycle ordering, stale-generation
rejection, controller cleanup, atomic replacement and unresolved restoration with isolated fakes.
Hardware writes, live shell takeover, live Steam and attended Device Lab work remain device
verification and must record the exact build, device, observed result and cleanup.
