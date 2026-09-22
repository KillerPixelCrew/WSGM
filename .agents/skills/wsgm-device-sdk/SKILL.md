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

## Know which contract you are in

- `src/WSGM.Device.Sdk` (MIT, `DeviceApi.Version`) is the device hardware contract: `IDevicePlugin`,
  `IPluginHostAdapter`, capabilities, controller, haptics, OEM controls, glyphs and `PluginTrace`.
- `src/WSGM.Plugin.Sdk` (MIT, `PluginApi.Version`) is the common plugin contract used by independent
  plugins such as `src/WSGM.Plugin.Ir`. The Shell's common `PluginHost` admits the Device runtime
  through `DevicePluginCompatibilityAdapter`. See `docs/plugin-system.md`.
- Device plugins declare preferences with a settings manifest
  (`IPluginHostAdapter.PublishSettingsManifestAsync`) and receive the complete set through
  `ApplySettingsAsync`. `IConfigurablePlugin`, host-owned revisions and `IPluginHost.PublishState`
  belong to common plugins only. Never persist an observation or initialization fallback as a user
  edit in either model.

## Know the device projects

| Project                                | State                                                                                                                                           |
| -------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------- |
| `src/WSGM.Device.Msi.Claw8A2Vm`        | The only working plugin and the reference implementation. Read its `AGENTS.md` before changing it.                                              |
| `src/WSGM.Device.Asus.RogAllyX`        | Passive API scaffold whose detection never matches. `AllyXProtocol.cs` and the ATKACPI reader are unwired reference code. HHD is primary.       |
| `src/WSGM.Device.HandheldCompanion`    | Design scaffold with no entry type, so it cannot be installed. Its named-pipe `docs/ipc-protocol.md` is a proposal, not retired DeviceHost IPC. |
| `src/WSGM.DeviceLab`, `tools/AllyXLab` | Evidence tools. Use `wsgm-device-lab`.                                                                                                          |

Only one device package can be installed at a time. With Device Integration off, no Device plugin
lifecycle, controller target, Device hardware write or AutoTDP runs, while core, common plugins and
Windows power schemes keep working. `docs/device-projects.md` has the topology.

## Establish the boundary first

1. Resolve the WSGM root with `git rev-parse --show-toplevel`, read every applicable `AGENTS.md`
   (the SDK, Claw and Device Lab each have one), and inspect `git status --short --branch` plus
   `git submodule status --recursive`.
2. Read `src/WSGM.Device.Sdk/docs/reference.md` and verify the compatibility integer in
   `src/WSGM.Device.Sdk/DeviceApi.cs`. The pinning test is
   `tests/WSGM.Device.Sdk.Tests/Boundaries/ContractBoundaryTests.cs`. The source and that test win
   over copied examples, including the numbers in this skill.
3. Read [references/contract-and-ownership.md](references/contract-and-ownership.md) before adding a
   type or changing lifecycle behavior.
4. For plugin authoring, packaging, deployment or an API change, read
   [references/authoring-and-packaging.md](references/authoring-and-packaging.md) and
   `docs/device-plugin-authoring.md`.
5. For host behavior, logs, package admission, or a broken plugin, read
   [references/host-and-debugging.md](references/host-and-debugging.md) and
   `docs/device-plugin-system.md`.

The user's task controls scope. A diagnosis does not authorize plugin installation, `dev-deploy`,
Device Lab capture, a hardware action, controller/HidHide changes, or running WSGM as the shell.

## Keep ownership exact

- The SDK owns zero-dependency semantic records, validation helpers (`PlainText`, the `TryValidate`
  family), bounded static glyph import, the diagnostics facade, and the test adapter.
- A plugin owns exact device detection, direct transports, firmware gates, device-specific codecs,
  readback, rollback, restoration, physical-controller acquisition, OEM sources, and static glyph
  data.
- WSGM owns the installed Device slot, loading and deadlines, lifecycle orchestration, generations,
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
- Publish descriptors (with their sections, categories and layout hints), physical devices, OEM
  controls and the settings manifest as complete replacements. Publish full controller samples,
  never deltas or synthesized controls.
- Revalidate identity, firmware, bounds, generations, and current state immediately before every
  hardware command. `AppliedVerified` requires independent readback; uncertain writes are never
  retried automatically.
- A caller timeout can return immediately while the plugin task continues. The host accepts a late
  result only for the same runtime, command ID, and generations; plugin code must not launch a
  second write or treat cancellation as proof that the first did nothing.
- Keep controller release ordered: WSGM neutralizes; the plugin stops acquisition, restores the
  original mode, and verifies topology by physical location; WSGM then removes its target and only
  its own HidHide entries.
- WSGM drops haptic frames for a stale target generation before delivery. The plugin clamps to its
  declared channels (`HapticCapabilities.Clamp`), drops unsupported channels without redistribution,
  and always has an explicit zero path.
- Trace decisions and transitions, not samples. Use `PluginTrace.Change` for a polled value and keep
  correctness evidence at Info/Warn/Error rather than Debug alone.

## Change the smallest owning surface

Prefer an existing semantic role, value type, reason code, lifecycle method, or closed vocabulary. A
one-device need is not a public abstraction: require both the Claw reference plugin and a materially
different future plugin before generalizing it into the SDK. Do not restore deleted IPC, pipe,
ring-buffer, wire-message, authoring-helper, capability-registry, or generic resource-coordinator
layers.

For a public contract change, update XML documentation, the consolidated SDK reference, SDK tests,
host consumers, Device Lab, and every device project together in one WSGM pull request. All
first-party device projects reference the same SDK source.

## Finish with evidence

Build first and hand the change to the maintainer for manual testing. Write TestKit and host tests
alongside the change, and run them after the maintainer reports the manual test, following the root
validation policy. The test adapter records publications and intentionally does not reproduce the
host/router rules, so add a production host test when acceptance matters. Separate hardware-free
proof from the attended device scenarios that remain unverified.
