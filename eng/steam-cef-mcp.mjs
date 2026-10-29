// Shared project-local launcher for Codex and Claude. Never starts a browser or changes Steam flags.
import { existsSync } from "node:fs";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../", import.meta.url));
const server = new URL(
  "../node_modules/chrome-devtools-mcp/build/src/bin/chrome-devtools-mcp.js",
  import.meta.url,
);
if (!existsSync(server)) {
  console.error("Steam CEF MCP is not installed. Run npm ci from the WSGM checkout.");
  process.exit(1);
}

process.chdir(root);
process.argv = [
  process.execPath,
  fileURLToPath(server),
  "--browserUrl",
  "http://127.0.0.1:8080",
  "--usageStatistics",
  "false",
  "--performanceCrux",
  "false",
  ...process.argv.slice(2),
];

// Steam creates native popup targets before their JavaScript context is ready. Pausing those
// targets during Puppeteer's auto-attach can deadlock CEF and freeze every Steam window.
// Keep the normal MCP tools, but never ask Steam to wait for a debugger on a new target.
const { puppeteer } = await import(
  new URL("../node_modules/chrome-devtools-mcp/build/src/third_party/index.js", import.meta.url)
    .href
);
const connect = puppeteer.connect.bind(puppeteer);
puppeteer.connect = async (options) => {
  if (!options.browserURL || new URL(options.browserURL).origin !== "http://127.0.0.1:8080") {
    return connect(options);
  }
  const version = await (await fetch("http://127.0.0.1:8080/json/version")).json();
  const endpoint = new URL(version.webSocketDebuggerUrl);
  if (
    endpoint.protocol !== "ws:" ||
    !["127.0.0.1", "localhost"].includes(endpoint.hostname) ||
    endpoint.port !== "8080"
  ) {
    throw new Error("Steam returned an unexpected debugger endpoint.");
  }
  const socket = new WebSocket(endpoint);
  await new Promise((resolve, reject) => {
    socket.addEventListener("open", resolve, { once: true });
    socket.addEventListener("error", reject, { once: true });
  });
  const transport = {
    onmessage: undefined,
    onclose: undefined,
    send(message) {
      const request = JSON.parse(message);
      if (request.method === "Target.setAutoAttach" && request.params?.waitForDebuggerOnStart) {
        request.params.waitForDebuggerOnStart = false;
        message = JSON.stringify(request);
      }
      socket.send(message);
    },
    close() {
      socket.close();
    },
  };
  socket.addEventListener("message", (event) => transport.onmessage?.(String(event.data)));
  socket.addEventListener("close", () => transport.onclose?.());
  try {
    return await connect({ ...options, browserURL: undefined, transport });
  } catch (error) {
    socket.close();
    throw error;
  }
};
await import(server.href);
