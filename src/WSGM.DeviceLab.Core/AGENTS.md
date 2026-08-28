# WSGM.DeviceLab.Core

The Device Lab engine: preflight, inventory, candidate matching, passive capture, probe
orchestration, evidence assessment, scaffold generation, and package validation.

Both the GUI and the `wsgm-device` CLI are thin shells over this assembly, so a workflow cannot exist
in one surface and be missing from the other. Put behavior here, not in a surface.

- **Read-only is the default and mutation is one named door.** Everything in this engine —
  `inventory`, `candidates`, `capture run`, `inspect`, `diff`, `correlate`, `scaffold`, fixture
  extraction, offline validation, packing — is incapable of touching hardware. The single exception
  is the `probe run` trial path, and it accepts only a locally installed, WSGM-reviewed trial ID plus
  its pinned hash.
- **Imported files are never authority.** A capture, recipe, manifest, plugin package, evidence lock,
  or acceptance manifest can describe a mutation but can never authorize or supply one. If a code
  path can be reached by a file the user was sent, it must not write to hardware.
- Never disable Device Integration, race the production plugin, or treat a process/resource-name
  match as ownership. When the production host owns a resource, ask it for a bounded read-only
  diagnostic session; a direct trial requires the operator, a distinct experiment lease, and an
  orderly per-resource release first.
- **Similarity nominates, evidence authorizes.** Reuse rank, evidence grade, and write eligibility
  are three independent values and one must never be derived from another. A top-ranked candidate may
  legitimately stay read-only.
- Evaluate hard constraints before scoring, and explain every rejection. Wrong report length,
  excluded firmware, absent required WMI method, mismatched descriptor hash, or a missing endpoint
  rejects a module outright rather than lowering its score.
- Output is deterministic: same inventory in, same candidates and same generated project out,
  regardless of enumeration order.
- Every output path is explicit. Reject the live `%LOCALAPPDATA%\WSGM` directory, the repository
  root, and broad home paths (`eng\check-no-live-data-paths.ps1` enforces this at build time).
- Label platform limits rather than papering over them: user-mode HID cannot see another process's
  writes, WMI Activity does not promise arguments, a low-level hook cannot identify a device, and
  timing correlation yields a candidate, never proof of causality.
