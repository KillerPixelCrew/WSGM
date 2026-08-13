// Capture a PNG of a named CEF target.
//   node shot.mjs "Big-Picture-Modus" out.png
import { writeFileSync } from "node:fs";

const PORT = 8080;
const title = process.argv[2];
const out = process.argv[3] || "shot.png";

const res = await fetch(`http://localhost:${PORT}/json`);
const targets = await res.json();
const t = targets.find((x) => x.title === title);
if (!t) {
  console.error("target not found:", title);
  process.exit(1);
}

const ws = new WebSocket(t.webSocketDebuggerUrl);
ws.onopen = () =>
  ws.send(JSON.stringify({ id: 1, method: "Page.captureScreenshot", params: { format: "png" } }));
ws.onmessage = (ev) => {
  const m = JSON.parse(ev.data);
  if (m.id === 1) {
    if (m.error) console.log("CDP ERROR", JSON.stringify(m.error));
    else {
      writeFileSync(out, Buffer.from(m.result.data, "base64"));
      console.log("wrote " + out);
    }
    try {
      ws.close();
    } catch {}
    process.exit(0);
  }
};
setTimeout(() => {
  console.log("timeout");
  process.exit(1);
}, 20000);
