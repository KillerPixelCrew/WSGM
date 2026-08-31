// Builds the injected Steam UI asset from its ordered TypeScript source fragments.
//
// The shipped asset is reviewable JavaScript, not a bundle: a maintainer reads it
// beside the page it is injected into, and the drift gate hashes it. So this
// compiles with type-stripping only — no bundling, no minification, no helpers —
// and formats the result with the repository's pinned Prettier so the output is
// byte-stable across machines.
//
//   node eng/build-steam-assets.mjs          regenerate the asset and its hash
//   node eng/build-steam-assets.mjs --check  fail if either is out of date
//
// The --check mode is what CI runs. It rebuilds into memory and compares, so a
// source edit that was never compiled cannot ship, and neither can a hand edit of
// the generated file.

import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(scriptDirectory, "..");
const assetDirectory = join(repositoryRoot, "src", "WSGM", "Core", "SteamUiAssets");
const sourceDirectory = join(assetDirectory, "Source");
const sourcePaths = [
  join(sourceDirectory, "types.ts"),
  join(sourceDirectory, "bridge.ts"),
  join(sourceDirectory, "gates", "performance.ts"),
  join(sourceDirectory, "gates", "steam-os-manager.ts"),
  join(sourceDirectory, "gates", "brightness.ts"),
  join(sourceDirectory, "gates", "bluetooth.ts"),
  join(sourceDirectory, "gates", "network.ts"),
  join(sourceDirectory, "gates", "audio.ts"),
  join(sourceDirectory, "components.ts"),
];
const outputPath = join(assetDirectory, "NativeQamBootstrap.js");
const catalogPath = join(repositoryRoot, "src", "WSGM", "Core", "SteamUiAssetCatalog.cs");

// Everything above this marker is type declaration that exists only to type the
// injected script. The asset starts at the IIFE.
const bundleMarker = "// @wsgm-bundle-start";

const check = process.argv.includes("--check");

function run(command, args, options = {}) {
  // No shell: every invocation here is `node` with an explicit script path, and a
  // shell would only add quoting hazards on paths that already contain spaces.
  const result = spawnSync(command, args, {
    cwd: repositoryRoot,
    encoding: "utf8",
    ...options,
  });
  if (result.status !== 0) {
    const detail = `${result.stdout ?? ""}${result.stderr ?? ""}`.trim();
    throw new Error(`${command} ${args.join(" ")} failed:\n${detail}`);
  }
  return result.stdout ?? "";
}

const temporaryRoot = await mkdtemp(join(tmpdir(), "wsgm-steam-assets-"));
let compiled;
try {
  const inputDirectory = join(temporaryRoot, "input");
  const outputDirectory = join(temporaryRoot, "output");
  await mkdir(inputDirectory);
  await mkdir(outputDirectory);
  const combinedSourcePath = join(inputDirectory, "NativeQamBootstrap.ts");
  const source = (await Promise.all(sourcePaths.map((path) => readFile(path, "utf8")))).join("");
  await writeFile(combinedSourcePath, source, "utf8");
  const temporaryProject = join(temporaryRoot, "tsconfig.json");
  await writeFile(
    temporaryProject,
    JSON.stringify({
      extends: join(sourceDirectory, "tsconfig.json"),
      compilerOptions: { outDir: outputDirectory, rootDir: inputDirectory },
      files: [combinedSourcePath],
    }),
    "utf8",
  );
  run("node", [
    join(repositoryRoot, "node_modules", "typescript", "lib", "tsc.js"),
    "--project",
    temporaryProject,
  ]);
  compiled = await readFile(join(outputDirectory, "NativeQamBootstrap.js"), "utf8");
} finally {
  await rm(temporaryRoot, { recursive: true, force: true });
}

const markerIndex = compiled.indexOf(bundleMarker);
if (markerIndex < 0) {
  throw new Error(
    `${relative(repositoryRoot, sourcePaths[1])} must contain "${bundleMarker}" so the emitted asset has an exact start.`,
  );
}

// Format through the same Prettier the repository formats everything else with,
// so the generated file is stable no matter which machine emitted it and never
// fails the repository's own format check.
const unformattedPath = join(assetDirectory, "NativeQamBootstrap.generated.js");
await writeFile(
  unformattedPath,
  compiled.slice(markerIndex + bundleMarker.length).trimStart(),
  "utf8",
);
let formatted;
try {
  formatted = run("node", [
    join(repositoryRoot, "node_modules", "prettier", "bin", "prettier.cjs"),
    "--parser",
    "babel",
    unformattedPath,
  ]);
} finally {
  await rm(unformattedPath, { force: true });
}

const sha256 = createHash("sha256").update(formatted, "utf8").digest("hex").toUpperCase();
const catalog = await readFile(catalogPath, "utf8");
const hashPattern = /(NativeQamBootstrapSha256\s*=\s*\r?\n\s*")([0-9A-F]{64})(";)/u;
if (!hashPattern.test(catalog)) {
  throw new Error(
    `Could not find NativeQamBootstrapSha256 in ${relative(repositoryRoot, catalogPath)}.`,
  );
}

const currentAsset = await readFile(outputPath, "utf8").catch(() => null);
const currentHash = catalog.match(hashPattern)[2];

if (check) {
  const problems = [];
  if (currentAsset !== formatted) {
    problems.push(`${relative(repositoryRoot, outputPath)} does not match its TypeScript source.`);
  }
  if (currentHash !== sha256) {
    problems.push(
      `NativeQamBootstrapSha256 is ${currentHash}, but the built asset hashes to ${sha256}.`,
    );
  }
  if (problems.length > 0) {
    throw new Error(`${problems.join("\n")}\nRun: npm run steam-assets:build`);
  }
  console.log(`Steam UI asset is current: SHA-256 ${sha256}`);
} else {
  await writeFile(outputPath, formatted, "utf8");
  await writeFile(catalogPath, catalog.replace(hashPattern, `$1${sha256}$3`), "utf8");
  console.log(`Steam UI asset built from TypeScript: SHA-256 ${sha256}`);
}
