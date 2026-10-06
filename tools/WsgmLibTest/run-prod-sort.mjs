// Drives the PRODUCTION download-sort gate (src/WSGM/Core/SteamUiAssets/Source/download-sort.ts)
// through the Steam UI bridge WSGM installed in the live Steam CEF session, exactly as the
// wsgm.download-sort patch's apply, remove and verify do.
//   node run-prod-sort.mjs [enable|disable|status] <bridge-fixture.json>
// The fixture is emitted by SteamUiSessionHostTests, as documented in qam-harness.mjs.
import { readFileSync } from "node:fs";
import { evaluate, findTarget, probeParams } from "./cdp.mjs";

const [mode = "enable", configurationPath] = process.argv.slice(2);
if (!configurationPath) throw new Error("Pass the emitted C# bridge fixture after the mode.");
const configuration = JSON.parse(readFileSync(configurationPath, "utf8"));
if (typeof configuration.namespace !== "string" || !configuration.allowed?.["wsgm.download-sort"]) {
  throw new Error("The emitted bridge fixture does not declare download sort.");
}
// The bridge identity and vocabulary come from the emitted host, rather than C# source text.
const bridgeNamespace = JSON.stringify(configuration.namespace);

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
