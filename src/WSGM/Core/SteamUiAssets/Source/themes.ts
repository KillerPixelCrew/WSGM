// Theme frontend; shared host services own catalogue, profiles, settings and CSS deployment.
const ThemesPatchId = "wsgm.themes";

const themesTabs = [
  { id: "browse", title: "Browse" },
  { id: "installed", title: "Installed" },
  { id: "profiles", title: "Profiles" },
  { id: "settings", title: "Settings" },
];

const themesKey = (...parts: string[]) => parts.join("\u0000");

/**
 * Sends a committed settings row as the host command its key encodes.
 * @param context The page frame, whose `send` hands the refusal back to the row that asked.
 * @param row Published row whose NUL-separated key identifies the command and target.
 * @param value Edited value, converted according to the row kind.
 * @returns The host's answer; a rejection is the refusal the row shows.
 */
const themesSend = (context, row, value) => {
  const [kind, theme, patch, component] = String(row.key).split("\u0000");
  switch (kind) {
    case "theme":
      return context.send("setEnabled", { name: theme, enabled: !!value });
    case "patch":
      return context.send("setPatch", { theme, patch, value: String(value) });
    case "checkbox":
      return context.send("setPatch", { theme, patch, value: value ? "Yes" : "No" });
    case "slider":
      return context.send("setPatch", {
        theme,
        patch,
        value: String(row.labels?.[Number(value)] ?? ""),
      });
    case "component":
      return context.send("setComponent", { theme, patch, component, value: String(value) });
    case "profile":
      return context.send("setProfile", { name: String(value) });
    case "setting":
      return context.send("setSetting", { key: theme, value });
    default:
      return undefined;
  }
};

/**
 * Builds settings descriptors from one installed theme snapshot.
 * @param theme Host-published theme and its patch/component choices.
 * @returns Rows headed by the enable switch; disabled themes omit patch controls.
 */
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
        rows.push({
          key: themesKey("none", theme.name, patch.name),
          kind: "note",
          label: patch.name,
          text: "",
          nested: true,
        });
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
    for (const component of (patch.components ?? []).filter(
      (component) => component.on === patch.value,
    )) {
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
      { glyph: renderSteamUiIcon(react, "download", 13), text: String(item.downloads ?? 0) },
      { glyph: renderSteamUiIcon(react, "star", 13), text: String(item.stars ?? 0) },
      ...(item.target
        ? [{ glyph: renderSteamUiIcon(react, "target", 13), text: item.target }]
        : []),
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

function ThemesDetail({ context, detail, busy }: any) {
  const ui = context.ui();
  const react = ui.react;
  const h = react.createElement;
  const [focusedImage, setFocusedImage] = react.useState(0);
  const item = detail.item;
  const installLabel = steamUiAcquireLabel(
    item.localStatus === "installed" || item.localStatus === "outdated",
    item.localStatus !== "outdated",
  );
  return renderSteamUiItem(ui, {
    title: item.displayName,
    badge: item.version,
    media: renderSteamUiGallery(ui, {
      images: detail.imageUrls ?? [],
      index: focusedImage,
      onSelect: setFocusedImage,
      empty: "No screenshot",
    }),
    byline: [item.author ? `By ${item.author}` : "", item.updated ? `Last Updated ${item.updated}` : ""]
      .filter(Boolean)
      .join(" · "),
    description: detail.description ?? "",
    loading: !!detail.loading,
    error: detail.error ?? null,
    sections: [
      item.targets?.length
        ? {
            title: "Targets",
            content: renderSteamUiChips(
              ui,
              item.targets.map((target) => ({
                label: target,
                description: `View Other "${target}" Themes`,
                onClick: () =>
                  void context
                    .act(SteamUiBrowse.browse, { filter: target, order: "", search: "" })
                    .then(() => context.act(SteamUiBrowse.closeDetail)),
              })),
            ),
          }
        : null,
      detail.dependencies?.length
        ? {
            title: "Requires",
            content: h(
              "div",
              { className: "steam-ui-kit-muted" },
              detail.dependencies
                .map(
                  (dependency) =>
                    `${dependency.displayName}${dependency.installed ? "" : " (not installed)"}`,
                )
                .join(", "),
            ),
          }
        : null,
    ],
    acquire: {
      title: `${installLabel} ${item.displayName}`,
      meta: `${item.downloads ?? 0} Downloads`,
      label: installLabel,
      busy: !!busy,
      disabled: !!detail.loading,
      onAcquire: () => void context.act("install", { id: item.id }),
      note: "Downloads into WSGM's themes folder, with every theme it needs. Turn it on under Installed.",
    },
    aside: [
      renderSteamUiBox(
        react,
        h(react.Fragment, null, renderSteamUiIcon(react, "star", 18), ` ${item.stars ?? 0} Stars`),
        h(
          "div",
          { className: "steam-ui-kit-muted" },
          "Starring needs a DeckThemes account, which WSGM does not sign in to.",
        ),
      ),
    ],
    onBack: () => void context.act(SteamUiBrowse.closeDetail),
  });
}

function ThemesBrowse({ context, state }: any) {
  const ui = context.ui();
  const h = ui.react.createElement;
  const browse = state.browse ?? {};
  if (state.detail) return h(ThemesDetail, { context, detail: state.detail, busy: state.busy });
  const query = { filter: browse.filter ?? "All", order: browse.order ?? "", search: browse.search ?? "" };
  const ask = (changes: any) => void context.act(SteamUiBrowse.browse, { ...query, ...changes });
  const filters = browse.filters ?? {};
  const total = Object.values(filters).reduce(
    (sum: number, count: any) => sum + Number(count || 0),
    0,
  );
  const filterLabel = (name: string, count: string) =>
    h("div", { className: "wsgm-themes-filter" }, h("span", null, name), h("b", null, count));
  const items: any[] = browse.items ?? [];
  const open = (id: string) => void context.act(SteamUiBrowse.open, { id });
  return renderSteamUiPane(
    ui,
    {},
    renderSteamUiCollection(ui, {
      id: "wsgm-themes-browse",
      items,
      itemKey: (item) => item.id,
      render: (item) => themesCard(ui, item, open),
      choices: [
        {
          label: "Sort",
          options: (browse.orders ?? []).map((order) => ({ data: order, label: order })),
          selected: browse.order,
          onChange: (order) => ask({ order }),
        },
        {
          label: "Filter",
          options: [
            { data: "All", label: filterLabel("All", total ? String(total) : "") },
            ...Object.keys(filters)
              .filter((name) => Number(filters[name]) > 0)
              .map((name) => ({ data: name, label: filterLabel(name, String(filters[name])) })),
          ],
          selected: browse.filter ?? "All",
          onChange: (filter) => ask({ filter }),
        },
      ],
      search: { value: browse.search ?? "", onSubmit: (search) => ask({ search }) },
      actions: [{ id: "refresh", label: "Refresh", onClick: () => ask({}) }],
      loading: !!browse.loading,
      loadingText: "Asking the store…",
      error: browse.error ?? null,
      onRetry: () => ask({}),
      empty: { text: "The store has no themes to show right now." },
      noMatch: { text: "Nothing matched." },
      filtered: !!browse.search || (!!browse.filter && browse.filter !== "All"),
      more:
        items.length < (browse.total ?? 0)
          ? { total: browse.total, onMore: () => void context.act(SteamUiBrowse.more) }
          : null,
      // The first look at the store is the page's own: nothing is fetched until someone opens the tab.
      onFirstShow:
        !browse.loading && !browse.error && items.length === 0 && !browse.page
          ? () => ask({})
          : null,
    }),
  );
}

function ThemesInstalled({ context, state }: any) {
  const ui = context.ui();
  const themes: any[] = state.themes ?? [];
  const busy = !!state.busy;
  return renderSteamUiPane(
    ui,
    {},
    renderSteamUiActions(
      ui,
      [
        { id: "refresh", label: "Refresh", disabled: busy, onClick: () => void context.act("refresh") },
        ...(state.updates > 0
          ? [
              {
                id: "update-all",
                label: `Update All Themes (${state.updates})`,
                disabled: busy,
                onClick: () => void context.act("updateAll"),
              },
            ]
          : []),
      ],
      { compact: true },
    ),
    themes.length === 0
      ? renderSteamUiEmpty(ui, {
          kind: "empty",
          title: "No themes installed",
          text: "You have no themes installed. Get started under Browse.",
          action: { label: "Browse themes", onClick: () => context.setTab("browse") },
        })
      : null,
    ...themes.map((theme) =>
      renderSteamUiFields(ui, {
        key: theme.name,
        rows: themesRowsOf(theme),
        revision: state.revision,
        onChange: (row, value) => themesSend(context, row, value),
        extra: [
          ui.react.createElement(
            "div",
            { key: "manage", className: "wsgm-themes-manage" },
            renderSteamUiChips(ui, [
              ...(theme.status === "outdated"
                ? [
                    {
                      label: `Update to ${theme.latestVersion}`,
                      onClick: () => {
                        if (!busy) void context.act("update", { name: theme.name });
                      },
                    },
                  ]
                : []),
              {
                label: theme.hidden ? "Show in Quick Access" : "Hide from Quick Access",
                onClick: () =>
                  void context.act("setHidden", { name: theme.name, hidden: !theme.hidden }),
              },
              {
                label: "Delete",
                onClick: () =>
                  showSteamUiConfirm(ui, {
                    title: "Delete Theme",
                    text: `Are you sure you want to delete ${theme.displayName}?`,
                    confirmLabel: "Delete",
                    onConfirm: () => void context.act("delete", { name: theme.name }),
                  }),
              },
            ]),
          ),
        ],
      }),
    ),
    (state.errors ?? []).length
      ? ui.react.createElement(
          ui.settingsSection,
          { label: "Errors" },
          renderSteamUiStatus(
            ui,
            state.errors.map((error) => ({
              key: error.folder,
              text: `${error.folder}: ${error.error}`,
              error: true,
            })),
          ),
        )
      : null,
  );
}

function ThemesProfiles({ context, state }: any) {
  const ui = context.ui();
  const presets: any[] = state.presets ?? [];
  const enabledCount = (state.themes ?? []).filter((theme) => theme.enabled).length;
  const rows = [
    {
      key: themesKey("profile"),
      kind: "choice",
      label: "Selected Profile",
      description: "A profile turns a set of themes on with their settings, and off again together.",
      text: state.selectedPreset ?? "",
      choices: [
        ...(state.selectedPreset === "Invalid State"
          ? [{ value: "Invalid State", label: "Invalid State", selectable: false }]
          : []),
        { value: "", label: "None" },
        ...presets.map((preset) => ({ value: preset.name, label: preset.displayName })),
      ],
    },
    {
      key: "create",
      kind: "action",
      label: "New Profile",
      description: `Combines the ${enabledCount} theme${enabledCount === 1 ? "" : "s"} you have enabled now, with their settings.`,
      buttonLabel: "Create…",
    },
    ...presets.map((preset) => ({
      key: themesKey("preset", preset.name),
      kind: "action",
      label: preset.displayName,
      description: (preset.dependencies ?? []).join(", "),
      buttonLabel: "Delete",
    })),
  ];
  // A profile is named in a modal, as CSS Loader names one: the enabled themes and their settings
  // under one name. Deleting one asks first.
  const action = (row) => {
    if (row.key === "create") {
      showSteamUiPrompt(ui, {
        title: "Create Profile",
        text: `This profile will combine all ${enabledCount} themes you currently have enabled. Enabling or disabling it will toggle them all at once.`,
        label: "Profile Name",
        confirmLabel: "Create",
        onConfirm: (name) => void context.act("createProfile", { name }),
      });
      return undefined;
    }
    const preset = presets.find((candidate) => themesKey("preset", candidate.name) === row.key);
    if (preset) {
      showSteamUiConfirm(ui, {
        title: "Delete Profile",
        text: `Delete the profile ${preset.displayName}?`,
        confirmLabel: "Delete",
        onConfirm: () => void context.act("delete", { name: preset.name }),
      });
    }
    return undefined;
  };
  return renderSteamUiPane(
    ui,
    {},
    renderSteamUiFields(ui, {
      title: "Profiles",
      rows,
      revision: state.revision,
      onChange: (row, value) => themesSend(context, row, value),
      onAction: action,
    }),
  );
}

function ThemesSettings({ context, state }: any) {
  const ui = context.ui();
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
        {
          value: "auto",
          label: settings.steamBeta ? "Auto-Detect (beta)" : "Auto-Detect (stable)",
        },
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
  return renderSteamUiPane(
    ui,
    {},
    renderSteamUiFields(ui, {
      title: "Themes",
      rows,
      revision: state.revision,
      onChange: (row, value) => themesSend(context, row, value),
    }),
  );
}

/**
 * Draws the active tab of the Themes page: the store, the installed themes, profiles and settings.
 * @param props The frame's context, the host's state and the active tab.
 * @returns The tab's content; the frame draws the tabs, banner and loading state around it.
 */
function ThemesPage({ context, state, tab }: any) {
  const h = context.react().createElement;
  switch (tab) {
    case "installed":
      return h(ThemesInstalled, { context, state });
    case "profiles":
      return h(ThemesProfiles, { context, state });
    case "settings":
      return h(ThemesSettings, { context, state });
    default:
      return h(ThemesBrowse, { context, state });
  }
}

// The page's own layout: where the kit's elements go, not how they look.
const themesStyles = `
#wsgm-themes .steam-ui-kit-tool:not(.grow) { width: 240px; }
#wsgm-themes .wsgm-themes-filter { display: flex; justify-content: space-between; width: 100%; gap: 12px; }
#wsgm-themes .steam-ui-kit-box-title svg { color: #ffd166; }
#wsgm-themes .wsgm-themes-manage { padding: 6px 0 12px; }
`;

const themesPage = registerSteamPage({
  template: "themes",
  gate: "themes",
  patchId: ThemesPatchId,
  title: "Themes",
  id: "wsgm-themes",
  style: themesStyles,
  loading: "Loading themes…",
  tabs: { source: "host", list: themesTabs },
  status: () => ({ tab: themesPage.state()?.activeTab ?? "" }),
  Page: ThemesPage,
});
