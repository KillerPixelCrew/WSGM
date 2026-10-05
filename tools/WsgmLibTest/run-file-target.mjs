// Evaluate a JS file in a named CEF target, for example the Big Picture window. Same as
// run-file.mjs --target <title>.
//   node run-file-target.mjs "Big-Picture-Modus" <file.js>
import { parseArguments, runFile } from "./cdp.mjs";

const usage = "usage: node run-file-target.mjs <title> <file.js>";
const { positional } = parseArguments(process.argv.slice(2), [], usage);
if (positional.length !== 2) {
  console.error(usage);
  process.exit(1);
}
await runFile(positional[1], { target: positional[0] });
