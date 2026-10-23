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
await import(server.href);
