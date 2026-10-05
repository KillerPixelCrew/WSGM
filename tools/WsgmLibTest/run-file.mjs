// Evaluate a JS file in a Steam CEF target, SharedJSContext unless --target names another.
//   node run-file.mjs <file.js> [--target <title>]
import { parseArguments, runFile } from "./cdp.mjs";

const usage = "usage: node run-file.mjs <file.js> [--target <title>]";
const { positional, options } = parseArguments(process.argv.slice(2), ["target"], usage);
if (positional.length !== 1) {
  console.error(usage);
  process.exit(1);
}
await runFile(positional[0], options);
