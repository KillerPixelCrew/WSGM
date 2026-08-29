# Device integration security boundary

This record applies WSGM's accepted same-user/elevation posture from `docs/decisions.md`. Elevated
WSGM, one elevated administrator-installed hardware plugin, native helpers, Steam CEF patching, and
raw input observation are deliberate product mechanisms. The relevant boundary is concrete path,
identity, IPC, ownership, recovery, and cleanup correctness—not an enterprise plugin marketplace.

## Authority map

| Component        | Owns                                                                               | Must not do                                                                                   |
| ---------------- | ---------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------- |
| WSGM             | Single-slot discovery, session policy, semantic commands, controller/HidHide state | Load plugin code from a user-writable root or expose a raw hardware broker                    |
| DeviceHost       | One installed package, one lifecycle, bounded IPC, package-local loading           | Select/install another package, broker arbitrary privileged work, or outlive its job          |
| Installed plugin | Exact-device transports, writes, readback, restoration, diagnostics                | Broaden identity/ranges at runtime, supply UI/Steam code, or manage WSGM controller ownership |
| Device Lab       | Read-only diagnosis and one attended compiled plugin test                          | Treat imported files as mutation authority or offer unattended/bulk hardware writes           |
| Steam UI host    | Fixed WSGM patch IDs and semantic command vocabulary                               | Expose generic JavaScript, filesystem, process, plugin, or device authority                   |
| Installer        | Atomic component/plugin replacement and bounded cleanup fallback                   | Leave two discoverable plugins or let runtime IPC request arbitrary installation              |

## Protected single slot

Package-root cardinality is checked before manifest parsing or privileged startup. Zero packages is
a supported core-only state. One root is the sole installed package. Two or more roots refuse normal
startup and list every absolute path; WSGM never resolves the ambiguity through ranking, trust,
version, signature, preference, enablement, or quarantine policy.

The slot is administrator-protected. A plugin update stages in the fixed `.staging` sibling outside
all discovery paths, parks rollback at `.previous`, and atomically replaces `installed`; the prior
`.installed.previous` name is reconciled during migration. Entry assembly and native dependency
paths are normalized, contained beneath the package root, and loaded only from that root. The
minimal manifest is bounded and identifies code; hardware matching and capability publication remain
executable plugin logic. Elevated maintenance rejects lexical, reparse, and filesystem-identity
aliases before reconciliation, holds every existing source path component against replacement, and
copies each enumerated entry only from a no-follow handle whose identity was revalidated. Every
protected slot, recovery, and staging path has its attributes inspected exactly before a cleanup or
replacement mutation; an access or I/O failure is never collapsed into path absence. Runtime
discovery/host creation and elevated maintenance share the exact `Global\WSGM.DevicePackageSlot`
gate, with the exact global device-owner marker atomically reserved and all DeviceHost processes
rechecked under it before replacement or removal. Setup holds both objects from shutdown through
staging/publication and fails closed when the process snapshot cannot prove every DeviceHost exited.
Uninstall holds them through `[UninstallDelete]`, so another session cannot start a host against
files being removed.

Administrator installation is the consent and authority boundary. The plugin intentionally inherits
WSGM's integrity because the MSI provider and device writes require it. DeviceHost process/job
isolation contains crashes and dependency loading; it is not a sandbox against malicious
administrator-installed code. Runtime trust tiers, publisher grants, signer promotion/revocation,
per-file evidence ledgers, and de-elevated plugin classes do not improve that stated boundary and
are not part of the design.

## IPC, lifecycle, and input safety

The parent creates one pipe, launch token, fixed input ring, signal, and kill-on-close job before
starting DeviceHost. Frames are size-bounded and exact-versioned. Requests carry IDs, cancellation,
and one current cycle generation where stale-action rejection is required. Lifecycle, commands,
output, and ownership remain on authenticated control IPC; the shared ring is advisory high-rate
input only.

There is no generic execute, shell, file, WMI, HID, EC, IOCTL, registry, or process endpoint. The
plugin receives semantic commands and revalidates exact current identity, device availability,
ownership/conflict, range, and sequencing immediately before every write. An uncertain write is
returned to the owning service and is never automatically retried.

Usable physical input is a recovery invariant. Target replacement and source switching establish a
safe replacement before releasing the current source. WSGM removes only its virtual target and its
HidHide ledger delta. Plugin failure cannot justify killing or reconfiguring an external manager.

## Recovery and shutdown

One outer deadline flows from normal/update/logoff/uninstall shutdown into controller release and
plugin restoration. WSGM does not stack per-phase timeout tables. Later WSGM-owned cleanup continues
after an unverified plugin step, the DeviceHost job closes on forced exit, and the compact outcome
is logged as clean, unverified, timed-out, or failed.

Recovery persists only temporary plugin-owned state that was changed and could not be restored. It
does not retain general evidence claims, attempt receipts, or a second host-owned hardware journal.
Persistent desired state is separate, and next-start reconciliation requires the exact device.

Update/uninstall first captures whether the logon service exists and is running, stops it only when
running, requests the correct bounded shutdown, then applies an installer-owned force-stop fallback.
A setup refusal, retry, or pre-mutation cancellation restores only that captured running service
through its installer-tagged start and restores the initially observed shell/settings mode. An
unverified prior DeviceHost suppresses hardware-cycle admission in the restored shell process rather
than risking an overlapping host; the restored process takes and acknowledges a second handle to the
same global owner marker before the installer releases its reservation. The acknowledgement event
keeps the elevated user's default DACL and is relabeled medium/no-write-up before launch, allowing
the restored same-user medium Settings process to signal it without admitting low-integrity callers.
Atomic replacement never makes two packages discoverable. Uninstall removes only WSGM-owned
service/task/target/HidHide/plugin/CEF/configuration state and does not invent hardware restoration
after the plugin is unavailable.

## Device Lab and untrusted data

Captures, manifests, packages, and diagnostic files are untrusted data. Parsers cap size and shape:
manifest length is checked before allocating its buffer, and package traversal has entry, file,
per-file, and aggregate-byte ceilings that are enforced before unbounded sorting or traversal. Entry
assemblies must be AMD64 managed assemblies with readable CLR and assembly metadata. Paths are
explicit. Offline packing holds each validated source file against writes, rename, and deletion and
archives those same handles rather than reopening path names. Exports pass one redaction step that
removes user, machine, network, and account identifiers. Tools never read or write the live
`%LOCALAPPDATA%\WSGM` directory and upload nothing automatically.

The sole mutation door is a locally attended action that invokes compiled plugin-owned
snapshot/readback/restore behavior for the exact current device. It has no imported recipe, trial
hash, authorization snapshot, evidence grade, remembered consent, `--yes`, bulk, or CI route. A
production owner already holding the hardware causes refusal.

The attended run atomically reserves WSGM's exact machine-wide owner object before plugin loading
and keeps the unowned handle through plugin cleanup and disposal. This makes production startup and
the developer run mutually exclusive instead of relying on a stale owner observation. Normal
production startup also snapshots all DeviceHost processes after creating that marker and fails
closed, releasing its new marker, when an earlier host is present or process enumeration is
unverified.

## Steam and glyph isolation

The CDP transport accepts only loopback Steam-owned targets and generation-tags contexts/documents.
Built-in patches have fixed IDs, bounded semantic payloads, positive probes, independent removal,
and owned namespaces. The bridge exposes no generic evaluation endpoint and patch failure retains
Valve's native UI.

Glyphs are static plugin data. WSGM validates local paths, known IDs, format, dimensions, size, and
references, then owns Steam selectors, CDP expressions, cleanup, and Avalonia rendering. A plugin
cannot provide JavaScript, CSS selectors, URLs, or runtime filesystem paths. Missing, ambiguous, or
mismatched profiles keep native Steam/generic WSGM presentation.

## Validation boundary

Automated coverage focuses on malformed/bounded frames, exact-version mismatch, stale cycle action,
package cardinality and path escape, host crash/hang, controller cleanup, atomic replacement,
unresolved restoration, and native Steam fallback. Hardware writes, shell takeover, live Steam, and
attended Device Lab tests remain outside unattended automation and must record exact build/device,
observed result, cleanup, and external-state preservation.
