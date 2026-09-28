// The Themes page in Steam: CSSLoader-compatible themes browsed from DeckThemes, installed and managed.
//
// Laid out the way CSS Loader lays out its store and its settings, and drawn entirely with Steam's
// own components so it behaves like the rest of Big Picture under a controller: Steam's tabs over a
// toolbar and a grid of cards, one theme's details with its screenshots, and the installed themes as
// the same settings rows a host's settings page uses. WSGM owns the data, every label and every
// decision; the toolkit owns the page gate, the settings rows, the modal frame and the fail-closed
// component discovery used here.
const ThemesPatchId = "steam-ui.themes";

let themesUi: any = null;

// A command whose refusal the host explains in its next state; the page draws that, so nothing is
// swallowed here.
const themesAct = (command: string, payload: any = {}) =>
  request(ThemesPatchId, command, payload).catch(() => undefined);

const themesTabs = [
  { id: "browse", title: "Browse" },
  { id: "installed", title: "Installed" },
  { id: "profiles", title: "Profiles" },
  { id: "settings", title: "Settings" },
];

const themesGlyphs = {
  download: "M11 3h2v9.2l3.6-3.6 1.4 1.4-6 6-6-6 1.4-1.4L11 12.2zM4 19h16v2H4z",
  star: "M12 2.5l2.9 6 6.6.9-4.8 4.6 1.2 6.5L12 17.4 6.1 20.5l1.2-6.5L2.5 9.4l6.6-.9z",
  target: "M12 3a9 9 0 1 1 0 18 9 9 0 0 1 0-18zm0 2a7 7 0 1 0 0 14 7 7 0 0 0 0-14zm0 3a4 4 0 1 1 0 8 4 4 0 0 1 0-8zm0 2a2 2 0 1 0 0 4 2 2 0 0 0 0-4z",
};
const themesGlyph = (react: any, name: keyof typeof themesGlyphs) => renderSteamGlyph(react, themesGlyphs[name]);

// One row's change, sent as the command its key names. The rows are the settings renderer's, so a
// theme's switch, a patch and a component all draw and navigate like Steam's own settings.
const themesRowChange = (row, value, commit = true) => {
  if (!commit) return;
  const [kind, theme, patch, component] = String(row.key).split("\u0000");
  switch (kind) {
    case "theme":
      void themesAct("setEnabled", { name: theme, enabled: !!value });
      break;
    case "patch":
      void themesAct("setPatch", { theme, patch, value: String(value) });
      break;
    case "checkbox":
      void themesAct("setPatch", { theme, patch, value: value ? "Yes" : "No" });
      break;
    case "slider":
      void themesAct("setPatch", { theme, patch, value: String(row.labels?.[Number(value)] ?? "") });
      break;
    case "component":
      void themesAct("setComponent", { theme, patch, component, value: String(value) });
      break;
    case "profile":
      void themesAct("setProfile", { name: String(value) });
      break;
    case "setting":
      void themesAct("setSetting", { key: theme, value });
      break;
    default:
      break;
  }
};
const themesKey = (...parts: string[]) => parts.join("\u0000");

// The rows one installed theme is drawn with: its switch, and while it is on, its patches and the
// components of each patch's chosen option, indented under it.
const themesRowsOf = (theme) => {
  const rows: any[] = [];
  const description =
    theme.status === "outdated"
      ? `Update available (${theme.latestVersion}) · ${theme.author}`
      : theme.author
        ? `${theme.version} · ${theme.author}`
        : theme.version;
  rows.push({
    key: themesKey("theme", theme.name),
    kind: "boolean",
    label: theme.displayName,
    description,
    checked: !!theme.enabled,
  });
  if (!theme.enabled) return rows;
  for (const patch of theme.patches ?? []) {
    switch (patch.type) {
      case "checkbox":
        rows.push({
          key: themesKey("checkbox", theme.name, patch.name),
          kind: "boolean",
          label: patch.name,
          checked: patch.value === "Yes",
          nested: true,
        });
        break;
      case "slider":
        rows.push({
          key: themesKey("slider", theme.name, patch.name),
          kind: "range",
          label: patch.name,
          number: Math.max(0, (patch.options ?? []).indexOf(patch.value)),
          labels: patch.options,
          nested: true,
        });
        break;
      case "none":
        rows.push({ key: themesKey("none", theme.name, patch.name), kind: "note", label: patch.name, text: "", nested: true });
        break;
      default:
        rows.push({
          key: themesKey("patch", theme.name, patch.name),
          kind: "choice",
          label: patch.name,
          text: patch.value,
          choices: (patch.options ?? []).map((option) => ({ value: option, label: option })),
          nested: true,
        });
        break;
    }
    for (const component of (patch.components ?? []).filter((component) => component.on === patch.value)) {
      rows.push({
        key: themesKey("component", theme.name, patch.name, component.name),
        kind: component.type === "color-picker" ? "color" : "text",
        label: component.name,
        text: component.value,
        nested: true,
      });
    }
  }
  return rows;
};

const themesConfirm = (ui, title: string, text: string, confirmLabel: string, proceed: () => void) => {
  const h = ui.react.createElement;
  showSteamModal(ui, {
    title,
    className: "wsgm-themes-modal",
    render: (close) =>
      h(
        "div",
        { className: "wsgm-themes-modal-body" },
        h("p", null, text),
        h(
          ui.focusable,
          { "flow-children": "row", className: "wsgm-themes-modal-actions" },
          h(ui.dialogButton, { onClick: close }, "Cancel"),
          h(
            ui.dialogButtonPrimary,
            {
              onClick: () => {
                proceed();
                close();
              },
            },
            confirmLabel,
          ),
        ),
      ),
  });
};

// A profile is named in a modal, as CSS Loader names one: the enabled themes and their settings
// under one name.
function ThemesProfileNameBody({ ui, count, close }: any) {
  const react = ui.react;
  const h = react.createElement;
  const [name, setName] = react.useState("");
  return h(
    "div",
    { className: "wsgm-themes-modal-body" },
    h(
      "p",
      null,
      `This profile will combine all ${count} themes you currently have enabled. Enabling or disabling it will toggle them all at once.`,
    ),
    h(ui.textField, {
      label: "Profile Name",
      value: name,
      onChange: (event) => setName(event?.target?.value ?? ""),
    }),
    h(
      ui.focusable,
      { "flow-children": "row", className: "wsgm-themes-modal-actions" },
      h(ui.dialogButton, { onClick: close }, "Cancel"),
      h(
        ui.dialogButtonPrimary,
        {
          onClick: () => {
            if (!name.trim()) return;
            void themesAct("createProfile", { name: name.trim() });
            close();
          },
        },
        "Create",
      ),
    ),
  );
}

function ThemesCard({ item, open }: any) {
  const ui = themesUi;
  const react = ui.react;
  const h = react.createElement;
  const status =
    item.localStatus === "installed" ? "Installed" : item.localStatus === "outdated" ? "Update" : null;
  return h(
    ui.focusable,
    {
      className: "wsgm-themes-card",
      onActivate: () => open(item.id),
      onOKActionDescription: "Open",
    },
    h(
      "div",
      { className: "wsgm-themes-shot" },
      item.imageUrl ? h("img", { src: item.imageUrl, alt: "", loading: "lazy" }) : null,
      h(
        "div",
        { className: "wsgm-themes-stats" },
        h("span", null, themesGlyph(react, "download"), String(item.downloads ?? 0)),
        h("span", null, themesGlyph(react, "star"), String(item.stars ?? 0)),
        item.target ? h("span", null, themesGlyph(react, "target"), item.target) : null,
      ),
      status ? h("div", { className: `wsgm-themes-badge ${item.localStatus}` }, status) : null,
    ),
    h("div", { className: "wsgm-themes-title" }, item.displayName),
    h(
      "div",
      { className: "wsgm-themes-meta" },
      item.updated ? `${item.version} - Last Updated ${item.updated}` : item.version,
    ),
    h("div", { className: "wsgm-themes-meta" }, item.author ? `By ${item.author}` : ""),
  );
}

function ThemesDetail({ detail, busy }: any) {
  const ui = themesUi;
  const react = ui.react;
  const h = react.createElement;
  const [focusedImage, setFocusedImage] = react.useState(0);
  const item = detail.item;
  const images: string[] = detail.imageUrls ?? [];
  const shown = images[Math.min(focusedImage, Math.max(0, images.length - 1))];
  const installLabel =
    item.localStatus === "outdated" ? "Update" : item.localStatus === "installed" ? "Reinstall" : "Install";
  return h(
    ui.focusable,
    {
      className: "wsgm-themes-detail",
      onCancelButton: () => void themesAct("closeDetail"),
      onCancelActionDescription: "Back",
    },
    h(
      "div",
      { className: "wsgm-themes-detail-left" },
      h(
        "div",
        { className: "wsgm-themes-gallery" },
        images.length > 1
          ? h(
              ui.focusable,
              { className: "wsgm-themes-thumbs", "flow-children": "column" },
              ...images.map((url, index) =>
                h(
                  ui.focusable,
                  {
                    key: url,
                    className: `wsgm-themes-thumb${index === focusedImage ? " current" : ""}`,
                    onActivate: () => setFocusedImage(index),
                    onFocus: () => setFocusedImage(index),
                  },
                  h("img", { src: url, alt: "" }),
                ),
              ),
            )
          : null,
        h(
          "div",
          { className: "wsgm-themes-hero" },
          shown ? h("img", { src: shown, alt: "" }) : h("div", { className: "wsgm-themes-noimage" }, "No screenshot"),
          images.length > 1 ? h("div", { className: "wsgm-themes-count" }, `${focusedImage + 1}/${images.length}`) : null,
        ),
      ),
      h(
        "div",
        { className: "wsgm-themes-heading" },
        h("h2", null, item.displayName),
        h("span", { className: "wsgm-themes-version" }, item.version),
      ),
      h(
        "div",
        { className: "wsgm-themes-muted" },
        item.author ? `By ${item.author}` : "",
        item.updated ? ` · Last Updated ${item.updated}` : "",
      ),
      h("h3", null, "Description"),
      h(
        "p",
        { className: detail.description ? "" : "wsgm-themes-muted" },
        detail.loading ? "Loading…" : detail.error ? detail.error : detail.description || "No description provided.",
      ),
      item.targets?.length
        ? h(
            react.Fragment,
            null,
            h("h3", null, "Targets"),
            h(
              ui.focusable,
              { "flow-children": "row", className: "wsgm-themes-chips" },
              ...item.targets.map((target) =>
                h(
                  ui.dialogButton,
                  {
                    key: target,
                    onClick: () => void themesAct("browse", { filter: target, order: "", search: "" }).then(() =>
                      themesAct("closeDetail"),
                    ),
                    onOKActionDescription: `View Other "${target}" Themes`,
                  },
                  target,
                ),
              ),
            ),
          )
        : null,
      detail.dependencies?.length
        ? h(
            react.Fragment,
            null,
            h("h3", null, "Requires"),
            h(
              "div",
              { className: "wsgm-themes-muted" },
              detail.dependencies
                .map((dependency) => `${dependency.displayName}${dependency.installed ? "" : " (not installed)"}`)
                .join(", "),
            ),
          )
        : null,
    ),
    h(
      "div",
      { className: "wsgm-themes-detail-right" },
      h(
        "div",
        { className: "wsgm-themes-box" },
        h("div", { className: "wsgm-themes-box-title" }, themesGlyph(react, "star"), ` ${item.stars ?? 0} Stars`),
        h("div", { className: "wsgm-themes-muted" }, "Starring needs a DeckThemes account, which WSGM does not sign in to."),
      ),
      h(
        "div",
        { className: "wsgm-themes-box" },
        h("div", { className: "wsgm-themes-box-title" }, `${installLabel} ${item.displayName}`),
        h("div", { className: "wsgm-themes-muted" }, `${item.downloads ?? 0} Downloads`),
        h(
          ui.dialogButtonPrimary,
          {
            disabled: !!busy || !!detail.loading,
            onClick: () => void themesAct("install", { id: item.id }),
          },
          busy ? "Working…" : installLabel,
        ),
        h(
          "div",
          { className: "wsgm-themes-muted" },
          "Downloads into WSGM's themes folder, with every theme it needs. Turn it on under Installed.",
        ),
      ),
      h(ui.dialogButton, { onClick: () => void themesAct("closeDetail") }, "Back"),
    ),
  );
}

function ThemesBrowse({ state }: any) {
  const ui = themesUi;
  const react = ui.react;
  const h = react.createElement;
  const browse = state.browse ?? {};
  const [search, setSearch] = react.useState(browse.search ?? "");
  react.useEffect(() => setSearch(browse.search ?? ""), [browse.search]);
  // The first look at the store is the page's own: nothing is fetched until someone opens the tab.
  react.useEffect(() => {
    if (!browse.loading && !browse.error && (browse.items ?? []).length === 0 && !browse.page) {
      void themesAct("browse", { filter: browse.filter ?? "All", order: browse.order ?? "", search: browse.search ?? "" });
    }
  }, []);
  const ask = (changes: any) =>
    void themesAct("browse", {
      filter: browse.filter ?? "All",
      order: browse.order ?? "",
      search,
      ...changes,
    });
  if (state.detail) return h(ThemesDetail, { detail: state.detail, busy: state.busy });

  const filters = browse.filters ?? {};
  const total = Object.values(filters).reduce((sum: number, count: any) => sum + Number(count || 0), 0);
  const filterOptions = [
    { data: "All", label: h("div", { className: "wsgm-themes-filter" }, h("span", null, "All"), h("b", null, total ? String(total) : "")) },
    ...Object.keys(filters)
      .filter((name) => Number(filters[name]) > 0)
      .map((name) => ({
        data: name,
        label: h("div", { className: "wsgm-themes-filter" }, h("span", null, name), h("b", null, String(filters[name]))),
      })),
  ];
  const orderOptions = (browse.orders ?? []).map((order) => ({ data: order, label: order }));
  const items: any[] = browse.items ?? [];
  const open = (id: string) => void themesAct("open", { id });
  return h(
    "div",
    { className: "wsgm-themes-pane" },
    h(
      ui.focusable,
      { className: "wsgm-themes-toolbar", "flow-children": "row" },
      h(
        "div",
        { className: "wsgm-themes-tool" },
        h("span", { className: "DialogLabel" }, "Sort"),
        renderSteamDropdown(ui, {
          label: "Sort",
          rgOptions: orderOptions,
          selectedOption: browse.order,
          onChange: (option) => ask({ order: option?.data }),
        }),
      ),
      h(
        "div",
        { className: "wsgm-themes-tool" },
        h("span", { className: "DialogLabel" }, "Filter"),
        renderSteamDropdown(ui, {
          label: "Filter",
          rgOptions: filterOptions,
          selectedOption: browse.filter ?? "All",
          onChange: (option) => ask({ filter: option?.data }),
        }),
      ),
      h(
        "div",
        { className: "wsgm-themes-search" },
        h(ui.textField, {
          label: "Search",
          value: search,
          onChange: (event) => setSearch(event?.target?.value ?? ""),
          onBlur: () => {
            if (search !== (browse.search ?? "")) ask({ search });
          },
        }),
      ),
      h(ui.dialogButton, { onClick: () => ask({}) }, "Refresh"),
    ),
    browse.error ? h("div", { className: "wsgm-themes-status error" }, browse.error) : null,
    h(
      ui.focusable,
      { className: "wsgm-themes-grid", "flow-children": "grid" },
      ...items.map((item) => h(ThemesCard, { key: item.id, item, open })),
    ),
    browse.loading
      ? h("div", { className: "wsgm-themes-status" }, "Asking the store…")
      : items.length === 0 && !browse.error
        ? h("div", { className: "wsgm-themes-status" }, "Nothing matched.")
        : null,
    items.length < (browse.total ?? 0) && !browse.loading
      ? h(
          "div",
          { className: "wsgm-themes-more" },
          h(ui.dialogButton, { onClick: () => void themesAct("loadMore") }, "Load More"),
        )
      : null,
  );
}

function ThemesInstalled({ state }: any) {
  const ui = themesUi;
  const react = ui.react;
  const h = react.createElement;
  const themes: any[] = state.themes ?? [];
  return h(
    "div",
    { className: "wsgm-themes-pane" },
    h(
      ui.focusable,
      { className: "wsgm-themes-toolbar", "flow-children": "row" },
      h(ui.dialogButton, { disabled: !!state.busy, onClick: () => void themesAct("refresh") }, "Refresh"),
      state.updates > 0
        ? h(
            ui.dialogButton,
            { disabled: !!state.busy, onClick: () => void themesAct("updateAll") },
            `Update All Themes (${state.updates})`,
          )
        : null,
    ),
    themes.length === 0
      ? h("div", { className: "wsgm-themes-status" }, "You have no themes installed. Get started under Browse.")
      : null,
    ...themes.map((theme) =>
      h(
        ui.settingsSection,
        { key: theme.name, label: undefined },
        ...themesRowsOf(theme).map((row) => {
          const control = renderSteamSettingRow(ui, row, undefined, themesRowChange, () => {});
          return row.nested ? h("div", { key: row.key, className: "wsgm-themes-nested" }, control) : control;
        }),
        h(
          ui.focusable,
          { className: "wsgm-themes-manage", "flow-children": "row" },
          theme.status === "outdated"
            ? h(
                ui.smallButton,
                { disabled: !!state.busy, onClick: () => void themesAct("update", { name: theme.name }) },
                `Update to ${theme.latestVersion}`,
              )
            : null,
          h(
            ui.smallButton,
            { onClick: () => void themesAct("setHidden", { name: theme.name, hidden: !theme.hidden }) },
            theme.hidden ? "Show in Quick Access" : "Hide from Quick Access",
          ),
          h(
            ui.smallButton,
            {
              onClick: () =>
                themesConfirm(ui, "Delete Theme", `Are you sure you want to delete ${theme.displayName}?`, "Delete", () =>
                  void themesAct("delete", { name: theme.name }),
                ),
            },
            "Delete",
          ),
        ),
      ),
    ),
    (state.errors ?? []).length
      ? h(
          ui.settingsSection,
          { label: "Errors" },
          ...state.errors.map((error) =>
            h("div", { key: error.folder, className: "wsgm-themes-error" }, h("b", null, error.folder), h("span", null, error.error)),
          ),
        )
      : null,
  );
}

function ThemesProfiles({ state }: any) {
  const ui = themesUi;
  const react = ui.react;
  const h = react.createElement;
  const presets: any[] = state.presets ?? [];
  const enabledCount = (state.themes ?? []).filter((theme) => theme.enabled).length;
  const NewProfile = "\u0000new";
  const choices = [
    ...(state.selectedPreset === "Invalid State" ? [{ value: "Invalid State", label: "Invalid State" }] : []),
    { value: "", label: "None" },
    ...presets.map((preset) => ({ value: preset.name, label: preset.displayName })),
    { value: NewProfile, label: "New Profile" },
  ];
  const change = (row, value, commit = true) => {
    if (!commit) return;
    if (value === NewProfile) {
      showSteamModal(ui, {
        title: "Create Profile",
        className: "wsgm-themes-modal",
        render: (close) => h(ThemesProfileNameBody, { ui, count: enabledCount, close }),
      });
      return;
    }
    themesRowChange(row, value);
  };
  return h(
    "div",
    { className: "wsgm-themes-pane" },
    h(
      ui.settingsSection,
      { label: "Profiles" },
      renderSteamSettingRow(
        ui,
        {
          key: themesKey("profile"),
          kind: "choice",
          label: "Selected Profile",
          description: "A profile turns a set of themes on with their settings, and off again together.",
          text: state.selectedPreset ?? "",
          choices,
        },
        undefined,
        change,
        () => {},
      ),
      ...presets.map((preset) =>
        h(
          "div",
          { key: preset.name, className: "wsgm-themes-profile" },
          h("span", null, preset.displayName),
          h("span", { className: "wsgm-themes-muted" }, (preset.dependencies ?? []).join(", ")),
          h(
            ui.smallButton,
            {
              onClick: () =>
                themesConfirm(ui, "Delete Profile", `Delete the profile ${preset.displayName}?`, "Delete", () =>
                  void themesAct("delete", { name: preset.name }),
                ),
            },
            "Delete",
          ),
        ),
      ),
    ),
  );
}

function ThemesSettings({ state }: any) {
  const ui = themesUi;
  const settings = state.settings ?? {};
  const rows = [
    {
      key: themesKey("setting", "enabled"),
      kind: "boolean",
      label: "Install themes into Steam",
      description: "Off leaves Steam's own styling and keeps every theme as it is.",
      checked: !!settings.enabled,
    },
    {
      key: themesKey("setting", "translationsBranch"),
      kind: "choice",
      label: "Class translations",
      description:
        "Steam renames its style classes with every client build; DeckThemes publishes the table that maps themes onto the current names.",
      text: settings.translationsBranch ?? "auto",
      choices: [
        { value: "auto", label: settings.steamBeta ? "Auto-Detect (beta)" : "Auto-Detect (stable)" },
        { value: "stable", label: "Force Stable" },
        { value: "beta", label: "Force Beta" },
      ],
    },
    {
      key: "translations",
      kind: "note",
      label: "Translations",
      text: settings.translations
        ? `${settings.translations} names${settings.translationsFetched ? `, fetched ${settings.translationsFetched}` : ""}`
        : "Not fetched yet",
    },
    { key: "path", kind: "note", label: "Themes folder", text: settings.themesPath ?? "" },
    { key: "link", kind: "note", label: "Steam's themes_custom", text: settings.steamLink ?? "" },
  ];
  const h = ui.react.createElement;
  return h(
    "div",
    { className: "wsgm-themes-pane" },
    h(
      ui.settingsSection,
      { label: "Themes" },
      ...rows.map((row) => renderSteamSettingRow(ui, row, undefined, themesRowChange, () => {})),
    ),
  );
}

// Declared once for the life of the asset, and drawn by the toolkit's page frame only once the gate
// holds: the frame says why when it does not.
function ThemesPage({ context }: any) {
  const react = context.react();
  const h = react.createElement;
  const ui = context.ui();
  themesUi = ui;
  const state = context.state();
  if (!state) return h("div", { className: "wsgm-themes-status" }, context.refusal() ?? "Loading themes…");

  const active = themesTabs.some((tab) => tab.id === state.activeTab) ? state.activeTab : "browse";
  const content = (id: string) => {
    if (id !== active) return null;
    switch (id) {
      case "installed":
        return h(ThemesInstalled, { state });
      case "profiles":
        return h(ThemesProfiles, { state });
      case "settings":
        return h(ThemesSettings, { state });
      default:
        return h(ThemesBrowse, { state });
    }
  };
  const banner = state.error || state.notice;
  return h(
    "div",
    { id: "wsgm-themes", "aria-label": "Themes" },
    h("style", null, themesStyles),
    banner
      ? h(
          "div",
          { className: `wsgm-themes-banner${state.error ? " error" : ""}` },
          h("span", null, banner),
          h(ui.smallButton, { onClick: () => void themesAct("dismiss") }, "Dismiss"),
        )
      : null,
    h(ui.tabs, {
      autoFocusContents: true,
      activeTab: active,
      onShowTab: (tab) => void themesAct("setTab", { tab }),
      tabs: themesTabs.map((tab) => ({ id: tab.id, title: tab.title, content: content(tab.id) })),
    }),
  );
}

const themesStyles = `
#wsgm-themes { margin-top: var(--basicui-header-height, 40px); height: calc(100% - var(--basicui-header-height, 40px));
  display: flex; flex-direction: column; background: var(--gpSystemDarkestGrey, #0e141b); color: #dcdedf; }
#wsgm-themes div[class*="gamepadtabbedpage_TabHeaderRowWrapper"] { background: #1b2838; }
#wsgm-themes .wsgm-themes-banner { display: flex; align-items: center; justify-content: space-between; gap: 12px;
  margin: 8px 48px 0; padding: 10px 14px; border-radius: 2px; background: rgba(26,159,255,.18); font-size: 14px; }
#wsgm-themes .wsgm-themes-banner.error { background: rgba(194,70,62,.25); }
#wsgm-themes .wsgm-themes-pane { display: flex; flex-direction: column; gap: 14px; padding: 12px 4px 72px; }
#wsgm-themes .wsgm-themes-toolbar { display: flex; align-items: flex-end; gap: 12px; flex-wrap: nowrap; }
#wsgm-themes .wsgm-themes-toolbar .DialogButton { width: auto; min-width: auto; height: 40px; padding: 0 16px; white-space: nowrap; }
#wsgm-themes .wsgm-themes-tool { display: flex; flex-direction: column; width: 240px; flex: 0 0 auto; }
#wsgm-themes .wsgm-themes-tool .DialogLabel { font-size: 12px; margin-bottom: 4px; }
#wsgm-themes .wsgm-themes-tool .DialogDropDown_CurrentDisplay { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
#wsgm-themes .wsgm-themes-filter { display: flex; justify-content: space-between; width: 100%; gap: 12px; }
#wsgm-themes .wsgm-themes-search { flex: 1; min-width: 160px; }
#wsgm-themes .wsgm-themes-search .DialogInputLabelGroup, #wsgm-themes .wsgm-themes-search .DialogInput_Wrapper { margin: 0; }
#wsgm-themes .wsgm-themes-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(240px, 1fr)); gap: 14px; }
#wsgm-themes .wsgm-themes-card { display: flex; flex-direction: column; border-radius: 4px; overflow: hidden;
  background: #ACB2C924; outline: 2px solid transparent; transition: outline-color 150ms, background 150ms; }
#wsgm-themes .wsgm-themes-card.gpfocus, #wsgm-themes .wsgm-themes-card:hover { background: #ACB2C947; outline-color: #fff; }
#wsgm-themes .wsgm-themes-shot { position: relative; aspect-ratio: 16 / 10; background: #10151c; overflow: hidden; }
#wsgm-themes .wsgm-themes-shot img { width: 100%; height: 100%; object-fit: cover; display: block; }
#wsgm-themes .wsgm-themes-stats { position: absolute; left: 0; right: 0; bottom: 0; display: flex; gap: 12px; padding: 6px 8px;
  font-size: 12px; color: #fff; background: linear-gradient(180deg, transparent, rgba(0,0,0,.75)); }
#wsgm-themes .wsgm-themes-stats span { display: inline-flex; align-items: center; gap: 4px; }
#wsgm-themes .wsgm-themes-stats svg { width: 13px; height: 13px; }
#wsgm-themes .wsgm-themes-badge { position: absolute; top: 6px; right: 6px; padding: 2px 8px; border-radius: 12px;
  font-size: 11px; font-weight: 700; background: #5cb85c; color: #000; }
#wsgm-themes .wsgm-themes-badge.outdated { background: #fca904; }
#wsgm-themes .wsgm-themes-title { font-size: 15px; font-weight: 600; color: #fff; padding: 8px 10px 0;
  white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
#wsgm-themes .wsgm-themes-meta { font-size: 11px; color: rgba(255,255,255,.55); padding: 2px 10px; }
#wsgm-themes .wsgm-themes-card .wsgm-themes-meta:last-child { padding-bottom: 10px; }
#wsgm-themes .wsgm-themes-status { padding: 12px; text-align: center; color: #b8bcbf; font-size: 14px; }
#wsgm-themes .wsgm-themes-status.error { color: #ff6d6d; }
#wsgm-themes .wsgm-themes-more { display: flex; justify-content: center; padding: 8px 0 24px; }
#wsgm-themes .wsgm-themes-more .DialogButton { width: 50%; }
#wsgm-themes .wsgm-themes-detail { display: flex; gap: 32px; padding: 12px 4px 72px; }
#wsgm-themes .wsgm-themes-detail-left { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 10px; }
#wsgm-themes .wsgm-themes-detail-right { width: 300px; flex: 0 0 auto; display: flex; flex-direction: column; gap: 14px; }
#wsgm-themes .wsgm-themes-gallery { display: flex; gap: 12px; }
#wsgm-themes .wsgm-themes-thumbs { display: flex; flex-direction: column; gap: 8px; }
#wsgm-themes .wsgm-themes-thumb { width: 96px; aspect-ratio: 16 / 10; border-radius: 3px; overflow: hidden; opacity: .6;
  outline: 2px solid transparent; }
#wsgm-themes .wsgm-themes-thumb.current, #wsgm-themes .wsgm-themes-thumb.gpfocus { opacity: 1; outline-color: #fff; }
#wsgm-themes .wsgm-themes-thumb img { width: 100%; height: 100%; object-fit: cover; display: block; }
#wsgm-themes .wsgm-themes-hero { position: relative; width: 556px; max-width: 100%; aspect-ratio: 16 / 10; border-radius: 4px;
  overflow: hidden; background: #10151c; }
#wsgm-themes .wsgm-themes-hero img { width: 100%; height: 100%; object-fit: cover; display: block; }
#wsgm-themes .wsgm-themes-noimage { display: flex; align-items: center; justify-content: center; height: 100%; color: #8b929a; }
#wsgm-themes .wsgm-themes-count { position: absolute; right: 10px; bottom: 10px; padding: 3px 8px; border-radius: 2px;
  background: rgba(0,0,0,.7); font-size: 12px; color: #fff; }
#wsgm-themes .wsgm-themes-heading { display: flex; align-items: baseline; gap: 12px; }
#wsgm-themes .wsgm-themes-heading h2 { margin: 0; font-size: 30px; font-weight: 700; color: #fff; }
#wsgm-themes .wsgm-themes-version { font-size: 16px; font-weight: 700; color: #fff; }
#wsgm-themes h3 { margin: 6px 0 0; font-size: 15px; font-weight: 700; color: #fff; }
#wsgm-themes p { margin: 0; font-size: 14px; line-height: 1.5; color: #c6d4df; max-width: 700px; }
#wsgm-themes .wsgm-themes-muted { color: rgb(124,142,163); font-size: 13px; }
#wsgm-themes .wsgm-themes-chips { display: flex; flex-wrap: wrap; gap: 8px; }
#wsgm-themes .wsgm-themes-chips .DialogButton { width: auto; min-width: auto; height: 32px; padding: 0 12px; }
#wsgm-themes .wsgm-themes-box { background: rgba(27,40,56,.9); border-radius: 4px; padding: 16px; display: flex; flex-direction: column; gap: 10px; }
#wsgm-themes .wsgm-themes-box-title { display: flex; align-items: center; gap: 6px; font-size: 16px; font-weight: 600; color: #fff; }
#wsgm-themes .wsgm-themes-box-title svg { width: 18px; height: 18px; color: #ffd166; }
#wsgm-themes .wsgm-themes-nested { margin-left: 16px; border-left: 2px solid rgba(255,255,255,.12); }
#wsgm-themes .wsgm-themes-manage { display: flex; gap: 8px; padding: 6px 0 12px; }
#wsgm-themes .wsgm-themes-profile { display: flex; align-items: center; gap: 12px; padding: 8px 0; }
#wsgm-themes .wsgm-themes-profile > span:first-child { font-size: 15px; color: #fff; }
#wsgm-themes .wsgm-themes-profile > .wsgm-themes-muted { flex: 1; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
#wsgm-themes .wsgm-themes-error { display: flex; flex-direction: column; gap: 2px; padding: 8px 10px; margin: 4px 0; border-radius: 2px; background: #f002; }
.wsgm-themes-modal-body { display: flex; flex-direction: column; gap: 12px; }
.wsgm-themes-modal-body p { margin: 0; }
.wsgm-themes-modal-actions { display: flex; justify-content: flex-end; gap: 8px; margin-top: 8px; }
`;

const themesPage = registerSteamPage({
  template: "themes",
  gate: "themes",
  patchId: ThemesPatchId,
  components: resolveSteamSettingsComponents,
  required: [
    "react",
    "focusable",
    "toggleField",
    "dropdown",
    "sliderField",
    "textField",
    "dialogButton",
    "dialogButtonPrimary",
    "smallButton",
    "valueField",
    "settingsSection",
    "tabs",
    "modalRoot",
    "showModal",
  ],
  status: () => ({ tab: themesPage.state()?.activeTab ?? "" }),
  Page: ThemesPage,
});
