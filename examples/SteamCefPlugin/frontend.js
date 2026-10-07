/** @file Trusted frontend evaluated with the owner-scoped `api`; registrations are removed on unload. */
const React = api.react;
const ui = api.resolveComponents();
/**
 * Renders the shared backend count and an increment action on every contributed surface.
 * @returns A React tree; its effect unsubscribes from host state on unmount.
 */
function Counter() {
  const [count, setCount] = React.useState(0);
  React.useEffect(() => api.subscribe((state) => setCount(state.count)), []);
  return React.createElement(
    "div",
    { className: "cef-example" },
    React.createElement("h2", null, "Steam CEF example"),
    React.createElement("p", null, `Backend count: ${count}`),
    React.createElement(
      ui.dialogButton,
      { onClick: api.guard(() => api.call("increment")) },
      "Increment",
    ),
  );
}
return Promise.all([
  api.registerPage("page", { path: "/example/steam-cef", title: "CEF example" }, () =>
    React.createElement(Counter),
  ),
  api.registerMenuEntry("menu", {
    label: "CEF example",
    route: "/example/steam-cef",
    before: "power",
  }),
  api.registerQuickAccessTab("tab", { title: "CEF example" }, () => React.createElement(Counter)),
  api.registerQuickAccessRow("row", "perf", () => React.createElement(Counter)),
  api.registerLibraryAddition("library", (_react, { overview }) =>
    React.createElement("span", null, `Example ${overview?.appid ?? ""}`),
  ),
  api.registerGamePageAddition("game", (_react, { overview }) =>
    React.createElement("div", null, `CEF example for ${overview?.appid ?? ""}`),
  ),
]);
