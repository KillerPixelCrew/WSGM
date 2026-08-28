# WSGM 2.0 delivery tracker

Status: implementation paused at the 2026-08-28 safety checkpoint
Branch: `2.0`
Latest checkpoint commit: `448b2e0` (`Document 2.0 stop checkpoint`)

This is the operational plan. It tracks product outcomes and the next work queue, not every method,
test case, failure mode, or manual matrix cell.

The exhaustive `INV-*` and `P0-*` through `P10-*` specification is preserved in
[`implementation-requirements.md`](./implementation-requirements.md). It remains the requirement-ID
source for design decisions and generated traceability, but it is no longer a progress counter.

## Reading status

Source and validation are deliberately separate:

- **Done** — committed implementation, focused tests, and required documentation exist.
- **Mostly** — the main production path exists; bounded implementation gaps remain.
- **Partial** — useful foundations exist, but a major path is still absent or fail-closed.
- **Not started** — no production implementation exists.
- **Validated** — the applicable automated/live/hardware acceptance has passed.
- **Partial evidence** — useful evidence exists but not the complete acceptance matrix.
- **Not run** — do not infer runtime behavior from source presence.
- **Attended** — requires the maintainer at the reference unit; never run unattended.

Milestones are not equal-sized, so their row count is not a completion percentage. At this
checkpoint the best engineering estimate is **55–60% source implementation** and **25–30% release
readiness**. Those are estimates, not arithmetic derived from this table.

## Current outcome map

### Foundations and Device Lab

| ID | Outcome | Source | Validation | Real remaining work |
| --- | --- | --- | --- | --- |
| M01 | Product architecture, trust model, and 2.0 decisions | Done | Validated at design level | Shipment-specific notices remain ordinary packaging evidence, not architecture blockers |
| M02 | Repository, NativeAOT, module, build, and agent boundaries | Done | Automated checks exist | Final clean-checkout release proof belongs to M34/M38 |
| M03 | Semantic contracts and authenticated IPC | Done | Focused tests exist | Protocol 1 and `wsgm-device-v1` remain frozen |
| M04 | Offline inventory, capture, matching, evidence, redaction, and deterministic fixtures | Done | Focused tests exist | No core work remains; product workflow acceptance is M08 |
| M05 | SDK, scaffold, package, and plugin-owned glyph authoring | Mostly | Partial evidence | Finish the packaged analyzer/generator and explicit regeneration-review workflow |
| M06 | Reviewed read probes and bounded mutation framework | Mostly | Not run on hardware | Finish reviewed-trial registry/hash admission, five concrete trials, and Developer Mode validation |
| M07 | Device Lab CLI and guided Hardware Owner/Plugin Developer GUI flows | Partial | Not run end to end | Finish guided workflows, analysis workbench, live validation, and recovery/privacy review |
| M08 | Device Lab end-to-end product acceptance | Not started | Not run | Safe sweep, fault, privacy/export, runtime separation, scaffold, packaging, and contributor acceptance |

### Device runtime and MSI Claw package

| ID | Outcome | Source | Validation | Real remaining work |
| --- | --- | --- | --- | --- |
| M09 | Exact A2VM definition and first-party package | Mostly | Partial evidence | Finish evidence-locked firmware/endpoint identity and resume/hotplug proof |
| M10 | A2VM power, fan, telemetry, scenario, and RGB | Mostly | Attended | Close fail-closed bounds/readback/rollback gaps; run bounded writes, thermal/fan/RGB mapping, restoration, and soaks |
| M11 | A2VM physical input, motion, rumble, OEM events, and chord suppression | Partial | Attended | Finish XInput fallback/container binding and edge/hotplug logic; run report, gyro, rumble, mode, and Win+G matrices |
| M12 | A2VM evidence and release dossier | Partial | Not accepted | Populate sanitized claims/golden fixtures, close exact unknowns, prove restoration, and disable unsupported firmware/capabilities |
| M13 | Device runtime, package discovery/trust, host supervision, and authority | Mostly | Partial evidence | Finish multi-process/stale-owner, package update/rollback, crash/hang, user-switch, and same-session cycle tests |
| M14 | Capability state, profiles, lifecycle, recovery journal, and deactivation | Mostly | Partial evidence | Finish startup reconciliation and bounded handoff semantics; run timeout/fault/hardware-restoration matrix |
| M15 | Device policy UI, OEM action routing, diagnostics, and generic-device conformance | Partial | Not run end to end | Finish diagnostics/recovery UI, same-run re-enable, and a materially different synthetic plugin |

### Controller integration

| ID | Outcome | Source | Validation | Real remaining work |
| --- | --- | --- | --- | --- |
| M16 | Reproducible production virtual-controller backend and dependencies | Partial, fail-closed | Not run | Finish one technically acceptable backend and its installer/update/removal lifecycle; licensing is not a blocker |
| M17 | Managed target, HidHide ownership, translation, output, and handoff | Partial | Not run on hardware | Integrate the real backend/source; finish target replacement, output, fault injection, duplicate-input, and restoration matrices |
| M18 | Managed UI capture with SDL and Steam Input lease fallback | Not started | Not run | Implement one `IUiGamepadSource`, reference-counted capture owner, make-before-break source arbiter, and failure tests |

### Steam UI, performance, and AutoTDP

| ID | Outcome | Source | Validation | Real remaining work |
| --- | --- | --- | --- | --- |
| M19 | Persistent Steam CDP transport, bridge, and patch registry | Mostly | Partial live evidence | Migrate remaining one-shot loops and validate malformed/reconnect/startup/context-churn/coexistence paths |
| M20 | Native QAM components, shared semantic controls, and OEM2 toggle | Mostly | Partial live evidence | Complete component/error matrix; validate focus, navigation, latency, one-press/one-toggle, and supported Steam builds |
| M21 | Deterministic TypeScript/React Steam asset pipeline | Seed only | Not run | Add locked dependencies, generator/minifier, manifest/hash/revision, schema/security checks, drift gate, and verification wiring |
| M22 | RTSS frame-limit and overlay controls | Mostly | Read-only evidence | Validate a disposable profile, app mapping, external edits, restart, rollback, readback truth, and native-QAM binding |
| M23 | Shared RTSS/plugin performance metrics and frametime stream | Not started | Not run | Implement typed metrics, source precedence, bounded history/freshness, consumer-aware cadence, and shared overlay/QAM projection |
| M24 | Frametime-driven AutoTDP | Design only | Not run | After M23: implement deterministic policy/model, arbiter/lease, persistence, diagnostics, replay, then attended calibration/soaks |

### Physical glyphs and Steam delivery

| ID | Outcome | Source | Validation | Real remaining work |
| --- | --- | --- | --- | --- |
| M25 | Validated plugin-owned glyph packages and deterministic import | Mostly | Focused tests exist | Accept a real A2VM profile/assets with exact-device visual review and retained provenance |
| M26 | Physical glyph runtime, fallback, diagnostics, and selection UI | Partial | Not run end to end | Review/commit current selection work; add graphical preview, accessibility/scaling coverage, and complete fallback diagnostics |
| M27 | Steam handheld-route targeting and independent glyph-tier framework | Partial, fail-closed | Narrow live evidence | Review/commit selectors/tiers; preserve independent health/kill switches and native fallback |
| M28 | Active Steam glyph delivery, cleanup, and CSS Loader coexistence | Not started | Not run | Add accepted assets, bounded context-local delivery, exact inline/hiding maps, cleanup/leak proof, coexistence, and full live matrix |

### Final overlay and Settings

| ID | Outcome | Source | Validation | Real remaining work |
| --- | --- | --- | --- | --- |
| M29 | Final Home/Steam/Device/System navigation shell | Partial | Focused foundation tests | Finish information architecture, Back/focus/scroll restoration, dynamic visibility, and nested navigation |
| M30 | Home, Steam, and System destination parity | Partial | Not run end to end | Migrate existing actions without reimplementation and close cancellation/error/focus parity |
| M31 | Complete Device destination | Partial | Not run end to end | Finish overview, profiles, thermals, controller/motion, OEM, lighting, diagnostics, recovery, and glyph preview |
| M32 | Settings ownership-only UI and cross-process behavior | Partial | Not run end to end | Finish standalone connection, ownership transactions, conflicts, and deactivation/fallback results |
| M33 | Handheld overlay acceptance | Not started | Not run | Controller/touch/keyboard, DPI/themes, accessibility, responsiveness, disposal, and complete parity matrix |

### Integration and release

| ID | Outcome | Source | Validation | Real remaining work |
| --- | --- | --- | --- | --- |
| M34 | Deterministic traceability, governance, build graph, and component staging | Mostly | Automated foundations exist | Integrate M21; prove clean-checkout/reproducible outputs and complete component/dependency health paths |
| M35 | Graceful exit, update, rollback, downgrade, and uninstall | Partial, uncommitted | Not compiled | Finish result-channel docs/semantics, compile/Inno validation, atomic lifecycle, owned-state removal, and hardware restoration |
| M36 | Automated compatibility, fault, and security coverage | Mostly | Broad focused suites exist | Add consolidated fault scheduling, remaining malformed/fuzz/property/process cases, then run full verification |
| M37 | Live Steam, hardware, coexistence, performance, and soak acceptance | Mostly open | Partial evidence only | Run the approved current-Steam matrix and attended A2VM/controller/lifecycle/resource/performance soaks |
| M38 | Release documentation and artifact handoff | Not started | Not run | Finish user/developer docs and notices; run verification/build; validate installer; copy newest installer to `Z:\` and hash it |

## Immediate work queue

This is the only day-to-day checkbox list. Keep it short; add a new item only when another leaves.

- [ ] **Q01 · Stabilize the current worktree.** Review and commit the shutdown/result slice, physical
      glyph selection, Steam route/tier patches, and TypeScript seed as separate coherent commits;
      preserve the unrelated `native/SteamInput/crates/steam-input-recovery/src/lib.rs` edit.
- [ ] **Q02 · Finish M21.** Make the Steam TypeScript source/bundle pipeline reproducible and
      verification-enforced before further injected UI growth.
- [ ] **Q03 · Finish M23.** Add the verified shared performance metric/frametime service and bind
      overlay/QAM consumers to it.
- [ ] **Q04 · Implement M24 source and replay.** Build pure AutoTDP policy, calibration/model,
      arbitration, persistence, diagnostics, and replay without enabling unattended hardware writes.
- [ ] **Q05 · Close Device Lab product gaps.** Finish M05–M08: generator/analyzer, reviewed trials,
      guided workflows, regeneration review, privacy/export, and end-to-end acceptance.
- [ ] **Q06 · Finish controller integration.** Complete M16–M18 with a real backend, exact HidHide
      ownership/handoff, output, managed UI capture, and fallback.
- [ ] **Q07 · Accept one real A2VM glyph package and finish Steam delivery.** Complete M25–M28 while
      keeping every unproven tier disabled and native Valve rendering intact.
- [ ] **Q08 · Finish the final overlay and Settings.** Complete M29–M33 by migrating existing
      behavior rather than rebuilding already-working services.
- [ ] **Q09 · Close runtime and A2VM source gaps.** Finish M09–M15 with deterministic fixtures and
      fault injection before attended validation.
- [ ] **Q10 · Run live and attended acceptance.** Execute M37 only with the approved safe modes,
      explicit maintainer presence for mutation, exact restoration, and recorded evidence.
- [ ] **Q11 · Finish release integration.** Complete M34–M36, including installer lifecycle,
      security/fault closure, clean-checkout staging, rollback, downgrade, and uninstall.
- [ ] **Q12 · Produce the handoff artifact.** Complete M38 with `eng/verify.ps1 -Fix`, `build.ps1`,
      newest-installer copy to `Z:\`, and matching SHA-256 hashes.

## Non-negotiable release gates

These are constraints, not hundreds of separate progress boxes:

1. Device Integration off must leave normal WSGM, Steam, overlay, and desktop behavior usable.
2. Hardware writes require exact device/firmware identity, bounded values, readback where available,
   rollback/restoration, and attended acceptance. Unknown identity fails closed.
3. Controller failure must return to usable physical or established fallback input without leaving
   foreign HidHide state or duplicate input.
4. Steam CEF/QAM/glyph failures must remain isolated and fail open to native Valve UI.
5. Plugin packages own hardware protocol and artwork; WSGM consumes only validated semantic data.
6. No runtime downloader, generic hardware broker, arbitrary plugin code/UI/command authority, or
   silent dependency repair is introduced.
7. Optional capabilities and conditional experiments do not block the base release when omitted
   honestly; required shipped notices/provenance remain packaging evidence.
8. Final release requires the automated suite, NativeAOT publish, installer lifecycle, live Steam
   matrix, attended reference-device restoration matrix, and verified `Z:\` artifact handoff.

## Current uncommitted checkpoint

Do not lose or accidentally combine these slices:

- Bounded shutdown/update/uninstall result handoff and session-logoff ownership (`M35`), missing final
  result-channel documentation and compile/Inno validation.
- Physical glyph selection plus Steam route/tier patches (`M26`–`M27`), source-complete only at a
  fail-closed checkpoint; no production delivery tier is enabled.
- `SteamUiAssets/Source/NativeQamBootstrap.ts` (`M21`), a source seed only; the embedded JavaScript
  still matches its existing pinned hash.
- User-owned `native/SteamInput/crates/steam-input-recovery/src/lib.rs`, unrelated to these slices.

No full `eng/verify.ps1 -Fix`, `build.ps1`, installer copy, or final live/hardware matrix has run
since these integration slices landed.
