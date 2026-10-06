import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { readFingerprints } from "./steam-fingerprints.mjs";

test("fingerprints use emitted string escapes, spreads and indexed tokens", () => {
  const source = String.raw`
    const FieldTokens = ["\"Panel\"", "line\nend", "\u0061"];
    const LabelToken = "activeTab:";
    resolver.findUnique([...FieldTokens, LabelToken]);
    resolver.resolve([FieldTokens[0], LabelToken]);
  `;
  const found = readFingerprints(source).map(({ tokens }) => tokens);
  assert.deepEqual(found, [
    ['"Panel"', "line\nend", "a", "activeTab:"],
    ['"Panel"', "activeTab:"],
  ]);
});

test("each gate's constants resolve in its own lexical scope", () => {
  const source = `
    const LabelToken = "outer";
    function first() {
      const LabelToken = "first";
      const GateTokens = [LabelToken];
      resolver.findUnique(GateTokens);
    }
    function second() {
      const GateTokens = [LabelToken];
      resolver.findUnique(GateTokens);
    }
  `;
  assert.deepEqual(
    readFingerprints(source).map(({ tokens }) => tokens),
    [["first"], ["outer"]],
  );
});

test("an unresolved emitted fingerprint fails instead of disappearing from the check", () => {
  assert.throws(
    () => readFingerprints(`resolver.findUnique([MissingToken]);`),
    /Cannot read the emitted module fingerprint/u,
  );
  assert.throws(
    () => readFingerprints(`function gate(LabelToken) { resolver.resolve([LabelToken]); }`),
    /Cannot read the emitted module fingerprint/u,
  );
});

test("comments, quoted source and unrelated arrays never create module fingerprints", () => {
  const source = String.raw`
    // resolver.resolve(["dead"]);
    const description = 'resolver.resolve(["quoted"])';
    const values = ["ordinary data"];
    const FormatTokens = ["localization keys"];
    Promise.resolve(values);
    Promise.resolve(["ordinary promise result"]);
    resolver.findUnique(["live"]);
  `;
  assert.deepEqual(
    readFingerprints(source).map(({ tokens }) => tokens),
    [["live"]],
  );
});

test("optional exports use the fingerprint passed after the resolver", () => {
  assert.deepEqual(
    readFingerprints(`optionalSteamExport(runtime, ["Settings.Root()"], isPage);`).map(
      ({ tokens }) => tokens,
    ),
    [["Settings.Root()"]],
  );
});

test("the shipped asset exposes its complete literal module queries", () => {
  const asset = readFileSync(
    new URL("../src/WSGM/Core/SteamUiAssets/NativeQamBootstrap.js", import.meta.url),
    "utf8",
  );
  const found = readFingerprints(asset).map(({ tokens }) => tokens);
  assert.ok(found.some((tokens) => tokens.includes("react.transitional.element")));
  assert.ok(found.some((tokens) => tokens.includes("k_EAppUpdateProgress_Download=")));
  assert.ok(
    found.some(
      (tokens) => tokens.includes("#Quit_Shutdown") && tokens.includes("#SwitchToDesktop"),
    ),
  );
});
