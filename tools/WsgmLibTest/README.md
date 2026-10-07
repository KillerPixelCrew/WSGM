# Attended Steam CEF diagnostics

These Node/PowerShell tools connect to the running Steam client. They are development probes outside
`WSGM.slnx`, not isolated regression tests. Use only the explicitly requested, reviewed diagnostic
against an attended session. File/expression runners can execute arbitrary JavaScript and do not
check whether it is safe; artwork and gate commands can change live Steam state.

Before any debugger or tool connection, confirm from the current run's Steam logs that Steam and Big
Picture have both fully started. An open port, debugger endpoint or visible window is not enough. An
early connection can hang the entire Steam UI and leave force-closing Steam as the recovery; these
tools do not authorize that recovery action. Follow the
[Steam CEF debugging workflow](../../.agents/skills/wsgm-steam-cef-debugging/SKILL.md) before using
them.

After that startup check, Steam must expose its loopback CEF debugger on port 8080. The Node scripts
require a runtime with built-in `fetch` and `WebSocket`. The shared [`cdp.mjs`](cdp.mjs) selects an
exact target title (`SharedJSContext` by default), opens a WebSocket, sends one `Runtime.evaluate`,
returns the protocol reply and closes the connection on completion/timeout. A timeout does not prove
a command was never executed.

| Tool                                                               | Purpose                                                                                                                                                                       |
| ------------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `run-file.mjs <file.js> [--target <title>]`                        | Evaluate a reviewed local file in the selected live target.                                                                                                                   |
| `run-file-target.mjs <title> <file.js>`                            | Equivalent file runner with a required target title.                                                                                                                          |
| `cdp-eval.mjs`                                                     | Expression runner; read its usage before invocation. It has the same live-session boundary.                                                                                   |
| `qam-harness.mjs`                                                  | Install/publish/remove or inspect the production QAM bootstrap using a host-emitted configuration fixture; also capture the visible Big Picture window.                       |
| `run-prod-sort.mjs [enable                                         | disable                                                                                                                                                                       | status] <bridge-fixture.json>` | Exercise the production download-sort gate through the live host bridge. |
| `art-test.mjs <appid> <asset-type>`                                | Fetch SteamGridDB artwork with `SGDB_KEY`, clear existing custom art and apply an image. This is a destructive artwork probe; explicitly supply the intended app ID and type. |
| `capture-steam-window.ps1`                                         | Capture an attended Steam window for visual diagnosis.                                                                                                                        |
| `qam-device-controls-fixture.json`, `artwork-browser-fixture.json` | Synthetic surface publications, not captured hardware or user-library state.                                                                                                  |

The QAM harness takes `--configuration <fixture.json>` for its bridge operations. Generate that
fixture during the permitted test phase by setting `WSGM_QAM_CONFIGURATION` to an absolute output
path and running `SteamUiSessionHostTests.EmittedBridgeConfigurationMatchesTheEmbeddedAsset`. The
fixture supplies the production namespace, allowlist and asset hash; the harness refuses a stale
asset hash before connecting. It does not discover new optional backends by guessing.

These probes bypass some production orchestration and are not examples of safe production asset
validation. Prefer the typed toolkit and host service contracts in feature code. The
[Steam CEF system](../../docs/steam-cef-system.md), [feature evidence](../../docs/steam-cef.md) and
[frontend source map](../../src/WSGM/Core/SteamUiAssets/Source/README.md) explain the actual
runtime. Do not sweep Steam's module registry or instantiate unknown exports to discover a target.
