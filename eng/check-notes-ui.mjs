import assert from "node:assert/strict";
import {
  fragment,
  instantiate,
  loadAsset,
} from "../external/steam-ui-toolkit/eng/check-harness.mjs";

// Exercise the emitted page's real image owner while editors publish and secondary views unmount.
let scope;
const react = {
  createElement: (type, props, ...children) => ({ type, props, children }),
  useRef(initial) {
    const index = scope.cursor++;
    return (scope.values[index] ??= { current: initial });
  },
  useState(initial) {
    const owner = scope;
    const index = owner.cursor++;
    if (!(index in owner.values)) owner.values[index] = initial;
    return [
      owner.values[index],
      (value) => {
        owner.values[index] = typeof value === "function" ? value(owner.values[index]) : value;
      },
    ];
  },
  useEffect(effect, deps) {
    const index = scope.cursor++;
    const previous = scope.effects[index];
    if (previous && deps.every((value, at) => value === previous.deps[at])) return;
    previous?.cleanup?.();
    scope.effects[index] = { deps, cleanup: effect() };
  },
};
const requests = [];
const notes = instantiate(
  {
    AbortController,
    request: (_patch, _command, payload, _generation, signal) =>
      new Promise((resolve) => requests.push({ payload, signal, resolve })),
    registerSteamPage: () => ({}),
  },
  fragment(loadAsset(), "consumer/notes.ts"),
  "({NotesSurface})",
);
const asset = "a".repeat(64) + ".png";
const page = {
  id: "page-one",
  revision: 1,
  elements: [{ id: "image", kind: "image", asset, x: 0, y: 0, width: 400, height: 200 }],
  strokes: [],
};
const owner = () => ({ cursor: 0, values: [], effects: [] });
const draw = (instance, images, document = page) => {
  scope = instance;
  scope.cursor = 0;
  return notes.NotesSurface({
    ui: { react },
    images,
    page: document,
    mode: "navigate",
    tool: "pen",
    color: "#FFFFFF",
    width: 6,
  });
};
const close = (instance) => instance.effects.forEach((effect) => effect?.cleanup?.());
const tick = async () => {
  await Promise.resolve();
  await Promise.resolve();
};
const images = new Map();
const first = owner();
draw(first, images);
assert.equal(requests.length, 1);
draw(first, images, { ...page, revision: 2 });
assert.equal(requests.length, 1, "text/ink publications do not restart unchanged image requests");
requests[0].resolve({ dataUrl: "data:image/png;base64,example" });
await tick();
close(first);
assert.equal(requests[0].signal.aborted, true);
assert.equal(images.size, 1, "the page retains its completed image across a secondary view");
const returned = owner();
draw(returned, images);
assert.equal(requests.length, 1, "Back to the page reuses its completed image");
close(returned);
const secondImages = new Map();
const second = owner();
draw(second, secondImages, { ...page, id: "page-two" });
assert.equal(requests.length, 2, "different page instances own independent caches");
close(second);
assert.equal(requests[1].signal.aborted, true);
requests[1].resolve({ dataUrl: "late image" });
await tick();
assert.equal(
  secondImages.size,
  0,
  "leaving cancels pending images and late replies cannot repopulate the cache",
);
assert.equal(images.size, 1, "closing another instance cannot clear this page's image");
console.log(
  "Notes UI: stable image requests, secondary-view return, per-page ownership and late-reply cleanup passed.",
);
