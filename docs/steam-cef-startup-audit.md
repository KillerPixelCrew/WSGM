# Steam CEF startup audit, 2026-09-05

The reference Claw's desktop cold start failed in login initialization. This is diagnosis evidence,
not a live validation of the subsequent changes.

## Evidence

Machine: N1GHT-CLAW, device definition ms-1t52. Steam public beta build 1788400362, CEF Chrome
126.0.6478.183. Installed WSGM and source: 2dee78bba3ba57a28dda22d560b0a8cc4822b7a3. CEF master,
native QAM, Wi-Fi indicator and download sorting were enabled. Times below are local CEST on
2026-09-05, from `%LOCALAPPDATA%\WSGM\wsgm.log` and Steam's `logs\cef_log.txt` and
`logs\webhelper_js.txt`.

| Time         | Observed                                                                              |
| ------------ | ------------------------------------------------------------------------------------- |
| 14:37:39.952 | WSGM resumed next to Explorer in desktop mode.                                        |
| 14:37:39.971 | Desktop policy opened the transport.                                                  |
| 14:37:43     | Steam and steamwebhelper started.                                                     |
| 14:37:46.847 | Network probe failed with `Cannot read properties of undefined (reading 'call')`.     |
| 14:37:47.694 | Download sort applied before login initialization completed.                          |
| 14:37:48.105 | Steam reported SystemNetworkStore initialization failure, reading `Get` of undefined. |
| 14:37:48.957 | Login rendering failed with `(0 , d.jh) is not a function`.                           |

Read-only MCP inspection confirmed the visible error reference
`undefined_undefined_60d3f049215072eb`. Port 8080 belonged to steamwebhelper; the target list
contained SharedJSContext and a login popup, with no shaped MainWindow. Source inspection of literal
module 77347 found both `OQ` (store holder) and `jh` (the hook reading initial network readiness).
`window.SystemNetworkStore` was undefined. Steam's captured loader cached an export object before
calling its factory, with no removal on failure. An offline reproduction using that loader left
empty exports cached even after the missing factory was registered.

Inference: WSGM's early network module request poisoned exports before Steam's own initialization.
The private cache was not exposed, so its exact contents were not directly inspected. The later
download scan was another premature-execution risk, not evidence that it caused the earlier error.

## Audit and changes

The production audit covered WSGM's Steam one-shot operations, resident scripts, running-app probe,
shell feature policy and transitions, plus toolkit discovery, transport generation handling,
patch/bridge lifecycle, gate probes, and injected module consumers.

- Desktop cold starts could attach to the headless context. WSGM now opts into the toolkit's
  validated MainWindow requirement before attachment to any role.
- Resuming the shell next to Explorer left the initially enabled download sorter and indicator
  active. Both are disabled before enabling desktop discovery.
- The network probe and gate could load or construct the network store. Both now read Steam's
  existing published singleton.
- Other gates had unchecked literal module loads; tabs and download sorting executed broad registry
  scans. The toolkit now owns one resolver source for all production module consumers. Features
  supply source fingerprints; absent and ambiguous matches refuse before module loading.
- Download-sort installation swallowed failures after partially wrapping JSX. It now records the
  owned runtime first, unwinds partial installation, and verifies both wrappers.
- Remote-debugging opt-in incorrectly consulted the temporary transport hold. The configured master
  switch is now passed explicitly through cold launch to the toolkit flag writer.

Artwork, launch options, collections, downloads, badges and the running-app observer already borrow
the same transport. They do not open an independent attachment that bypasses its discovery gate.
Historical live helpers remain attended tools; this audit did not execute registry sweeps, install
patches, restart Steam, or write device state through them.

## Verification boundary

Offline regressions cover target-list startup rejection, missing factories arriving later,
source-only matching, ambiguity, load failure, feature installation/removal, partial rollback and
explicit debug-flag opt-in. Factory presence cannot establish that every dependency is ready.

Validation on 2026-09-05: `eng/verify.ps1` passed formatting, asset drift, ownership/startup checks,
repository invariants, the warning-clean Release build, solution tests and coverage (2,087 WSGM
tests). The toolkit's 165 tests and standalone `npm run prelude:claims` also passed.

Still requires an attended live pass after deployment: desktop cold login, game-mode cold boot,
desktop/game transitions, Steam restart, library tabs, and the download sort controls. The broken
session was left intact during diagnosis; these scenarios have not been reported as passes.
