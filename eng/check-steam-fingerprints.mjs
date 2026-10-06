// Answers "did a Steam client update move anything WSGM finds by fingerprint?" from the files Steam
// installed, without attaching to Steam or running any of its code.
//
//   node eng/check-steam-fingerprints.mjs [<Steam directory>]
//
// Probes and gates find Steam's webpack modules by a conjunction of source tokens that has to match
// exactly one module. Module ids and export names are renumbered by client builds (the September
// 2026 beta renumbered all of them), so nothing names those. This reads the conjunctions from the
// shipped asset, parses the installed bundle into its module factories, and
// reports how many modules each conjunction matches. It exits non-zero when one matches none or
// more than one.
//
// The bundle on disk carries every module the client ships and the live registry only those loaded
// so far, so a unique match here is unique there and a missing match here is missing there. Export
// shapes, rendered trees and whether a row actually draws remain questions for the running client.
// Additional emitted JavaScript files may follow the Steam directory argument, for standalone
// probes or plugin scripts. C# and TypeScript sources are never scraped here.
import { execFileSync } from "node:child_process";
import { readdirSync, readFileSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import * as meriyah from "prettier/plugins/meriyah";
import { readFingerprints } from "./steam-fingerprints.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const parser = (meriyah.parsers ?? meriyah.default.parsers).meriyah;

const steamDirectory = () => {
  if (process.argv[2]) return resolve(process.argv[2]);
  try {
    const output = execFileSync(
      "reg",
      ["query", "HKCU\\Software\\Valve\\Steam", "/v", "SteamPath"],
      { encoding: "utf8" },
    );
    const match = output.match(/SteamPath\s+REG_SZ\s+(.+)/u);
    if (match) return resolve(match[1].trim());
  } catch {
    // No registry entry: fall back to the default install location.
  }
  return "C:/Program Files (x86)/Steam";
};

// Every webpack factory in the installed bundle, by module id, as source text. A factory is a
// function-valued property with a numeric key; the files are parsed, never evaluated.
const readBundle = (steamui) => {
  const modules = new Map();
  const files = [steamui, join(steamui, "libraries")].flatMap((directory) =>
    readdirSync(directory)
      .filter((name) => name.endsWith(".js"))
      .map((name) => join(directory, name)),
  );
  for (const file of files) {
    const text = readFileSync(file, "utf8");
    if (!text.includes("webpackChunksteamui")) continue;
    const stack = [parser.parse(text, {})];
    while (stack.length) {
      const node = stack.pop();
      if (!node || typeof node !== "object") continue;
      if (Array.isArray(node)) {
        stack.push(...node);
        continue;
      }
      if (node.type === "ObjectExpression") {
        for (const property of node.properties) {
          const { key, value } = property;
          if (
            property.type === "Property" &&
            key &&
            typeof key.value === "number" &&
            (value?.type === "ArrowFunctionExpression" || value?.type === "FunctionExpression")
          ) {
            modules.set(
              String(key.value),
              text.slice(parser.locStart(value), parser.locEnd(value)),
            );
          } else {
            stack.push(property);
          }
        }
        continue;
      }
      for (const [name, child] of Object.entries(node)) {
        if (name !== "loc" && name !== "range" && child && typeof child === "object") {
          stack.push(child);
        }
      }
    }
  }
  return modules;
};

const assets = [
  resolve(root, "src/WSGM/Core/SteamUiAssets/NativeQamBootstrap.js"),
  ...process.argv.slice(3).map((path) => resolve(path)),
];
const fingerprints = new Map();
for (const file of assets) {
  if (!file.endsWith(".js")) throw new Error(`Expected emitted JavaScript: ${file}`);
  const source = readFileSync(file, "utf8");
  for (const { tokens, offset } of readFingerprints(source)) {
    const key = JSON.stringify(tokens);
    const line = source.slice(0, offset).split("\n").length;
    const where = `${relative(root, file).replaceAll("\\", "/")}:${line}`;
    if (!fingerprints.has(key)) fingerprints.set(key, { tokens, locations: [] });
    fingerprints.get(key).locations.push(where);
  }
}
if (fingerprints.size === 0) throw new Error("The emitted assets contain no module fingerprints.");

const steam = steamDirectory();
const modules = readBundle(join(steam, "steamui"));
if (modules.size === 0) {
  console.error(`No Steam UI modules found under ${join(steam, "steamui")}.`);
  process.exit(2);
}

let failures = 0;
for (const { tokens, locations } of [...fingerprints.values()].sort((a, b) =>
  a.locations[0].localeCompare(b.locations[0]),
)) {
  const ids = [...modules].filter(([, source]) => tokens.every((token) => source.includes(token)));
  const verdict = ids.length === 1 ? "ok" : ids.length === 0 ? "MISSING" : "AMBIGUOUS";
  if (ids.length !== 1) failures++;
  const shown = ids.slice(0, 6).map(([id]) => id);
  const found =
    ids.length === 1 ? ` module ${shown[0]}` : ids.length > 1 ? ` modules ${shown.join(", ")}` : "";
  console.log(`${verdict.padEnd(9)} ${JSON.stringify(tokens)}${found}`);
  for (const location of locations) console.log(`          ${location}`);
}
console.log(
  `${fingerprints.size} fingerprints against ${modules.size} modules in ${steam}: ${failures ? `${failures} did not match exactly one module` : "every one matches exactly one module"}.`,
);
process.exit(failures ? 1 : 0);
