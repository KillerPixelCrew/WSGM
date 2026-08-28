# Device platform security and trust review

This threat model covers the WSGM 2.0 device platform: package discovery, DeviceHost launch and
IPC, shared state, optional dependencies, Device Lab inputs, Steam CEF patches, glyph assets,
recovery, update, and uninstall. It applies WSGM's accepted same-user/elevation posture from
`docs/decisions.md`; it does not redefine UAC as a product security boundary or treat the deliberate
elevated shell and reviewed first-party plugin as defects.

## Authority map

| Component | Authority | Untrusted input | Must never do |
| --- | --- | --- | --- |
| WSGM | Session/device-cycle policy, semantic commands, WSGM-owned controller and HidHide state | Config, package metadata, host frames, CEF results | Expose a generic raw hardware or plugin command broker |
| DeviceHost | Load one pinned plugin package, bound its lifecycle and IPC | Package assembly and plugin results | Install dependencies, select another package, or outlive its job |
| Reviewed plugin | Exact reviewed operations for one matched device | Firmware/provider/HID responses | Broaden its own identity, ranges, transports, dependencies, or persistence |
| Community/developer plugin | Read-only or explicitly granted semantic operations at its trust tier | Same as above | Inherit reviewed/elevated authority from package contents alone |
| Device Lab | Offline inventory, evidence, generation, validation, packaging; one interactive trial door | Captures, recipes, manifests, evidence, packages | Treat an imported file as write authority or automate mutation |
| Steam UI host | Fixed patch identities and semantic request vocabulary | Loopback CDP target state and fixed-size bridge payloads | Provide general JavaScript, filesystem, device, process, or shell authority |
| Installer | Install/repair verified components and perform bounded cleanup fallback | Existing installation/component state | Let a plugin or runtime IPC request installation or arbitrary recovery |

## Protected assets and failure impact

- **Usable physical input.** A failed handoff must not leave the only controller hidden. WSGM owns
  only its virtual target and its HidHide ledger entries, and deactivation continues through later
  cleanup phases after an earlier timeout.
- **Original hardware state.** Captured state is restoration-only. Desired-state precedence cannot
  overwrite it, and an indeterminate write remains in the recovery journal for exact next-start
  reconciliation.
- **Package identity and code.** Selection pins package ID/version, exact device identity, trust
  tier, entry assembly, signer/grant, file hashes, and contained paths before spawn. Mutable or
  traversing entry paths are rejected before assembly resolution.
- **Control-plane integrity.** Each DeviceHost launch gets a random one-use nonce, current
  user/session pipe ACL, protocol/schema negotiation, bounded frames, correlation/generation IDs,
  and a single authenticated connection.
- **Availability and cleanup.** DeviceHost is assigned to a kill-on-close job. Restart/backoff is
  bounded and quarantine is manual-retry-only. Process shutdown has one outer deadline; timeout
  permits process/job closure and preserves journal evidence.
- **User privacy.** Default logs and inventory omit raw high-rate samples, secrets, serial numbers,
  and user content. Capture/export requires an explicit destination and redaction report; no path
  uploads automatically.

## Boundary review

### Package discovery and loading

`DevicePackagePolicy`, `PluginManifestValidator`, the coordinator's signature verifier, and
`PluginPackageLoader` form one fail-closed chain. Candidate similarity never grants authority. A
package must be in an approved root, remain beneath it after normalization, contain no link/traversal
escape, satisfy schema/API/architecture and exact identity, and pass its trust-tier integrity rules.
DeviceHost resolves managed and native dependencies only from that pinned package; current-directory
and global probing are not package policy.

Residual risk: WSGM-reviewed packages intentionally run with WSGM's integrity and community code is
not sandboxed from malicious same-user behavior. Per-package process and job isolation reduce crash,
dependency, and resource blast radius; they are not a security sandbox.

### Launch, IPC, and shared state

The parent creates the pipe, nonce, shared ring, signal, and job before launch. A valid hello must
match protocol, schema, launch identity, and the nonce in constant time; accepting it consumes the
nonce. Wire framing caps lengths before allocation and unknown/incompatible messages fail the
operation rather than changing protocol state. Device generations reject stale state and commands.
High-rate shared state is advisory observation; lifecycle, command admission, output, and ownership
remain on authenticated control IPC.

No unrelated inheritable handle is part of the contract. The child process receives named endpoints
and a nonce as explicit launch arguments, then enters the parent-owned job. Untrusted trust tiers are
launched through the interactive Explorer token and a missing de-elevation path is a refusal, not an
elevated fallback.

### Hardware and helper authority

The main NativeAOT process consumes only semantic contracts. WMI, WinRT sensors, HID reports,
firmware methods, controller protocols, and raw buffers remain inside the exact plugin. Native radio,
volume, and Steam Input helpers expose fixed ABIs for their existing WSGM-owned operations; they are
not a route for plugin commands. A plugin cannot ask WSGM to open an arbitrary device, invoke WMI,
write a registry/file path, launch a process, or install a component.

Each production write must validate exact device/generation, supported semantic operation, bounds,
ownership/conflict, expected original state, remaining deadline, and journal transition. Imported
captures and recipes can nominate or document a trial but cannot authorize one.

### Device Lab

Offline inputs are hostile. Parsers bound size/shape, normalize deterministically, redact before
export, and keep failed/denied/absent observations distinct. All paths are explicit and the real
`%LOCALAPPDATA%\WSGM` directory, repository root, and broad home paths are forbidden outputs.
`probe run` remains the sole mutation door and requires a locally reviewed trial hash, matching
identity/generation/preflight/original state, an interactive local operator, and the existing
emergency/restore sequence. There is no unattended consent flag.

### Steam CEF, native QAM, and glyph delivery

The CDP transport accepts only loopback Steam-owned targets and generation-tags contexts/documents.
Every patch has a fixed ID/version/target/resource key, positive structural probe, bounds, independent
kill switch, apply/result verification, owned cleanup, and incompatible fallback. The bridge admits
only compiled semantic patch/command schemas with size, replay, context, and action-generation
checks. It exposes no generic evaluation endpoint.

Glyph selectors require an exact approved controller route and resolved handheld subject. Assets are
plugin-owned declarative data validated by WSGM's schema/importer; a plugin cannot provide JavaScript,
CSS selectors, URLs, or runtime filesystem paths. Missing, ambiguous, unverified, or version-mismatched
profiles retain native Steam and generic WSGM presentation.

### Update, rollback, and uninstall

Runtime code may discover and stage policy, but only the installer/component manager may install,
repair, update, or remove drivers, providers, services, tasks, packages, or helpers. Active packages
are not replaced inside a device cycle. Applying or rolling back requires full deactivation and an
atomic verified composition; a partial package never becomes launchable.

Update and uninstall first stop the logon service, then use distinct same-user named events and
bounded application cleanup. The force-stop fallback is installer-owned. Uninstall may remove only
the WSGM virtual target and WSGM-owned HidHide ledger entries after graceful failure; it never invents
hardware restoration without the plugin. External MSI Center, Handheld Companion, HidHide entries,
Steam state, and RTSS profiles remain externally owned.

## Required negative and fault evidence

Automated coverage must retain minimal deterministic cases for malformed/oversized frames, bad and
replayed nonces, stale generations, duplicate commands, out-of-order state, path/link/native-search
escapes, tampered hashes/assets, incompatible schema/API, signer continuity/revocation/downgrade,
ambiguous identities/routes, dependency substitution, host crash/hang/forced kill, phase timeouts,
locked update files, rollback failure, and unresolved journals. Fuzz/property runs are bounded by
time, memory, handles, and output size, and their reduced fixtures contain no live identifiers.

Hardware, driver, shell-takeover, live Steam, and mutation trials remain outside ordinary automated
test authority. Their evidence must name the exact Windows/Steam/firmware/package/build identity,
expected and observed result, cleanup, and retained external-state comparison.

## Review closure

A release review closes this model only when every shipped operation appears in the generated 2.0
traceability report, no high/critical finding is open, lower risks are explicitly accepted or scoped
out, malformed and fault paths are covered, manual gates remain honest, and Device Integration off
leaves every optional host/helper/hook/patch/target absent or inert.
