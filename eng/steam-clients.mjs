// Reads a Steam client's files from disk without running any of them: where its steamui folder is,
// which build it is, the webpack modules it can load, the newest snapshot of a channel under
// artifacts/, and the list of checked builds WSGM ships (src/WSGM/Core/Steam/steam-client-builds.json).
import { existsSync, readdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import * as meriyah from "prettier/plugins/meriyah";

const parser = (meriyah.parsers ?? meriyah.default.parsers).meriyah;

// Steam's own name for a channel, as `package/beta` holds it. No file, or an empty one, is stable.
export const Stable = "stable";
export const PublicBeta = "publicbeta";

// A snapshot folder's channel, by the folder name artifacts/steam-channel-<date>/<name> uses.
const channelOfFolder = (name) =>
  name === "beta" ? PublicBeta : name === "stable" ? Stable : null;

const isDirectory = (path) => existsSync(path) && statSync(path).isDirectory();

/**
 * The client root holding `steamui/index.html`: a Steam directory, a snapshot copied from one
 * (artifacts/steam-channel-20261101/beta), or a snapshot extracted from Valve's package, whose one
 * `active-source/<version>` folder is the root (artifacts/steam-channel-20261012/stable).
 */
export const resolveClientRoot = (directory) => {
  const root = resolve(directory);
  if (existsSync(join(root, "steamui", "index.html"))) return root;
  const extracted = join(root, "active-source");
  if (isDirectory(extracted)) {
    const versions = readdirSync(extracted).filter((name) =>
      existsSync(join(extracted, name, "steamui", "index.html")),
    );
    if (versions.length === 1) return join(extracted, versions[0]);
    if (versions.length > 1) {
      throw new Error(`${extracted} holds ${versions.length} client versions; name one.`);
    }
  }
  return null;
};

const manifestVersion = (path) => {
  if (!existsSync(path)) return null;
  return readFileSync(path, "utf8").match(/"version"\s+"(\d+)"/u)?.[1] ?? null;
};

/**
 * Which build a client root is: its channel (`package/beta`), its client version (the `version` of
 * the channel's `package/steam_client_<channel>_win64.manifest`, the same file WSGM reads at runtime)
 * and the content hash of its `sp.js`, which Steam's own log lines carry. A snapshot extracted from
 * Valve's package has no package folder and records both in the package-identity.json beside it.
 * Any part that cannot be read is null.
 */
export const readClientIdentity = (root, spContenthash = null) => {
  const beta = join(root, "package", "beta");
  let channel = existsSync(beta) ? readFileSync(beta, "utf8").trim() || Stable : null;
  let version = null;
  if (channel !== null || isDirectory(join(root, "package"))) {
    channel ??= Stable;
    const prefix = channel === Stable ? "steam_client_" : `steam_client_${channel}_`;
    version = manifestVersion(join(root, "package", `${prefix}win64.manifest`));
  } else {
    // <snapshot>/active-source/<version> beside <snapshot>/package-identity.json.
    const identity = join(root, "..", "..", "package-identity.json");
    if (existsSync(identity)) {
      const recorded = JSON.parse(readFileSync(identity, "utf8").replace(/^﻿/u, ""));
      channel = channelOfFolder(recorded.channel) ?? recorded.channel ?? null;
      version = typeof recorded.version === "string" ? recorded.version : null;
    }
  }
  return { channel, version, spContenthash };
};

const children = (node) =>
  Object.entries(node)
    .filter(
      ([name, child]) => name !== "loc" && name !== "range" && child && typeof child === "object",
    )
    .map(([, child]) => child);

// The chunk file names the webpack runtime can request, with their content hashes:
// `__webpack_require__.u` maps a chunk id to `(names[id] || id) + ".js?contenthash=" + hashes[id]`,
// so its objects' keys are every chunk, the object read through `||` names the ones not called by
// their id, and the other object holds each one's hash.
export const chunkFiles = (program) => {
  const files = new Map();
  const stack = [program];
  while (stack.length) {
    const node = stack.pop();
    if (!node || typeof node !== "object") continue;
    if (Array.isArray(node)) {
      stack.push(...node);
      continue;
    }
    const isChunkFileName =
      node.type === "AssignmentExpression" &&
      node.left.type === "MemberExpression" &&
      !node.left.computed &&
      node.left.property.name === "u" &&
      /^(?:ArrowFunctionExpression|FunctionExpression)$/u.test(node.right.type);
    if (!isChunkFileName) {
      stack.push(...children(node));
      continue;
    }
    const ids = new Set();
    const names = new Map();
    const hashes = new Map();
    const inner = [node.right.body];
    while (inner.length) {
      const child = inner.pop();
      if (!child || typeof child !== "object") continue;
      if (Array.isArray(child)) {
        inner.push(...child);
        continue;
      }
      if (child.type === "ObjectExpression") {
        for (const property of child.properties) {
          if (typeof property.key?.value !== "number") continue;
          ids.add(property.key.value);
          const value = property.value?.value;
          if (typeof value === "string" && /^[0-9a-f]{16,}$/u.test(value)) {
            hashes.set(property.key.value, value);
          }
        }
      }
      if (
        child.type === "LogicalExpression" &&
        child.operator === "||" &&
        child.left.type === "MemberExpression" &&
        child.left.object.type === "ObjectExpression"
      ) {
        for (const property of child.left.object.properties) {
          if (typeof property.key?.value === "number" && typeof property.value.value === "string") {
            names.set(property.key.value, property.value.value);
          }
        }
      }
      inner.push(...children(child));
    }
    for (const id of ids) files.set(`${names.get(id) ?? id}.js`, hashes.get(id) ?? null);
  }
  return files;
};

// The scripts the client loads: index.html's entry scripts and their runtime's chunks, by file name
// with each chunk's content hash.
const loadableScripts = (steamui) => {
  const html = readFileSync(join(steamui, "index.html"), "utf8");
  const entries = [...html.matchAll(/<script\b[^>]*\bsrc="\/?([^"?#]+\.js)/gu)].map(
    (match) => match[1],
  );
  const files = new Map(entries.map((entry) => [entry, null]));
  for (const entry of entries) {
    const text = readFileSync(join(steamui, entry), "utf8");
    for (const [file, hash] of chunkFiles(parser.parse(text, {}))) files.set(file, hash);
  }
  if (files.size === entries.length) {
    throw new Error(`No webpack chunk map found in the entry scripts of ${steamui}/index.html.`);
  }
  return files;
};

/**
 * Every webpack factory the client can load, by module id, as source text, and the client's
 * identity. A factory is a function-valued property with a numeric key; the files are parsed, never
 * evaluated. Steam leaves the chunks of earlier builds in steamui, so only the scripts the current
 * runtime maps count, and its localization chunks hold string tables, not code.
 */
export const readClient = (root) => {
  const steamui = join(root, "steamui");
  const scripts = loadableScripts(steamui);
  const modules = new Map();
  for (const [file] of scripts) {
    if (file.startsWith("localization/") || !existsSync(join(steamui, file))) continue;
    const text = readFileSync(join(steamui, file), "utf8");
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
      stack.push(...children(node));
    }
  }
  return { modules, identity: readClientIdentity(root, scripts.get("sp.js") ?? null) };
};

/**
 * The newest snapshot under `artifacts/steam-channel-<yyyymmdd>/<stable|beta>` of any channel but
 * `excluded`, as `{ root, channel, folder }`, or null when there is none.
 */
export const latestSnapshot = (artifacts, excluded) => {
  if (!isDirectory(artifacts)) return null;
  const dated = readdirSync(artifacts)
    .filter((name) => /^steam-channel-\d{8}$/u.test(name))
    .sort()
    .reverse();
  for (const name of dated) {
    for (const folder of ["stable", "beta"]) {
      const channel = channelOfFolder(folder);
      if (channel === excluded) continue;
      const root = resolveClientRoot(join(artifacts, name, folder));
      if (root) return { root, channel, folder: join(artifacts, name, folder) };
    }
  }
  return null;
};

/** The checked builds WSGM ships, as the runtime reads them. */
export const readKnownBuilds = (path) => JSON.parse(readFileSync(path, "utf8")).builds;

export const isKnownBuild = (builds, identity) =>
  builds.some((build) => build.channel === identity.channel && build.version === identity.version);

/**
 * Records a build whose every fingerprint matched exactly one module, replacing an entry for the
 * same channel and version. Earlier builds stay: a user may not have updated yet.
 */
export const recordBuild = (path, identity, checked) => {
  const document = JSON.parse(readFileSync(path, "utf8"));
  const entry = {
    channel: identity.channel,
    version: identity.version,
    spContenthash: identity.spContenthash,
    checked,
  };
  document.builds = [
    ...document.builds.filter(
      (build) => !(build.channel === entry.channel && build.version === entry.version),
    ),
    entry,
  ].sort((a, b) => a.channel.localeCompare(b.channel) || a.version.localeCompare(b.version));
  writeFileSync(path, `${JSON.stringify(document, null, 2)}\n`);
};
