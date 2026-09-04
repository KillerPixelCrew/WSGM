---
name: wsgm-steam-cef-toolkit
description:
  Implement or review WSGM Steam CEF features and SteamUiToolkit integration, including transport,
  bridge, patch lifecycle, gates, native Quick Access rows, generated assets, and deciding whether
  code belongs in WSGM or the toolkit. Use for code changes; use wsgm-steam-cef-debugging when
  diagnosis is the primary task.
---

# WSGM Steam CEF toolkit

Use the repository's proven Steam UI architecture without rediscovering its boundaries or repeating
unsafe experiments.

## Establish the working context

1. Resolve the repository root with `git rev-parse --show-toplevel`; do not assume the launch
   directory is the WSGM checkout.
2. Read the applicable `AGENTS.md`, then inspect `git status --short --branch` and
   `git submodule status --recursive`. Preserve unrelated work.
3. Read `docs/steam-cef-system.md` for the current design. Use `docs/steam-cef.md` as dated device
   evidence, not as a substitute for current code and tests.
4. Read [references/architecture.md](references/architecture.md) before choosing an owner or
   changing lifecycle code. Read [references/change-playbook.md](references/change-playbook.md)
   before editing.
5. If the task starts from a failure or missing UI, use the sibling `wsgm-steam-cef-debugging` skill
   first and identify the first broken boundary.

The user's request controls scope. This skill does not authorize live Steam mutation, a release, or
changes outside the requested feature.

## Preserve these invariants

- Open the transport only when `master && ((!inGameMode && !transitionPending) || bigPictureReady)`.
  A reachable CEF endpoint or a new `SharedJSContext` is not Big Picture readiness.
- Keep one persistent transport and one attached session. One-shot evaluations borrow that session.
- Use `SharedJSContext` for stores, webpack, React, bridge, and patches. Use the visible shaped
  `MainWindow` target for DOM and screenshots; never select it by localized title.
- Every patch is bounded `probe -> apply -> verify -> remove`, scoped to a target generation and a
  unique semantic fingerprint. Remove applied-but-unverified work.
- Recognize state already owned by WSGM, save the exact original on a durable object or string
  marker, and remove only WSGM's change. Accept the owned post-apply state on the next probe.
- Discover only named module ids or uniquely matched source/prototype strings. Never execute the
  webpack registry, instantiate unknown exports, or spoof broad platform state such as
  `TS.IS_STEAMOS` or `force_deck_perf_tab`.
- Keep the bridge vocabulary closed and derived from registered modules. Maintain camelCase wire
  fields, payload limits, positive sequence/action generations, validation, and replay rejection.
- Treat `null` projected state as "publish nothing," not a zero/default value. Keep data
  availability gates separate from render gates.
- Never hand-edit `src/WSGM/Core/SteamUiAssets/NativeQamBootstrap.js` or its catalog hash.
  Regenerate both through the asset build.

## Implement through the owning layer

- Put reusable CDP, patch, bridge, Valve contract, and Valve-backed surface behavior in
  `external/steam-ui-toolkit`.
- Put reusable Windows audio, radio, display, and device-control primitives in
  `external/windows-device-control`; keep WSGM as the policy and lifecycle owner.
- Put WSGM readiness policy, module registration, state projection, command routing, and backend
  adapters in `src/WSGM/Shell`.
- Put WSGM-only library tabs, badges, artwork, downloads, launch options, and glyph behavior in
  `src/WSGM/Core`.
- Do not copy toolkit source into WSGM. A toolkit edit is a submodule change: validate and commit
  the child first, then update the parent gitlink only when the task includes that delivery.

Prefer the smallest extension of an existing surface, module, gate, publication, vocabulary, and
ownership primitive. A new row does not imply a new gate or state channel. Do not create a parallel
transport, bridge, or patch lifecycle.

## Finish with evidence

Regenerate assets when toolkit TypeScript changes, run the narrow tests while iterating, and run the
repository gate before delivery. Record what was established offline separately from what still
requires a maintainer-directed live Big Picture or device pass. A successful build does not prove
that a row rendered after a Steam client update.
