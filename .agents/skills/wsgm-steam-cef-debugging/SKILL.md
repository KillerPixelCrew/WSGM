---
name: wsgm-steam-cef-debugging
description:
  Diagnose broken or missing WSGM Steam UI behavior, including CEF/CDP connectivity, startup
  readiness, patch and bridge failures, native Quick Access rows, library tabs, badges, downloads,
  and glyphs. Use for evidence gathering and safe live or offline probing; not for routine
  implementation.
---

# WSGM Steam CEF debugging

Locate the first broken boundary in the actual failing run. Do not revive an old theory merely
because its symptom looks similar.

## Start from current evidence

1. Resolve the repository root, read the applicable `AGENTS.md`, and inspect both the WSGM and
   submodule status. Preserve unrelated changes.
2. Capture the exact scenario: machine, Steam client/build, WSGM commit, game mode, cold or warm
   start, feature settings, expected result, observed result, and a narrow time window.
3. Read `docs/steam-cef-system.md` for the current system. Treat `docs/steam-cef.md` as dated
   evidence.
4. Read [references/diagnostic-map.md](references/diagnostic-map.md), choose the symptom, and walk
   its layers in order. Stop at the first boundary contradicted by evidence.
5. Before any live attachment or helper script, read
   [references/live-tools.md](references/live-tools.md) and perform its listener/target preflight.

A diagnosis request authorizes inspection and explanation, not an implementation, a Steam UI
mutation, a restart, or a device write. Ask for maintainer direction when the next discriminating
step crosses that line.

## Use the evidence ladder

Prefer evidence in this order:

1. WSGM logs from the affected run.
2. Current code, generated asset, and focused tests.
3. Read-only process, listener, and `/json/list` inspection.
4. Bounded read-only evaluation of one known target and literal module/source token.
5. Attended capture or mutation only when the maintainer explicitly requests it and a recovery path
   is clear.

Do not start with an arbitrary JavaScript probe. Reachable port 8080, a running Steam process, or a
new `SharedJSContext` does not prove that WSGM should attach in game mode.

## Classify each conclusion

- **Observed:** directly present in the named log, target list, code, test, or bounded evaluation.
- **Inferred:** the smallest explanation joining those observations; state the inference.
- **Unverified live:** requires current Steam/device behavior that was not exercised.

Report the first broken layer, evidence for it, ruled-out alternatives, the smallest next test, and
whether that test is offline, read-only live, or mutating live. Exit code zero from a CEF helper is
not proof of a successful JavaScript evaluation.

## Safety invariants

- Verify that loopback port 8080 belongs to `steam` or `steamwebhelper` and that the websocket URL
  is loopback port 8080 before attaching.
- Inspect a helper's source before running it. `probe-` is not a safety classification.
- Never sweep or execute the webpack registry, instantiate unknown exports, spoof global platform
  state, or use `close_page` as cleanup.
- Treat every non-screenshot `qam-harness.mjs` command as an attended live change because connecting
  adds a runtime binding. Its `remove` command does not remove that binding.
- Do not delete `.cef-enable-remote-debugging`; it is shared with other tools and only affects a
  cold Steam start.
