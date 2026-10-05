// Drives the PRODUCTION download-sort gate (src/WSGM/Core/SteamUiAssets/Source/download-sort.ts)
// through the Steam UI bridge WSGM installed in the live Steam CEF session, exactly as the
// wsgm.download-sort patch's apply, remove and verify do.
//   node run-prod-sort.mjs [enable|disable|status]
import { readFileSync } from "node:fs";
import { evaluate, findTarget, probeParams } from "./cdp.mjs";

const mode = process.argv[2] || "enable";
const root = new URL("../../", import.meta.url);
const identity = readFileSync(
  new URL("external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiBridgeIdentity.cs", root),
  "utf8",
);

// The bridge is the window property its identity names; the gate is registered there by the asset.
const bridgeNamespace = identity.match(/const string Namespace = ("[^"]+");/)?.[1];
if (!bridgeNamespace) throw new Error("SteamUiBridgeIdentity.Namespace not found");

const call =
  mode === "disable"
    ? "const removed=gate.remove();return JSON.stringify({...removed,status:gate.status()});"
    : mode === "status"
      ? "return JSON.stringify(gate.status());"
      : "const installed=gate.install();return JSON.stringify({...installed,status:gate.status()});";
const expression =
  `(()=>{try{const b=window[${bridgeNamespace}];` +
  "const gate=b&&b.gate?b.gate('wsgmDownloadSort'):null;" +
  "if(!gate)return JSON.stringify({ok:false,error:'bridge or gate unavailable'});" +
  call +
  "}catch(e){return JSON.stringify({ok:false,error:String((e&&e.stack)||e)});}})()";

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
