// Live end-to-end SteamGridDB apply test.
//   node art-test.mjs <appid> <assetType 0grid 1hero 2logo 3wide>
import { readFileSync } from "node:fs";

const PORT = 8080;
// Provide your SteamGridDB API key via the SGDB_KEY env var (never hardcode it).
const KEY = process.env.SGDB_KEY || "";
if (!KEY) {
  console.error("set SGDB_KEY env var to your SteamGridDB API key");
  process.exit(1);
}
const APPID = process.argv[2] || "3602290";
const TYPE = Number(process.argv[3] ?? 1);

const seg =
  TYPE === 0 || TYPE === 3 ? "grids" : TYPE === 1 ? "heroes" : TYPE === 2 ? "logos" : "icons";
const dims = TYPE === 0 ? "?dimensions=600x900" : TYPE === 3 ? "?dimensions=460x215" : "";

const listRes = await fetch(`https://www.steamgriddb.com/api/v2/${seg}/steam/${APPID}${dims}`, {
  headers: { Authorization: `Bearer ${KEY}`, Accept: "application/json" },
});
const list = await listRes.json();
if (!list.success || !list.data?.length) {
  console.log("no assets", JSON.stringify(list).slice(0, 200));
  process.exit(1);
}
const url = list.data[0].url;

const imgRes = await fetch(url);
const buf = Buffer.from(await imgRes.arrayBuffer());
const b64 = buf.toString("base64");
const ext = /\.jpe?g($|\?)/i.test(url) ? "jpg" : "png";
console.log(`asset ${list.data[0].id}  ${url}  (${buf.length} bytes, ${ext})`);

const expr =
  `(async()=>{try{await SteamClient.Apps.ClearCustomArtworkForApp(${APPID},${TYPE});` +
  `await new Promise(r=>setTimeout(r,500));` +
  `await SteamClient.Apps.SetCustomArtworkForApp(${APPID},"${b64}","${ext}",${TYPE});` +
  `return JSON.stringify({ok:true});}catch(e){return JSON.stringify({ok:false,err:String(e)});}})()`;

const res = await fetch(`http://localhost:${PORT}/json`);
const t = (await res.json()).find((x) => x.title === "SharedJSContext");
const ws = new WebSocket(t.webSocketDebuggerUrl);
ws.onopen = () =>
  ws.send(
    JSON.stringify({
      id: 1,
      method: "Runtime.evaluate",
      params: { expression: expr, awaitPromise: true, returnByValue: true },
    }),
  );
ws.onmessage = (ev) => {
  const m = JSON.parse(ev.data);
  if (m.id === 1) {
    console.log("apply:", m.error ? JSON.stringify(m.error) : m.result?.result?.value);
    try {
      ws.close();
    } catch {}
    process.exit(0);
  }
};
setTimeout(() => {
  console.log("timeout");
  process.exit(1);
}, 25000);
