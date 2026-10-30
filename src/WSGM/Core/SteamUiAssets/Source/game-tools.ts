// Game Tools presentation for one game. Folder reading, program detection, downloads, the install
// transaction and every refusal belong to GameToolsService; this page draws its published state and
// sends its exact commands, each carrying the AppID from the route.
const GameToolsPatchId = "wsgm.game-tools";

// The page's own layout: where the kit's elements go, not how they look.
const gameToolsStyles = `
#wsgm-game-tools { overflow-wrap: anywhere; }
#wsgm-game-tools .wsgm-game-tools-busy { display: flex; flex-direction: column; gap: 6px; padding: 12px 14px; border-radius: 3px; background: rgba(255,255,255,.06); }
#wsgm-game-tools .wsgm-game-tools-busy-title { font-size: 15px; font-weight: 600; color: #fff; }
#wsgm-game-tools .wsgm-game-tools-list { display: flex; flex-direction: column; gap: 8px; }
#wsgm-game-tools .wsgm-game-tools-list .DialogButton { width: 100%; min-width: 0; height: auto; padding: 10px 14px; }
#wsgm-game-tools .wsgm-game-tools-tag { font-size: 12px; font-weight: 600; white-space: nowrap; color: #8b929a; }
#wsgm-game-tools .wsgm-game-tools-tag[data-current="true"] { color: #a3dca3; }
#wsgm-game-tools .wsgm-game-tools-warnings { display: flex; flex-direction: column; gap: 8px; }
`;

const gameToolsOperations = {
  install: "Installing",
  replaceExisting: "Replacing the existing installation",
  update: "Updating",
  repair: "Repairing",
  configure: "Saving settings",
  remove: "Removing",
};

/**
 * Keeps a field description's separate facts on separate lines; a long path still wraps.
 * @param react Steam's React instance.
 * @param lines The facts in display order; empty ones are dropped.
 * @returns The description element, or undefined when nothing is left.
 */
const gameToolsLines = (react, lines: any[]) => {
  const text = lines.filter(Boolean).join("\n");
  return text ? react.createElement("span", { style: { whiteSpace: "pre-line" } }, text) : undefined;
};

/**
 * The running operation: its phase, a meter while the size is known, and Cancel while nothing in the
 * game folder has been written yet.
 * @param props Resolved components, the published busy record and the cancel callback.
 * @returns The progress block.
 */
function GameToolsProgress({ ui, busy, onCancel }: any) {
  const h = ui.react.createElement;
  const title = `${gameToolsOperations[busy.operation] ?? "Working"}…`;
  const amount = busy.bytes > 0
    ? busy.total > 0
      ? `${wsgmFormatBytes(busy.bytes)} of ${wsgmFormatBytes(busy.total)}`
      : wsgmFormatBytes(busy.bytes)
    : "";
  return h(
    "div",
    { className: "wsgm-game-tools-busy", role: "status" },
    h("div", { className: "wsgm-game-tools-busy-title" }, title),
    h("div", { className: "steam-ui-kit-muted" }, [busy.phase, amount].filter(Boolean).join(" · ")),
    renderSteamUiMeter(ui.react, { value: busy.bytes, max: busy.total, label: title }),
    busy.cancellable
      ? renderSteamUiActions(ui, [{ id: "cancel", label: "Cancel", onClick: onCancel }], { compact: true })
      : h("div", { className: "steam-ui-kit-muted" }, "Game files are being written; this can't be cancelled now."),
  );
}

/**
 * Every program in the game folder, best first, each with what WSGM read from it.
 * @param props Resolved components, the published state, whether a change is allowed and the choose callback.
 * @returns A focusable list of select rows.
 */
function GameToolsCandidates({ ui, state, disabled, onChoose }: any) {
  const h = ui.react.createElement;
  const candidates = state.candidates ?? [];
  if (candidates.length === 0) {
    return renderSteamUiEmpty(ui.react, "No programs found in the game's folder. Use Browse to choose one.");
  }
  return h(
    ui.focusable,
    { className: "wsgm-game-tools-list", "flow-children": "column" },
    ...candidates.map((candidate) => {
      const role = candidate.roleLabel || "Program";
      return renderSteamUiSelectRow(ui, {
        key: candidate.relativePath,
        title: candidate.fileName,
        selected: !!candidate.chosen,
        status: h(
          "span",
          { className: "wsgm-game-tools-tag", "data-current": candidate.chosen ? "true" : "false" },
          candidate.chosen ? "In use" : role,
        ),
        detail: [
          candidate.relativePath,
          [role, candidate.architectureLabel || "Unknown architecture"].join(" · "),
          ...(candidate.reasons ?? []),
        ].join("\n"),
        onClick: () => {
          if (!disabled) onChoose(candidate.relativePath);
        },
      });
    }),
  );
}

/**
 * The program choice: a heading, the large-folder note, every candidate and Browse, with Back when it
 * is a level of its own.
 * @param props Resolved components, published state, whether changes are allowed, the heading and the
 * choose, browse and optional back callbacks.
 * @returns The chooser's elements.
 */
function GameToolsChooser({ ui, state, locked, page, title, detail, onChoose, onBrowse, onBack }: any) {
  const react = ui.react;
  const h = react.createElement;
  const actions: any[] = [{ id: "browse", label: "Browse…", disabled: locked, onClick: onBrowse }];
  if (onBack) actions.push({ id: "back", label: "Back", onClick: onBack });
  return h(
    react.Fragment,
    null,
    renderSteamUiHeader(ui, { page, title, detail }),
    state.scanTruncated
      ? renderSteamUiEmpty(react, "The folder is large, so this list may be incomplete. Use Browse if the program is missing.")
      : null,
    h(GameToolsCandidates, { ui, state, disabled: locked, onChoose }),
    renderSteamUiActions(ui, actions, { compact: true }),
  );
}

/**
 * One tool: its status, versions, conflicts, primary action, proxy name, settings and Remove.
 * @param props Resolved components, published state, the tool, the local draft, whether changes are
 * allowed and the command, draft and confirm callbacks.
 * @returns The tool's settings section and its actions.
 */
function GameToolsTool({ ui, state, tool, draft, locked, act, setDraft, confirm }: any) {
  const react = ui.react;
  const h = react.createElement;
  const actions: string[] = tool.actions ?? [];
  const installing = actions.includes("install");
  // Before an install the choices are a draft that goes out with Install; afterwards each change is
  // sent on its own and the row shows what the host published until it answers.
  const proxyName = installing ? (draft.proxyName ?? tool.proxyName) : tool.proxyName;
  const settingValue = (setting) =>
    installing && draft.settings?.[setting.id] !== undefined ? draft.settings[setting.id] : setting.value;
  const appId = state.appId;
  const toolId = tool.id;
  const install = (replaceExisting: boolean) => {
    const payload: any = { appId, toolId, replaceExisting };
    if (draft.proxyName && draft.proxyName !== tool.proxyName) payload.proxyName = draft.proxyName;
    if (draft.settings && Object.keys(draft.settings).length) payload.settings = draft.settings;
    // The host's anti-cheat text names what it found; showing it is the acknowledgement it asks for.
    const warning = state.antiCheatWarning ?? "";
    if (warning) payload.acknowledgeRisk = true;
    const run = () => void act("install", payload);
    if (replaceExisting) {
      confirm({
        title: `Replace the existing ${tool.name}?`,
        text: `WSGM backs up the files of the ${tool.name} installed outside WSGM, removes them and installs its own as ${proxyName}. Removing it later restores them.${warning ? `\n\n${warning}` : ""}`,
        confirmLabel: "Replace",
        onConfirm: run,
      });
    } else if (warning) {
      confirm({
        title: `Install ${tool.name} anyway?`,
        text: warning,
        confirmLabel: "Install anyway",
        onConfirm: run,
      });
    } else {
      run();
    }
  };
  const update = () => {
    if (!tool.updateChangesConfig) {
      void act("update", { appId, toolId });
      return;
    }
    confirm({
      title: `Update ${tool.name}?`,
      text: `${tool.latestVersion ?? "The new version"} uses a different settings file. WSGM replaces the settings file with the new one, so settings changed in the tool's own menu are reset.`,
      confirmLabel: "Update",
      onConfirm: () => void act("update", { appId, toolId, replaceConfig: true }),
    });
  };
  const remove = () =>
    confirm({
      title: `Remove ${tool.name} from ${state.name}?`,
      text: `WSGM deletes the files it installed, including its settings file, and restores the game files it replaced. Files the game changed since are left in place.`,
      confirmLabel: "Remove",
      onConfirm: () => void act("remove", { appId, toolId }),
    });

  const buttons: any[] = [];
  if (installing) buttons.push({ id: "install", label: "Install", primary: true, onClick: () => install(false) });
  if (actions.includes("replaceExisting"))
    buttons.push({ id: "replaceExisting", label: "Replace existing installation…", primary: true, onClick: () => install(true) });
  if (actions.includes("update"))
    buttons.push({ id: "update", label: tool.latestVersion ? `Update to ${tool.latestVersion}` : "Update", primary: true, onClick: update });
  if (actions.includes("repair"))
    buttons.push({ id: "repair", label: "Repair", primary: tool.status === "modified", onClick: () => void act("repair", { appId, toolId }) });
  if (actions.includes("remove")) buttons.push({ id: "remove", label: "Remove…", onClick: remove });
  for (const button of buttons) button.disabled = locked;

  const versions = [
    tool.installedVersion ? `Installed ${tool.installedVersion}` : null,
    tool.checkingLatest
      ? "Checking for the latest release…"
      : tool.latestVersion
        ? `Latest ${tool.latestVersion}`
        : tool.latestError || "Latest release not checked yet",
  ].filter(Boolean).join(" · ");

  const rows: any[] = [
    {
      key: "status",
      kind: "note",
      label: "Status",
      text: "",
      description: gameToolsLines(react, [
        tool.incompatibleReason ?? tool.statusText,
        versions,
      ]),
      accent: tool.status === "conflict" || tool.status === "modified",
    },
  ];
  for (const conflict of tool.conflicts ?? []) {
    rows.push({
      key: `conflict-${conflict.path}`,
      kind: "note",
      label: conflict.kindLabel || "Conflict",
      text: "",
      description: gameToolsLines(react, [conflict.message, conflict.path]),
      accent: true,
    });
  }

  // Proxy names another file already uses stay listed, with the reason, so the user sees why the
  // suggested one differs; choosing one says why it can't be used instead of sending it.
  const proxyOption = (tool.proxyOptions ?? []).find((option) => option.name === proxyName);
  const editable = !locked && !!tool.canEditOptions;
  if ((tool.proxyOptions ?? []).length && tool.showOptions) {
    rows.push({
      key: "proxy",
      kind: "choice",
      label: "Load as",
      description: proxyOption?.conflict ?? proxyOption?.hint ?? "",
      text: proxyName,
      choices: tool.proxyOptions.map((option) => ({
        value: option.name,
        label: option.available || option.name === tool.proxyName ? option.name : `${option.name} (in use)`,
      })),
      disabled: !editable,
      layout: "below",
    });
  }
  if (tool.showOptions) {
    for (const setting of tool.settings ?? []) {
      const value = settingValue(setting);
      const choices = (setting.options ?? []).map((option) => ({ value: option.value, label: option.label }));
      // The file holds a value no option names; show it as is instead of claiming a default.
      if (!choices.some((choice) => choice.value === value)) {
        choices.unshift({ value, label: setting.iniValue ? `Custom (${setting.iniValue})` : "Custom" });
      }
      rows.push({
        key: `setting-${setting.id}`,
        kind: "choice",
        label: setting.label,
        description: [setting.description, setting.note].filter(Boolean).join(" "),
        text: value,
        choices,
        disabled: !editable || !setting.available,
        layout: "below",
      });
    }
  }
  const files = tool.files;
  if (files && (files.owned > 0 || files.replaced > 0 || files.missing?.length || files.modified?.length)) {
    const lines = [
      `${files.owned} files added${files.replaced ? `, ${files.replaced} game files backed up and replaced` : ""}`,
      files.targetDirectory ? `In ${files.targetDirectory}` : "In the game's folder",
      files.missing?.length ? `Missing: ${files.missing.join(", ")}` : null,
      files.modified?.length ? `Changed: ${files.modified.join(", ")}` : null,
    ].filter(Boolean);
    rows.push({
      key: "files",
      kind: "note",
      label: "Files",
      text: "",
      description: gameToolsLines(react, lines),
      accent: !!(files.missing?.length || files.modified?.length),
    });
  }
  rows.push({
    key: "about",
    kind: "note",
    label: "About",
    text: tool.license ?? "",
    description: gameToolsLines(react, [tool.description, tool.homepage, tool.notice]),
  });

  const change = (row, value) => {
    if (locked || value === undefined || value === null) return;
    if (row.key === "proxy") {
      const option = (tool.proxyOptions ?? []).find((entry) => entry.name === value);
      if (option && !option.available && option.name !== tool.proxyName) {
        act.refuse(option.conflict ?? `${option.name} is already used by another file.`);
        return;
      }
      if (installing) setDraft({ ...draft, proxyName: value });
      else if (value !== tool.proxyName) void act("configure", { appId, toolId, proxyName: value });
      return;
    }
    const setting = (tool.settings ?? []).find((entry) => `setting-${entry.id}` === row.key);
    if (!setting || value === "") return;
    if (installing) setDraft({ ...draft, settings: { ...(draft.settings ?? {}), [setting.id]: value } });
    else if (value !== setting.value) void act("configure", { appId, toolId, settings: { [setting.id]: value } });
  };

  return h(
    react.Fragment,
    { key: `tool-${tool.id}` },
    renderSteamUiHeader(ui, { title: tool.name, detail: versions }),
    (tool.warnings ?? []).length
      ? h(
          "div",
          { className: "wsgm-game-tools-warnings" },
          ...tool.warnings.map((warning, index) =>
            h("div", { key: index }, renderSteamUiBanner(ui, { text: warning })),
          ),
        )
      : null,
    buttons.length ? renderSteamUiActions(ui, buttons, { compact: true }) : null,
    h(ui.settingsSection, null, ...rows.map((row) => renderSteamSettingRow(ui, row, undefined, change, () => {}))),
  );
}

/**
 * The game's program: what WSGM will install beside, why, and the ways to change it.
 * @param props Resolved components, published state, whether changes are allowed and the change/browse/choose callbacks.
 * @returns The program section; when WSGM needs a choice, the candidate list is the content.
 */
function GameToolsProgram({ ui, state, locked, onChange, onBrowse, onChoose }: any) {
  const react = ui.react;
  const h = react.createElement;
  const runtime = state.runtime;
  if (!runtime) {
    return h(GameToolsChooser, {
      ui,
      state,
      locked,
      title: "Choose the program the game runs",
      detail: state.rememberedMissing
        ? "The program you chose earlier is gone, maybe after an update. Choose it again."
        : "WSGM couldn't tell which program starts the game. Tools install beside the one you choose.",
      onChoose,
      onBrowse,
    });
  }
  const row = {
    key: "program",
    kind: "action",
    label: runtime.fileName,
    description: gameToolsLines(react, [
      runtime.relativePath,
      [state.engineLabel, runtime.architectureLabel || "Unknown architecture"].filter(Boolean).join(" · "),
      runtime.confidenceLabel,
    ]),
    buttonLabel: "Change…",
    disabled: locked,
  };
  return h(ui.settingsSection, { label: "Game program" }, renderSteamSettingRow(ui, row, undefined, () => {}, onChange));
}

/**
 * What the folder scan found that bears on the tools: upscaler libraries and anti-cheat.
 * @param props Resolved components and published state.
 * @returns The detection section.
 */
function GameToolsDetected({ ui, state }: any) {
  const react = ui.react;
  const h = react.createElement;
  const upscalers = state.upscalers ?? [];
  const rows = upscalers.length
    ? upscalers.map((upscaler) => ({
        key: `upscaler-${upscaler.relativePath}`,
        kind: "note",
        label: upscaler.technology,
        text: upscaler.version ?? "",
        description: upscaler.relativePath,
      }))
    : [
        {
          key: "upscaler-none",
          kind: "note",
          label: "Upscalers",
          text: "",
          description: "No standalone upscaler library found. The game may still have one built in.",
        },
      ];
  return h(
    ui.settingsSection,
    { label: "Detected in the game folder" },
    ...rows.map((row) => renderSteamSettingRow(ui, row, undefined, () => {}, () => {})),
  );
}

/**
 * Renders the Game Tools page for the game in the route, with a nested program list.
 * @param context Registered page accessors for Steam components, latest state and publication refusal.
 * @param page The routed page descriptor carrying the AppID parameter.
 * @returns The page React tree, including loading, unavailable and refusal states.
 */
function GameToolsPage({ context, page }: any) {
  const react = context.react();
  const h = react.createElement;
  const ui = context.ui();
  const published = context.state();
  const routeAppId = Number(page?.params?.appid ?? 0);
  // Where the user is: the page itself or the full program list opened by Change.
  const [level, setLevel] = react.useState("page");
  const [drafts, setDrafts] = react.useState({});
  const [refusal, setRefusal] = react.useState("");
  const [sending, setSending] = react.useState(false);
  const lifetime = react.useRef(null as AbortController | null);
  react.useEffect(() => {
    lifetime.current = new AbortController();
    return () => lifetime.current?.abort();
  }, []);
  react.useEffect(() => {
    // Direct navigation and Back/forward into the page both land here; the host publishes this game.
    setLevel("page");
    setDrafts({});
    setRefusal("");
    if (routeAppId > 0) void request(GameToolsPatchId, "open", { appId: routeAppId }).catch(() => undefined);
  }, [routeAppId]);
  const state = published && routeAppId > 0 && Number(published.appId) === routeAppId ? published : null;
  // A tool that got installed or removed leaves no draft behind for the next install.
  react.useEffect(() => {
    if (!state) return;
    const installable = new Set((state.tools ?? []).filter((tool) => (tool.actions ?? []).includes("install")).map((tool) => tool.id));
    if (Object.keys(drafts).some((id) => !installable.has(id))) {
      setDrafts(Object.fromEntries(Object.entries(drafts).filter(([id]) => installable.has(id))));
    }
  }, [state]);

  const shell = (...children) =>
    h(
      "div",
      { id: "wsgm-game-tools", className: "steam-ui-kit-page", "aria-label": "Game Tools" },
      steamUiKitStyle(react),
      h("style", null, gameToolsStyles),
      ...children,
    );
  if (routeAppId <= 0) return shell(renderSteamUiEmpty(react, "No game was supplied.", true));
  if (!state) return shell(renderSteamUiEmpty(react, context.refusal() ?? "Reading this game…"));

  const busy = state.busy;
  const locked = !!busy || sending;
  /**
   * Sends one command for this game; a synchronous refusal is shown on the page, and the outcome of
   * admitted background work arrives later as the published notice or error.
   */
  const act: any = async (command: string, payload: any) => {
    if (sending && command !== "cancel") return undefined;
    setRefusal("");
    setSending(true);
    try {
      return await request(GameToolsPatchId, command, payload);
    } catch (failure) {
      if (!lifetime.current?.signal.aborted) setRefusal(String((failure as any)?.message ?? failure));
      return undefined;
    } finally {
      if (!lifetime.current?.signal.aborted) setSending(false);
    }
  };
  act.refuse = (message: string) => setRefusal(message);
  const confirm = (props) => {
    if (!showSteamUiConfirm(ui, props)) setRefusal("Steam's confirmation dialog is unavailable.");
  };
  const choose = async (path: string) => {
    const result = await act("chooseRuntime", { appId: routeAppId, path });
    if (result !== undefined && !lifetime.current?.signal.aborted) setLevel("page");
  };
  const browse = async () => {
    const path = await showSteamFilePicker(ui, {
      title: `Choose the program ${state.name} runs`,
      mode: "file",
      extensions: [".exe"],
      start: state.installFolder || undefined,
      signal: lifetime.current?.signal,
    });
    if (path && !lifetime.current?.signal.aborted) await choose(path);
  };
  const refresh = () => void act("refresh", { appId: routeAppId });

  const banners: any[] = [];
  const message = refusal || state.error || state.notice;
  if (message) {
    banners.push(
      renderSteamUiBanner(ui, {
        text: message,
        error: !!(refusal || state.error),
        // Only the page's own refusal is local; the host's message stays until the next command.
        onDismiss: refusal ? () => setRefusal("") : undefined,
      }),
    );
  }
  if (state.availability !== "ready" && state.reason) {
    banners.push(renderSteamUiBanner(ui, { text: state.reason, error: state.availability !== "running" }));
  }
  if (state.antiCheatWarning) {
    banners.push(renderSteamUiBanner(ui, { text: state.antiCheatWarning, error: true }));
  }
  const banner = banners.length
    ? h("div", { className: "steam-ui-kit-page-banner" }, ...banners.map((element, index) => h("div", { key: index }, element)))
    : null;

  const header = renderSteamUiHeader(ui, {
    page: true,
    title: state.name || `Game ${routeAppId}`,
    detail: [
      state.loading ? "Scanning the game folder…" : state.engineLabel,
      state.installFolder || (state.isShortcut ? "Non-Steam shortcut" : ""),
    ].filter(Boolean).join(" · "),
  });
  const pageActions = renderSteamUiActions(
    ui,
    [
      { id: "refresh", label: "Refresh", disabled: locked || state.loading, onClick: refresh },
      {
        id: "checkUpdates",
        label: "Check for updates",
        disabled: locked || (state.tools ?? []).some((tool) => tool.checkingLatest),
        onClick: () => void act("checkUpdates", { appId: routeAppId }),
      },
    ],
    { compact: true },
  );
  const progress = busy
    ? h(GameToolsProgress, { ui, busy, onCancel: () => void act("cancel", { appId: routeAppId }) })
    : null;

  if (level === "programs" && state.availability === "ready") {
    return shell(
      banner,
      renderSteamUiLevel(
        ui,
        { bounded: true, onBack: () => setLevel("page") },
        h(GameToolsChooser, {
          ui,
          state,
          locked,
          page: true,
          title: "Choose the game's program",
          detail: `${state.name} · Tools install beside the program you choose.`,
          onChoose: (path) => void choose(path),
          onBrowse: () => void browse(),
          onBack: () => setLevel("page"),
        }),
      ),
    );
  }

  // Before the first scan finishes every field is a placeholder; say so instead of drawing it.
  const scanned = !(state.loading && !state.installFolder && (state.tools ?? []).length === 0);
  const ready = state.availability === "ready" || state.availability === "running";
  const content: any[] = [];
  if (!scanned) {
    content.push(renderSteamUiEmpty(react, "Scanning the game folder…"));
  } else if (ready) {
    content.push(
      h(GameToolsProgram, {
        key: "program",
        ui,
        state,
        locked: locked || state.availability !== "ready",
        onChange: () => setLevel("programs"),
        onBrowse: () => void browse(),
        onChoose: (path) => void choose(path),
      }),
      ...(state.tools ?? []).map((tool) =>
        h(GameToolsTool, {
          key: `tool-${tool.id}`,
          ui,
          state,
          tool,
          draft: drafts[tool.id] ?? {},
          locked,
          act,
          confirm,
          setDraft: (next) => setDrafts({ ...drafts, [tool.id]: next }),
        }),
      ),
      // Supporting evidence follows the tools so each primary action stays near the top.
      h(GameToolsDetected, { key: "detected", ui, state }),
    );
  }

  return shell(
    banner,
    renderSteamUiPane(ui, { key: "page", bounded: true }, header, pageActions, progress, ...content),
  );
}

const gameToolsPage = registerSteamPage({
  template: "game-tools",
  gate: "gameTools",
  patchId: GameToolsPatchId,
  components: resolveSteamSettingsComponents,
  required: [...SteamSettingsRequired, "dialogButtonPrimary", "modalRoot"],
  status: () => ({ appId: gameToolsPage.state()?.appId ?? 0 }),
  Page: GameToolsPage,
});
