import assert from "node:assert/strict";
import { test } from "node:test";
import { assess, parseSessions } from "./check-steam-patch-health.mjs";

let clock = 0;
const at = () => {
  clock++;
  return `2026-11-01 10:00:${String(Math.floor(clock / 1000) % 60).padStart(2, "0")}.${String(clock % 1000).padStart(3, "0")}`;
};
const line = (level, message) => `${at()} [${level.padEnd(5)}] ${message}`;
const start = (args) => line("info", `---- WSGM 2.4.5.1 started, args: ["WSGM.dll"${args}]`);
const patch = (id, state, detail = "") =>
  line(state === "Verified" ? "info" : "warn", `Steam UI patch ${id}: ${state}${detail}`);
const log = (...lines) => lines.join("\r\n") + "\r\n";

const verdictOf = (text) => {
  const sessions = parseSessions(text);
  assert.ok(sessions.length > 0, "expected a shell session");
  return assess(sessions.at(-1));
};
const states = (verdict) =>
  Object.fromEntries(verdict.patches.map((entry) => [entry.id, [entry.state, entry.ok]]));

test("a boot deployment starts a new session instead of inheriting historical failures", () => {
  const sessions = parseSessions(
    log(
      start(" --shell"),
      patch("steam-ui.bridge", "AbsentTarget"),
      start(" --boot"),
      patch("steam-ui.bridge", "Verified"),
      patch("steam-ui.shortcut-running-state", "Verified"),
    ),
  );
  assert.equal(sessions.length, 2);
  assert.equal(assess(sessions.at(-1)).passed, true);
});

test("verified and disabled patches pass, including held-state suffixes", () => {
  const verdict = verdictOf(
    log(
      start(" --shell --desktop-resident"),
      patch("steam-ui.audio", "Applying"),
      patch("steam-ui.audio", "Applied"),
      patch("steam-ui.audio", "Verified", " (previous state held for 3 more polls)"),
      patch("wsgm.notes", "Disabled"),
    ),
  );
  assert.deepEqual(states(verdict), {
    "steam-ui.audio": ["Verified", true],
    "wsgm.notes": ["Disabled", true],
  });
  assert.equal(verdict.passed, true);
});

test("Big Picture-only patches pass Disabled while Big Picture is closed", () => {
  // The page host and Home's carousel exist only in Big Picture's own document. WSGM wants them only
  // while Big Picture is admitted, so on the desktop with Big Picture closed they end Disabled.
  const verdict = verdictOf(
    log(
      start(" --shell --desktop-resident"),
      patch("steam-ui.bridge", "Verified"),
      patch("steam-ui.pages", "Verified"),
      patch("steam-ui.home-carousel", "Verified"),
      patch("steam-ui.pages", "Disabled"),
      patch("steam-ui.home-carousel", "Disabled"),
    ),
  );
  assert.deepEqual(states(verdict), {
    "steam-ui.bridge": ["Verified", true],
    "steam-ui.home-carousel": ["Disabled", true],
    "steam-ui.pages": ["Disabled", true],
  });
  assert.equal(verdict.passed, true);
});

test("a patch that was incompatible and later verified passes", () => {
  const verdict = verdictOf(
    log(
      start(" --shell"),
      patch("steam-ui.home-carousel", "Incompatible", ' — {"homeFound":0}'),
      patch("steam-ui.home-carousel", "Retrying", " — Waiting for the Steam UI bridge."),
      patch("steam-ui.home-carousel", "Verified"),
    ),
  );
  assert.equal(verdict.passed, true);
  assert.equal(verdict.patches[0].verifiedAt !== null, true);
});

test("a patch that ends incompatible fails with its reason", () => {
  const verdict = verdictOf(
    log(
      start(" --shell"),
      patch("steam-ui.audio", "Verified"),
      patch("steam-ui.home-carousel", "Verified"),
      patch("steam-ui.home-carousel", "Incompatible", ' — {"homeFound":0}'),
    ),
  );
  assert.equal(verdict.passed, false);
  assert.equal(verdict.failures, 1);
  const failing = verdict.patches.find((entry) => !entry.ok);
  assert.equal(failing.id, "steam-ui.home-carousel");
  assert.equal(failing.state, "Incompatible");
  assert.equal(failing.reason, '{"homeFound":0}');
  assert.notEqual(failing.verifiedAt, null);
});

test("a patch that never reached Verified fails and keeps its last reason", () => {
  const verdict = verdictOf(
    log(
      start(" --shell"),
      patch(
        "steam-ui.frame-limit",
        "AbsentTarget",
        " — Steam UI SharedJsContext target is absent.",
      ),
      patch("steam-ui.frame-limit", "Applying", " (previous state held for 2 more polls)"),
      patch("steam-ui.frame-limit", "Applied"),
    ),
  );
  assert.equal(verdict.passed, false);
  const [failing] = verdict.patches;
  assert.equal(failing.state, "Applied");
  assert.equal(failing.verifiedAt, null);
  assert.equal(failing.reason, null);
  assert.deepEqual(
    [failing.lastReason.state, failing.lastReason.reason],
    ["AbsentTarget", "Steam UI SharedJsContext target is absent."],
  );
});

test("the retry-exhaustion warning fails the session in either wording", () => {
  for (const wording of [
    "Steam UI patches still do not all hold after 8 retries; waiting for the next reload, switch change or failed health check.",
    "Steam UI patches still do not hold after 8 retries; waiting for the next reload, switch change or failed health check: steam-ui.audio: Retrying.",
  ]) {
    const verdict = verdictOf(
      log(
        start(" --shell"),
        patch("steam-ui.audio", "Retrying"),
        line("warn", wording),
        patch("steam-ui.audio", "Verified"),
      ),
    );
    assert.equal(verdict.failures, 0);
    assert.equal(verdict.passed, false);
    assert.equal(verdict.exhaustion.length, 1);
    assert.match(verdict.exhaustion[0].timestamp, /^2026-11-01 /u);
    assert.deepEqual(verdict.exhaustion[0].notHolding, ["steam-ui.audio Retrying"]);
  }
});

test("lines that only mention a patch are not states", () => {
  const verdict = verdictOf(
    log(
      start(" --shell"),
      patch("steam-ui.audio", "Verified"),
      line(
        "warn",
        "Steam UI patch steam-ui.audio applied but did not verify; removing it: probe failed",
      ),
      line("warn", "Steam UI patch synchronization failed: bridge closed"),
    ),
  );
  assert.deepEqual(states(verdict), { "steam-ui.audio": ["Verified", true] });
});

test("an empty session does not pass", () => {
  const verdict = verdictOf(log(start(" --shell")));
  assert.equal(verdict.patches.length, 0);
  assert.equal(verdict.passed, false);
});

test("setup, answer export and other non-shell starts open no session", () => {
  assert.deepEqual(
    parseSessions(
      log(
        start(" --export-setup-answers=answers.json"),
        start(" --setup --answers=answers.json"),
        start(""),
        patch("steam-ui.audio", "Verified"),
      ),
    ),
    [],
  );
  const sessions = parseSessions(
    log(
      start(" --shell"),
      patch("steam-ui.audio", "Incompatible", " — old"),
      start(" --setup"),
      start(""),
      patch("steam-ui.audio", "Verified"),
    ),
  );
  assert.equal(sessions.length, 1);
  assert.deepEqual(states(assess(sessions[0])), { "steam-ui.audio": ["Verified", true] });
});

test("the newest shell session is chosen, and an elevated relaunch that takes over is one", () => {
  const sessions = parseSessions(
    log(
      start(" --shell"),
      patch("steam-ui.audio", "Verified"),
      start(" --shell --activate"),
      line("info", "Steam requires matching elevation — handed over to elevated instance (pid 1)."),
      start(" --shell --activate --elevated-relaunch"),
      line("info", "Run mode: Shell"),
      patch("steam-ui.audio", "Retrying", " — Waiting for the Steam UI bridge."),
    ),
  );
  // The relaunch found the shell mutex free, so it is a new session and the earlier one ended.
  assert.deepEqual(
    sessions.map((session) => session.start.flags),
    [["--shell"], ["--shell", "--activate", "--elevated-relaunch"]],
  );
  assert.deepEqual(states(assess(sessions.at(-1))), { "steam-ui.audio": ["Retrying", false] });
});

test("activation starts that defer to the running shell open no session", () => {
  const sessions = parseSessions(
    log(
      start(" --shell --desktop-resident"),
      patch("steam-ui.audio", "Verified"),
      start(" --shell --activate"),
      start(" --shell --activate"),
      line("info", "Run mode: Shell"),
      line("info", "Steam requires matching elevation — handed over to elevated instance (pid 1)."),
      start(" --shell --activate --elevated-relaunch"),
      line("warn", "Another WSGM shell instance is running; exiting."),
      line("info", "Steam requires matching elevation — handed over to elevated instance (pid 2)."),
      start(" --shell --activate --elevated-relaunch"),
      line("warn", "Another WSGM shell instance is running; exiting."),
      patch("wsgm.notes", "Verified"),
    ),
  );
  assert.equal(sessions.length, 1);
  assert.deepEqual(sessions[0].start.flags, ["--shell", "--desktop-resident"]);
  assert.deepEqual(states(assess(sessions[0])), {
    "steam-ui.audio": ["Verified", true],
    "wsgm.notes": ["Verified", true],
  });
});

test("rows Steam Settings could not draw fail the session though the patch is Verified", () => {
  // The native Settings gate stays installed while it drops rows it cannot draw; only the host's
  // warning shows it. Two unknown GPU colour ranges once took every WSGM row out of Settings this way.
  const verdict = verdictOf(
    log(
      start(" --shell"),
      patch("steam-ui.native-settings", "Verified"),
      line(
        "warn",
        "Steam Settings could not draw gpu/wsgm.gpu.intel/display.color-hue#display0 (invalid range); the other rows are shown.",
      ),
    ),
  );
  assert.equal(verdict.failures, 0, "the patch itself holds");
  assert.equal(verdict.refusals.length, 1);
  assert.match(verdict.refusals[0].message, /display\.color-hue/u);
  assert.equal(verdict.passed, false);
});
