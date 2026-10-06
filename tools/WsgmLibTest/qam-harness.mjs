// Injects WSGM's native-QAM bootstrap into the RUNNING Steam client and plays the host's role, so
// CEF-side work can be exercised without building, installing and restarting anything.
//
// Why this exists: every fault in this area so far has lived entirely in injected JavaScript or in
// the config the host hands it — an allowlist entry, a namespace ownership check, a state field
// that was published by nobody. Each one cost a full verify + build + install + restart cycle to
// see, and each would have shown up here in seconds.
//
// It is a DIAGNOSTIC, not a second implementation. The bootstrap source, the asset hash, the
// allowlist and config are emitted by the real C# bridge over a fake transport. The fixture covers
// the modules declared by that host; absent optional backends are not added by the harness.
//
// Usage:
//   node qam-harness.mjs --configuration <fixture.json> status
//   node qam-harness.mjs --configuration <fixture.json> install
//   node qam-harness.mjs --configuration <fixture.json> publish <file.json>
//   node qam-harness.mjs --configuration <fixture.json> remove
//   node qam-harness.mjs screenshot [file.png]  capture the visible Big Picture window
//
// To emit a fixture during the permitted test phase, set WSGM_QAM_CONFIGURATION to an absolute
// output path and run SteamUiSessionHostTests.EmittedBridgeConfigurationMatchesTheEmbeddedAsset.
// A fixture whose asset hash differs from the current asset is refused before connecting.
//
// It never runs WSGM and never touches configuration. It talks to Steam's debug port only.
import { readFileSync, writeFileSync } from "node:fs";
import { createHash } from "node:crypto";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = join(here, "..", "..");
const assetPath = join(
  repositoryRoot,
  "src",
  "WSGM",
  "Core",
  "SteamUiAssets",
  "NativeQamBootstrap.js",
);
// Every value spliced into a script string handed to session.evaluate() goes through this rather
// than a bare JSON.stringify. Today's inputs are all trusted (constants parsed from this repository's
// own source, or a JSON file the developer running the harness passes on the command line), but
// JSON.stringify alone does not escape </script>-shaped sequences, and CodeQL's js/code-injection
// check has no way to know the provenance is safe. Escaping here is cheap and keeps every call site
// below from having to reason about it individually.
// Built with fromCharCode rather than literal escapes so the two line-terminator characters this
// exists to neutralize never appear as raw bytes in this file's own source.
const lineSeparator = String.fromCharCode(0x2028);
const paragraphSeparator = String.fromCharCode(0x2029);
const scriptEscapes = {
  "<": "\\u003C",
  ">": "\\u003E",
  "/": "\\u002F",
  [lineSeparator]: "\\u2028",
  [paragraphSeparator]: "\\u2029",
};
const scriptEscapePattern = new RegExp(`[<>/${lineSeparator}${paragraphSeparator}]`, "g");
const stringifyForScript = (value) =>
  JSON.stringify(value).replace(scriptEscapePattern, (char) => scriptEscapes[char]);

const asset = readFileSync(assetPath, "utf8");
const args = process.argv.slice(2);
let configurationPath;
if (args[0] === "--configuration") {
  args.shift();
  configurationPath = args.shift();
}
const [command, argument] = args;
const readConfiguration = () => {
  if (!configurationPath) {
    throw new Error("Pass --configuration <fixture.json> emitted by SteamUiSessionHostTests.");
  }
  const emitted = JSON.parse(readFileSync(configurationPath, "utf8"));
  const hash = createHash("sha256").update(asset).digest("hex").toUpperCase();
  if (emitted.assetHash !== hash) {
    throw new Error("The C# bridge fixture does not match the current asset; emit it again.");
  }
  if (
    typeof emitted.namespace !== "string" ||
    typeof emitted.binding !== "string" ||
    typeof emitted.allowed !== "object" ||
    emitted.allowed === null ||
    Array.isArray(emitted.allowed) ||
    !Object.values(emitted.allowed).every(
      (commands) => Array.isArray(commands) && commands.every((name) => typeof name === "string"),
    ) ||
    !Number.isSafeInteger(emitted.version) ||
    emitted.version <= 0 ||
    typeof emitted.vocabularyRevision !== "string"
  ) {
    throw new Error("The fixture is not an emitted C# bridge configuration.");
  }
  return emitted;
};
// Screenshots need no bridge or vocabulary and retain their standalone invocation.
const configuration = command === "screenshot" ? null : readConfiguration();
// Installation follows the emitted host's optional backends. These are gate-to-state identities,
// not a command allowlist: only the emitted configuration admits a state or command.
const optionalGatePatches = new Map([
  ["audio", "steam-ui.audio"],
  ["network", "steam-ui.network"],
  ["bluetooth", "steam-ui.bluetooth"],
  ["brightness", "steam-ui.brightness"],
  ["extensionsTab", "steam-ui.extensions-tab"],
  ["gameContextMenu", "steam-ui.game-context-menu"],
  ["artworkBrowser", "wsgm.artwork-browser"],
  ["libraryImport", "wsgm.library-import"],
  ["wsgmSettings", "wsgm.settings"],
  ["wsgmGraphics", "wsgm.graphics"],
  ["navigationPanel", "steam-ui.navigation-panel"],
  ["pages", "steam-ui.pages"],
]);
const componentKinds = [
  "autoTdp",
  "frameLimit",
  "controllerTarget",
  "deviceControls",
  "resolution",
  "vrr",
  "valveProfileHeader",
  "valveReset",
  "valveRefreshRate",
  "valveOverlayLevel",
  "valveTdp",
];

const targets = async () => {
  const response = await fetch("http://127.0.0.1:8080/json/list");
  if (!response.ok) throw new Error(`Steam target discovery failed: HTTP ${response.status}`);
  return response.json();
};

const validatedSocket = (target, role) => {
  if (!target) throw new Error(`${role} is not open; is Steam running?`);
  const socket = new URL(target.webSocketDebuggerUrl);
  if (
    !["ws:", "wss:"].includes(socket.protocol) ||
    !["127.0.0.1", "localhost"].includes(socket.hostname) ||
    socket.port !== "8080"
  ) {
    throw new Error(`${role} reported a non-loopback DevTools socket`);
  }
  return socket.href;
};

const sharedTarget = async () => {
  const shared = (await targets()).find(
    (entry) =>
      entry.type === "page" &&
      entry.title === "SharedJSContext" &&
      entry.url.startsWith("https://steamloopback.host/"),
  );
  if (!shared) throw new Error("SharedJSContext is not open; is Steam running?");
  return validatedSocket(shared, "SharedJSContext");
};

const mainWindowTarget = async () => {
  const matches = (await targets()).filter(
    (entry) =>
      entry.type === "page" &&
      entry.url.startsWith("about:blank?") &&
      entry.url.includes("createflags") &&
      entry.url.includes("minwidth") &&
      !entry.url.includes("browserviewpopup") &&
      !entry.url.includes("openerid"),
  );
  if (matches.length !== 1) {
    throw new Error(`expected one MainWindow target, found ${matches.length}`);
  }
  return validatedSocket(matches[0], "MainWindow");
};

class Session {
  #socket;
  #next = 0;
  #pending = new Map();
  #onBinding;

  constructor(socket, onBinding) {
    this.#socket = socket;
    this.#onBinding = onBinding;
    socket.onmessage = (event) => {
      const message = JSON.parse(event.data);
      if (message.id !== undefined) {
        const entry = this.#pending.get(message.id);
        if (entry) {
          this.#pending.delete(message.id);
          if (message.error) entry.reject(new Error(JSON.stringify(message.error)));
          else entry.resolve(message.result);
        }
        return;
      }
      if (message.method === "Runtime.bindingCalled") this.#onBinding(message.params);
    };
  }

  send(method, params = {}) {
    const id = ++this.#next;
    return new Promise((resolve, reject) => {
      this.#pending.set(id, { resolve, reject });
      this.#socket.send(JSON.stringify({ id, method, params }));
    });
  }

  async evaluate(expression) {
    const result = await this.send("Runtime.evaluate", {
      expression,
      returnByValue: true,
      awaitPromise: true,
      // The injected page has a CSP the product's own bridge is exempted from; without this the
      // harness cannot inject the very script it exists to test.
      allowUnsafeEvalBlockedByCSP: true,
      userGesture: true,
    });
    if (result.exceptionDetails) {
      throw new Error(result.exceptionDetails.exception?.description ?? "evaluation threw");
    }
    return result.result?.value;
  }
}

// The host answers the bridge's requests. The harness answers them too, but only enough to prove
// the JS side asked the right thing: it echoes an empty success and prints the call, because what
// is being tested here is the injected half, not WSGM's services.
const respond = async (session, envelope) => {
  console.log(
    `  request  ${envelope.patchId} ${envelope.command}`,
    JSON.stringify(envelope.payload ?? null),
  );
  const response = {
    version: configuration.version,
    type: "response",
    patchId: envelope.patchId,
    command: envelope.command,
    sequence: envelope.sequence,
    contextGeneration: configuration.contextGeneration,
    documentGeneration: configuration.documentGeneration,
    ok: true,
    payload: null,
  };
  const accepted = await session.evaluate(
    `window[${stringifyForScript(configuration.namespace)}].deliver(${stringifyForScript(response)})`,
  );
  if (accepted !== true) throw new Error("bridge rejected the harness response envelope");
};

const connect = async () => {
  const socket = new WebSocket(await sharedTarget());
  await new Promise((resolve, reject) => {
    socket.onopen = resolve;
    socket.onerror = reject;
  });
  let session;
  session = new Session(socket, (params) => {
    if (params.name !== configuration.binding) return;
    try {
      void respond(session, JSON.parse(params.payload)).catch((error) => {
        console.log("  bridge response failed:", String(error));
      });
    } catch (error) {
      console.log("  binding payload was not readable:", String(error));
    }
  });
  await session.send("Runtime.enable");
  await session.send("Runtime.addBinding", { name: configuration.binding });
  return { session, socket };
};

const install = async (session) => {
  const source = asset.replace(
    "__STEAM_UI_CONFIGURATION_JSON__",
    stringifyForScript(configuration),
  );
  const result = await session.evaluate(source);
  console.log("bootstrap:", result);

  const bridge = `window[${stringifyForScript(configuration.namespace)}]`;
  for (const gate of [
    "audio",
    "network",
    "bluetooth",
    "brightness",
    "perf",
    "steamOsManager",
    "extensionsTab",
    "gameContextMenu",
    "artworkBrowser",
    "libraryImport",
    "wsgmSettings",
    "wsgmGraphics",
    "navigationPanel",
    "pages",
  ]) {
    const patchId = optionalGatePatches.get(gate);
    if (patchId && !Object.hasOwn(configuration.allowed, patchId)) {
      console.log(`  ${gate.padEnd(11)} not declared by the emitted host`);
      continue;
    }
    const outcome = await session.evaluate(
      `(()=>{const b=${bridge};const g=b&&b.gate?b.gate(${stringifyForScript(gate)}):null;` +
        `if(!g)return 'absent';try{return JSON.stringify(g.install());}catch(e){return String(e);}})()`,
    );
    console.log(`  ${gate.padEnd(11)} ${outcome}`);
  }
  for (const component of componentKinds) {
    const outcome = await session.evaluate(
      `(()=>{const b=${bridge};const g=b&&b.gate?b.gate('nativeComponents'):null;` +
        `if(!g)return 'absent';try{return JSON.stringify(g.install(${stringifyForScript(component)}));}` +
        `catch(e){return String(e);}})()`,
    );
    console.log(`  ${component.padEnd(18)} ${outcome}`);
  }
};

const status = async (session) => {
  const bridge = `window[${stringifyForScript(configuration.namespace)}]`;
  const report = await session.evaluate(
    `(()=>{const b=${bridge};const s=window.SteamClient&&window.SteamClient.System;` +
      `const out={bridge:!!b,version:b&&b.version,` +
      `audioNamespace:!!(s&&s.Audio),audioOwned:!!(s&&s.Audio&&s.Audio.__steamUiOwnedNamespace===true),` +
      `perfNamespace:!!(s&&s.Perf),perfOwned:!!(s&&s.Perf&&s.Perf.__steamUiOwnedNamespace===true)};` +
      `if(b){for(const n of ['audio','network','bluetooth','brightness','perf','steamOsManager','extensionsTab','gameContextMenu','artworkBrowser','libraryImport','wsgmSettings','wsgmGraphics','navigationPanel','pages']){` +
      `try{const g=b.gate?b.gate(n):null;out[n]=g?g.status():'absent';}catch(e){out[n]='ERR '+e;}}` +
      // nativeComponents.status takes a KIND. Calling it bare reports registered:false for every
      // component, which reads as "nothing registered" and is purely an artefact of the call.
      `try{const c=b.gate?b.gate('nativeComponents'):null;if(!c)throw new Error('gate absent');` +
      `out.components={};for(const k of ${stringifyForScript(componentKinds)}){` +
      `const s=c.status(k);out.components[k]=s.registered;}` +
      `const any=c.status('frameLimit');out.lastAppend=any.lastAppend;` +
      `out.renderOutcomes=any.renderOutcomes;out.rootWrapped=any.performanceRootWrapped;}` +
      `catch(e){out.components='ERR '+e;}}` +
      `return JSON.stringify(out,null,1);})()`,
  );
  console.log(report);
};

const publish = async (session, file) => {
  const states = JSON.parse(readFileSync(file, "utf8"));
  const bridge = `window[${stringifyForScript(configuration.namespace)}]`;
  for (const [patchId, state] of Object.entries(states)) {
    // deliver() takes an OBJECT, and rejects any envelope whose generations do not match the config
    // it was installed with. Passing a JSON string, or omitting either generation, returns a bare
    // false with no reason — which is how this harness first reported "published: false".
    const envelope = {
      version: configuration.version,
      contextGeneration: configuration.contextGeneration,
      documentGeneration: configuration.documentGeneration,
      type: "state",
      patchId,
      payload: state,
    };
    const outcome = await session.evaluate(`${bridge}.deliver(${stringifyForScript(envelope)})`);
    console.log(`  published ${patchId}: ${outcome}`);
  }
};

const navigate = async (session, route) => {
  if (typeof route !== "string" || !/^\/[a-z0-9/_:-]+$/iu.test(route)) {
    throw new Error("navigate requires one Steam route");
  }

  const outcome = await session.evaluate(
    `(()=>{const h=window.tempNavStore&&window.tempNavStore.m_history;` +
      `if(!h||typeof h.push!=='function')return 'navigation unavailable';` +
      `h.push(${stringifyForScript(route)});return 'navigated';})()`,
  );
  console.log(outcome);
};

const remove = async (session) => {
  const bridge = `window[${stringifyForScript(configuration.namespace)}]`;
  for (const gate of [
    "pages",
    "navigationPanel",
    "wsgmGraphics",
    "wsgmSettings",
    "libraryImport",
    "artworkBrowser",
    "gameContextMenu",
    "extensionsTab",
    "steamOsManager",
    "perf",
    "brightness",
    "bluetooth",
    "network",
    "audio",
  ]) {
    const outcome = await session.evaluate(
      `(()=>{const b=${bridge};const g=b&&b.gate?b.gate(${stringifyForScript(gate)}):null;` +
        `if(!g)return 'absent';try{return JSON.stringify(g.remove());}catch(e){return String(e);}})()`,
    );
    console.log(`  ${gate.padEnd(11)} ${outcome}`);
  }
  await session.evaluate(
    `(()=>{const b=${bridge};if(b&&b.dispose)b.dispose('harness');return true;})()`,
  );
};

const screenshot = async (file) => {
  let lastError;
  for (const [name, target, options] of [
    ["MainWindow surface", mainWindowTarget, { format: "png", fromSurface: true }],
    [
      "MainWindow view",
      mainWindowTarget,
      { format: "png", fromSurface: false, captureBeyondViewport: false },
    ],
    ["SharedJSContext", sharedTarget, { format: "png", fromSurface: true }],
  ]) {
    const socket = new WebSocket(await target());
    await new Promise((resolve, reject) => {
      socket.onopen = resolve;
      socket.onerror = reject;
    });
    const session = new Session(socket, () => {});
    try {
      await session.send("Page.enable");
      const result = await Promise.race([
        session.send("Page.captureScreenshot", options),
        new Promise((_, reject) =>
          setTimeout(() => reject(new Error(`${name} screenshot timed out`)), 10000),
        ),
      ]);
      if (typeof result.data !== "string" || result.data.length === 0) {
        throw new Error(`${name} returned no screenshot data`);
      }
      writeFileSync(file, Buffer.from(result.data, "base64"));
      console.log(`wrote ${file} from ${name}`);
      return;
    } catch (error) {
      lastError = error;
    } finally {
      socket.close();
    }
  }
  throw lastError;
};

if (command === "screenshot") {
  await screenshot(argument || "qam.png");
  process.exit(0);
}
const { session, socket } = await connect();
try {
  if (command === "install") await install(session);
  else if (command === "publish") await publish(session, argument);
  else if (command === "navigate") await navigate(session, argument);
  else if (command === "remove") await remove(session);
  else await status(session);

  // Requests arrive asynchronously after a control renders, so hold briefly to print them.
  if (command === "install" || command === "publish") {
    await new Promise((resolve) => setTimeout(resolve, 1500));
  }
} finally {
  socket.close();
}
