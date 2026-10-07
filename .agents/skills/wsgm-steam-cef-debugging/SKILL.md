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
[ordered preflight](references/live-tools.md). This rule governs attended debugging, including
read-only work. It is stricter than WSGM's current production desktop-mode gate; this documentation
rule does not claim that the runtime parses Steam logs.

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
   [references/live-tools.md](references/live-tools.md) and perform its startup-log, listener and
   target preflight in that order.

A diagnosis-only request authorizes inspection and explanation. Implement repairs when the current
request also authorizes them; do not invent a new approval step for already authorized work. Live
Steam mutation, restart and device writes still require applicable explicit maintainer direction.

## Use the evidence ladder

Prefer evidence in this order:

1. Steam and WSGM logs from the affected run, read from disk without CEF.
2. Current code, generated asset and existing regression sources; obey manual-first test timing.
3. Read-only process/window/listener evidence. Only after log-confirmed startup, `/json/list`.
4. Bounded read-only evaluation of one known target and authored source-token fingerprint.
5. Attended capture or mutation only when the maintainer explicitly requests it and a recovery path
   is clear.

Do not start with an arbitrary JavaScript probe. Reachable port 8080, a running Steam process, or a
new `SharedJSContext` does not establish the startup prerequisite in either mode.

## Classify each conclusion

- **Observed:** directly present in the named log, target list, code, test, or bounded evaluation.
- **Inferred:** the smallest explanation joining those observations; state the inference.
- **Unverified live:** requires current Steam/device behavior that was not exercised.

Report the first broken layer, evidence for it, ruled-out alternatives, the smallest next test, and
whether that test is offline, read-only live, or mutating live. Exit code zero from a CEF helper is
not proof of a successful JavaScript evaluation.

## Safety invariants

- Complete current-run Steam-log confirmation before even a read-only CEF target listing.
- Verify that loopback port 8080 belongs to `steam` or `steamwebhelper` and that the websocket URL
  is loopback port 8080 before attaching.
- Inspect a helper's source before running it.
- Never sweep or execute the webpack registry, instantiate unknown exports, spoof global platform
  state, or use `close_page` as cleanup.
- Treat every non-screenshot `qam-harness.mjs` command as an attended live change because connecting
  adds a runtime binding. Its `remove` command does not remove that binding.
- Do not delete `.cef-enable-remote-debugging`; it is shared with other tools and only affects a
  cold Steam start.
