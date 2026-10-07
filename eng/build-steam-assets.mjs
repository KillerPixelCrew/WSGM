// Builds the injected Steam UI asset from the toolkit's TypeScript fragments and WSGM's own.
//
// The shipped asset is reviewable JavaScript, not a bundle: a maintainer reads it beside the page
// it is injected into, and the drift gate compares it. So this compiles with type-stripping only
// (no bundling, no minification, no helpers) and formats the result with the repository's pinned
// Prettier so the output is byte-stable across machines.
//
//   node eng/build-steam-assets.mjs          regenerate the asset
//   node eng/build-steam-assets.mjs --check  fail if it is out of date
//
// The --check mode is what CI runs. It rebuilds into memory and compares, so a source edit that was
// never compiled cannot ship, and neither can a hand edit of the generated file. The runtime hashes
// the embedded bytes itself (SteamUiAssetCatalog), so nothing here rewrites C# source.
//
// The fragment list and its order are the toolkit's (steam-ui-fragments.mjs), the same list its own
// prelude build and checks use. WSGM's fragments under SteamUiAssets/Source are appended after the
// toolkit's component host, sorted, and a new one needs no change here.

import { createHash } from "node:crypto";
import { readFile, readdir, writeFile } from "node:fs/promises";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { compileSteamUiAsset } from "../external/steam-ui-toolkit/eng/steam-ui-fragments.mjs";
import { format, resolveConfig } from "prettier";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const assetDirectory = join(repositoryRoot, "src", "WSGM", "Core", "SteamUiAssets");
const sourceDirectory = join(assetDirectory, "Source");
const outputPath = join(assetDirectory, "NativeQamBootstrap.js");

const check = process.argv.includes("--check");

const compiled = await compileSteamUiAsset({
  extraDirectories: [sourceDirectory, join(sourceDirectory, "gates")],
  typescript: join(repositoryRoot, "node_modules", "typescript", "lib", "tsc.js"),
});

const options = {
  ...(await resolveConfig(outputPath, { editorconfig: true })),
  parser: "babel",
  filepath: outputPath,
};
const formatAsset = (input) => format(input, options);
const formatted = await formatAsset(compiled);
const sha256 = createHash("sha256").update(formatted, "utf8").digest("hex").toUpperCase();

if (check) {
  const problems = [];
  const currentAsset = await readFile(outputPath, "utf8").catch(() => null);
  if (currentAsset !== formatted) {
    problems.push(`${relative(repositoryRoot, outputPath)} does not match its TypeScript source.`);
  }

  // The shipped set is one reviewed file. Anything else under the asset directory is either a
  // stray build output or a new asset nobody decided to embed, and both must stop the gate.
  const shipped = (await readdir(assetDirectory, { withFileTypes: true }))
    .filter((entry) => entry.isFile() && entry.name.endsWith(".js"))
    .map((entry) => entry.name)
    .sort((left, right) => left.localeCompare(right, "en", { sensitivity: "variant" }));
  if (shipped.length !== 1 || shipped[0] !== "NativeQamBootstrap.js") {
    problems.push(
      `Steam UI assets must stay an explicit, reviewed set; found: ${shipped.join(", ") || "none"}.`,
    );
  }

  // The asset is embedded and evaluated in one CDP call, so its bytes are the contract: non-empty,
  // UTF-8, and without a byte-order mark that would land inside the evaluated expression.
  const bytes = await readFile(outputPath).catch(() => null);
  if (bytes === null || bytes.length === 0) {
    problems.push(`${relative(repositoryRoot, outputPath)} must not be empty.`);
  } else if (bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) {
    problems.push(
      `${relative(repositoryRoot, outputPath)} must be UTF-8 without a byte-order mark.`,
    );
  } else {
    new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  }

  if (problems.length > 0) {
    throw new Error(`${problems.join("\n")}\nRun: npm run steam-assets:build`);
  }
  console.log(`Steam UI asset is current: SHA-256 ${sha256}`);
} else {
  await writeFile(outputPath, formatted, "utf8");
  console.log(`Steam UI asset built from TypeScript: SHA-256 ${sha256}`);
}
