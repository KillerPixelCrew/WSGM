// Injects WSGM's native-QAM bootstrap into the RUNNING Steam client and plays the host's role, so
// CEF-side work can be exercised without building, installing and restarting anything.
//
// Why this exists: every fault in this area so far has lived entirely in injected JavaScript or in
// the config the host hands it — an allowlist entry, a namespace ownership check, a state field
// that was published by nobody. Each one cost a full verify + build + install + restart cycle to
// see, and each would have shown up here in seconds.
//
// It is a DIAGNOSTIC, not a second implementation. The bootstrap source, the asset hash, the
// allowlist and the config shape are all read from the repository rather than restated, so a drift
// between what this exercises and what WSGM ships is not possible in the direction that matters:
// if the harness passes and the product fails, the difference is the host, not the script.
//
// Usage:
//   node qam-harness.mjs status                 what is installed right now
//   node qam-harness.mjs install                inject the bridge and install every namespace
//   node qam-harness.mjs publish <file.json>    publish {patchId: state} to the bridge
//   node qam-harness.mjs remove                 remove the namespaces and dispose the bridge
//
// It never runs WSGM and never touches configuration. It talks to Steam's debug port only.
import { readFileSync } from "node:fs";
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
const bridgeSourcePath = join(repositoryRoot, "src", "WSGM", "Core", "SteamUiBridge.cs");

// These three are the host's, and are read from its source rather than copied. The allowlist in
// particular is what a new control forgets: a patch id missing here makes subscribe() throw
// "subscription not allowlisted" during render, which Steam's error boundary turns into a blank
// tab rather than a missing row.
const readHostConstant = (pattern, what) => {
  const source = readFileSync(bridgeSourcePath, "utf8");
  const match = source.match(pattern);
  if (!match) throw new Error(`could not read ${what} from SteamUiBridge.cs`);
  return match[1];
};

const readAllowlist = () => {
  const source = readFileSync(bridgeSourcePath, "utf8");
  const block = source.match(
    /IReadOnlyDictionary<string, string\[\]> Commands =([\s\S]*?)\n {8}};/,
  );
  if (!block) throw new Error("could not read the allowlist from SteamUiBridge.cs");
  const allowed = {};
  const entry = /\["([^"]+)"\]\s*=\s*\[([^\]]*)\]/g;
  let match;
  while ((match = entry.exec(block[1])) !== null) {
    allowed[match[1]] = [...match[2].matchAll(/"([^"]*)"/g)].map((m) => m[1]);
  }
  if (!Object.keys(allowed).length) throw new Error("the allowlist parsed empty");
  return allowed;
};

const asset = readFileSync(assetPath, "utf8");
const configuration = {
  version: 1,
  namespace: readHostConstant(/private const string Namespace = "([^"]+)"/, "the bridge namespace"),
  binding: readHostConstant(/private const string BindingName = "([^"]+)"/, "the binding name"),
  // The product pins the asset's own hash so a changed script replaces a running bridge. The
  // harness does the same, for the same reason: without it an edit appears to do nothing.
  assetHash: createHash("sha256").update(asset).digest("hex").toUpperCase(),
  contextGeneration: 1,
  documentGeneration: 1,
  maximumPending: 32,
  timeoutMilliseconds: 5000,
  allowed: readAllowlist(),
};

const target = async () => {
  const response = await fetch("http://127.0.0.1:8080/json/list");
  const targets = await response.json();
  const shared = targets.find((entry) => entry.title === "SharedJSContext");
  if (!shared) throw new Error("SharedJSContext is not open; is Steam running?");
  return shared.webSocketDebuggerUrl;
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
  await session.evaluate(
    `window[${JSON.stringify(configuration.namespace)}].deliver(${JSON.stringify(
      JSON.stringify({
        version: configuration.version,
        type: "response",
        sequence: envelope.sequence,
        ok: true,
        payload: null,
      }),
    )})`,
  );
};

const connect = async () => {
  const socket = new WebSocket(await target());
  await new Promise((resolve, reject) => {
    socket.onopen = resolve;
    socket.onerror = reject;
  });
  let session;
  session = new Session(socket, (params) => {
    if (params.name !== configuration.binding) return;
    try {
      respond(session, JSON.parse(params.payload));
    } catch (error) {
      console.log("  binding payload was not readable:", String(error));
    }
  });
  await session.send("Runtime.enable");
  await session.send("Runtime.addBinding", { name: configuration.binding });
  return { session, socket };
};

const install = async (session) => {
  const source = asset.replace("__WSGM_CONFIGURATION_JSON__", JSON.stringify(configuration));
  const result = await session.evaluate(source);
  console.log("bootstrap:", result);

  const bridge = `window[${JSON.stringify(configuration.namespace)}]`;
  for (const gate of ["audio", "network", "bluetooth", "brightness", "perf"]) {
    const outcome = await session.evaluate(
      `(()=>{const b=${bridge};if(!b||!b.${gate})return 'absent';` +
        `try{return JSON.stringify(b.${gate}.install());}catch(e){return String(e);}})()`,
    );
    console.log(`  ${gate.padEnd(11)} ${outcome}`);
  }
};

const status = async (session) => {
  const bridge = `window[${JSON.stringify(configuration.namespace)}]`;
  const report = await session.evaluate(
    `(()=>{const b=${bridge};const s=window.SteamClient&&window.SteamClient.System;` +
      `const out={bridge:!!b,version:b&&b.version,` +
      `audioNamespace:!!(s&&s.Audio),audioOwned:!!(s&&s.Audio&&s.Audio.__wsgmOwnedNamespace===true),` +
      `perfNamespace:!!(s&&s.Perf),perfOwned:!!(s&&s.Perf&&s.Perf.__wsgmOwnedNamespace===true)};` +
      `if(b){for(const g of ['audio','network','bluetooth','brightness','perf']){` +
      `try{out[g]=b[g]?b[g].status():'absent';}catch(e){out[g]='ERR '+e;}}` +
      // nativeComponents.status takes a KIND. Calling it bare reports registered:false for every
      // component, which reads as "nothing registered" and is purely an artefact of the call.
      `try{out.components={};for(const k of ['tdp','autoTdp','frameLimit','overlayLevel',` +
      `'controllerTarget','resolution','valveVrr','valveProfileHeader','valveReset']){` +
      `const s=b.nativeComponents.status(k);out.components[k]=s.registered;}` +
      `const any=b.nativeComponents.status('tdp');out.lastAppend=any.lastAppend;` +
      `out.renderOutcomes=any.renderOutcomes;out.rootWrapped=any.performanceRootWrapped;}` +
      `catch(e){out.components='ERR '+e;}}` +
      `return JSON.stringify(out,null,1);})()`,
  );
  console.log(report);
};

const publish = async (session, file) => {
  const states = JSON.parse(readFileSync(file, "utf8"));
  const bridge = `window[${JSON.stringify(configuration.namespace)}]`;
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
    const outcome = await session.evaluate(`${bridge}.deliver(${JSON.stringify(envelope)})`);
    console.log(`  published ${patchId}: ${outcome}`);
  }
};

const remove = async (session) => {
  const bridge = `window[${JSON.stringify(configuration.namespace)}]`;
  for (const gate of ["perf", "brightness", "bluetooth", "network", "audio"]) {
    const outcome = await session.evaluate(
      `(()=>{const b=${bridge};if(!b||!b.${gate})return 'absent';` +
        `try{return JSON.stringify(b.${gate}.remove());}catch(e){return String(e);}})()`,
    );
    console.log(`  ${gate.padEnd(11)} ${outcome}`);
  }
  await session.evaluate(
    `(()=>{const b=${bridge};if(b&&b.dispose)b.dispose('harness');return true;})()`,
  );
};

const [command, argument] = process.argv.slice(2);
const { session, socket } = await connect();
try {
  if (command === "install") await install(session);
  else if (command === "publish") await publish(session, argument);
  else if (command === "remove") await remove(session);
  else await status(session);

  // Requests arrive asynchronously after a control renders, so hold briefly to print them.
  if (command === "install" || command === "publish") {
    await new Promise((resolve) => setTimeout(resolve, 1500));
  }
} finally {
  socket.close();
}
