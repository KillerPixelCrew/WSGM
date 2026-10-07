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

## Before any live CEF connection

Confirm from the current Steam run's logs that **both Steam and Big Picture have fully started**
before connecting a debugger, starting/invoking the Steam CEF MCP server, running a CDP helper or
reading `/json/list`/`/json/version`. Read-only target listing is still a connection and must wait.
An early connection can hang the entire Steam UI and leave Steam requiring force-close. This warning
does not authorize a force-close or restart.

Use Steam's current installation and run, read its `logs/cef_log.txt` and `logs/webhelper_js.txt`
from disk, and record the affirmative startup evidence and timestamps. A process, open port,
SharedJSContext, login popup or elapsed delay does not prove completed startup. WSGM window/gate log
lines corroborate readiness but do not replace the requested Steam-log confirmation. No stable
Steam-log success marker is implemented in this repository; if the current logs do not establish
completion, remain offline and leave CEF disconnected. Reconfirm after Steam restarts, renderer
replacement or a mode transition.

After log-confirmed startup, verify the listener owner and target shape using the
[ordered preflight](../wsgm-steam-cef-debugging/references/live-tools.md). This rule governs
attended debugging, including read-only work. It is stricter than WSGM's current production
desktop-mode gate; this documentation rule does not claim that the runtime parses Steam logs.

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
5. Read [references/reusable-elements.md](references/reusable-elements.md) to choose the existing
   toolkit surface, typed client call, native field or UI kit element. Read
   [references/ui-and-overlay-parity.md](references/ui-and-overlay-parity.md) for the corresponding
   Overlay control, styling and navigation.
6. If the task starts from a failure or missing UI, use the sibling `wsgm-steam-cef-debugging` skill
   first and identify the first broken boundary.

The user's request controls scope. This skill does not authorize live Steam mutation, a release, or
changes outside the requested feature.

## Pair every Big Picture feature with Overlay

**Always add the same capability or workflow to WSGM's Overlay when adding or changing Steam Big
Picture UI.** Build both on one existing service, state projection and semantic operation. Cover
both entry points, availability, pending/refusal states, controller navigation, cancellation and
cleanup. Use each UI's shared elements and style guide; do not duplicate backend/policy code or
create an ad hoc widget to make the screens match. An unfinished Overlay counterpart means the
feature is unfinished. Follow an explicit task exception only when the maintainer supplies one.

Prefer the smallest reusable element that expresses the requirement. The reusable-elements guide
indexes every toolkit family and supported script helper; extend its existing primitive only when
necessary, keeping the code readable and maintainable by a human.

## Preserve these invariants

- In QAM, "Reset to Default" is always the last control, after every section, including dynamic
  GPU/plugin sections. Enforce this at the final panel composition, never through registration
  order.
- Steam's left menu has WSGM immediately above Power, with Power last. Do not add a separate
  Graphics entry. Normalize React's nested child-key prefixes before matching Steam's descriptor
  anchors.
- The production transport gate is
  `master && !exitPending && ((!inGameMode && !transitionPending) || bigPictureReady)`. A reachable
  CEF endpoint or a new `SharedJSContext` is not Big Picture readiness. WSGM also requires a
  validated MainWindow during toolkit discovery in both modes; a desktop login popup does not
  authorize attachment. The configured master switch, not that temporary hold, controls creating the
  remote-debugging flag before a cold start.
- Keep one persistent transport and one attached session. One-shot evaluations borrow that session.
- Use `SharedJSContext` for stores, webpack, React, bridge, and patches. Use the visible shaped
  `MainWindow` target for DOM and screenshots; never select it by localized title.
- Every patch is bounded `probe -> apply -> verify -> remove`, scoped to a target generation and a
  unique semantic fingerprint. Remove applied-but-unverified work.
- Recognize state already owned by WSGM, save the exact original on a durable object or string
  marker, and remove only WSGM's change. Accept the owned post-apply state on the next probe.
- Discover modules only by uniquely matched source/prototype strings and exports only by shape
  (`exported(tokens, predicate)`). Never write down a module id or a minified export name: client
  builds renumber and rename both, and the September 2026 beta refused every gate that named one.
  Never execute the webpack registry, instantiate unknown exports, or spoof broad platform state
  such as `TS.IS_STEAMOS` or `force_deck_perf_tab`. Use the toolkit's `SteamUiModuleResolver` for
  matching and module resolution. Features provide fingerprints; they must not implement registry
  scans or expose raw webpack require. After a Steam update, `node eng/check-steam-fingerprints.mjs`
  counts every fingerprint's matches in the installed bundle without attaching to Steam.
- React has one `useMemo`; a surface that needs its results registers a transform on the toolkit's
  shared claim (`interceptMemo`) instead of wrapping it.
- Keep the bridge vocabulary closed and derived from registered modules. Maintain camelCase wire
  fields, positive sequence/action generations, validation, and replay rejection. Host deliveries
  stream in 262,144-character parts; page-to-host requests still have the complete CDP notification
  parameter cap of 1 MiB, 32 pending page requests and a five-second timeout.
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

The toolkit [source map](../../../external/steam-ui-toolkit/docs/code-map.md) and
[frontend guide](../../../external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/README.md)
map every implementation file and injected fragment. Update current mechanism/API documentation with
code changes; leave dated evidence dated. Regenerate assets when toolkit TypeScript changes. Follow
the root AGENTS.md manual-first timing: test suites and ownership claims wait for the maintainer's
manual test unless requested sooner. They do not block a requested development deployment. Record
what was established offline separately from what still requires a maintainer-directed live Big
Picture or device pass. A successful build does not prove that a row rendered after a Steam client
update.
