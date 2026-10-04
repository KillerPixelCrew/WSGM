// The Themes page in Steam: CSSLoader-compatible themes browsed from DeckThemes, installed and managed.
//
// Laid out the way CSS Loader lays out its store and its settings, and drawn with Steam's own
// components where one fits and the toolkit's UI kit for the rest, so it behaves like the rest of
// Big Picture under a controller: Steam's tabs over a toolbar and a grid of cards, one theme's
// details with its screenshots, and the installed themes as the same settings rows a host's settings
// page uses. WSGM owns the data, every label and every decision; the toolkit owns the page gate, the
// settings rows, the kit, the modal frame and the fail-closed component discovery used here.
const ThemesPatchId = "wsgm.themes";

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

// A card in the store's grid: the kit's card with the theme's screenshot, its counts and target,
// an Installed or Update badge, and its version and author.
const themesCard = (ui, item, open) => {
  const react = ui.react;
  const badge =
    item.localStatus === "installed"
      ? { text: "Installed" }
      : item.localStatus === "outdated"
        ? { text: "Update", warn: true }
        : null;
  return renderSteamUiCard(ui, {
    key: item.id,
    image: item.imageUrl,
    stats: [
      { glyph: renderSteamUiGlyph(react, "download"), text: String(item.downloads ?? 0) },
      { glyph: renderSteamUiGlyph(react, "star"), text: String(item.stars ?? 0) },
      ...(item.target ? [{ glyph: renderSteamUiGlyph(react, "target"), text: item.target }] : []),
    ],
    badge,
    title: item.displayName,
    meta: [
      item.updated ? `${item.version} - Last Updated ${item.updated}` : item.version,
      item.author ? `By ${item.author}` : "",
    ],
    onActivate: () => open(item.id),
  });
};

function ThemesDetail({ detail, busy }: any) {
  const ui = themesUi;
  const react = ui.react;
  const h = react.createElement;
  const [focusedImage, setFocusedImage] = react.useState(0);
  const item = detail.item;
  const installLabel =
    item.localStatus === "outdated" ? "Update" : item.localStatus === "installed" ? "Reinstall" : "Install";
  return renderSteamUiDetail(ui, {
    title: item.displayName,
    badge: item.version,
    media: renderSteamUiGallery(ui, {
      images: detail.imageUrls ?? [],
      index: focusedImage,
      onSelect: setFocusedImage,
      empty: "No screenshot",
    }),
    main: [
      h(
        "div",
        { className: "steam-ui-kit-muted" },
        item.author ? `By ${item.author}` : "",
        item.updated ? ` · Last Updated ${item.updated}` : "",
      ),
      h("h3", null, "Description"),
      h(
        "p",
        { className: detail.description ? "" : "steam-ui-kit-muted" },
        detail.loading ? "Loading…" : detail.error ? detail.error : detail.description || "No description provided.",
      ),
      item.targets?.length
        ? h(
            react.Fragment,
            null,
            h("h3", null, "Targets"),
            renderSteamUiChips(
              ui,
              item.targets.map((target) => ({
                label: target,
                description: `View Other "${target}" Themes`,
                onClick: () =>
                  void themesAct("browse", { filter: target, order: "", search: "" }).then(() =>
                    themesAct("closeDetail"),
                  ),
              })),
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
              { className: "steam-ui-kit-muted" },
              detail.dependencies
                .map((dependency) => `${dependency.displayName}${dependency.installed ? "" : " (not installed)"}`)
                .join(", "),
            ),
          )
        : null,
    ],
    aside: [
      renderSteamUiBox(
        react,
        h(react.Fragment, null, renderSteamUiGlyph(react, "star"), ` ${item.stars ?? 0} Stars`),
        h("div", { className: "steam-ui-kit-muted" }, "Starring needs a DeckThemes account, which WSGM does not sign in to."),
      ),
      renderSteamUiBox(
        react,
        `${installLabel} ${item.displayName}`,
        h("div", { className: "steam-ui-kit-muted" }, `${item.downloads ?? 0} Downloads`),
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
          { className: "steam-ui-kit-muted" },
          "Downloads into WSGM's themes folder, with every theme it needs. Turn it on under Installed.",
        ),
      ),
    ],
    onBack: () => void themesAct("closeDetail"),
  });
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
  return renderSteamUiPane(
    ui,
    {},
    renderSteamUiToolbar(
      ui,
      renderSteamUiTool(
        ui,
        "Sort",
        renderSteamDropdown(ui, {
          label: "Sort",
          rgOptions: orderOptions,
          selectedOption: browse.order,
          onChange: (option) => ask({ order: option?.data }),
        }),
      ),
      renderSteamUiTool(
        ui,
        "Filter",
        renderSteamDropdown(ui, {
          label: "Filter",
          rgOptions: filterOptions,
          selectedOption: browse.filter ?? "All",
          onChange: (option) => ask({ filter: option?.data }),
        }),
      ),
      renderSteamUiTool(
        ui,
        null,
        h(ui.textField, {
          label: "Search",
          value: search,
          onChange: (event) => setSearch(event?.target?.value ?? ""),
          onBlur: () => {
            if (search !== (browse.search ?? "")) ask({ search });
          },
        }),
        true,
      ),
      h(ui.dialogButton, { onClick: () => ask({}) }, "Refresh"),
    ),
    browse.error ? renderSteamUiEmpty(react, browse.error, true) : null,
    renderSteamUiGrid(
      ui,
      items.map((item) => themesCard(ui, item, open)),
    ),
    browse.loading
      ? renderSteamUiEmpty(react, "Asking the store…")
      : items.length === 0 && !browse.error
        ? renderSteamUiEmpty(react, "Nothing matched.")
        : null,
    items.length < (browse.total ?? 0) && !browse.loading
      ? renderSteamUiMore(ui, { onClick: () => void themesAct("loadMore") })
      : null,
  );
}

function ThemesInstalled({ state }: any) {
  const ui = themesUi;
  const react = ui.react;
  const h = react.createElement;
  const themes: any[] = state.themes ?? [];
  return renderSteamUiPane(
    ui,
    {},
    renderSteamUiToolbar(
      ui,
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
      ? renderSteamUiEmpty(react, "You have no themes installed. Get started under Browse.")
      : null,
    ...themes.map((theme) =>
      h(
        ui.settingsSection,
        { key: theme.name, label: undefined },
        ...themesRowsOf(theme).map((row) => {
          const control = renderSteamSettingRow(ui, row, undefined, themesRowChange, () => {});
          return row.nested ? h("div", { key: row.key, className: "steam-ui-kit-nested" }, control) : control;
        }),
        h(
          "div",
          { className: "wsgm-themes-manage" },
          renderSteamUiChips(ui, [
            ...(theme.status === "outdated"
              ? [
                  {
                    label: `Update to ${theme.latestVersion}`,
                    onClick: () => {
                      if (!state.busy) void themesAct("update", { name: theme.name });
                    },
                  },
                ]
              : []),
            {
              label: theme.hidden ? "Show in Quick Access" : "Hide from Quick Access",
              onClick: () => void themesAct("setHidden", { name: theme.name, hidden: !theme.hidden }),
            },
            {
              label: "Delete",
              onClick: () =>
                showSteamUiConfirm(ui, {
                  title: "Delete Theme",
                  text: `Are you sure you want to delete ${theme.displayName}?`,
                  confirmLabel: "Delete",
                  onConfirm: () => void themesAct("delete", { name: theme.name }),
                }),
            },
          ]),
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
  // A profile is named in a modal, as CSS Loader names one: the enabled themes and their settings
  // under one name.
  const change = (row, value, commit = true) => {
    if (!commit) return;
    if (value === NewProfile) {
      showSteamUiPrompt(ui, {
        title: "Create Profile",
        text: `This profile will combine all ${enabledCount} themes you currently have enabled. Enabling or disabling it will toggle them all at once.`,
        label: "Profile Name",
        confirmLabel: "Create",
        onConfirm: (name) => void themesAct("createProfile", { name }),
      });
      return;
    }
    themesRowChange(row, value);
  };
  return renderSteamUiPane(
    ui,
    {},
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
          h("span", { className: "steam-ui-kit-muted" }, (preset.dependencies ?? []).join(", ")),
          renderSteamUiChips(ui, [
            {
              label: "Delete",
              onClick: () =>
                showSteamUiConfirm(ui, {
                  title: "Delete Profile",
                  text: `Delete the profile ${preset.displayName}?`,
                  confirmLabel: "Delete",
                  onConfirm: () => void themesAct("delete", { name: preset.name }),
                }),
            },
          ]),
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
  return renderSteamUiPane(
    ui,
    {},
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
  if (!state) return renderSteamUiEmpty(react, context.refusal() ?? "Loading themes…");
  const banner = state.error || state.notice;
  return renderSteamUiTabbedPage(ui, {
    id: "wsgm-themes",
    label: "Themes",
    style: themesStyles,
    tabs: themesTabs,
    active: state.activeTab,
    onTab: (tab) => void themesAct("setTab", { tab }),
    banner: banner ? { text: banner, error: !!state.error, onDismiss: () => void themesAct("dismiss") } : null,
    content: (id) => {
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
    },
  });
}

// The page's own layout: where the kit's elements go, not how they look.
const themesStyles = `
#wsgm-themes .steam-ui-kit-tool:not(.grow) { width: 240px; }
#wsgm-themes .wsgm-themes-filter { display: flex; justify-content: space-between; width: 100%; gap: 12px; }
#wsgm-themes .steam-ui-kit-box-title svg { color: #ffd166; }
#wsgm-themes .wsgm-themes-manage { padding: 6px 0 12px; }
#wsgm-themes .wsgm-themes-profile { display: flex; align-items: center; gap: 12px; padding: 8px 0; }
#wsgm-themes .wsgm-themes-profile > span:first-child { font-size: 15px; color: #fff; }
#wsgm-themes .wsgm-themes-profile > .steam-ui-kit-muted { flex: 1; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
#wsgm-themes .wsgm-themes-error { display: flex; flex-direction: column; gap: 2px; padding: 8px 10px; margin: 4px 0; border-radius: 2px; background: #f002; }
`;

const themesPage = registerSteamPage({
  template: "themes",
  gate: "themes",
  patchId: ThemesPatchId,
  components: resolveSteamSettingsComponents,
  required: SteamUiTabbedPageRequired,
  status: () => ({ tab: themesPage.state()?.activeTab ?? "" }),
  Page: ThemesPage,
});
