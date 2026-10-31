// Steam's Settings > Customization page as the hub for WSGM's themes, UI sounds and boot animation.
// The page stays Valve's: its LED, Startup Movie, Screensaver and Advanced sections render as Steam
// draws them, and the toolkit's screensaver gate keeps decorating its own section. This gate adds
// three native sections in front of Valve's Startup Movie section, through the Settings sections the
// toolkit's gates share. They read the publications the Themes, UI sounds and Animations pages
// already receive and send those pages' own commands, so the services behind them stay the only
// authority for install, apply, update and reset.
const createWsgmCustomization = () => {
  const HubKey = "wsgm-customization-hub";
  const Sources = {
    themes: { patchId: "wsgm.themes", route: "/wsgm/themes" },
    sounds: { patchId: "wsgm.sounds", route: "/wsgm/sounds" },
    animations: { patchId: "wsgm.animations", route: "/wsgm/animations" },
  } as const;
  type Source = keyof typeof Sources;
  // What the hub's rows are drawn with; a client without one of them gets no hub.
  const Required = ["settingsSection", "toggleField", "dropdown", "valueField", "dialogButton"];

  let react: any = null;
  let ui: any = null;
  let lastError = "";
  const local = createLocalStore();
  // Each source's last publication; null until it arrives, absent when this session has no such service.
  const states: Record<Source, any> = { themes: null, sounds: null, animations: null };
  const available: Record<Source, boolean> = { themes: false, sounds: false, animations: false };

  // The request is the answer: a refused change is shown on the row that asked.
  const send = (source: Source, command: string, payload: any = {}) =>
    request(Sources[source].patchId, command, payload);

  // Opens a source's page, on the tab that suits what the user has so far.
  const open = (source: Source, tab?: string) => {
    const go = () => {
      if (!navigateSteamRoute(Sources[source].route))
        lastError = `could not open ${Sources[source].route}`;
    };
    if (!tab) {
      go();
      return undefined;
    }
    return send(source, SteamUiBrowse.setTab, { tab }).then(go);
  };

  const manage = (key: string, label: string, description: string) => ({
    key,
    kind: "action",
    label,
    description,
    buttonLabel: "Open",
  });
  const failed = (key: string, error: string | null | undefined) =>
    error ? { key, kind: "note", label: "Last change failed", text: "", description: error } : null;
  const waiting = (key: string, text: string) => [{ key, kind: "note", label: text, text: "" }];

  // One section of the hub, drawn with Steam's fields over the shared drafts.
  const section = (
    key: Source,
    title: string,
    rows: any[],
    change: (row: any, value: any) => unknown,
    action: (row: any) => unknown,
  ) =>
    renderSteamUiFields(ui, {
      key,
      title,
      rows,
      revision: states[key]?.revision ?? 0,
      onChange: change,
      onAction: action,
    });

  const themesSection = () => {
    const state = states.themes;
    if (!state) return section("themes", "Themes", waiting("themes-wait", "Loading themes…"), () => {}, () => {});
    const themes: any[] = state.themes ?? [];
    const presets: any[] = state.presets ?? [];
    const on = !!state.settings?.enabled;
    const enabled = themes.filter((theme) => theme.enabled).length;
    const summary = !on
      ? "Off: Steam's own look. Your themes stay installed and come back when this is on."
      : themes.length
        ? `${enabled} of ${themes.length} themes on${state.selectedPreset ? ` · Profile: ${state.selectedPreset}` : ""}`
        : "No themes installed yet.";
    const rows = [
      {
        key: "themes-on",
        kind: "boolean",
        label: "Use themes",
        description: summary,
        checked: on,
        disabled: !!state.busy,
      },
      presets.length
        ? {
            key: "themes-profile",
            kind: "choice",
            label: "Profile",
            description: "Turns a saved set of themes and their settings on together.",
            text: state.selectedPreset ?? "",
            disabled: !on || !!state.busy,
            choices: [
              ...(state.selectedPreset === "Invalid State"
                ? [{ value: "Invalid State", label: "Invalid State", selectable: false }]
                : []),
              { value: "", label: "None" },
              ...presets.map((preset) => ({ value: preset.name, label: preset.displayName })),
            ],
          }
        : null,
      ...themes.map((theme) => ({
        key: `themes-theme-${theme.name}`,
        kind: "boolean",
        label: theme.displayName,
        description:
          theme.status === "outdated"
            ? `Update available (${theme.latestVersion})`
            : [theme.version, theme.author].filter(Boolean).join(" · "),
        checked: !!theme.enabled,
        disabled: !on || !!state.busy,
        nested: true,
        name: theme.name,
      })),
      state.updates > 0
        ? {
            key: "themes-update",
            kind: "action",
            label: `Updates for ${state.updates} theme${state.updates === 1 ? "" : "s"}`,
            buttonLabel: "Update All",
            disabled: !!state.busy,
          }
        : null,
      failed("themes-error", state.error),
      manage(
        "themes-manage",
        "Browse and manage themes",
        "Screenshots, install, update and remove, each theme's options and profiles.",
      ),
    ].filter(Boolean);
    return section(
      "themes",
      "Themes",
      rows,
      (row, value) =>
        row.key === "themes-on"
          ? send("themes", "setSetting", { key: "enabled", value: !!value })
          : row.key === "themes-profile"
            ? send("themes", "setProfile", { name: String(value ?? "") })
            : row.name
              ? send("themes", "setEnabled", { name: row.name, enabled: !!value })
              : undefined,
      (row) =>
        row.key === "themes-update"
          ? send("themes", "updateAll")
          : row.key === "themes-manage"
            ? open("themes", themes.length ? "installed" : "browse")
            : undefined,
    );
  };

  const soundsSection = () => {
    const state = states.sounds;
    if (!state)
      return section("sounds", "UI sounds", waiting("sounds-wait", "Loading sound packs…"), () => {}, () => {});
    const packs: any[] = (state.packs ?? []).filter((pack) => !pack.error);
    const known = !state.selected || packs.some((pack) => pack.id === state.selected);
    const rows = [
      {
        key: "sounds-pack",
        kind: "choice",
        label: "Sound pack",
        // With no pack chosen the coverage line only repeats that Steam's sounds play.
        description: [state.selected ? state.compatibility : "", state.integration]
          .filter(Boolean)
          .join(" "),
        text: state.selected ?? "",
        disabled: !!state.busy,
        choices: [
          { value: "", label: "Steam default" },
          ...(known ? [] : [{ value: state.selected, label: "Unavailable pack", selectable: false }]),
          ...packs.map((pack) => ({ value: pack.id, label: pack.name })),
        ],
      },
      failed("sounds-error", state.error),
      manage(
        "sounds-manage",
        "Browse and manage sound packs",
        "DeckThemes Audio Loader packs, previews, ZIP import, update and remove.",
      ),
    ].filter(Boolean);
    return section(
      "sounds",
      "UI sounds",
      rows,
      (row, value) =>
        row.key === "sounds-pack" ? send("sounds", "select", { id: String(value ?? "") }) : undefined,
      (row) => (row.key === "sounds-manage" ? open("sounds") : undefined),
    );
  };

  const animationsSection = () => {
    const state = states.animations;
    if (!state)
      return section(
        "animations",
        "Boot animation",
        waiting("animations-wait", "Loading boot movies…"),
        () => {},
        () => {},
      );
    const library: any[] = state.library ?? [];
    const known = !state.selected || library.some((item) => item.id === state.selected);
    const description = state.selected
      ? "Plays when Big Picture starts. Steam's Startup Movie choice below is set aside while it does, and comes back when you choose Steam's own here."
      : "Steam's Startup Movie choice below decides what plays.";
    const rows = [
      {
        key: "animations-boot",
        kind: "choice",
        label: "Boot movie",
        description,
        text: state.selected ?? "",
        disabled: !!state.busy,
        choices: [
          { value: "", label: state.stockName || "Steam's own" },
          ...(known ? [] : [{ value: state.selected, label: "Unavailable movie", selectable: false }]),
          ...library.map((item) => ({ value: item.id, label: item.name })),
        ],
      },
      state.settings?.restartNeeded
        ? {
            key: "animations-restart",
            kind: "note",
            label: "Restart Steam to see the change",
            text: "",
            description: "Steam reads its startup movie when it starts.",
            accent: true,
          }
        : null,
      {
        key: "animations-suspend",
        kind: "note",
        label: "Suspend and standby movies",
        text: "",
        description:
          "Steam plays these only in its own suspend flow, which Windows does not run, so the boot movie is the one Steam shows here.",
      },
      failed("animations-error", state.error),
      manage(
        "animations-manage",
        "Browse and manage boot animations",
        "SteamDeckRepo movies with previews, your own WebM files, shuffle and volume.",
      ),
    ].filter(Boolean);
    return section(
      "animations",
      "Boot animation",
      rows,
      (row, value) =>
        row.key === "animations-boot"
          ? send("animations", "select", { id: String(value ?? "") })
          : undefined,
      (row) =>
        row.key === "animations-manage"
          ? open("animations", library.length ? "library" : "browse")
          : undefined,
    );
  };

  function WsgmCustomizationHub() {
    react.useSyncExternalStore(local.subscribe, local.revision);
    if (!ui) return null;
    const sections = [
      available.themes ? themesSection() : null,
      available.sounds ? soundsSection() : null,
      available.animations ? animationsSection() : null,
    ].filter(Boolean);
    return react.createElement(react.Fragment, null, steamUiKitStyle(react), ...sections);
  }

  // In front of Valve's Startup Movie section, or last when this client has none.
  const hub = registerSteamSettingsSection({
    page: "Customization",
    key: HubKey,
    before: ['"#Settings_Customization_StartupVideo"'],
    render: WsgmCustomizationHub,
  });

  // Pure: Steam's settings fields the rows are drawn with, React with the store hook the hub
  // redraws through, and the Customization page's route. A client without one is incompatible.
  const find = (runtime) => {
    const resolved = resolveSteamUiComponents(runtime);
    const resolvedReact = resolved?.react ?? null;
    const missing = Required.filter((name) => !resolved?.[name]);
    const facts = { react: resolvedReact ? 1 : 0, missing, route: "" };
    if (!resolvedReact || typeof resolvedReact.useSyncExternalStore !== "function") {
      return gateIncompatible("React with useSyncExternalStore is unavailable", facts);
    }
    if (missing.length) {
      return gateIncompatible(`Steam settings components unavailable: ${missing.join(", ")}`, facts);
    }
    const page = hub.find(runtime);
    if ("incompatible" in page) return gateIncompatible(page.incompatible, facts);
    facts.route = page.route;
    return gateReady({ ui: resolved as any, route: page.route }, facts);
  };

  return defineGate({
    name: "wsgmCustomization",
    find,
    bind: (handles, ledger) => {
      ui = handles.ui;
      react = handles.ui.react;
      for (const source of Object.keys(Sources) as Source[]) {
        // A session without the service has no publication to subscribe to, and no section to show.
        try {
          ledger.subscribe(Sources[source].patchId, (published) => {
            states[source] = published && typeof published === "object" ? published : null;
            local.changed();
          });
          available[source] = true;
        } catch {
          available[source] = false;
        }
      }
      const attached = hub.attach(ledger, react, handles.route);
      if (attached) return attached;
      lastError = "";
      local.changed();
    },
    // An open page re-renders without the hub; the shared wrapper passes Steam's own tree through.
    unbind: () => {
      for (const source of Object.keys(Sources) as Source[]) {
        states[source] = null;
        available[source] = false;
      }
      ui = null;
      local.changed();
    },
    status: () => ({
      registered: hub.attached(),
      section: hub.status(),
      sources: Object.fromEntries(
        (Object.keys(Sources) as Source[]).map((source) => [
          source,
          available[source] ? (states[source] ? "state" : "waiting") : "absent",
        ]),
      ),
      navigationError: lastError,
    }),
  });
};

registerGate("wsgmCustomization", createWsgmCustomization());
