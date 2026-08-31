// Runs the ownership primitives out of the SHIPPED asset against the scenarios that cost device
// sessions: reclaiming a previous bridge's work instead of tearing it down, and handing back
// exactly what was displaced.
//
// The primitives are extracted from the generated JavaScript rather than the TypeScript source, so
// this tests the bytes that are actually injected. It caught a real defect the day it was written:
// `typeof next === "object"` excluded FUNCTIONS, and every member claim replaces a method — so the
// marker was never written, the release found nothing of ours, and an overlaid method outlived its
// own removal. Four of these checks fail against that version.
//
// Node built-ins only, so it runs in an offline release build with no node_modules.
//
//   node eng/check-ownership-claims.mjs [asset path]
import { readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const assetPath =
  process.argv[2] ??
  join(repositoryRoot, "src", "WSGM", "Core", "SteamUiAssets", "NativeQamBootstrap.js");
const asset = readFileSync(assetPath, "utf8");
const start = asset.indexOf("const defineHidden");
const end = asset.indexOf("function createNativeComponentHost");
if (start < 0 || end < 0 || end <= start) {
  console.error(
    "Could not locate the ownership primitives in the asset. They are expected between " +
      "`const defineHidden` and `function createNativeComponentHost`; if the asset was " +
      "reordered, update this check rather than deleting it.",
  );
  process.exit(1);
}
const primitives = asset.slice(start, end);

const harness = `
${primitives}
return { claimValue, releaseValue, claimMember, releaseMember, memberClaimed, claimAccessor, releaseAccessor, claimed };
`;
const api = new Function(harness)();
let failures = 0;
const check = (name, condition) => {
  if (!condition) {
    console.log(`  FAIL  ${name}`);
    failures++;
  } else {
    console.log(`  ok    ${name}`);
  }
};

// --- value claim: the brightness availability flag -------------------------------------------
const keys = { marker: "__mark", original: "__orig" };
{
  const host = { flag: false };
  const first = api.claimValue(host, "flag", keys, true, false);
  check("value: claims a hidden flag", first.ok && host.flag === true);
  check("value: marker is not enumerable", !Object.keys(host).includes("__mark"));

  // The teardown trap: a second bridge sees its predecessor's work and must reclaim, not refuse.
  const second = api.claimValue(host, "flag", keys, true, false);
  check("value: reclaims its own work rather than refusing", second.ok && second.reclaimed);

  api.releaseValue(host, "flag", keys);
  check("value: restores the original", host.flag === false);
  check("value: removes its markers", !("__mark" in host) && !("__orig" in host));
}
{
  // A client that already reports available needs nothing; claiming would invent an original.
  const host = { flag: true };
  const outcome = api.claimValue(host, "flag", keys, true, false);
  check("value: stands aside when the client already set it", !outcome.ok);
}
{
  // Reclaim whose stored original went missing must fall back to `absent`, not undefined.
  const host = { flag: true };
  Object.defineProperty(host, "__mark", { value: true, configurable: true });
  api.claimValue(host, "flag", keys, true, false);
  api.releaseValue(host, "flag", keys);
  check("value: a lost original restores the absent value, not undefined", host.flag === false);
}

// --- member claim: an overlaid METHOD ---------------------------------------------------------
{
  let called = 0;
  const host = {
    Set: (v) => {
      called = v;
      return "native";
    },
  };
  const native = host.Set;
  const claim = api.claimMember(host, "Set", keys, () => (v) => {
    called = v * 2;
    return "ours";
  });
  check("member: claims a function", claim.ok);
  check("member: marks the replacement", api.memberClaimed(host, "Set", keys));
  host.Set(5);
  check("member: the overlay runs", called === 10);

  api.releaseMember(host, "Set", keys);
  check("member: restores the native method", host.Set === native);
  host.Set(5);
  check("member: the native method runs after release", called === 5);
}
{
  // A wrap that calls through, reclaimed by a second bridge: must not stack.
  const order = [];
  const host = { Go: () => order.push("native") };
  const wrapOnce = (original) => () => {
    order.push("wrap");
    return original();
  };
  api.claimMember(host, "Go", keys, wrapOnce);
  api.claimMember(host, "Go", keys, wrapOnce);
  host.Go();
  check("member: reclaim replaces rather than stacking", order.join(",") === "wrap,native");
}

// --- accessor claim: the network availability getter -------------------------------------------
{
  const proto = {};
  Object.defineProperty(proto, "avail", { get: () => false, configurable: true });
  const claim = api.claimAccessor(proto, "avail", keys, () => true);
  check("accessor: claims a prototype getter", claim.ok && proto.avail === true);

  const second = api.claimAccessor(proto, "avail", keys, () => true);
  check("accessor: reclaims its own work", second.ok && second.reclaimed);

  api.releaseAccessor(proto, "avail", keys);
  check("accessor: restores the original getter", proto.avail === false);
}
{
  const locked = {};
  Object.defineProperty(locked, "avail", { get: () => false, configurable: false });
  const outcome = api.claimAccessor(locked, "avail", keys, () => true);
  check("accessor: stands aside on a non-configurable property", !outcome.ok);
}

console.log(failures === 0 ? "\nALL PASS" : `\n${failures} FAILURE(S)`);
process.exit(failures === 0 ? 0 : 1);
