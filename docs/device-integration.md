# Device integration

Device Integration is an optional, process-long WSGM subsystem. It is independent from Steam and
from Desktop/Game Mode transitions. Turning it off leaves the existing shell, overlay, Steam Input
lease, storage, artwork, and launch features unchanged.

## Runtime topology

`ShellSession` creates at most one `DeviceCoordinator` for the current interactive session. A named
owner mutex prevents a second WSGM process from starting another device cycle. The coordinator
collects a normalized machine identity, discovers offline packages, selects at most one eligible
exact match, and launches one isolated `WSGM.DeviceHost.exe` from the installed `DeviceHost`
component directory.

The control plane is an ACL-restricted named pipe authenticated with a random launch nonce and a
protocol/schema handshake. Every request is bounded, generation-tagged, and semantically typed.
Canonical controller samples use a bounded shared-memory ring plus a named event; the pipe remains
the authoritative lifecycle, capability, command, and output channel.

DeviceHost is placed in a kill-on-close job with CPU, working-set, process-count, and handle
supervision. A reviewed first-party package inherits WSGM's existing integrity because the MSI WMI
provider requires it. Community and developer packages are launched through the interactive
Explorer token and therefore cannot use privilege-dependent capabilities. Process isolation limits
crash and dependency blast radius; it is not a sandbox for malicious same-user code.

## Package and trust rules

Runtime discovery reads only the administrator and current-user package roots defined by
`DevicePackagePolicy`. A package must pass manifest/schema/API, exact identity, path containment,
file-hash, signature/trust, and entry-point checks before it is eligible. Developer packages are
inspection-only and never become production write authority merely because Developer Mode is on.

The installer keeps the reviewed host and reviewed package roots under `%ProgramFiles%\WSGM`.
User-writable copies beside the per-user NativeAOT application are never candidates for the
`WSGM-reviewed` tier. A signed release grant pins the entry assembly's Authenticode subject,
thumbprint, and every package-file hash. Unsigned local installer builds leave the bundled grant
disabled, so packaging can be exercised without silently granting reviewed runtime trust.

An active package is pinned by exact ID and version for the current device identity. Updates are
verified and staged offline. Applying a staged version or rolling back performs full deactivation
first and starts a new device cycle; a package is never swapped inside a live host.

Plugins may publish only the frozen semantic contracts. They cannot supply XAML, JavaScript, URLs,
arbitrary commands, or a raw hardware broker. All WMI, HID, sensor, lighting, firmware, controller,
and recovery implementation remains inside the selected plugin process.

## Lifecycle and recovery

The coordinator owns one cycle across Steam restarts, games, and shell-mode transitions. Normal
terminal triggers are WSGM shutdown and the Device Integration master toggle. Suspend/lock quiesces
the host; resume recollects identity and advances the device generation before accepting state or
commands again.

Unexpected host exits stay inside the same logical cycle. Supervision uses bounded restart/backoff
and then quarantine. Quarantine retains desired state, exposes an explicit cooldown-bound retry, and
does not silently hand hardware to or interfere with MSI Center, Handheld Companion, or another
manager.

DeviceHost writes its recovery journal before and after ownership-changing operations. Startup and
activation receive unresolved entries for exact identity/generation reconciliation. Persistent or
indeterminate operations are never blindly retried. If restoration cannot be verified, the journal
is retained and the affected resource stays visibly degraded or quarantined.

## UI ownership

Settings owns only subsystem policy: Device Integration, the controller-management child switch,
package/update trust policy, target preference, glyph selection, and diagnostic level. It never
contains live TDP, fan, lighting, motion, controller-test, or OEM controls.

The overlay's Device destination is built from WSGM-owned semantic descriptors and state. It shows
desired, pending, observed/readback quality, persistence, and bounded failure information without
accepting plugin-supplied presentation. `--overlay-test` uses an in-memory source and starts no
package discovery, DeviceHost, IPC, hook, driver, or device handle.

## Safe diagnostics

Production logs contain stable lifecycle, package, generation, capability, resource, and bounded
failure fields. They do not include raw high-rate samples, secrets, serial numbers, or plugin raw
buffers. `wsgm-device doctor`, `inventory`, `candidates`, offline inspection, and offline package
validation are read-only. Live capture, hardware validation, plugin activation, and the single
mutation-trial path always require an interactive maintainer and must not be automated.
