// Exercises only the emitted helper over inert Steam window/history fixtures.
import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import {
  declarations,
  fragment,
  instantiate,
  loadAsset,
  withSource,
} from "../external/steam-ui-toolkit/eng/check-harness.mjs";

const asset = loadAsset(
  process.argv[2] ??
    fileURLToPath(new URL("../src/WSGM/Core/SteamUiAssets/NativeQamBootstrap.js", import.meta.url)),
);
const source = fragment(asset, "overlay-browser.ts");
const route = "/native-external-browser";
const makeHistory = () => {
  const listeners = new Set();
  return {
    location: { pathname: "/wsgm/browser", state: undefined },
    listeners,
    pushes: [],
    listen(callback) {
      listeners.add(callback);
      return () => listeners.delete(callback);
    },
    push(pathname, state) {
      this.pushes.push({ pathname, state });
      this.change(pathname, state);
    },
    change(pathname, state) {
      this.location = { pathname, state };
      for (const callback of [...listeners]) callback(this.location, "REPLACE");
    },
  };
};
const makeWindow = (appId = 220, processId = 1234, gameId = "220", kind = "gamepad") => {
  const history = makeHistory();
  const instance = {
    params: { browserInfo: { m_unPID: processId, m_unAppID: appId, m_gameID: gameId } },
    IsGamepadUIOverlayWindow: () => kind === "gamepad",
    IsDesktopOverlayWindow: () => kind === "desktop",
    History: history,
    requests: [],
    navigations: [],
    Navigate(pathname, replace, sameRoute, state) {
      this.navigations.push({ pathname, replace, sameRoute, state });
      this.History.push(pathname, state);
    },
    Navigator: {
      ExternalWeb(url) {
        instance.requests.push(url);
        instance.History.push(route, { url });
      },
    },
  };
  return instance;
};
const setup = (windows) => {
  let desktopReads = 0;
  const window = {
    SteamUIStore: { WindowStore: { OverlayWindows: windows } },
    appStore: { GetAppOverviewByAppID: (id) => ({ display_name: `Game ${id}` }) },
  };
  const useWindow = withSource(
    () => windows[0],
    "function(){return React.useContext(context).instance}",
  );
  const runtime = {
    exported(tokens, shape) {
      const value = tokens.includes("VirtualKeyboardManager")
        ? useWindow
        : { GamepadUI: { ExternalWeb: () => route } };
      assert.ok(shape(value), "resolver must validate the documented export shape");
      return value;
    },
  };
  for (const instance of windows) {
    Object.defineProperty(instance, "DesktopOverlay", {
      configurable: true,
      get: () => {
        desktopReads++;
        return instance.desktop;
      },
    });
  }
  const resolve = instantiate({ window }, source, "resolveSteamOverlayBrowser");
  return { helper: resolve(runtime), window, desktopReads: () => desktopReads };
};

{
  const game = makeWindow();
  const { helper, desktopReads } = setup([game]);
  assert.equal(helper.useWindow(), game);
  assert.equal(helper.identify(game).title, "Game 220");
  assert.equal(
    game.History.listeners.size,
    0,
    "resolving/identifying must not create a browser or subscription",
  );
  assert.equal(desktopReads(), 0, "gamepad identity must not instantiate DesktopOverlay");
  assert.equal(helper.open(game, "https://example.com/guide"), true);
  assert.deepEqual(game.requests, ["https://example.com/guide"]);
  assert.equal(game.History.listeners.size, 1);
  const nativeState = {
    initialIndex: 4,
    finalIndex: 3,
    url: "https://example.com/guide",
    nativeOnly: { untouched: true },
  };
  game.History.change(route, nativeState);
  game.History.change("/wsgm/browser");
  assert.equal(helper.open(game), true);
  assert.equal(
    game.requests.length,
    1,
    "resume must never invoke ExternalWeb(undefined) or load another URL",
  );
  assert.deepEqual(game.navigations.at(-1), {
    pathname: route,
    replace: false,
    sameRoute: false,
    state: { initialIndex: 4, finalIndex: 3, url: "https://example.com/guide" },
  });
  assert.notEqual(game.navigations.at(-1).state, nativeState, "native route state is copied");
  assert.equal(nativeState.nativeOnly.untouched, true);
  game.History.change(route, { ...nativeState, finalIndex: 8 });
  game.History.change("/wsgm/browser");
  helper.open(game);
  assert.equal(game.navigations.at(-1).state.finalIndex, 8, "native history changes are retained");
  const oldHistory = game.History;
  game.History = makeHistory();
  helper.open(game);
  assert.equal(
    oldHistory.listeners.size,
    0,
    "history replacement releases the previous generation",
  );
  assert.equal(
    game.requests.at(-1),
    "about:blank",
    "a new generation has no invented native indices",
  );
  assert.equal(game.History.listeners.size, 1);
  helper.dispose();
  helper.dispose();
  assert.equal(game.History.listeners.size, 0);
  assert.equal(helper.open(game, "https://example.com"), false);
  assert.equal(helper.identify(game), null);
}

{
  const first = makeWindow();
  const second = makeWindow(221, 1235, "221");
  const { helper } = setup([first, second]);
  for (const [game, initial] of [
    [first, 2],
    [second, 7],
  ]) {
    helper.open(game, "https://example.com");
    game.History.change(route, { initialIndex: initial, finalIndex: initial + 1 });
    game.History.change("/wsgm/browser");
  }
  helper.open(first);
  helper.open(second);
  assert.equal(first.navigations.at(-1).state.initialIndex, 2);
  assert.equal(second.navigations.at(-1).state.initialIndex, 7);
  helper.dispose();
  assert.equal(first.History.listeners.size + second.History.listeners.size, 0);
}

for (const state of [
  {},
  { initialIndex: -1 },
  { initialIndex: 1.5 },
  { initialIndex: NaN },
  { initialIndex: Number.MAX_SAFE_INTEGER + 1 },
  { initialIndex: 8, finalIndex: 3 },
  { initialIndex: 2, finalIndex: Infinity },
  { initialIndex: 2, url: "file:///secret" },
]) {
  const game = makeWindow();
  const { helper } = setup([game]);
  helper.open(game, "https://example.com");
  game.History.change(route, state);
  game.History.change("/wsgm/browser");
  helper.open(game);
  assert.deepEqual(
    game.requests,
    ["https://example.com", "about:blank"],
    "invalid native state cannot be used for resume",
  );
  assert.equal(game.navigations.length, 0);
  helper.dispose();
}

{
  const game = makeWindow(0xffffffff, 0xffffffff, "18446744073709551615");
  const { helper, window } = setup([game]);
  assert.ok(helper.identify(game), "uint32/uint64 maximum identities remain valid");
  for (const [field, invalid] of [
    ["m_unPID", 0x100000000],
    ["m_unAppID", -1],
    ["m_unAppID", 1.2],
    ["m_gameID", "18446744073709551616"],
    ["m_gameID", "0220"],
  ]) {
    const previous = game.params.browserInfo[field];
    game.params.browserInfo[field] = invalid;
    assert.equal(helper.identify(game), null);
    game.params.browserInfo[field] = previous;
  }
  const duplicate = makeWindow(0xffffffff, 0xffffffff, "18446744073709551615");
  window.SteamUIStore.WindowStore.OverlayWindows.push(duplicate);
  assert.equal(helper.identify(game), null, "ambiguous game identity is refused");
  window.SteamUIStore.WindowStore.OverlayWindows = [game, game];
  assert.equal(helper.identify(game), null, "duplicate exact instances are also refused");
  window.SteamUIStore.WindowStore.OverlayWindows = [];
  assert.equal(
    helper.open(game, "https://example.com"),
    false,
    "a stale or main-window context cannot navigate",
  );
  helper.dispose();
}

{
  const game = makeWindow();
  const { helper, desktopReads } = setup([game]);
  for (const url of [
    "file:///secret",
    "steam://run/220",
    "javascript:alert(1)",
    "data:text/html,unsafe",
    "https://name:pass@example.com",
    "https://example.com/" + "x".repeat(4096),
  ]) {
    assert.equal(helper.open(game, url), false);
  }
  assert.equal(game.requests.length, 0);
  assert.equal(desktopReads(), 0);
  game.IsDesktopOverlayWindow = () => true;
  assert.equal(helper.identify(game), null, "one window cannot claim both semantic overlay types");
  game.IsGamepadUIOverlayWindow = () => false;
  game.IsDesktopOverlayWindow = () => false;
  assert.equal(helper.open(game), false);
  helper.dispose();
}

{
  const game = makeWindow(220, 1234, "220", "desktop");
  const calls = [];
  game.desktop = {
    web_requests: [{ requestid: "retained", strURL: "https://example.com" }],
    LoadWebRequestsIfNeeded: () => calls.push("load"),
    AddWebPageRequest: (url, select) => calls.push(["add", url, select]),
    SetWindowVisibility: (...args) => calls.push(["visible", ...args]),
    BringWindowToFront: (...args) => calls.push(["front", ...args]),
  };
  const { helper, desktopReads } = setup([game]);
  helper.identify(game);
  assert.equal(desktopReads(), 0, "desktop identity alone must not initialize its browser store");
  assert.equal(helper.open(game), true);
  assert.deepEqual(calls, ["load", ["visible", "Browser", 1], ["front", "Browser"]]);
  assert.equal(
    game.History.listeners.size,
    0,
    "desktop retention is Steam's native store contract",
  );
  calls.length = 0;
  helper.open(game, "https://example.com/new");
  assert.deepEqual(calls, [
    ["add", "https://example.com/new", true],
    ["front", "Browser"],
  ]);
  helper.dispose();
}

{
  const pageSource =
    declarations(asset, "ui-kit.ts", [
      "SteamUiKitStyles",
      "steamUiKitStyles",
      "steamUiKitStyle",
      "renderSteamUiLevel",
    ]) +
    "\n" +
    fragment(asset, "consumer/browser.ts");
  for (const [query, action, expected] of [
    ["https://example.com/guide", "lookup", "https://example.com/guide"],
    ["example.com/guide", "lookup", "https://example.com/guide"],
    ["shield & poison", "lookup", "https://www.google.com/search?q=shield%20%26%20poison"],
    [
      "shield & poison",
      "game-search",
      "https://www.google.com/search?q=Game%20220%20shield%20%26%20poison",
    ],
    ["steam://run/example.com", "lookup", null],
    ["file:///C:/secret.com", "lookup", null],
    ["https://name:pass@example.com", "lookup", null],
    ["javascript:alert(1)", "lookup", null],
  ]) {
    const opened = [];
    const errors = [];
    let states = 0;
    let actions;
    const ui = {
      focusable: "focusable",
      react: {
        createElement: (type, props, ...children) => ({ type, props, children }),
        useState: () => [states++ === 0 ? query : "", (value) => errors.push(value)],
      },
      textField: "text-field",
    };
    const instance = {};
    const native = {
      useWindow: () => instance,
      identify: () => ({ title: "Game 220", appId: 220 }),
      open: (window, url) => {
        assert.equal(window, instance);
        opened.push(url);
        return true;
      },
    };
    const page = instantiate(
      {
        registerSteamPage: (options) => options,
        resolveSteamSettingsComponents: () => ui,
        resolveSteamOverlayBrowser: () => native,
        renderSteamUiActions: (_ui, value) => {
          actions = value;
          return value;
        },
      },
      pageSource,
      "browserGuidePage",
    );
    assert.equal(page.prepare(ui, {}), null);
    const rendered = page.Page({
      context: {
        ui: () => ui,
        state: () => ({ home: "about:blank", searchPrefix: "https://www.google.com/search?q=" }),
      },
    });
    assert.equal(
      rendered.props.className,
      "steam-ui-kit-page",
      "native header inset belongs to the standard page shell",
    );
    assert.equal(
      rendered.children[0].type,
      "style",
      "content helpers require their stylesheet in this document",
    );
    assert.ok(rendered.children[0].children.join("").includes("--basicui-header-height"));
    assert.equal(
      rendered.children[1].props.style.overflowY,
      "auto",
      "short viewports retain a scrollable body",
    );
    assert.deepEqual(opened, [], "rendering never transmits game identity or opens a site");
    actions.find((candidate) => candidate.id === action).onClick();
    assert.deepEqual(
      opened,
      expected === null ? [] : [expected],
      `explicit ${action} handles ${query}`,
    );
    if (expected === null) assert.ok(errors.some((error) => error.includes("Only HTTP")));
    page.release();
  }
}

console.log(
  "Overlay browser: semantic identity, native resume, generations, disposal and safe user-initiated lookup passed.",
);
