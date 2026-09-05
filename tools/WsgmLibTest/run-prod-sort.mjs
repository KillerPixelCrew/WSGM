// Runs the PRODUCTION download-sort script, extracted verbatim from
// src/WSGM/Core/SteamDownloadSort.cs, against the live Steam CEF session.
//   node run-prod-sort.mjs [enable|disable]
import { readFileSync } from "node:fs";

const PORT = 8080;
const mode = process.argv[2] || "enable";
const root = new URL("../../", import.meta.url);
const cs = readFileSync(new URL("src/WSGM/Core/SteamDownloadSort.cs", root), "utf8");
const resolver = readFileSync(
  new URL(
    "external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/module-resolver.ts",
    root,
  ),
  "utf8",
);

const start = cs.indexOf('private const string ResidentSetup = """');
if (start === -1) throw new Error("ResidentSetup not found");
const bodyStart = cs.indexOf("\n", start) + 1;
const end = cs.indexOf('""";', bodyStart);
if (end === -1) throw new Error("ResidentSetup terminator not found");
let body = cs.slice(bodyStart, end);
// C# raw string literals strip the closing delimiter's indentation from every line.
const indent = body.split("\n").pop().length ? 0 : 0;
const lines = body.split("\n");
const pad = cs.slice(cs.lastIndexOf("\n", end) + 1, end).length;
body = lines.map((l) => (l.startsWith(" ".repeat(pad)) ? l.slice(pad) : l)).join("\n");

const expression =
  mode === "disable"
    ? "(()=>{try{var W=window.__wsgm;if(W&&W.dlSortRemove)W.dlSortRemove();return JSON.stringify({ok:true});}catch(e){return JSON.stringify({ok:false,err:String(e)});}})()"
    : "(()=>{try{const steamModules=(" +
      resolver +
      ")('download-sort');" +
      body +
      "\nreturn W.dlSortInstall();}catch(e){return JSON.stringify({ok:false,err:String((e&&e.stack)||e)});}})()";

const res = await fetch(`http://localhost:${PORT}/json`);
const targets = await res.json();
const t = targets.find((x) => x.title === "SharedJSContext");
if (!t) {
  console.error("SharedJSContext not found");
  process.exit(1);
}

const ws = new WebSocket(t.webSocketDebuggerUrl);
ws.onopen = () =>
  ws.send(
    JSON.stringify({
      id: 1,
      method: "Runtime.evaluate",
      params: {
        expression,
        awaitPromise: true,
        returnByValue: true,
        allowUnsafeEvalBlockedByCSP: true,
        userGesture: true,
      },
    }),
  );
ws.onmessage = (ev) => {
  const m = JSON.parse(ev.data);
  if (m.id === 1) {
    if (m.error) console.log("CDP ERROR", JSON.stringify(m.error));
    else if (m.result?.result?.value === undefined)
      console.log("UNDEFINED", JSON.stringify(m.result).slice(0, 600));
    else console.log(m.result.result.value);
    try {
      ws.close();
    } catch {}
    process.exit(0);
  }
};
setTimeout(() => {
  console.log("timeout");
  process.exit(1);
}, 30000);
