// Runs the PRODUCTION download-sort script, extracted verbatim from
// src/WSGM/Core/SteamDownloadSort.cs, against the live Steam CEF session.
//   node run-prod-sort.mjs [enable|disable]
import { readFileSync } from "node:fs";
import { evaluate, findTarget, probeParams } from "./cdp.mjs";

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

const wsUrl = await findTarget().catch((e) => {
  console.error(e.message);
  process.exit(1);
});
const m = await evaluate(wsUrl, probeParams(expression), 30000).catch((e) => {
  if (e.message !== "timeout") throw e;
  console.log("timeout");
  process.exit(1);
});
if (m.error) console.log("CDP ERROR", JSON.stringify(m.error));
else if (m.result?.result?.value === undefined)
  console.log("UNDEFINED", JSON.stringify(m.result).slice(0, 600));
else console.log(m.result.result.value);
process.exit(0);
