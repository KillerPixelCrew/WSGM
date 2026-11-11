// Answers "did every Steam UI patch reach Verified in the running WSGM session?" from WSGM's log,
// without attaching to Steam, its CEF endpoint or WSGM itself.
//
//   node eng/check-steam-patch-health.mjs [--log <path>] [--wait <seconds>] [--all-sessions]
//
// This is the step after an authorized eng/dev-deploy.ps1, once Steam and Big Picture have fully
// started. It only reads files: the log (default %LOCALAPPDATA%\WSGM\wsgm.log) and, when present, the
// one rotated archive beside it (wsgm.old.log), read first so a session that began before rotation
// keeps its start. Nothing here connects to Steam or CEF.
//
// A session is a `---- WSGM <version> started, args: [...]` line whose args carry --shell or --boot, up to the
// next one. A shell start that hands over to an elevated copy ("handed over to elevated instance")
// or finds the shell mutex taken ("Another WSGM shell instance is running; exiting.") ends at once
// and starts no session; the instance it deferred to keeps logging. Each such line is matched to
// the latest still-open start it can belong to, so an --elevated-relaunch start that takes over
// becomes the new session. Setup, answer export, Settings and other non-shell starts are ignored.
//
// Every patch logs `Steam UI patch <id>: <State>[ — <reason>][ (previous state held for N more
// polls)]` when its state changes, apart from the transitional Applying and Applied and a Retrying that only waits for the bridge. A patch passes when its last state in the session is Verified, or
// Disabled: by its setting, or, for the Big Picture-only steam-ui.pages and steam-ui.home-carousel,
// because Big Picture is closed (WSGM wants those only while it is up). Anything else fails: a final Incompatible, RemoveFailed, AbsentTarget,
// Degraded, Retrying, Applying or Applied, with the last reason it gave. The patch manager's
// retry-exhaustion warning ("Steam UI patches still do not [all ]hold after 8 retries; ...") fails
// the session wherever it appears, since the manager then waits for a reload. So does the native
// Settings host's "Steam Settings could not draw ..." warning: its patch stays Verified while the
// page drops rows (or a whole publication) it cannot draw, so the patch state alone hides it.
//
// --wait re-reads the log until the newest session passes or the time runs out, and stops early
// once that session carries a retry-exhaustion warning, which no later poll can undo. It then
// reports the state it last read. --all-sessions reports every shell session in the log
// separately and fails when any one fails. Exit codes: 0 pass, 1 failure, 2 no log or no shell
// session (never a pass).
import { existsSync, readFileSync } from "node:fs";
import { basename, dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const LinePattern = /^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+) \[(\w+)\s*\] (.*)$/u;
const StartPattern = /^---- WSGM (\S+) started, args: \[(.*)\]$/u;
const PatchPattern =
  /^Steam UI patch (\S+): ([A-Z][A-Za-z]*)(?: — (.*?))?(?: \(previous state held for \d+ more polls\))?$/u;
const ExhaustionPattern = /^Steam UI patches still do not (?:all )?hold after \d+ retries\b/u;
const SettingsRefusalPattern = /^Steam Settings could not draw /u;
const HandoverPattern = /handed over to elevated instance/u;
const MutexTakenPattern = /^Another WSGM shell instance is running; exiting\.$/u;
const PassingStates = new Set(["Verified", "Disabled"]);

const hasFlag = (args, flag) => new RegExp(`(?:^|[\\s"])${flag}(?=[\\s"=]|$)`, "u").test(args);
const flagsOf = (args) => args.match(/(?<=^|[\s"])--[\w-]+/gu) ?? [];

// Every shell session in the log text, oldest first. A session holds its start and every log line
// after it up to the next session's start.
export const parseSessions = (text) => {
  const entries = [];
  const lines = text.replace(/^﻿/u, "").split(/\r?\n/u);
  for (let index = 0; index < lines.length; index++) {
    const match = LinePattern.exec(lines[index]);
    if (match) {
      entries.push({
        line: index + 1,
        timestamp: match[1],
        level: match[2],
        message: match[3],
      });
    }
  }

  // Shell starts still open, newest last. Secondary instances end within moments of their start,
  // so each ending line belongs to the latest open start it can come from.
  const open = [];
  const starts = [];
  for (const [position, entry] of entries.entries()) {
    const start = StartPattern.exec(entry.message);
    if (start) {
      if (!hasFlag(start[2], "--shell") && !hasFlag(start[2], "--boot")) continue;
      const candidate = {
        position,
        line: entry.line,
        timestamp: entry.timestamp,
        version: start[1],
        flags: flagsOf(start[2]),
        elevated: hasFlag(start[2], "--elevated-relaunch"),
        secondary: false,
      };
      starts.push(candidate);
      open.push(candidate);
      continue;
    }
    const handedOver = HandoverPattern.test(entry.message);
    if (!handedOver && !MutexTakenPattern.test(entry.message)) continue;
    for (let index = open.length - 1; index >= 0; index--) {
      if (handedOver && open[index].elevated) continue;
      open[index].secondary = true;
      open.splice(index, 1);
      break;
    }
  }

  const sessions = starts.filter((start) => !start.secondary);
  return sessions.map((start, index) => {
    const end = index + 1 < sessions.length ? sessions[index + 1].position : entries.length;
    const { position, secondary, ...rest } = start;
    return { start: rest, entries: entries.slice(position + 1, end) };
  });
};

// The verdict for one session: each patch's final state and why it fails, plus every
// retry-exhaustion warning.
export const assess = (session) => {
  const patches = new Map();
  const exhaustion = [];
  const refusals = [];
  for (const entry of session.entries) {
    if (SettingsRefusalPattern.test(entry.message)) {
      refusals.push({ line: entry.line, timestamp: entry.timestamp, message: entry.message });
      continue;
    }
    if (ExhaustionPattern.test(entry.message)) {
      // The older wording names no patch, so record which ones were not holding at that moment.
      const holding = [...patches.values()]
        .filter((patch) => !PassingStates.has(patch.state))
        .map((patch) => `${patch.id} ${patch.state}`)
        .sort();
      exhaustion.push({
        line: entry.line,
        timestamp: entry.timestamp,
        message: entry.message,
        notHolding: holding,
      });
      continue;
    }
    const match = PatchPattern.exec(entry.message);
    if (!match) continue;
    const [, id, state, reason] = match;
    const patch = patches.get(id) ?? { id, verifiedAt: null, lastReason: null };
    patch.state = state;
    patch.timestamp = entry.timestamp;
    patch.line = entry.line;
    patch.reason = reason ?? null;
    if (reason) patch.lastReason = { state, reason, timestamp: entry.timestamp };
    if (state === "Verified") patch.verifiedAt = entry.timestamp;
    patches.set(id, patch);
  }
  const results = [...patches.values()]
    .sort((a, b) => a.id.localeCompare(b.id))
    .map((patch) => ({ ...patch, ok: PassingStates.has(patch.state) }));
  const failures = results.filter((patch) => !patch.ok).length;
  return {
    patches: results,
    exhaustion,
    refusals,
    failures,
    passed:
      results.length > 0 && failures === 0 && exhaustion.length === 0 && refusals.length === 0,
  };
};

// The report lines for one assessed session.
export const formatReport = (session, verdict) => {
  const { start } = session;
  const output = [
    `WSGM ${start.version} shell session started ${start.timestamp} (log line ${start.line}, ${start.flags.join(" ")}):`,
  ];
  const width = Math.max(0, ...verdict.patches.map((patch) => patch.id.length));
  for (const patch of verdict.patches) {
    let text = `${patch.ok ? "ok  " : "FAIL"} ${patch.id.padEnd(width)}  ${patch.state}`;
    if (!patch.ok) {
      text += ` at ${patch.timestamp}`;
      if (patch.reason) {
        text += ` — ${patch.reason}`;
      } else if (patch.lastReason) {
        text += ` — last reason (${patch.lastReason.state} at ${patch.lastReason.timestamp}): ${patch.lastReason.reason}`;
      }
      text += patch.verifiedAt ? ` [last Verified ${patch.verifiedAt}]` : " [never Verified]";
    }
    output.push(text);
  }
  for (const warning of verdict.exhaustion) {
    output.push(
      `FAIL retry exhaustion at ${warning.timestamp} (log line ${warning.line}): ${warning.message}`,
    );
    if (warning.notHolding.length > 0) {
      output.push(`     not Verified or Disabled then: ${warning.notHolding.join(", ")}`);
    }
  }
  for (const refusal of verdict.refusals) {
    output.push(
      `FAIL Steam Settings rows refused at ${refusal.timestamp} (log line ${refusal.line}): ${refusal.message}`,
    );
  }
  const passing = verdict.patches.length - verdict.failures;
  const summary =
    verdict.patches.length === 0
      ? "no Steam UI patch has logged a state in this session yet"
      : `${passing} of ${verdict.patches.length} patches Verified or Disabled, ${verdict.failures} failing`;
  output.push(
    `${verdict.passed ? "PASS" : "FAIL"}: ${summary}, ${verdict.exhaustion.length} retry-exhaustion warning(s), ${verdict.refusals.length} Steam Settings refusal(s).`,
  );
  return output;
};

const parseArguments = (argv) => {
  const options = { log: null, wait: 0, allSessions: false };
  for (let index = 0; index < argv.length; index++) {
    const argument = argv[index];
    if (argument === "--log" && argv[index + 1]) {
      options.log = resolve(argv[++index]);
    } else if (argument === "--wait" && argv[index + 1]) {
      options.wait = Number(argv[++index]);
      if (!Number.isFinite(options.wait) || options.wait < 0) {
        throw new Error(`--wait takes a number of seconds, not ${argv[index]}.`);
      }
    } else if (argument === "--all-sessions") {
      options.allSessions = true;
    } else {
      throw new Error(
        `Unknown argument ${argument}. Usage: node eng/check-steam-patch-health.mjs [--log <path>] [--wait <seconds>] [--all-sessions]`,
      );
    }
  }
  if (!options.log) {
    if (!process.env.LOCALAPPDATA) throw new Error("LOCALAPPDATA is not set; pass --log <path>.");
    options.log = join(process.env.LOCALAPPDATA, "WSGM", "wsgm.log");
  }
  return options;
};

// The rotated archive first, then the live file, so a session keeps its start across a rotation.
const readLog = (path) => {
  if (!existsSync(path)) return null;
  const archive = join(dirname(path), `${basename(path).replace(/\.log$/iu, "")}.old.log`);
  const parts = [];
  if (archive !== path && existsSync(archive)) parts.push(readFileSync(archive, "utf8"));
  parts.push(readFileSync(path, "utf8"));
  return { text: parts.join("\n"), files: parts.length === 2 ? [archive, path] : [path] };
};

const main = async () => {
  const options = parseArguments(process.argv.slice(2));
  const deadline = Date.now() + options.wait * 1000;
  let log;
  let sessions;
  for (;;) {
    log = readLog(options.log);
    sessions = log ? parseSessions(log.text) : [];
    const newest = sessions.at(-1);
    const verdict = newest ? assess(newest) : null;
    const settled =
      verdict && (verdict.passed || verdict.exhaustion.length > 0 || verdict.refusals.length > 0);
    if (settled || Date.now() >= deadline) break;
    await new Promise((done) => setTimeout(done, 2000));
  }

  if (!log) {
    console.error(
      `NO LOG: ${options.log} does not exist. Nothing was checked; this is not a pass.`,
    );
    return 2;
  }
  if (sessions.length === 0) {
    console.error(
      `NO SHELL SESSION: ${log.files.join(" + ")} has no WSGM --shell start. Nothing was checked; this is not a pass.`,
    );
    return 2;
  }

  console.log(`Log: ${log.files.join(" + ")}`);
  const chosen = options.allSessions ? sessions : [sessions.at(-1)];
  let failed = 0;
  for (const [index, session] of chosen.entries()) {
    if (index > 0) console.log("");
    const verdict = assess(session);
    if (!verdict.passed) failed++;
    for (const line of formatReport(session, verdict)) console.log(line);
  }
  if (options.allSessions) {
    console.log("");
    console.log(
      `${failed ? "FAIL" : "PASS"}: ${chosen.length - failed} of ${chosen.length} shell sessions pass.`,
    );
  }
  return failed ? 1 : 0;
};

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().then(
    (code) => process.exit(code),
    (error) => {
      console.error(error.message);
      process.exit(2);
    },
  );
}
