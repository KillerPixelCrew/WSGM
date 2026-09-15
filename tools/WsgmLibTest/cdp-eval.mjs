// Dev CDP client for Steam's SharedJSContext (CEF devtools port 8080).
// Subcommands avoid passing backslashes through the shell into JS source:
//   node cdp-eval.mjs raw  "<js expression>"     evaluate arbitrary JS
//   node cdp-eval.mjs add  "Z:\SteamLibrary"      AddInstallFolder(path) — path JSON-encoded in Node
//   node cdp-eval.mjs list                         GetInstallFolders summary
// Requires Steam launched with .cef-enable-remote-debugging.

import { evaluate, findTarget, jsStringLiteral, probeParams } from "./cdp.mjs";

// Resolves with the returned value; a CDP error or a JavaScript exception rejects.
async function evalInContext(wsUrl, expression) {
  const msg = await evaluate(wsUrl, probeParams(expression));
  if (msg.error) throw new Error(JSON.stringify(msg.error));
  const r = msg.result;
  if (r.exceptionDetails) throw new Error("JS exception: " + JSON.stringify(r.exceptionDetails));
  return r.result.value;
}

function buildExpression(cmd, arg) {
  switch (cmd) {
    case "raw":
      return arg;
    case "add":
      return `(async()=>{try{const r=await SteamClient.InstallFolder.AddInstallFolder(${jsStringLiteral(arg)});return 'RESOLVED: '+JSON.stringify(r);}catch(e){return 'REJECTED: '+JSON.stringify(e);}})()`;
    case "remove":
      if (!/^\d+$/.test(arg || "")) throw new Error("remove requires a numeric nFolderIndex");
      return `(async()=>{try{const r=await SteamClient.InstallFolder.RemoveInstallFolder(${Number(arg)});return 'RESOLVED: '+JSON.stringify(r);}catch(e){return 'REJECTED: '+JSON.stringify(e);}})()`;
    case "list":
      return `(async()=>{const f=await SteamClient.InstallFolder.GetInstallFolders();return f.map(x=>x.nFolderIndex+':'+x.strFolderPath+' ('+(x.vecApps?x.vecApps.length:'?')+' apps)').join('\\n');})()`;
    default:
      throw new Error(`unknown command: ${cmd}`);
  }
}

const cmd = process.argv[2];
const arg = process.argv[3];
if (!cmd) {
  console.error("usage: node cdp-eval.mjs <raw|add|remove|list> [arg]");
  process.exit(2);
}

try {
  const wsUrl = await findTarget();
  const value = await evalInContext(wsUrl, buildExpression(cmd, arg));
  console.log(typeof value === "string" ? value : JSON.stringify(value, null, 2));
} catch (e) {
  console.error("ERROR: " + e.message);
  process.exit(1);
}
