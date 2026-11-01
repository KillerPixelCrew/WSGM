import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { after, test } from "node:test";
import {
  isKnownBuild,
  latestSnapshot,
  readClient,
  readClientIdentity,
  readKnownBuilds,
  recordBuild,
  resolveClientRoot,
} from "./steam-clients.mjs";

const scratch = mkdtempSync(join(tmpdir(), "wsgm-steam-clients-"));
after(() => rmSync(scratch, { recursive: true, force: true }));

const write = (path, text) => {
  mkdirSync(join(path, ".."), { recursive: true });
  writeFileSync(path, text);
};

// A client whose runtime maps chunk 3714 to sp.js with its hash and 77 to a chunk called by id.
const client = (path, { beta, version }) => {
  write(join(path, "steamui", "index.html"), '<script defer="defer" src="/library.js"></script>');
  write(
    join(path, "steamui", "library.js"),
    `r.u=e=>({3714:"sp"}[e]||e)+".js?contenthash="+{77:"00112233445566778899",3714:"7039b9918d21fc46ca79"}[e];`,
  );
  write(
    join(path, "steamui", "sp.js"),
    `(self.webpackChunksteamui=self.webpackChunksteamui||[]).push([[3714],{51008:(e,t,n)=>{"#QuickAccess_Tab_Perf_Common_Settings"}}]);`,
  );
  write(
    join(path, "steamui", "77.js"),
    `(self.webpackChunksteamui=self.webpackChunksteamui||[]).push([[77],{12:(e,t,n)=>{"other"}}]);`,
  );
  if (beta !== undefined) write(join(path, "package", "beta"), beta);
  const prefix = beta ? `steam_client_${beta.trim()}_` : "steam_client_";
  write(
    join(path, "package", `${prefix}win64.manifest`),
    `"win64"\n{\n\t"version"\t\t"${version}"\n}\n`,
  );
  return path;
};

test("a client's identity is its channel, manifest version and sp.js content hash", () => {
  const beta = client(join(scratch, "beta"), { beta: "publicbeta\n", version: "1793235415" });
  const { modules, identity } = readClient(beta);
  assert.deepEqual(identity, {
    channel: "publicbeta",
    version: "1793235415",
    spContenthash: "7039b9918d21fc46ca79",
  });
  assert.deepEqual([...modules.keys()].sort(), ["12", "51008"]);
  const stable = client(join(scratch, "stable"), { version: "1792618285" });
  assert.equal(readClientIdentity(stable).channel, "stable");
  assert.equal(readClientIdentity(stable).version, "1792618285");
});

test("an empty beta file is stable, and a missing manifest leaves the version unread", () => {
  const path = client(join(scratch, "empty-beta"), { version: "1" });
  write(join(path, "package", "beta"), "");
  assert.deepEqual(readClientIdentity(path), {
    channel: "stable",
    version: "1",
    spContenthash: null,
  });
  rmSync(join(path, "package", "steam_client_win64.manifest"));
  assert.equal(readClientIdentity(path).version, null);
});

test("an extracted package snapshot resolves to its one version and records its identity", () => {
  const snapshot = join(scratch, "extracted", "stable");
  const version = join(snapshot, "active-source", "1788652215");
  write(join(version, "steamui", "index.html"), "");
  write(
    join(snapshot, "package-identity.json"),
    `﻿${JSON.stringify({ channel: "stable", version: "1788652215" })}`,
  );
  assert.equal(resolveClientRoot(snapshot), version);
  assert.deepEqual(readClientIdentity(version), {
    channel: "stable",
    version: "1788652215",
    spContenthash: null,
  });
  assert.equal(resolveClientRoot(join(scratch, "nothing")), null);
});

test("the other channel's snapshot is the newest one of that channel", () => {
  const artifacts = join(scratch, "artifacts");
  client(join(artifacts, "steam-channel-20261012", "beta"), { beta: "publicbeta", version: "1" });
  client(join(artifacts, "steam-channel-20261012", "stable"), { version: "2" });
  client(join(artifacts, "steam-channel-20261101", "stable"), { version: "3" });
  write(join(artifacts, "steam-channel-20261201", "notes.txt"), "no client here");
  assert.equal(
    latestSnapshot(artifacts, "stable").root,
    join(artifacts, "steam-channel-20261012", "beta"),
  );
  assert.equal(
    latestSnapshot(artifacts, "publicbeta").root,
    join(artifacts, "steam-channel-20261101", "stable"),
  );
  assert.equal(latestSnapshot(join(scratch, "no-artifacts"), "stable"), null);
});

test("recording a build replaces its entry and keeps the others", () => {
  const path = join(scratch, "builds.json");
  writeFileSync(
    path,
    JSON.stringify({
      builds: [{ channel: "stable", version: "1", spContenthash: "a", checked: "2026-10-01" }],
    }),
  );
  recordBuild(path, { channel: "publicbeta", version: "2", spContenthash: "b" }, "2026-11-01");
  recordBuild(path, { channel: "stable", version: "1", spContenthash: "c" }, "2026-11-01");
  const builds = readKnownBuilds(path);
  assert.deepEqual(
    builds.map((build) => [build.channel, build.version, build.spContenthash]),
    [
      ["publicbeta", "2", "b"],
      ["stable", "1", "c"],
    ],
  );
  assert.ok(isKnownBuild(builds, { channel: "stable", version: "1" }));
  assert.ok(!isKnownBuild(builds, { channel: "publicbeta", version: "1" }));
  assert.ok(readFileSync(path, "utf8").endsWith("}\n"));
});

test("the shipped list holds valid, unique builds", () => {
  const builds = readKnownBuilds(
    new URL("../src/WSGM/Core/Steam/steam-client-builds.json", import.meta.url),
  );
  assert.ok(builds.length > 0);
  for (const build of builds) {
    assert.match(build.channel, /^[a-z]+$/u);
    assert.match(build.version, /^\d+$/u);
  }
  const keys = builds.map((build) => `${build.channel}/${build.version}`);
  assert.equal(new Set(keys).size, keys.length);
});
