# Safe live Steam CEF investigation

Live CEF inspection touches the user's actual Steam session. **Do not connect until the current
Steam logs confirm that Steam and Big Picture have fully started.** An early debugger connection can
hang the entire Steam UI and leave Steam requiring force-close. That consequence is not permission
to close or restart Steam.

## Required preflight, in order

### 1. Establish startup completion without CEF

Do not start or invoke the configured Steam CEF MCP server, run a CDP helper, open its WebSocket,
read `/json/list` or `/json/version`, or issue even a read-only evaluation while startup is
unconfirmed. Target discovery must not be used to decide whether it is safe to attach.

Find the current Steam installation through the same registry-backed discovery described in
`docs/steam-cef-system.md`. Read its current-run logs from disk, particularly `logs/cef_log.txt` and
`logs/webhelper_js.txt`. Inspect affirmative initialization/completion records for both the Steam
client and its Big Picture UI, their timestamps and any later failure, restart or mode transition.
Search current log text around startup and Big Picture/Gamepad UI records, then inspect the
surrounding lines; a keyword match alone is not proof of completion. Record the exact paths,
timestamps and records that establish readiness.

The repository contains no stable Steam-log success-marker parser. Do not invent a fixed string,
assume absence of errors means success, reuse records from an earlier run, or use a fixed delay in
place of evidence. A live Steam process, listening port, login popup or headless SharedJSContext can
all precede completed startup. If the logs are missing, ambiguous or still show startup, keep CEF
disconnected and continue code/log/bundle inspection offline while the user finishes launch.

Corroborate the logs with a ready, responsive Big Picture window. WSGM's own current-run records can
help: `BootSplash.OnBigPictureDetected` writes
`Big Picture window detected — dismissing boot splash.`, and `ShellSession.SteamUi` writes
`Steam UI transport open: Big Picture window is up.`. These confirm the window/policy observations
made by WSGM; neither is a substitute for the required Steam-log evidence. `Steam started.` alone
means process liveness, not completed Big Picture initialization.

This attended debugging rule applies even when WSGM runs next to Explorer. The current production
transport has its own desktop-mode policy plus a validated MainWindow discovery requirement, and
uses process/window checks rather than parsing Steam logs. Do not silently change that runtime
behavior as part of a documentation or debugging task.

### 2. Prove listener ownership

After startup is confirmed, inspect the listener without making an HTTP request:

```powershell
Get-NetTCPConnection -State Listen -LocalPort 8080 -ErrorAction SilentlyContinue |
    Select-Object LocalAddress, LocalPort, OwningProcess,
        @{Name='ProcessName';Expression={(Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName}}
```

Accept only a loopback listener owned by `steam` or `steamwebhelper`. Refuse foreign-process,
wildcard or ambiguous ownership; a port number by itself is not identity. Inspect each helper's
source because historical raw helpers do not enforce all production transport checks.

### 3. Read and select the target

Only now inspect targets without evaluating code:

```powershell
Invoke-RestMethod -Uri 'http://127.0.0.1:8080/json/list' |
    Select-Object id, type, title, url, webSocketDebuggerUrl
```

Require an absolute `ws://` or `wss://` debugger URL with host `127.0.0.1` or `localhost` and port
8080, as the production URL validator does. Stop if ownership or target identity is ambiguous. Use
SharedJSContext for Steam stores, webpack, React, bridge and patches. For visible DOM or
screenshots, select MainWindow by URL shape (`about:blank?`, `createflags`, `minwidth`, no
`openerid` or `browserviewpopup`), never by its localized title.

Reconfirm current-run startup evidence after a Steam restart, renderer replacement or mode
transition before another attachment. A tool already configured for port 8080 has no exemption. Both
`.codex/config.toml` and `.mcp.json` point their `steam-cef` client at this live endpoint.

## Tool classification

Every CEF action below requires the startup-log, ownership and target preflight above.

| Tool or action                               | Classification                                | Limits                                                                                                |
| -------------------------------------------- | --------------------------------------------- | ----------------------------------------------------------------------------------------------------- |
| MCP target listing                           | Read-only after preflight                     | Still connects to the debugger; never use it as a startup probe.                                      |
| Bounded MCP evaluation                       | Read-only only if the expression is read-only | A known source/property read can be observational; navigation, focus, click and `close_page` are not. |
| `run-file.mjs`, `run-file-target.mjs`        | Depends on the complete JavaScript file       | May print `undefined` and exit zero when CDP returned `exceptionDetails`.                             |
| `cdp-eval.mjs list`                          | Read-only after preflight                     | `raw` depends on its expression; `add` and `remove` mutate install folders.                           |
| `qam-harness.mjs status`                     | Attended live change                          | The connection adds a runtime binding before reporting status.                                        |
| `qam-harness.mjs install` or `publish`       | Mutating                                      | Bypasses the patch manager; explicit direction and feature cleanup are required.                      |
| `qam-harness.mjs remove`                     | Partial cleanup                               | Removes its gates/bridge, not its installed runtime binding.                                          |
| `qam-harness.mjs screenshot`                 | Capture                                       | Attaches to CEF and may expose visible personal content.                                              |
| `run-prod-sort.mjs enable` or `disable`      | Mutating                                      | Can reorder or resume downloads.                                                                      |
| `art-test.mjs`                               | Mutating                                      | Applies artwork and needs `SGDB_KEY`.                                                                 |
| `capture-steam-window.ps1 -OutputPath <png>` | Attended native capture                       | Uses no CEF; restores/focuses the Big Picture window and captures its screen rectangle.               |

## Safe query shape

1. Obtain a unique authored source-token fingerprint or an existing published handle from current
   implementation or offline bundle evidence. Do not copy old module ids or minified export names.
2. Use bounded source/property inspection. `SteamUiModuleResolver`'s `count` and `findUnique`
   inspect factories without executing them; `resolve` and `exported` may execute a matched factory
   and are not interchangeable with a read-only source count.
3. Do not sweep/execute the registry, instantiate unknown exports, call discovered methods, mutate
   globals or spoof Steam's platform state to diagnose availability.
4. Return only a small JSON-serializable answer: target/generation, fingerprint, match count and
   relevant types. Inspect CDP `exceptionDetails` separately from process exit status.

An already authorized live mutation follows its feature's removal path and verifies cleanup,
including residue a helper cannot remove. If the next step needs a new live mutation, navigation,
restart or device write outside the user's current scope, explain the concrete action and request
maintainer direction. Do not create another permission step for work already explicitly authorized.
`close_page` closes a real Steam window and is never generic cleanup. Do not remove the shared
`.cef-enable-remote-debugging` flag.
