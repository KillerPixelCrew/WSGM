---
name: wsgm-device-sdk
description:
  Implement, review, or diagnose WSGM Device SDK contracts, device plugins, and the WSGM plugin
  host, including lifecycle, capabilities, publications, commands, generations, settings, glyphs,
  packaging, controller handoff, and API compatibility. Use for contract or plugin-runtime work; use
  wsgm-device-lab when the primary task is discovering behavior on real hardware.
---

# WSGM Device SDK

Use the current semantic plugin contract without reviving the retired DeviceHost/IPC architecture or
moving machine policy into the SDK.

## Establish the boundary first

1. Resolve the WSGM root with `git rev-parse --show-toplevel`, read every applicable `AGENTS.md`,
   and inspect `git status --short --branch` plus `git submodule status --recursive`.
2. Read `external/WSGM.Device.Sdk/docs/reference.md` and verify the compatibility integer in
   `external/WSGM.Device.Sdk/src/WSGM.Device.Sdk/DeviceApi.cs`. It is currently API 3, but source
   and its pinning test win over copied examples.
3. Read [references/contract-and-ownership.md](references/contract-and-ownership.md) before adding a
   type or changing lifecycle behavior.
4. For plugin authoring, packaging, or an API change, read
   [references/authoring-and-packaging.md](references/authoring-and-packaging.md).
5. For host behavior, logs, package admission, or a broken plugin, read
   [references/host-and-debugging.md](references/host-and-debugging.md).

The user's task controls scope. A diagnosis does not authorize plugin installation, Device Lab
capture, a hardware action, controller/HidHide changes, or running WSGM as the shell.

## Keep ownership exact

- The SDK owns zero-dependency semantic records, validation helpers, bounded static glyph import,
  the diagnostics facade, and the test adapter.
- A plugin owns exact device detection, direct transports, firmware gates, device-specific codecs,
  readback, rollback, restoration, physical-controller acquisition, OEM sources, and static glyph
  data.
- WSGM owns the one installed slot, loading and deadlines, lifecycle orchestration, generations,
  publication validation, desired state and profiles, UI/localization, virtual targets, Steam Input,
  HidHide, AutoTDP, and OEM action policy.
- Device Lab owns inventory, evidence capture, compiled read probes, scaffolding, offline package
  checks, and the single attended hardware-test door.

A plugin is loaded in-process with WSGM's authority. The collectible load context isolates package
dependencies; it is not a security or crash boundary. Never describe validation as sandboxing.

## Preserve the protocol invariants

- `DetectAsync` is exact and side-effect free. Install `PluginTrace` first in `StartAsync`, then
  acquire and publish. Honor every cancellation token and unwind partial acquisition.
- Treat cycle and descriptor generations as separate authorities. Descriptor generation is strictly
  increasing inside one cycle and may restart after the host advances the cycle. Republish
  descriptors before state after resume or controller reacquisition; reject stale commands with
  `GenerationChanged`.
- Publish descriptors, physical devices, and OEM controls as complete replacements. Publish full
  controller samples, never deltas or synthesized controls.
- Revalidate identity, firmware, bounds, generations, and current state immediately before every
  hardware command. `AppliedVerified` requires independent readback; uncertain writes are never
  retried automatically.
- A caller timeout can return immediately while the plugin task continues. The host accepts a late
  result only for the same runtime, command ID, and generations; plugin code must not launch a
  second write or treat cancellation as proof that the first did nothing.
- Keep controller release ordered: WSGM neutralizes; the plugin stops acquisition, restores the
  original mode, and verifies topology by physical location; WSGM then removes its target and only
  its own HidHide entries.
- Drop stale haptic target generations and unsupported channels. Provide an explicit zero-output
  path; never redistribute an unsupported motor channel.
- Trace decisions and transitions, not samples. Use `PluginTrace.Change` for a polled value and keep
  correctness evidence at Info/Warn/Error rather than Debug alone.

## Change the smallest owning surface

Prefer an existing semantic role, value type, reason code, lifecycle method, or closed vocabulary. A
one-device need is not a public abstraction: require both the Claw reference plugin and a materially
different future plugin before generalizing it into the SDK. Do not restore deleted IPC, pipe,
ring-buffer, wire-message, authoring-helper, capability-registry, or generic resource-coordinator
layers.

For a public contract change, update XML documentation, the consolidated SDK reference, SDK tests,
host consumers, Device Lab, and real plugin consumers. Commit and push leaf first: SDK, Device Lab,
device packages, then WSGM gitlinks. Never leave a parent pointing to an unpublished child commit.

## Finish with evidence

Use the SDK TestKit for deterministic plugin behavior, but also exercise production host validation:
the test adapter records publications and intentionally does not reproduce the host/router rules.
Run focused SDK, plugin, Device Lab, and WSGM tests for the paths changed, then run `eng/verify.ps1`
from the WSGM root. Separate hardware-free proof from the attended device scenarios that remain
unverified.
