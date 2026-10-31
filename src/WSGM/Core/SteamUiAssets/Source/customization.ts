// Steam's Settings > Customization page as the hub for WSGM's themes, UI sounds and boot animation.
// The page stays Valve's: its LED, Startup Movie, Screensaver and Advanced sections render as Steam
// draws them, and the toolkit's screensaver gate keeps decorating its own section. This gate adds
// three native sections in front of Valve's Startup Movie section. They read the publications the
// Themes, UI sounds and Animations pages already receive and send those pages' own commands, so the
// services behind them stay the only authority for install, apply, update and reset.
const createWsgmCustomization = () => {
  const MemoName = "wsgm.customization";
  const HubKey = "wsgm-customization-hub";
  // The route table, by the on-screen keyboard route only it declares, as the screensaver gate finds it.
  const CustomizationRouteTokens = ["GameAPIOSK:", "/gameapiosk"] as const;
  // Valve's Startup Movie section names its heading; WSGM's sections go in front of it.
  const StartupMovieToken = '"#Settings_Customization_StartupVideo"';
  const Sources = {
    themes: { patchId: "wsgm.themes", route: "/wsgm/themes" },
    sounds: { patchId: "wsgm.sounds", route: "/wsgm/sounds" },
    animations: { patchId: "wsgm.animations", route: "/wsgm/animations" },
  } as const;
  type Source = keyof typeof Sources;

  let react: any = null;
  let ui: any = null;
  let route = "";
  let installed = false;
  let lastError = "";
  let lastOutcome = "never rendered";
  const local = createLocalStore();
  // Each source's last publication; null until it arrives, absent when this session has no such service.
  const states: Record<Source, any> = { themes: null, sounds: null, animations: null };
  const available: Record<Source, boolean> = { themes: false, sounds: false, animations: false };
  const unsubscribes: (() => void)[] = [];
  const pageCache = new Map();
  const listCache = new WeakMap<object, unknown[]>();
  const wrappers = new WeakSet<object>();

  const act = (source: Source, command: string, payload: any = {}) =>
    request(Sources[source].patchId, command, payload).catch((error) => {
      lastError = `${source} ${command} failed: ${String(error)}`;
      return undefined;
    });

  // Opens a source's page, on the tab that suits what the user has so far.
  const open = (source: Source, tab?: string) => {
    const go = () => {
      if (!navigateSteamRoute(Sources[source].route))
        lastError = `could not open ${Sources[source].route}`;
    };
    if (tab) void act(source, "setTab", { tab }).then(go);
    else go();
  };

  const row = (
    props: any,
    change: (value: any) => void = () => {},
    action: () => void = () => {},
  ) =>
    renderSteamSettingRow(
      ui,
      props,
      undefined,
      (_row, value, commit = true) => {
        if (commit) change(value);
      },
      action,
    );

  const manage = (key: string, label: string, description: string, onOpen: () => void) =>
    row({ key, kind: "action", label, description, buttonLabel: "Open" }, undefined, onOpen);

  const waiting = (key: string, text: string) => row({ key, kind: "note", label: text, text: "" });

  const themesSection = () => {
    const state = states.themes;
    const h = react.createElement;
    if (!state)
      return h(
        ui.settingsSection,
        { key: "themes", label: "Themes" },
        waiting("themes-wait", "Loading themes…"),
      );
    const themes: any[] = state.themes ?? [];
    const presets: any[] = state.presets ?? [];
    const on = !!state.settings?.enabled;
    const enabled = themes.filter((theme) => theme.enabled).length;
    const summary = !on
      ? "Off: Steam's own look. Your themes stay installed and come back when this is on."
      : themes.length
        ? `${enabled} of ${themes.length} themes on${state.selectedPreset ? ` · Profile: ${state.selectedPreset}` : ""}`
        : "No themes installed yet.";
    return h(
      ui.settingsSection,
      { key: "themes", label: "Themes" },
      row(
        {
          key: "themes-on",
          kind: "boolean",
          label: "Use themes",
          description: summary,
          checked: on,
          disabled: !!state.busy,
        },
        (value) => void act("themes", "setSetting", { key: "enabled", value: !!value }),
      ),
      presets.length
        ? row(
            {
              key: "themes-profile",
              kind: "choice",
              label: "Profile",
              description: "Turns a saved set of themes and their settings on together.",
              text: state.selectedPreset ?? "",
              disabled: !on || !!state.busy,
              choices: [
                ...(state.selectedPreset === "Invalid State"
                  ? [{ value: "Invalid State", label: "Invalid State" }]
                  : []),
                { value: "", label: "None" },
                ...presets.map((preset) => ({ value: preset.name, label: preset.displayName })),
              ],
            },
            (value) => void act("themes", "setProfile", { name: String(value ?? "") }),
          )
        : null,
      ...themes.map((theme) =>
        h(
          "div",
          { key: `theme-${theme.name}`, className: "steam-ui-kit-nested" },
          row(
            {
              key: `themes-theme-${theme.name}`,
              kind: "boolean",
              label: theme.displayName,
              description:
                theme.status === "outdated"
                  ? `Update available (${theme.latestVersion})`
                  : [theme.version, theme.author].filter(Boolean).join(" · "),
              checked: !!theme.enabled,
              disabled: !on || !!state.busy,
            },
            (value) => void act("themes", "setEnabled", { name: theme.name, enabled: !!value }),
          ),
        ),
      ),
      state.updates > 0
        ? row(
            {
              key: "themes-update",
              kind: "action",
              label: `Updates for ${state.updates} theme${state.updates === 1 ? "" : "s"}`,
              buttonLabel: "Update All",
              disabled: !!state.busy,
            },
            undefined,
            () => void act("themes", "updateAll"),
          )
        : null,
      state.error
        ? row({
            key: "themes-error",
            kind: "note",
            label: "Last change failed",
            text: "",
            description: state.error,
          })
        : null,
      manage(
        "themes-manage",
        "Browse and manage themes",
        "Screenshots, install, update and remove, each theme's options and profiles.",
        () => open("themes", themes.length ? "installed" : "browse"),
      ),
    );
  };

  const soundsSection = () => {
    const state = states.sounds;
    const h = react.createElement;
    if (!state)
      return h(
        ui.settingsSection,
        { key: "sounds", label: "UI sounds" },
        waiting("sounds-wait", "Loading sound packs…"),
      );
    const packs: any[] = (state.packs ?? []).filter((pack) => !pack.error);
    const known = !state.selected || packs.some((pack) => pack.id === state.selected);
    return h(
      ui.settingsSection,
      { key: "sounds", label: "UI sounds" },
      row(
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
            ...(known ? [] : [{ value: state.selected, label: "Unavailable pack" }]),
            ...packs.map((pack) => ({ value: pack.id, label: pack.name })),
          ],
        },
        (value) => void act("sounds", "select", { id: String(value ?? "") }),
      ),
      state.error
        ? row({
            key: "sounds-error",
            kind: "note",
            label: "Last change failed",
            text: "",
            description: state.error,
          })
        : null,
      manage(
        "sounds-manage",
        "Browse and manage sound packs",
        "DeckThemes Audio Loader packs, previews, ZIP import, update and remove.",
        () => open("sounds"),
      ),
    );
  };

  const animationsSection = () => {
    const state = states.animations;
    const h = react.createElement;
    if (!state)
      return h(
        ui.settingsSection,
        { key: "animations", label: "Boot animation" },
        waiting("animations-wait", "Loading boot movies…"),
      );
    const library: any[] = state.library ?? [];
    const known = !state.selected || library.some((item) => item.id === state.selected);
    const description = state.selected
      ? "Plays when Big Picture starts. Steam's Startup Movie choice below is set aside while it does, and comes back when you choose Steam's own here."
      : "Steam's Startup Movie choice below decides what plays.";
    return h(
      ui.settingsSection,
      { key: "animations", label: "Boot animation" },
      row(
        {
          key: "animations-boot",
          kind: "choice",
          label: "Boot movie",
          description,
          text: state.selected ?? "",
          disabled: !!state.busy,
          choices: [
            { value: "", label: state.stockName || "Steam's own" },
            ...(known ? [] : [{ value: state.selected, label: "Unavailable movie" }]),
            ...library.map((item) => ({ value: item.id, label: item.name })),
          ],
        },
        (value) => void act("animations", "select", { id: String(value ?? "") }),
      ),
      state.settings?.restartNeeded
        ? row({
            key: "animations-restart",
            kind: "note",
            label: "Restart Steam to see the change",
            text: "",
            description: "Steam reads its startup movie when it starts.",
            accent: true,
          })
        : null,
      row({
        key: "animations-suspend",
        kind: "note",
        label: "Suspend and standby movies",
        text: "",
        description:
          "Steam plays these only in its own suspend flow, which Windows does not run, so the boot movie is the one Steam shows here.",
      }),
      state.error
        ? row({
            key: "animations-error",
            kind: "note",
            label: "Last change failed",
            text: "",
            description: state.error,
          })
        : null,
      manage(
        "animations-manage",
        "Browse and manage boot animations",
        "SteamDeckRepo movies with previews, your own WebM files, shuffle and volume.",
        () => open("animations", library.length ? "library" : "browse"),
      ),
    );
  };

  function WsgmCustomizationHub() {
    react.useSyncExternalStore(local.subscribe, local.revision);
    if (!installed) return null;
    const sections = [
      available.themes ? themesSection() : null,
      available.sounds ? soundsSection() : null,
      available.animations ? animationsSection() : null,
    ].filter(Boolean);
    lastOutcome = `rendered ${sections.length} section(s)`;
    return react.createElement(react.Fragment, null, steamUiKitStyle(react), ...sections);
  }

  const childrenOf = (element) => {
    const children = element.props?.children;
    return Array.isArray(children) ? children : children === undefined ? [] : [children];
  };

  const isStartupMovie = (child) =>
    react.isValidElement(child) &&
    typeof child.type === "function" &&
    String(child.type).includes(StartupMovieToken);

  const isHub = (child) => react.isValidElement(child) && child.key === HubKey;

  // Whatever renders the page: Valve's component, or another gate's wrapper around it. The hub is
  // inserted once, in front of Valve's Startup Movie section, or last when this client has none.
  const pageFor = (original) => {
    let wrapped = pageCache.get(original);
    if (wrapped) return wrapped;
    wrapped = function WsgmCustomizationPage(props) {
      const tree = original(props);
      if (!installed || !react.isValidElement(tree)) return tree;
      const children = childrenOf(tree);
      if (children.some(isHub)) return tree;
      const hub = react.createElement(WsgmCustomizationHub, { key: HubKey });
      const at = children.findIndex(isStartupMovie);
      if (at < 0) lastOutcome = "Steam's Startup Movie section was not found; the hub is last";
      const next =
        at < 0 ? [...children, hub] : [...children.slice(0, at), hub, ...children.slice(at)];
      return react.cloneElement(tree, undefined, ...next);
    };
    pageCache.set(original, wrapped);
    wrappers.add(wrapped);
    return wrapped;
  };

  // The Settings page list, with the customization page's content wrapped. The same input list
  // always maps to the same output list, so memo consumers downstream see a stable identity.
  const transformPages = (value) => {
    if (!installed || !Array.isArray(value) || !value.length) return value;
    const first = value[0];
    if (!first || typeof first !== "object" || !("route" in first) || !("content" in first))
      return value;
    const cached = listCache.get(value);
    if (cached) return cached;
    let index = -1;
    for (let at = 0; at < value.length; at++) {
      const item = value[at];
      if (
        item &&
        typeof item === "object" &&
        item.route === route &&
        react.isValidElement(item.content)
      ) {
        if (index >= 0) return value;
        index = at;
      }
    }
    if (index < 0) return value;
    const content = value[index].content;
    if (typeof content.type !== "function" || wrappers.has(content.type)) return value;
    const next = value.slice();
    next[index] = {
      ...value[index],
      content: react.createElement(pageFor(content.type), keyed(content)),
    };
    listCache.set(value, next);
    return next;
  };

  const resolve = () => {
    const runtime = getWebpackRuntime("customization-hub");
    ui = resolveSteamSettingsComponents(runtime);
    react = ui?.react ?? null;
    if (!react || typeof react.useSyncExternalStore !== "function") {
      lastError = "React with useSyncExternalStore is unavailable";
      return false;
    }
    const missing = [
      "settingsSection",
      "toggleField",
      "dropdown",
      "valueField",
      "dialogButton",
    ].filter((name) => !ui[name]);
    if (missing.length) {
      lastError = `Steam settings components unavailable: ${missing.join(", ")}`;
      return false;
    }
    const pages = runtime.exported(
      [...CustomizationRouteTokens],
      (value) => typeof value?.Settings?.Customization === "function",
    ) as any;
    route = pages.Settings.Customization();
    if (typeof route !== "string" || !route.startsWith("/")) {
      lastError = "the customization settings route is unavailable";
      return false;
    }
    return true;
  };

  const install = () => {
    if (installed) return { ok: true, alreadyInstalled: true };
    if (
      !attemptResolution(
        resolve,
        (error) => (lastError = "customization hub resolution failed: " + String(error)),
      )
    )
      return { ok: false, error: lastError };
    installed = true;
    const intercepted = interceptMemo(react, MemoName, transformPages);
    if (!intercepted.ok) {
      installed = false;
      lastError = intercepted.error ?? "React useMemo could not be intercepted";
      return { ok: false, error: lastError };
    }
    lastError = "";
    for (const source of Object.keys(Sources) as Source[]) {
      // A session without the service has no publication to subscribe to, and no section to show.
      try {
        unsubscribes.push(
          subscribe(Sources[source].patchId, (published) => {
            states[source] = published && typeof published === "object" ? published : null;
            local.changed();
          }),
        );
        available[source] = true;
      } catch {
        available[source] = false;
      }
    }
    local.changed();
    return { ok: true, installed: true };
  };

  const remove = () => {
    if (!installed) return { ok: true, absent: true };
    const released = releaseMemo(react, MemoName);
    if (!released.ok) {
      lastError = released.error ?? "React useMemo could not be released";
      return { ok: false, error: lastError };
    }
    installed = false;
    while (unsubscribes.length) endSubscription(unsubscribes.pop() ?? null);
    for (const source of Object.keys(Sources) as Source[]) {
      states[source] = null;
      available[source] = false;
    }
    // An open page re-renders without the hub; its wrapper passes Steam's own tree through.
    local.changed();
    pageCache.clear();
    return { ok: true, removed: true };
  };

  // The standard probe answer. It asks only that Steam's modules are loaded, as the C# probe did; the
  // install's own resolution is what refuses a client without the settings route or fields.
  const probe = () => {
    if (installed) return { ok: true, outcome: "ready", installed: true, facts: { runtime: true } };
    try {
      getWebpackRuntime("customization-hub");
    } catch (error) {
      return { ok: false, outcome: "notReady", reason: `Steam modules unavailable: ${String(error)}` };
    }
    return { ok: true, outcome: "ready", facts: { runtime: true } };
  };

  const status = () => ({
    ok: true,
    installed,
    registered: !!react && memoIntercepted(react, MemoName),
    // The standard status: the memo transform and the publications it subscribed to.
    held: installed && !!react && memoIntercepted(react, MemoName),
    claims: (!!react && memoIntercepted(react, MemoName) ? 1 : 0) + unsubscribes.length,
    route,
    sources: Object.fromEntries(
      (Object.keys(Sources) as Source[]).map((source) => [
        source,
        available[source] ? (states[source] ? "state" : "waiting") : "absent",
      ]),
    ),
    lastOutcome,
    lastError,
  });

  return { install, remove, status, probe };
};

registerGate("wsgmCustomization", createWsgmCustomization());
