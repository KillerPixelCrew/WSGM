# Steam CEF diagnostic map

## Attachment prerequisite

Before any debugger/CDP/MCP connection or target listing, confirm from the current Steam run's logs
that Steam and Big Picture have fully started. An early connection can hang the entire UI and leave
Steam requiring force-close; it does not authorize that recovery. Begin with disk logs and offline
source. Missing or inconclusive readiness evidence means remain disconnected. Follow
[live-tools.md](live-tools.md) for the ordered log, listener and target preflight.

## Capture the affected run

Find the active WSGM log root from current configuration or code; do not assume an old machine path.
Read the current Steam installation's `logs/cef_log.txt` and `logs/webhelper_js.txt` as well; record
affirmative completed-start evidence for Steam and Big Picture before any CEF connection. Then
narrow WSGM records by timestamp and high-signal lines instead of dumping every log:

```powershell
$wsgmLogRoot = '<confirmed log directory>'
rg -n -i 'steam-ui-transport-gate|Big Picture window detected|Steam UI transport|steam\.ui\.|Native QAM|rtss\.command|RTSS FrameLimit|Library tabs|Card badge|Steam current app' $wsgmLogRoot
```

Record the file path and timestamps used. Logs from a different boot, device, or mode can disprove
nothing about the failing run.

## Walk layers in order

| Layer                    | Evidence that should exist                                                           | If it does not                                                                  |
| ------------------------ | ------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------- |
| 1. Policy and lifecycle  | `Cef.Enabled`, feature policy, game/desktop mode, transition state                   | Explain which gate is intentionally closed before debugging JavaScript          |
| 2. Big Picture readiness | Current Steam logs confirm full startup; WSGM window/gate records corroborate        | Investigate `SteamUiReadiness`, window detection, or transition ordering        |
| 3. CDP discovery         | Only after log confirmation: Steam-owned listener and unique shaped targets          | Separate flag/cold-start failure from wrong target selection                    |
| 4. Transport generation  | one open persistent transport, attached session, no stale-generation completion      | Investigate reconnect, cancellation, or duplicate attachment                    |
| 5. Patch lifecycle       | patch probe, apply, verify in order; no timeout or removal after failed verify       | Inspect fingerprint, bridge readiness, ownership marker and exact phase failure |
| 6. Bridge contract       | publication accepted; no `steam.ui.bridge.rejected`; sequence and generation current | Compare emitted JSON with registered vocabulary and built validators            |
| 7. State projection      | non-null valid state and expected availability                                       | Inspect backend/provider before render code; `null` publishes nothing           |
| 8. Render surface        | gate installed, Valve component uniquely found, row/tab/badge placed                 | Inspect literal token drift, cached availability, placement, and visible target |
| 9. Command path          | request, routed command, backend refusal/success, response, refreshed publication    | Preserve refusal; do not retry an uncertain write automatically                 |

Healthy game-mode cold boot evidence is ordered:

```text
Big Picture window detected
Steam UI transport open: Big Picture window is up.
steam.ui.patch.<id> ... Applied
steam.ui.patch.<id> ... Verified
```

Patch work before the readiness line points to a transport-gate regression, not a missing row token.

For production desktop cold starts, distinguish enabled transport policy from actual attachment. The
stricter attended debugging rule still requires Steam-log confirmation for both Steam and Big
Picture; the runtime itself has no Steam-log parser. Toolkit discovery requires a validated
MainWindow, not a login popup. The 2026-09-05 failure and whole-path audit are recorded in
`docs/steam-cef.md`, "Desktop cold start login failure, 2026-09-05". A missing-factory
`reading 'call'` followed by missing exports can mean an early module load poisoned webpack's cache,
even if its source is present by the time of inspection. Do not retry the module to investigate that
state.

## Symptom routing

| Symptom                                        | First checks                                                                                   | Common causes                                                                                                       |
| ---------------------------------------------- | ---------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------- |
| Startup hangs or headless Steam                | readiness and transport-open ordering                                                          | attaching to SharedJSContext before the `SDL_app` Big Picture window                                                |
| All injected surfaces absent                   | policy, transport, session generation, bridge install                                          | master disabled, transition pending, wrong target, stale generation                                                 |
| One QAM row absent                             | patch status, publication validation, render/data gates                                        | token drift, closed vocabulary rejection, cached availability                                                       |
| Row disappears after a change                  | exact backend outcome, projected publication, and normalizer result                            | enum removed by projection, value outside validator bookends, missing required field                                |
| Row renders but write fails                    | request/action generation, backend refusal, response publication                               | stale action, unsupported capability, uncertain device state                                                        |
| Custom tabs appear only after sidebar activity | boot tab-sync completion and retry evidence                                                    | badge success incorrectly treated as tab success; store not ready                                                   |
| Card badge missing                             | SharedJSContext badge/details patch status, current library publication and native tile anchor | incompatible tile/JSX shape, absent anchor, stale library reading; current-game DOM detection is a separate feature |
| Download order wrong                           | parser includes index 0, scheduled/unqueued handling, pause state                              | skipped first item, incomplete requeue, sort resuming a paused item                                                 |
| Glyph wrong or absent                          | selected glyph policy and parsed stylesheet evidence                                           | probing current DOM instead of stylesheet, feature gate disabled                                                    |
| Library registration wrong                     | serialized JSON, path identity, stable folder id                                               | bad escaping, treating `nFolderIndex` as array index, rejecting allowed duplicate path                              |

## Contract traps already paid for

- The no-game id is `769`, not `0`.
- Controller target ids are PascalCase.
- A closed TypeScript validator can silently reject a C# field that was renamed or removed.
- A 12 FPS value fails a vocabulary whose lower bookend is 30 unless the contract explicitly
  includes it.
- Some state shapes require `deferred`; omission can remove only the affected row.
- `bytes_total = 0` means unknown download size, not a completed zero-byte transfer.
- Library content identity and stable `nFolderIndex` are not array positions.

Tests should assert against the actual generated asset vocabulary where practical. A duplicated C#
allow-list can pass while the shipped TypeScript rejects the state.

For sliders, establish whether disappearance happens during pointer movement or only on release.
Rows that send commands through `onChangeComplete` have not entered the command/publication path
before release. Also, `steam.ui.append.*` is patch-verification evidence; it may not report a later
client-side normalization rejection after interaction. Do not substitute it for the exact projected
state or the injected row's validation outcome. `steam.ui.bridge.rejected` concerns envelope and
authorization failures, not a state payload that the client accepted and then normalized to null.

## Narrow offline checks

Asset drift/build checks may run before manual acceptance. Automated .NET tests, emitted-asset
claims and test-bearing gates wait for the maintainer's manual pass unless explicitly requested
sooner, under root AGENTS.md. The commands below are a menu, not permission to run all of them. They
avoid live Steam and device mutation, but are not filesystem-read-only: builds and tests write
ordinary artifacts, and asset checks may create and remove temporary generated files.

```powershell
npm run steam-assets:check
npm run steam-assets:claims
dotnet test .\external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj
dotnet test .\tests\WSGM.Tests\WSGM.Tests.csproj --filter 'FullyQualifiedName~SteamUi|FullyQualifiedName~NativeQam'
```

Use current test names to narrow further. Offline success validates code contracts, not the current
Steam client's tokens or visible rendering.
