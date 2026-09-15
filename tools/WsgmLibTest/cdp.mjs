// Shared Chrome DevTools Protocol helpers for the live Steam tools in this directory (CEF port 8080).
// Nothing here decides whether an expression is safe to evaluate; that is the caller's file, section
// or command. Requires Steam launched with .cef-enable-remote-debugging.
import { readFileSync } from "node:fs";

export const PORT = 8080;

// Returns the websocket URL of the CEF target with this exact title.
export async function findTarget(title = "SharedJSContext") {
  const res = await fetch(`http://localhost:${PORT}/json`);
  const targets = await res.json();
  const target = targets.find((x) => x.title === title);
  if (!target) {
    throw new Error(`target not found: ${title}; have: ${targets.map((x) => x.title).join(", ")}`);
  }
  return target.webSocketDebuggerUrl;
}

// Sends one Runtime.evaluate and resolves with the raw protocol message, so each caller keeps its
// own reporting of CDP errors and exceptionDetails. Rejects with "timeout" when no reply arrives.
export function evaluate(wsUrl, params, timeoutMs = 20000) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(wsUrl);
    const timer = setTimeout(() => {
      try {
        ws.close();
      } catch {}
      reject(new Error("timeout"));
    }, timeoutMs);
    ws.onopen = () => ws.send(JSON.stringify({ id: 1, method: "Runtime.evaluate", params }));
    ws.onmessage = (ev) => {
      const msg = JSON.parse(ev.data);
      if (msg.id !== 1) return;
      clearTimeout(timer);
      try {
        ws.close();
      } catch {}
      resolve(msg);
    };
    ws.onerror = (e) => {
      clearTimeout(timer);
      reject(new Error("ws error: " + (e.message || e)));
    };
  });
}

// The evaluation parameters the file runners and cdp-eval use.
export function probeParams(expression) {
  return {
    expression,
    awaitPromise: true,
    returnByValue: true,
    allowUnsafeEvalBlockedByCSP: true,
    userGesture: true,
  };
}

// JSON.stringify produces a correct JS string literal with escaped backslashes, so a path such as
// "Z:\\SteamLibrary" arrives intact, except for three characters that are legal in a JSON string but
// change meaning when the literal is spliced into source code: "<" (can close a script context) and
// the U+2028/U+2029 line separators. Escape those too.
export function jsStringLiteral(value) {
  return JSON.stringify(value)
    .replace(/</g, "\\u003C")
    .replace(/\u2028/g, "\\u2028")
    .replace(/\u2029/g, "\\u2029");
}

// Runs a merged probe-*.js section by declaring __probe in a block around the file, so nothing leaks
// onto the page's global scope and the block still evaluates to the file's result.
export function withSection(expression, section) {
  if (section === undefined) return expression;
  return `{\nconst __probe = ${jsStringLiteral(section)};\n${expression}\n}`;
}

// Splits "--name value" options from positional arguments; exits with the usage line on a bad option.
export function parseArguments(argv, optionNames, usage) {
  const positional = [];
  const options = {};
  for (let i = 0; i < argv.length; i++) {
    const name = argv[i].startsWith("--") ? argv[i].slice(2) : null;
    if (name === null) {
      positional.push(argv[i]);
    } else if (optionNames.includes(name) && i + 1 < argv.length) {
      options[name] = argv[++i];
    } else {
      console.error(usage);
      process.exit(1);
    }
  }
  return { positional, options };
}

// Evaluates a file in a target and prints the returned value. A CDP error is printed and still exits
// zero, and a JavaScript exception prints undefined: inspect the output, not the exit code.
export async function runFile(file, { target = "SharedJSContext", section } = {}) {
  const expression = withSection(readFileSync(file, "utf8"), section);
  let wsUrl;
  try {
    wsUrl = await findTarget(target);
  } catch (e) {
    console.error(e.message);
    process.exit(1);
  }
  let message;
  try {
    message = await evaluate(wsUrl, probeParams(expression));
  } catch (e) {
    if (e.message !== "timeout") throw e;
    console.log("timeout");
    process.exit(1);
  }
  if (message.error) console.log("CDP ERROR", JSON.stringify(message.error));
  else console.log(message.result && message.result.result && message.result.result.value);
  process.exit(0);
}
