// Evaluate a JS file in a named CEF target (default the Big Picture window).
//   node run-file-target.mjs "Big-Picture-Modus" <file.js>
import { readFileSync } from "node:fs";

const PORT = 8080;
const title = process.argv[2];
const file = process.argv[3];
if (!title || !file) {
  console.error("usage: node run-file-target.mjs <title> <file.js>");
  process.exit(1);
}
const expression = readFileSync(file, "utf8");

const res = await fetch(`http://localhost:${PORT}/json`);
const targets = await res.json();
const t = targets.find((x) => x.title === title);
if (!t) {
  console.error("target not found:", title, "— have:", targets.map((x) => x.title).join(", "));
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
    else console.log(m.result && m.result.result && m.result.result.value);
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
