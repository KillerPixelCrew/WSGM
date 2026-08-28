import { createHash } from "node:crypto";
import { readFile, readdir } from "node:fs/promises";
import { basename, dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(scriptDirectory, "..");
const assetDirectory = join(repositoryRoot, "src", "WSGM", "Core", "SteamUiAssets");
const catalogPath = join(repositoryRoot, "src", "WSGM", "Core", "SteamUiAssetCatalog.cs");
const maximumAssetBytes = 256 * 1024;

const entries = (await readdir(assetDirectory, { withFileTypes: true })).sort((left, right) =>
  left.name.localeCompare(right.name, "en", { sensitivity: "variant" }),
);
const files = entries.filter((entry) => entry.isFile() && entry.name.endsWith(".js"));
if (files.length !== 1 || files[0].name !== "NativeQamBootstrap.js") {
  throw new Error(
    "Steam UI assets must be an explicit, reviewed set. Update this drift gate for every new asset.",
  );
}

const sourcePath = join(assetDirectory, files[0].name);
const source = await readFile(sourcePath);
if (source.length === 0 || source.length > maximumAssetBytes) {
  throw new Error(
    `${relative(repositoryRoot, sourcePath)} must be between 1 and ${maximumAssetBytes} bytes.`,
  );
}
if (source[0] === 0xef && source[1] === 0xbb && source[2] === 0xbf) {
  throw new Error(`${relative(repositoryRoot, sourcePath)} must be UTF-8 without a byte-order mark.`);
}

const decoder = new TextDecoder("utf-8", { fatal: true });
decoder.decode(source);
const sha256 = createHash("sha256").update(source).digest("hex").toUpperCase();
const catalog = await readFile(catalogPath, "utf8");
const match = catalog.match(
  /NativeQamBootstrapSha256\s*=\s*\r?\n\s*"([0-9A-F]{64})";/u,
);
if (match === null) {
  throw new Error(`Could not find the pinned NativeQamBootstrapSha256 in ${basename(catalogPath)}.`);
}
if (match[1] !== sha256) {
  throw new Error(
    `Steam UI asset drift: ${files[0].name} is ${sha256}, but the catalog pins ${match[1]}.`,
  );
}

process.stdout.write(`Steam UI asset verified: ${files[0].name} SHA-256 ${sha256}\n`);
