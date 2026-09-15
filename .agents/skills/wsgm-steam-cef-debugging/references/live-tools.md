# Safe live Steam CEF investigation

Live CEF inspection touches the user's actual Steam session. Start read-only and keep every query
bounded to one known target, module, or token.

## Required preflight

Before MCP, raw Node helpers, or any runtime evaluation, verify the listener owner:

```powershell
Get-NetTCPConnection -State Listen -LocalPort 8080 -ErrorAction SilentlyContinue |
    Select-Object LocalAddress, LocalPort, OwningProcess,
        @{Name='ProcessName';Expression={(Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName}}
```

Accept only `steam` or `steamwebhelper`. Then inspect targets without evaluating code:

```powershell
Invoke-RestMethod -Uri 'http://127.0.0.1:8080/json/list' |
    Select-Object id, type, title, url, webSocketDebuggerUrl
```

Accept only a `ws://` or `wss://` loopback websocket on port 8080. Stop if ownership or target
identity is ambiguous.

Use `SharedJSContext` for Steam stores, webpack, React, bridge, and patches. For visible DOM or
screenshots, select MainWindow by URL shape (`about:blank?`, `createflags`, `minwidth`, no
`openerid` or `browserviewpopup`), never by its localized title.

## Tool classification

| Tool or action                                   | Classification                                             | Important limits                                                                     |
| ------------------------------------------------ | ---------------------------------------------------------- | ------------------------------------------------------------------------------------ |
| MCP target listing                               | read-only after preflight                                  | Configuration alone does not prove port ownership                                    |
| bounded MCP evaluation of a literal value/module | read-only only if the expression is read-only              | navigation, focus, click, capture, and `close_page` are not generic observation      |
| `run-file.mjs`, `run-file-target.mjs`            | depends entirely on the JavaScript file or its `--section` | may print `undefined` and exit zero even when evaluation returned `exceptionDetails` |
| `cdp-eval.mjs list`                              | read-only after preflight                                  | `raw` depends on expression; `add` and `remove` mutate install folders               |
| `qam-harness.mjs status`                         | attended live change                                       | connection calls `Runtime.addBinding` before reporting status                        |
| `qam-harness.mjs install` or `publish`           | mutating                                                   | bypasses the patch manager; follow with `remove` and verify the visible result       |
| `qam-harness.mjs remove`                         | partial cleanup                                            | removes its gates/bridge, not the installed runtime binding                          |
| `qam-harness.mjs screenshot`                     | capture                                                    | may expose user-visible Steam content                                                |
| `run-prod-sort.mjs enable` or `disable`          | mutating                                                   | can reorder or resume downloads; source paths resolve relative to the script         |
| `art-test.mjs`                                   | mutating                                                   | applies artwork and needs `SGDB_KEY`                                                 |

Both `.codex/config.toml` and `.mcp.json` attach their `steam-cef` client to the same loopback
endpoint. Neither relaxes these rules.

## Do not run these as probes

- `tabs-prod.js` and `unpatch.js` sweep and execute the webpack registry.
- The mutating sections of the `probe-*.js` family scripts. Each header lists them apart from the
  read-only sections, and a run without `--section` only prints that list.
  - `click` in `probe-misc.js` and `settings-change` in `probe-qam.js` interact with the live UI.
  - `perf-shim` and `tdp-rpc` in `probe-perf.js`, `audio-gate` and `audio-install` in
    `probe-audio.js`, and `nightmode-gate` in `probe-gates.js` install, override or invoke live
    gates.
  - `register`, `register2` and `subscribe` in `probe-register.js` target an obsolete bridge shape
    and mutate state.

The `probe-` prefix and a read-only label are historical classification, not a guarantee. Inspect
every section before execution.

## Safe query shape

Prefer existing read-only sections such as `probe-register.js --section token-exists` only after
reviewing their current contents. For a new query:

1. Read the current implementation or generated asset to obtain one explicit module id or unique
   source token.
2. Evaluate only property reads and `String(runtime.m[knownId])`-style source inspection.
3. Do not call the factory, traverse by executing entries, construct exports, patch globals, or
   invoke a discovered function.
4. Return a small JSON-serializable result with target id, token, match count, and relevant types.
5. Inspect the protocol response for `exceptionDetails`; do not trust process exit zero.

If a query needs a click, setting change, gate installation, state publication, navigation, or
restart to discriminate the cause, describe that next step and request maintainer direction. After
an approved mutation, use the feature's own removal path, verify cleanup, and document any residue
the tool cannot remove.
