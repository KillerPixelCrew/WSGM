// Answers "did a Steam client update move anything WSGM finds by fingerprint?" from the files Steam
// installed, without attaching to Steam or running any of its code.
//
//   node eng/check-steam-fingerprints.mjs [<client>...] [--other-channel] [--record] [<emitted .js>...]
//
// <client> is a Steam directory or a snapshot of one under artifacts/ (see docs/steam-cef-system.md,
// "Checking both Steam channels"). With none, the installed client is checked. --other-channel adds
// the newest snapshot of the channel the first client is not on, so one command covers Stable and
// Beta. Every client is checked on its own: a fingerprint must match exactly one module in each.
// --record adds each client that passes to src/WSGM/Core/Steam/steam-client-builds.json, the list
// of checked builds WSGM compares the running client with. Additional emitted JavaScript files are
// checked with the shipped asset, for standalone probes or plugin scripts.
//
// Probes and gates find Steam's webpack modules by a conjunction of source tokens that has to match
// exactly one module. Module ids and export names are renumbered by client builds (the September
// 2026 beta renumbered all of them), so nothing names those. This reads the conjunctions from the
// shipped asset, parses each client's bundle into its module factories, and reports how many modules
// each conjunction matches. The bundle on disk carries every module the client ships and the live
// registry only those loaded so far, so a unique match here is unique there and a missing match here
// is missing there. Export shapes, rendered trees and whether a row actually draws remain questions
// for the running client; check-steam-patch-health.mjs reads those answers from wsgm.log.
//
// Exit codes: 0 every client passed, 1 a fingerprint did not match exactly one module, 2 a client
// named or installed could not be read (no Steam installed: nothing was checked).
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import {
  isKnownBuild,
  latestSnapshot,
  readClient,
  readKnownBuilds,
  recordBuild,
  resolveClientRoot,
} from "./steam-clients.mjs";
import { readFingerprints } from "./steam-fingerprints.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const knownBuildsPath = join(root, "src/WSGM/Core/Steam/steam-client-builds.json");

const installedSteam = () => {
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

const args = process.argv.slice(2);
const otherChannel = args.includes("--other-channel");
const record = args.includes("--record");
const positional = args.filter((arg) => !arg.startsWith("--"));
const scripts = positional.filter((arg) => arg.endsWith(".js"));
const named = positional.filter((arg) => !arg.endsWith(".js"));

const clients = named.length ? named.map((path) => resolve(path)) : [installedSteam()];
const roots = [];
for (const client of clients) {
  const clientRoot = resolveClientRoot(client);
  if (!clientRoot) {
    console.error(`No Steam UI found under ${client}: nothing was checked there.`);
    process.exit(2);
  }
  roots.push(clientRoot);
}

const assets = [resolve(root, "src/WSGM/Core/SteamUiAssets/NativeQamBootstrap.js"), ...scripts];
const fingerprints = new Map();
for (const file of assets.map((path) => resolve(path))) {
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
const ordered = [...fingerprints.values()].sort((a, b) =>
  a.locations[0].localeCompare(b.locations[0]),
);

const knownBuilds = readKnownBuilds(knownBuildsPath);
const today = new Date().toISOString().slice(0, 10);
const results = [];

const check = (clientRoot) => {
  const { modules, identity } = readClient(clientRoot);
  console.log(`== ${clientRoot}`);
  if (modules.size === 0) {
    console.error(`No Steam UI modules found under ${join(clientRoot, "steamui")}.`);
    process.exit(2);
  }
  let failures = 0;
  for (const { tokens, locations } of ordered) {
    const ids = [...modules].filter(([, source]) =>
      tokens.every((token) => source.includes(token)),
    );
    const verdict = ids.length === 1 ? "ok" : ids.length === 0 ? "MISSING" : "AMBIGUOUS";
    if (ids.length !== 1) failures++;
    const shown = ids.slice(0, 6).map(([id]) => id);
    const found =
      ids.length === 1
        ? ` module ${shown[0]}`
        : ids.length > 1
          ? ` modules ${shown.join(", ")}`
          : "";
    console.log(`${verdict.padEnd(9)} ${JSON.stringify(tokens)}${found}`);
    for (const location of locations) console.log(`          ${location}`);
  }
  console.log(
    `${fingerprints.size} fingerprints against ${modules.size} modules: ${failures ? `${failures} did not match exactly one module` : "every one matches exactly one module"}.`,
  );
  // The identity WSGM reads at runtime, so a passing build can be recorded as checked.
  const readable = identity.channel && identity.version;
  const listed = readable && isKnownBuild(knownBuilds, identity);
  console.log(
    `identity  ${JSON.stringify(identity)} ${
      !readable
        ? "(no client version on disk: cannot be recorded)"
        : listed
          ? "(listed in steam-client-builds.json)"
          : failures
            ? "(not listed; fix the failures before recording it)"
            : record
              ? "(recorded in steam-client-builds.json)"
              : "(not listed; rerun with --record to add it)"
    }`,
  );
  if (record && readable && !listed && failures === 0) {
    recordBuild(knownBuildsPath, identity, today);
  }
  results.push({ clientRoot, identity, failures });
};

for (const clientRoot of roots) check(clientRoot);

if (otherChannel) {
  const first = results[0].identity.channel;
  const snapshot = latestSnapshot(join(root, "artifacts"), first);
  if (snapshot) {
    check(snapshot.root);
  } else {
    console.log(
      `SKIPPED   no snapshot of a channel other than ${first ?? "the first client's"} under artifacts/steam-channel-*: only one channel was checked.`,
    );
  }
}

const failed = results.filter((result) => result.failures);
console.log(
  failed.length
    ? `FAILED on ${failed.map((result) => `${result.identity.channel ?? "?"} ${result.identity.version ?? result.clientRoot}`).join(", ")}.`
    : `Every fingerprint matches exactly one module on ${results.map((result) => `${result.identity.channel ?? "?"} ${result.identity.version ?? ""}`.trim()).join(" and ")}.`,
);
process.exit(failed.length ? 1 : 0);
