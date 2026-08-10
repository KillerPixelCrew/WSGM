// Dev CDP client for Steam's SharedJSContext (CEF devtools port 8080).
// Subcommands avoid passing backslashes through the shell into JS source:
//   node cdp-eval.mjs raw  "<js expression>"     evaluate arbitrary JS
//   node cdp-eval.mjs add  "Z:\SteamLibrary"      AddInstallFolder(path) — path JSON-encoded in Node
//   node cdp-eval.mjs list                         GetInstallFolders summary
// Requires Steam launched with .cef-enable-remote-debugging.

const PORT = 8080;

async function findSharedJsContext() {
  const res = await fetch(`http://localhost:${PORT}/json`);
  const targets = await res.json();
  const t = targets.find((x) => x.title === "SharedJSContext");
  if (!t) throw new Error("SharedJSContext target not found");
  return t.webSocketDebuggerUrl;
}

function evalInContext(wsUrl, expression) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(wsUrl);
    const timer = setTimeout(() => {
      try {
        ws.close();
      } catch {}
      reject(new Error("timeout"));
    }, 20000);
    ws.onopen = () => {
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
    };
    ws.onmessage = (ev) => {
      const msg = JSON.parse(ev.data);
      if (msg.id === 1) {
        clearTimeout(timer);
        try {
          ws.close();
        } catch {}
        if (msg.error) return reject(new Error(JSON.stringify(msg.error)));
        const r = msg.result;
        if (r.exceptionDetails)
          return reject(new Error("JS exception: " + JSON.stringify(r.exceptionDetails)));
        resolve(r.result.value);
      }
    };
    ws.onerror = (e) => {
      clearTimeout(timer);
      reject(new Error("ws error: " + (e.message || e)));
    };
  });
}

function buildExpression(cmd, arg) {
  switch (cmd) {
    case "raw":
      return arg;
    case "add":
      // JSON.stringify produces a correct JS string literal with escaped backslashes,
      // so the path reaches AddInstallFolder intact (e.g. "Z:\\SteamLibrary").
      return `(async()=>{try{const r=await SteamClient.InstallFolder.AddInstallFolder(${JSON.stringify(arg)});return 'RESOLVED: '+JSON.stringify(r);}catch(e){return 'REJECTED: '+JSON.stringify(e);}})()`;
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
  const wsUrl = await findSharedJsContext();
  const value = await evalInContext(wsUrl, buildExpression(cmd, arg));
  console.log(typeof value === "string" ? value : JSON.stringify(value, null, 2));
} catch (e) {
  console.error("ERROR: " + e.message);
  process.exit(1);
}
