// Evaluate a JS file in a named CEF target, for example the Big Picture window. Same as
// run-file.mjs --target <title>.
//   node run-file-target.mjs "Big-Picture-Modus" <file.js> [--section <name>]
import { parseArguments, runFile } from "./cdp.mjs";

const usage = "usage: node run-file-target.mjs <title> <file.js> [--section <name>]";
const { positional, options } = parseArguments(process.argv.slice(2), ["section"], usage);
if (positional.length !== 2) {
  console.error(usage);
  process.exit(1);
}
await runFile(positional[1], { target: positional[0], section: options.section });
