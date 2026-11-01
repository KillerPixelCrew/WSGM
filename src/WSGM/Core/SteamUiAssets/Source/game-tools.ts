// Game Tools presentation: the list of games with tools (`/wsgm/game-tools`) and one game's page
// (`/wsgm/game-tools/<appId>`). Folder reading, program detection, downloads, the install
// transaction and every refusal belong to GameToolsService; this page draws its published state and
// sends its exact commands, each carrying the AppID from the route. The Overlay's GameToolsView is
// the same task over the same state, actions and wording.
const GameToolsPatchId = "wsgm.game-tools";
const GameToolsLibraryId = "wsgm.game-tools.library";
const GameToolsRoute = "/wsgm/game-tools";
const GameToolsSteps = ["Download", "Check the package", "Back up and copy", "Apply settings"];
const GameToolsStepOf: Record<string, number> = { download: 0, verify: 1, copy: 2, settings: 3, rescan: 4 };
// How many games the list mounts before Load More, as the Overlay's list does.
const GameToolsChunk = 48;
const GameToolsPurpose =
  "WSGM backs up every file it replaces and removes everything again on request.";

// The page's own layout: where the kit's elements go, not how they look. The root scrolls, so the
// hero, banners and both columns move together under Steam's header.
const gameToolsStyles = `
#wsgm-game-tools { overflow-wrap: anywhere; --steam-ui-kit-page-width: 1360px; }
#wsgm-game-tools .wsgm-game-tools-root { flex: 1 1 auto; min-height: 0; max-height: calc(100vh - var(--basicui-header-height, 40px)); overflow-y: auto; overflow-x: hidden; padding: 0 0 72px; gap: 0; }
#wsgm-game-tools .wsgm-game-tools-banners { display: flex; flex-direction: column; gap: 8px; width: 100%; max-width: calc(var(--steam-ui-kit-page-width) + 80px); min-width: 0; box-sizing: border-box; margin-inline: auto; padding: 16px 40px 0; }
#wsgm-game-tools .wsgm-game-tools-body { display: flex; align-items: flex-start; gap: 28px; width: 100%; max-width: calc(var(--steam-ui-kit-page-width) + 80px); min-width: 0; box-sizing: border-box; margin-inline: auto; padding: 24px 40px 0; }
#wsgm-game-tools .wsgm-game-tools-main { flex: 1 1 auto; min-width: 0; display: flex; flex-direction: column; gap: 16px; }
#wsgm-game-tools .wsgm-game-tools-tool { display: flex; flex-direction: column; gap: 14px; min-width: 0; }
#wsgm-game-tools .wsgm-game-tools-tool + .wsgm-game-tools-tool { margin-top: 16px; }
#wsgm-game-tools .wsgm-game-tools-remove { padding-top: 14px; border-top: 1px solid hsla(0, 0%, 100%, 0.1); }
#wsgm-game-tools .wsgm-game-tools-notes { display: flex; flex-direction: column; gap: 8px; min-width: 0; }
#wsgm-game-tools .wsgm-game-tools-section { display: flex; flex-direction: column; gap: 10px; min-width: 0; }
#wsgm-game-tools .wsgm-game-tools-browse { display: flex; flex-wrap: wrap; align-items: center; gap: 8px 12px; min-width: 0; }
#wsgm-game-tools .steam-ui-kit-toolbar { flex-wrap: wrap; }
@media (max-width: 760px) {
  #wsgm-game-tools .wsgm-game-tools-body { flex-direction: column; align-items: stretch; padding: 16px 20px 0; }
  #wsgm-game-tools .wsgm-game-tools-banners { padding: 12px 20px 0; }
}
`;

// The game list publication, followed while the page's gate holds.
let gameToolsLibrary: any = null;
let gameToolsLibraryEnd: any = null;
const gameToolsLibraryStore = createLocalStore();

const gameToolsMonths = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

/** "3 files" or "1 file". */
const gameToolsCount = (count: number, noun: string) => `${count} ${noun}${count === 1 ? "" : "s"}`;

/** The channel's latest release, short: "1 Nov build" for a dated build, else its version. */
const gameToolsShortLatest = (channel) => {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/u.exec(channel?.buildDate ?? "");
  if (match) {
    const month = gameToolsMonths[Number(match[2]) - 1];
    if (month) return `${Number(match[3])} ${month} build`;
  }
  return channel?.latestVersion ?? "";
};

/** The running operation as a tag: "Installing 0.9.3", "Saving settings". */
const gameToolsWorking = (busy) => {
  const verb =
    {
      install: "Installing",
      replaceExisting: "Installing",
      update: "Updating to",
      switchChannel: "Switching to",
      repair: "Repairing",
      configure: "Saving settings",
      remove: "Removing",
      addAddon: "Adding plugin",
      removeAddon: "Removing plugin",
    }[busy.operation] ?? "Working";
  return busy.version && !["configure", "remove", "addAddon", "removeAddon"].includes(busy.operation)
    ? `${verb} ${busy.version}`
    : verb;
};

/** The game's name: the scan's, or Steam's while the scan still carries the AppID as its name. */
const gameToolsName = (state, appId: number) => {
  const steam = steamAppOverview(appId)?.display_name ?? "";
  if (!state) return steam || `Game ${appId}`;
  const placeholder = state.loading && state.name === String(state.appId);
  return state.name && !placeholder ? state.name : steam || state.name || `Game ${appId}`;
};

/** The distinct upscaler technologies, each with its major and minor version when the name has none. */
const gameToolsUpscalers = (state): string[] => {
  const labels = (state.upscalers ?? []).map((upscaler) => {
    const parts = String(upscaler.version ?? "").split(".");
    return /\d/u.test(upscaler.technology) || parts.length < 2 || !parts[0]
      ? upscaler.technology
      : `${upscaler.technology} ${parts[0]}.${parts[1]}`;
  });
  return [...new Set<string>(labels)];
};

const gameToolsVendor = (vendor: string) =>
  ({ amd: "AMD", intel: "Intel", nvidia: "NVIDIA" })[vendor] ?? vendor;

const gameToolsAvailabilityTitle = (availability: string) =>
  ({
    running: "The game is running",
    folderUnavailable: "Folder unavailable",
    notInstalled: "Not installed",
    steamUnavailable: "Steam unavailable",
  })[availability];

/** The anti-cheat warning without the sentence the banner's title already says. */
const gameToolsAntiCheatMessage = (state) => {
  const warning = state.antiCheatWarning ?? "";
  const prefix = `Anti-cheat found: ${(state.antiCheat ?? []).join(", ")}.`;
  return warning.startsWith(prefix) ? warning.slice(prefix.length).trim() : warning;
};

/** Where a candidate sits: the folder root, a subfolder, or a folder outside the game's. */
const gameToolsLocation = (relativePath: string) => {
  const slash = Math.max(relativePath.lastIndexOf("/"), relativePath.lastIndexOf("\\"));
  if (slash < 0) return "in the folder root";
  const folder = relativePath.slice(0, slash).replace(/\//gu, "\\");
  return /^(?:[A-Za-z]:[\\/]|\\\\)/u.test(relativePath) ? folder : `${folder}\\`;
};

/** A list row's status tag, in the tone the Overlay's chip has. */
const gameToolsSummaryTag = (tool) => ({
  text: tool.statusLabel,
  tone:
    {
      upToDate: "success",
      updateAvailable: "update",
      modified: "update",
      interrupted: "danger",
    }[tool.status] ?? "neutral",
});

/**
 * The channel a tool's page shows: the user's draft while that channel is still offered, otherwise
 * the one the tool follows.
 */
const gameToolsChannel = (tool, draft) => {
  const channels = tool.channels ?? [];
  return (
    channels.find((channel) => channel.id === draft?.channel) ??
    channels.find((channel) => channel.current) ??
    channels[0] ?? {
      id: tool.channel,
      label: tool.channel,
      description: "",
      current: true,
      latestVersion: tool.latestVersion,
      latestLabel: tool.latestLabel,
      latestError: tool.latestError,
      checkingLatest: tool.checkingLatest,
      changesConfig: false,
    }
  );
};

/** The load name a tool's page shows: the draft while not installed and still offered. */
const gameToolsProxy = (tool, draft) =>
  !tool.installedVersion &&
  draft?.proxyName &&
  (tool.proxyOptions ?? []).some((option) => option.name === draft.proxyName && option.available)
    ? draft.proxyName
    : tool.proxyName;

/** What is installed, or what the tool does: the heading's summary line. */
const gameToolsSummary = (tool, working: boolean) => {
  if (!tool.installedVersion || working) return tool.description;
  const files = tool.files ?? { owned: 0, replaced: 0 };
  const parts: string[] = [];
  if (tool.installedLabel && tool.installedLabel !== tool.installedVersion) parts.push(tool.installedLabel);
  const from = files.targetDirectory ? ` from ${files.targetDirectory.replace(/\//gu, "\\")}` : "";
  parts.push(`${parts.length ? "loaded as" : "Loaded as"} ${tool.proxyName}${from}`);
  parts.push(`${gameToolsCount(files.owned, "file")} owned by WSGM`);
  if (files.replaced > 0) parts.push(`${gameToolsCount(files.replaced, "game file")} backed up`);
  return parts.join(" · ");
};

/** The status tags of a tool's heading. */
const gameToolsTags = (tool, channel, working: boolean, busy) => {
  const tags: any[] = [];
  if (working) {
    tags.push({ text: gameToolsWorking(busy), tone: "info" });
  } else if (tool.status === "notInstalled") {
    tags.push({ text: "Not installed" });
  } else if (tool.status === "installed" || tool.status === "updateAvailable") {
    tags.push({ text: `Installed ${tool.installedVersion}`, tone: "success" });
    if (tool.status === "updateAvailable") {
      const current = (tool.channels ?? []).find((item) => item.current) ?? channel;
      tags.push({ text: `Update ${gameToolsShortLatest(current)} available`, tone: "update" });
    }
  } else if (tool.status === "modified") {
    tags.push({ text: (tool.actions ?? []).includes("repair") ? "Needs repair" : "Interrupted", tone: "update" });
  } else if (tool.status === "conflict") {
    tags.push({ text: "Installed outside WSGM", tone: "update" });
  } else {
    tags.push({ text: "Unavailable" });
  }
  const shown =
    tool.installedVersion && !working ? ((tool.channels ?? []).find((item) => item.current) ?? channel) : channel;
  if (shown && tool.channels?.[0] && shown.id !== tool.channels[0].id) tags.push({ text: shown.label, tone: "info" });
  return tags;
};

/**
 * The running operation through the kit's progress: the four steps of an install, the phase, a bar
 * counted in bytes while the size is known, Cancel while nothing in the game folder has been written
 * yet, and what cancelling does.
 * @param ui Resolved components.
 * @param busy The published busy record.
 * @param onCancel Sends the cancel command.
 * @returns The progress block.
 */
const gameToolsProgress = (ui, busy, onCancel: () => void) => {
  const installs = ["install", "replaceExisting", "update", "switchChannel", "repair", "addAddon", "removeAddon"].includes(
    busy.operation,
  );
  const noun =
    { update: "update", switchChannel: "switch", repair: "repair", addAddon: "change", removeAddon: "change" }[
      busy.operation
    ] ?? "install";
  const amount =
    busy.bytes > 0
      ? busy.total > 0
        ? `${wsgmFormatBytes(busy.bytes)} of ${wsgmFormatBytes(busy.total)}`
        : wsgmFormatBytes(busy.bytes)
      : "";
  return renderSteamUiProgress(ui, {
    phase: `${busy.detail || "Working"}…`,
    done: busy.bytes,
    total: busy.total,
    count: amount,
    steps: installs ? GameToolsSteps : null,
    step: GameToolsStepOf[busy.phase] ?? -1,
    note: !installs
      ? "WSGM finishes this on its own. Leaving this page doesn't stop it."
      : busy.cancellable
        ? `Nothing in the game folder has changed yet, so cancelling now leaves the game as it was. Once files are being copied, the ${noun} finishes or is rolled back.`
        : `Files are being written. The ${noun} finishes or is rolled back on its own, and leaving this page doesn't stop it.`,
    onCancel: busy.cancellable ? onCancel : null,
    cancelLabel: "Cancel",
  });
};

/**
 * One tool for this game: its heading, notes, the running work or the one primary action, release
 * channel, load name and settings as Steam's own fields, the supporting actions, Remove set apart
 * and the license line.
 * @param props Resolved components, published state, the tool, its draft, the game's name, whether
 * the game is idle, whether a command is being sent, and the command, draft and confirm callbacks.
 * @returns The tool's column content.
 */
function GameToolsTool({ ui, state, tool, draft, game, idle, act, setDraft, confirm }: any) {
  const react = ui.react;
  const h = react.createElement;
  const appId = state.appId;
  const toolId = tool.id;
  const busy = state.busy;
  const working = busy?.toolId === toolId;
  const installed = !!tool.installedVersion;
  const actions: string[] = tool.actions ?? [];
  const channel = gameToolsChannel(tool, draft);
  const proxyName = gameToolsProxy(tool, draft);
  const antiCheat: string[] = state.antiCheat ?? [];
  // An add-on shows as installed, else as the user's draft, else as its recommendation.
  const addonOn = (addon) => (installed ? !!addon.installed : (draft.addons?.[addon.id] ?? !!addon.recommended));

  const installPayload = (replaceExisting: boolean, acknowledgeRisk: boolean) => {
    const payload: any = { appId, toolId, replaceExisting, acknowledgeRisk };
    if (proxyName !== tool.proxyName) payload.proxyName = proxyName;
    if (draft.settings && Object.keys(draft.settings).length) payload.settings = draft.settings;
    if (tool.channels?.[0] && channel.id !== tool.channels[0].id) payload.channel = channel.id;
    // Every add-on the page shows goes out as shown, so a recommendation left on is installed too.
    if ((tool.addons ?? []).length) {
      payload.addons = Object.fromEntries(tool.addons.map((addon) => [addon.id, addonOn(addon)]));
    }
    return payload;
  };
  const install = () => {
    if (!antiCheat.length) {
      void act("install", installPayload(false, false));
      return;
    }
    confirm({
      title: `Install ${tool.name} for ${game} anyway?`,
      text: [
        `${antiCheat.join(", ")}${antiCheat.length === 1 ? " was" : " were"} found in this game's folder. Third-party files in a game folder can get an online account banned.`,
        `Only continue if you play this game offline. You can remove ${tool.name} again from this page.`,
      ].join("\n\n"),
      confirmLabel: "Install anyway",
      onConfirm: () => void act("install", installPayload(false, true)),
    });
  };
  const replace = () =>
    confirm({
      title: `Replace the existing ${tool.name} installation?`,
      text: [
        `WSGM backs up the ${tool.name} files that were installed in ${game} outside WSGM, removes them and installs its own copy. Removing it later restores the backed-up files.`,
        antiCheat.length
          ? `${antiCheat.join(", ")} was found in this game's folder. Only continue if you play this game offline.`
          : "",
      ]
        .filter(Boolean)
        .join("\n\n"),
      confirmLabel: "Replace",
      onConfirm: () => void act("install", installPayload(true, antiCheat.length > 0)),
    });
  const update = () => {
    if (!tool.updateChangesConfig) {
      void act("update", { appId, toolId });
      return;
    }
    confirm({
      title: `Update ${tool.name} and replace its settings file?`,
      text: [
        `This version changes the format of ${tool.name}'s settings file. WSGM replaces it in ${game} with the new default and applies the settings on this page again.`,
        `Changes made in ${tool.name}'s own in-game menu are lost.`,
      ].join("\n\n"),
      confirmLabel: "Update",
      onConfirm: () => void act("update", { appId, toolId, replaceConfig: true }),
    });
  };
  const switchChannel = () => {
    const target = channel.id;
    if (!channel.changesConfig) {
      void act("switchChannel", { appId, toolId, channel: target });
      return;
    }
    confirm({
      title: `Switch ${tool.name} to ${channel.label.toLowerCase()} ${channel.latestVersion}?`,
      text: [
        `${channel.label} ${channel.latestVersion} uses a different settings file format. WSGM replaces ${tool.name}'s settings file in ${game} with the new default and applies the settings on this page again.`,
        `Changes made in ${tool.name}'s own in-game menu are lost.`,
      ].join("\n\n"),
      confirmLabel: "Switch",
      onConfirm: () => void act("switchChannel", { appId, toolId, channel: target, replaceConfig: true }),
    });
  };
  const remove = () => {
    const files = tool.files ?? { owned: 0, replaced: 0 };
    confirm({
      title: `Remove ${tool.name} from ${game}?`,
      text: [
        `WSGM deletes the ${gameToolsCount(files.owned, "file")} it added to ${game}, including ${tool.name}'s settings file, and the logs ${tool.name} wrote.`,
        files.replaced > 0
          ? `It restores the ${gameToolsCount(files.replaced, "game file")} it backed up. Files that changed after installation are left in place and named afterwards.`
          : "No game file was replaced, so nothing needs restoring. Files that changed after installation are left in place and named afterwards.",
      ].join("\n\n"),
      confirmLabel: "Remove",
      onConfirm: () => void act("remove", { appId, toolId }),
    });
  };

  // The one primary action, worded as the Overlay words it.
  let primary: any = null;
  const otherChannel = installed && (tool.channels ?? []).find((item) => item.current)?.id !== channel.id;
  const version = channel.latestVersion ? ` ${channel.latestVersion}` : "";
  if (actions.includes("replaceExisting")) {
    primary = { label: "Replace existing installation…", run: replace };
  } else if (tool.status === "notInstalled") {
    const named = tool.channels?.[0] && channel.id !== tool.channels[0].id ? ` ${channel.label.toLowerCase()}` : "";
    primary = { label: `Install ${tool.name}${named}${version}`, run: actions.includes("install") ? install : null };
  } else if (otherChannel) {
    primary = {
      label: `Switch to ${channel.label.toLowerCase()}${version}`,
      run: actions.includes("switchChannel") && channel.latestVersion ? switchChannel : null,
    };
  } else if (actions.includes("update")) {
    const current = (tool.channels ?? []).find((item) => item.current) ?? channel;
    primary = {
      label: `Update to ${current.buildDate ? `the ${gameToolsShortLatest(current)}` : tool.latestVersion}`,
      run: update,
    };
  } else if (tool.status === "modified" && actions.includes("repair")) {
    primary = { label: "Repair", run: () => void act("repair", { appId, toolId }) };
  }

  const latest = channel.checkingLatest
    ? "Checking for the latest release…"
    : channel.latestLabel
      ? `Latest ${channel.latestLabel}`
      : (channel.latestError ?? "");

  // Warnings, conflicts and the reason a tool can't be installed.
  const notes: any[] = [];
  if (tool.incompatibleReason && tool.status === "notInstalled") {
    notes.push(renderSteamUiBanner(ui, { text: tool.incompatibleReason }));
  }
  if (tool.status === "modified") notes.push(renderSteamUiBanner(ui, { text: tool.statusText }));
  const changed = [...(tool.files?.missing ?? []), ...(tool.files?.modified ?? [])];
  if (changed.length) notes.push(h("div", { className: "steam-ui-kit-muted" }, `Missing or changed: ${changed.join(", ")}`));
  for (const conflict of tool.conflicts ?? []) {
    notes.push(
      h("div", { className: "steam-ui-kit-muted" }, `${conflict.kindLabel}: ${conflict.path}. ${conflict.message}`),
    );
  }
  for (const warning of tool.warnings ?? []) notes.push(renderSteamUiBanner(ui, { text: warning }));

  // The value rows. Before installation they are a draft that goes out with Install; afterwards each
  // change is sent on its own, and the row shows what the host published until it answers.
  const editable = idle && !!tool.canEditOptions;
  const applyNow = installed && actions.includes("configure");
  const rows: any[] = [];
  if ((tool.channels ?? []).length > 1 && (tool.showOptions || installed)) {
    rows.push({
      key: "channel",
      kind: "choice",
      label: "Release channel",
      description: channel.description || undefined,
      text: channel.id,
      choices: tool.channels.map((item) => ({ value: item.id, label: item.label })),
      // The channel is a draft for the primary action, so it locks with the commands that would use it.
      disabled: !(idle && (tool.canEditOptions || actions.includes("switchChannel"))),
    });
  }
  if (tool.showOptions) {
    const options = tool.proxyOptions ?? [];
    const hint = options.find((option) => option.name === proxyName)?.hint ?? "";
    const taken = options
      .filter((option) => !option.available && option.name !== proxyName)
      .map((option) => `${option.name}${option.conflict ? ` (${option.conflict.replace(/\.$/u, "")})` : ""}`);
    rows.push({
      key: "proxy",
      kind: "choice",
      label: "Load as",
      description:
        [installed ? "Choosing another name renames the installed file." : hint, taken.length ? `Not offered: ${taken.join(", ")}.` : ""]
          .filter(Boolean)
          .join(" ") || undefined,
      text: proxyName,
      choices: options
        .filter((option) => option.available || option.name === proxyName)
        .map((option) => ({ value: option.name, label: option.name })),
      disabled: !editable,
    });
    for (const setting of tool.settings ?? []) {
      const value = applyNow ? setting.value : (draft.settings?.[setting.id] ?? setting.value);
      const options = setting.options ?? [];
      const description = [setting.description, setting.note].filter(Boolean).join(" ") || undefined;
      const disabled = !(editable && setting.available);
      // A plain Off/On choice is Steam's toggle; any other choice, such as Automatic or Off, stays a dropdown.
      const toggle =
        options.length === 2 &&
        options.some((option) => option.value === "on" && option.label === "On") &&
        options.some((option) => option.value === "off" && option.label === "Off") &&
        (value === "on" || value === "off");
      if (toggle) {
        rows.push({ key: `setting-${setting.id}`, kind: "boolean", label: setting.label, description, checked: value === "on", disabled });
        continue;
      }
      const choices = options.map((option) => ({ value: option.value, label: option.label }));
      // The file holds a value no option names; show it as is instead of claiming a default.
      if (!choices.some((choice) => choice.value === value)) {
        choices.unshift({ value, label: setting.iniValue ? `Custom (${setting.iniValue})` : "Custom" });
      }
      rows.push({ key: `setting-${setting.id}`, kind: "choice", label: setting.label, description, text: value, choices, disabled });
    }
    // An add-on is a toggle: a draft that goes out with Install, or added and removed at once afterwards.
    for (const addon of tool.addons ?? []) {
      rows.push({
        key: `addon-${addon.id}`,
        kind: "boolean",
        label: addon.name,
        description: [addon.description, addon.text, addon.note].filter(Boolean).join(" ") || undefined,
        checked: addonOn(addon),
        disabled: !(idle && addon.canChange),
      });
    }
  }
  const change = (row, value) => {
    if (!idle || value === undefined || value === null) return;
    if (row.key === "channel") {
      if (value !== channel.id) setDraft({ ...draft, channel: value });
      return;
    }
    if (row.key === "proxy") {
      if (value === proxyName) return;
      if (installed) void act("configure", { appId, toolId, proxyName: value });
      else setDraft({ ...draft, proxyName: value });
      return;
    }
    const addon = (tool.addons ?? []).find((entry) => `addon-${entry.id}` === row.key);
    if (addon) {
      const enabled = !!value;
      if (enabled === addonOn(addon)) return;
      if (installed) void act("setAddon", { appId, toolId, addonId: addon.id, enabled });
      else setDraft({ ...draft, addons: { ...(draft.addons ?? {}), [addon.id]: enabled } });
      return;
    }
    const setting = (tool.settings ?? []).find((entry) => `setting-${entry.id}` === row.key);
    if (!setting) return;
    const chosen = row.kind === "boolean" ? (value ? "on" : "off") : value;
    const shown = applyNow ? setting.value : (draft.settings?.[setting.id] ?? setting.value);
    if (chosen === "" || chosen === shown) return;
    if (applyNow) void act("configure", { appId, toolId, settings: { [setting.id]: chosen } });
    else setDraft({ ...draft, settings: { ...(draft.settings ?? {}), [setting.id]: chosen } });
  };

  const secondary: any[] = [];
  if (!working) {
    const checking = !!tool.checkingLatest;
    if (installed || !channel.latestVersion) {
      secondary.push({
        id: "check-updates",
        label: checking ? "Checking…" : "Check for updates",
        disabled: !idle || checking,
        onClick: () => void act("checkUpdates", { appId }),
      });
    }
    if (actions.includes("repair") && tool.status !== "modified") {
      secondary.push({ id: "repair", label: "Repair", disabled: !idle, onClick: () => void act("repair", { appId, toolId }) });
    }
    for (const addon of (tool.addons ?? []).filter((entry) => entry.installed && entry.updateAvailable)) {
      secondary.push({
        id: `addon-update-${addon.id}`,
        label: `Update ${addon.name}`,
        disabled: !idle || !addon.canChange,
        onClick: () => void act("setAddon", { appId, toolId, addonId: addon.id, enabled: true }),
      });
    }
  }
  const removable = !working && actions.includes("remove");
  const homepage = String(tool.homepage ?? "").replace(/^https:\/\//iu, "");
  const about = [tool.license, homepage, tool.notice].filter(Boolean).join(" · ");

  return h(
    ui.focusable,
    { key: `tool-${toolId}`, className: "wsgm-game-tools-tool", "flow-children": "column" },
    renderSteamUiItemHeading(ui, {
      title: tool.name,
      tags: gameToolsTags(tool, channel, working, busy),
      fact: working ? "" : latest,
      summary: gameToolsSummary(tool, working),
    }),
    notes.length ? h("div", { className: "wsgm-game-tools-notes" }, ...notes.map((note, index) => h("div", { key: index }, note))) : null,
    working ? gameToolsProgress(ui, busy, () => void act("cancel", { appId })) : null,
    !working && primary
      ? renderSteamUiActions(
          ui,
          [{ id: "primary", label: primary.label, primary: true, disabled: !idle || !primary.run, onClick: () => primary.run?.() }],
          { compact: true },
        )
      : null,
    rows.length
      ? h(ui.settingsSection, null, ...rows.map((row) => renderSteamSettingRow(ui, row, undefined, change, () => {})))
      : null,
    secondary.length ? renderSteamUiActions(ui, secondary, { compact: true }) : null,
    removable
      ? h(
          "div",
          { className: "wsgm-game-tools-remove" },
          renderSteamUiActions(ui, [{ id: "remove", label: `Remove ${tool.name}…`, disabled: !idle, onClick: remove }], {
            compact: true,
          }),
        )
      : null,
    about ? h("div", { className: "steam-ui-kit-muted" }, about) : null,
  );
}

/**
 * A tool whose game has no folder to work in: its name, why, and the license line.
 * @param props Resolved components and the tool.
 * @returns The tool's column content.
 */
function GameToolsUnavailableTool({ ui, tool }: any) {
  const h = ui.react.createElement;
  const homepage = String(tool.homepage ?? "").replace(/^https:\/\//iu, "");
  const about = [tool.license, homepage, tool.notice].filter(Boolean).join(" · ");
  return h(
    "div",
    { className: "wsgm-game-tools-tool" },
    renderSteamUiItemHeading(ui, { title: tool.name, tags: [{ text: "Unavailable" }], summary: tool.statusText }),
    about ? h("div", { className: "steam-ui-kit-muted" }, about) : null,
  );
}

/**
 * The "This game" panel: the program with Change program…, the upscalers and anti-cheat found in
 * the folder, the GPU, and the folder itself.
 * @param props Resolved components, published state, whether the program may change now and the
 * change callback.
 * @returns The side panel.
 */
function GameToolsGamePanel({ ui, state, canChange, onChange }: any) {
  const runtime = state.runtime;
  const hasFolder = state.availability === "ready" || state.availability === "running";
  const upscalers = gameToolsUpscalers(state);
  const antiCheat: string[] = state.antiCheat ?? [];
  const vendors: string[] = state.gpuVendors ?? [];
  const sections: any[] = hasFolder
    ? [
        {
          key: "program",
          label: "Program",
          value: runtime?.fileName ?? (state.loading ? "Looking…" : "Not chosen"),
          detail: runtime
            ? [runtime.architectureLabel, runtime.confidenceLabel].filter(Boolean).join(" · ")
            : state.rememberedMissing
              ? "The program chosen earlier is gone"
              : state.loading
                ? ""
                : "Several programs could be the game",
          error: !runtime && !state.loading,
          action: { label: runtime ? "Change program…" : "Choose the program…", disabled: !canChange, onClick: onChange },
        },
        upscalers.length
          ? { key: "upscalers", label: "Upscalers in the folder", tags: upscalers.map((text) => ({ text })), rule: true }
          : {
              key: "upscalers",
              label: "Upscalers in the folder",
              value: "None found",
              detail: "The game may still have one built in",
              rule: true,
            },
        { key: "anticheat", label: "Anti-cheat", value: antiCheat.length ? antiCheat.join(", ") : "None found" },
        vendors.length ? { key: "gpu", label: "GPU", value: vendors.map(gameToolsVendor).join(", ") } : null,
      ]
    : [];
  return renderSteamUiSidePanel(ui, {
    key: "game",
    label: "This game",
    sections,
    note: state.installFolder || (state.isShortcut ? "Non-Steam shortcut" : "Install folder not reported."),
  });
}

/**
 * The program chooser: every program in the game folder, best first, with what WSGM read from it,
 * and Browse for one the scan missed.
 * @param props Resolved components, published state, the game's name, whether the game is idle and
 * the choose, browse and back callbacks.
 * @returns The chooser level's content.
 */
function GameToolsChooser({ ui, state, game, idle, onChoose, onBrowse, onBack }: any) {
  const react = ui.react;
  const h = react.createElement;
  const candidates = state.candidates ?? [];
  const several = !state.runtime || state.runtime.confidence === "needsChoice";
  const installed = (state.tools ?? []).find((tool) => tool.installedVersion);
  const banners: any[] = [];
  if (state.rememberedMissing) {
    banners.push(renderSteamUiBanner(ui, { text: "The program you chose earlier is gone, maybe after a game update. Choose it again." }));
  }
  if (installed) {
    banners.push(
      renderSteamUiBanner(ui, {
        text: `${installed.name} is installed beside the current program, so only a program in the same folder can be chosen until you remove it.`,
      }),
    );
  }
  return h(
    react.Fragment,
    null,
    renderSteamUiHeader(ui, {
      page: true,
      title: `Which program does ${game} run?`,
      detail: `Tools are installed beside the program the game starts. ${
        several ? "Several programs in its folder could be it, so pick the game itself, not its launcher. " : "Pick the game itself, not its launcher. "
      }WSGM remembers your choice.`,
    }),
    ...banners.map((banner, index) => h("div", { key: `banner-${index}` }, banner)),
    candidates.length
      ? h(
          ui.focusable,
          { key: "candidates", className: "steam-ui-kit-list", "flow-children": "column" },
          ...candidates.map((candidate) => {
            const path = candidate.relativePath;
            return renderSteamUiSelectRow(ui, {
              key: path,
              title: candidate.fileName,
              selected: !!candidate.chosen,
              status: renderSteamUiTags(react, [
                candidate.chosen ? { text: "In use", tone: "info" } : null,
                { text: candidate.roleLabel || "Program", tone: candidate.role === "game" ? "success" : "neutral" },
              ]),
              detail: [
                [candidate.architectureLabel, gameToolsLocation(path)].filter(Boolean).join(" · "),
                (candidate.reasons ?? []).join(" · "),
              ]
                .filter(Boolean)
                .join("\n"),
              disabled: !idle,
              description: candidate.chosen ? "Back to the game" : "Use this program",
              onClick: () => (candidate.chosen ? onBack() : onChoose(path)),
            });
          }),
        )
      : renderSteamUiEmpty(ui, {
          kind: state.loading ? "loading" : "empty",
          text: state.loading
            ? "Looking for programs in the game folder…"
            : "No programs were found in the game folder. Use Browse to choose it.",
        }),
    state.scanTruncated
      ? renderSteamUiBanner(ui, {
          text: "This folder is too large to list completely, so the game's program may be missing here. Use Browse to choose it.",
        })
      : null,
    h(
      ui.focusable,
      { className: "wsgm-game-tools-browse", "flow-children": "row" },
      renderSteamUiActions(ui, [{ id: "browse", label: "Browse for the program…", disabled: !idle, onClick: onBrowse }], { compact: true }),
      h("span", { className: "steam-ui-kit-muted" }, "Use Browse when the game's program is not listed."),
    ),
    renderSteamUiActions(ui, [{ id: "back", label: "Back to the game", onClick: onBack }], { compact: true }),
  );
}

/**
 * One game's Game Tools page: the hero, the host's messages, each tool in the main column and the
 * "This game" panel, with the program chooser as a level of its own.
 * @param props The frame's context, the AppID from the route and the published state.
 * @returns The page's content.
 */
function GameToolsGame({ context, appId, published }: any) {
  const react = context.react();
  const h = react.createElement;
  const ui = context.ui();
  // Where the user is: the page itself or the program chooser opened by Change program.
  const [level, setLevel] = react.useState("page");
  const [drafts, setDrafts] = react.useState({});
  const [sending, setSending] = react.useState(false);
  const lifetime = react.useRef(null as AbortController | null);
  react.useEffect(() => {
    lifetime.current = new AbortController();
    // Direct navigation and Back into the page both land here; the host publishes this game.
    context.report(null);
    void request(GameToolsPatchId, "open", { appId }).catch(() => undefined);
    return () => lifetime.current?.abort();
  }, []);
  // Drafts (channel, load name, settings) live until the game is left, as the Overlay's do; an
  // installed tool reads its load name and settings from the host, not from a draft.
  const state = published && Number(published.appId) === appId ? published : null;

  const game = gameToolsName(state, appId);
  const hero = (compact: boolean) =>
    renderSteamUiHero(ui, {
      key: "hero",
      eyebrow: "Game Tools",
      title: game,
      detail: compact ? undefined : `Add upscaler mods to ${game}. ${GameToolsPurpose}`,
      images: steamAppHeroImages(appId),
    });
  const root = (...children) =>
    renderSteamUiPane(ui, { key: "game", className: "wsgm-game-tools-root" }, ...children);
  if (!state) {
    return root(
      hero(false),
      h("div", { className: "wsgm-game-tools-body" }, renderSteamUiEmpty(ui, { kind: "loading", text: "Reading this game…" })),
    );
  }

  const busy = state.busy;
  const idle = !busy && !state.loading && !sending;
  /**
   * Sends one command for this game; a synchronous refusal is the frame's banner, and the outcome of
   * admitted background work arrives later as the published notice or error.
   */
  const act: any = async (command: string, payload: any) => {
    if (sending && command !== "cancel") return undefined;
    context.report(null);
    setSending(true);
    try {
      return await request(GameToolsPatchId, command, payload);
    } catch (failure) {
      if (!lifetime.current?.signal.aborted) context.report((failure as any)?.message ?? failure);
      return undefined;
    } finally {
      if (!lifetime.current?.signal.aborted) setSending(false);
    }
  };
  // Every Game Tools question has a safe answer of no, so Cancel takes the controller first.
  const confirm = (props) => {
    if (!showSteamUiConfirm(ui, { ...props, focusCancel: true })) context.report("Steam's confirmation dialog is unavailable.");
  };
  const choose = async (path: string) => {
    const result = await act("chooseRuntime", { appId, path });
    if (result !== undefined && !lifetime.current?.signal.aborted) setLevel("page");
  };
  const browse = async () => {
    const path = await showSteamFilePicker(ui, {
      title: `Choose the program ${game} runs`,
      mode: "file",
      extensions: [".exe"],
      start: state.installFolder || undefined,
      signal: lifetime.current?.signal,
    });
    if (path && !lifetime.current?.signal.aborted) await choose(path);
  };

  const hasFolder = state.availability === "ready" || state.availability === "running";
  const compact = !!busy || (state.tools ?? []).some((tool) => tool.installedVersion);

  if (level === "programs" && hasFolder) {
    return root(
      hero(true),
      renderSteamUiLevel(
        ui,
        { className: "wsgm-game-tools-body", onBack: () => setLevel("page") },
        h(
          "div",
          { className: "wsgm-game-tools-main" },
          h(GameToolsChooser, {
            ui,
            state,
            game,
            idle,
            onChoose: (path) => void choose(path),
            onBrowse: () => void browse(),
            onBack: () => setLevel("page"),
          }),
        ),
      ),
    );
  }

  // The host's messages and what the game folder allows, under the hero.
  const banners: any[] = [];
  if (state.error) banners.push(renderSteamUiBanner(ui, { text: state.error, error: true }));
  else if (state.notice && !busy) banners.push(renderSteamUiBanner(ui, { text: state.notice, success: true }));
  if (state.availability !== "ready" && state.reason) {
    banners.push(renderSteamUiBanner(ui, { title: gameToolsAvailabilityTitle(state.availability), text: state.reason }));
  }
  const scanning = state.loading && (state.tools ?? []).length === 0;
  if (hasFolder && !scanning && (state.antiCheat ?? []).length) {
    banners.push(
      renderSteamUiBanner(ui, {
        title: `${state.antiCheat.join(", ")} found in this game's folder`,
        text: gameToolsAntiCheatMessage(state),
        error: true,
      }),
    );
  }

  const main: any[] = [];
  let progressShown = false;
  if (scanning) {
    main.push(renderSteamUiProgress(ui, { phase: "Scanning the game folder…" }));
    progressShown = true;
  } else {
    for (const tool of state.tools ?? []) {
      if (!hasFolder) {
        main.push(h(GameToolsUnavailableTool, { key: `tool-${tool.id}`, ui, tool }));
        continue;
      }
      progressShown ||= busy?.toolId === tool.id;
      main.push(
        h(GameToolsTool, {
          key: `tool-${tool.id}`,
          ui,
          state,
          tool,
          game,
          idle,
          act,
          confirm,
          draft: drafts[tool.id] ?? {},
          setDraft: (next) => setDrafts({ ...drafts, [tool.id]: next }),
        }),
      );
    }
  }
  // The folder scan, or work for a tool this page doesn't draw.
  if (!progressShown && busy) main.push(gameToolsProgress(ui, busy, () => void act("cancel", { appId })));
  else if (!progressShown && state.loading) main.push(renderSteamUiProgress(ui, { phase: "Scanning the game folder…" }));
  main.push(
    renderSteamUiActions(
      ui,
      [
        { id: "refresh", label: "Scan the folder again", disabled: !idle, onClick: () => void act("refresh", { appId }) },
        { id: "games", label: "All games", onClick: () => navigateSteamRoute(GameToolsRoute, steamInteractionWindow()) },
      ],
      { compact: true },
    ),
  );

  return root(
    hero(compact),
    banners.length
      ? h("div", { className: "wsgm-game-tools-banners" }, ...banners.map((banner, index) => h("div", { key: index }, banner)))
      : null,
    h(
      ui.focusable,
      { className: "wsgm-game-tools-body", "flow-children": "row" },
      h(ui.focusable, { className: "wsgm-game-tools-main", "flow-children": "column" }, ...main),
      h(GameToolsGamePanel, {
        ui,
        state,
        canChange: hasFolder && idle,
        onChange: () => setLevel("programs"),
      }),
    ),
  );
}

/**
 * The game list: games with tools from the host's list, then every game in the Steam library, with
 * search and the All, With tools and Updates filters.
 * @param props The frame's context.
 * @returns The list's content.
 */
function GameToolsList({ context }: any) {
  const react = context.react();
  const h = react.createElement;
  const ui = context.ui();
  react.useSyncExternalStore(gameToolsLibraryStore.subscribe, gameToolsLibraryStore.revision, gameToolsLibraryStore.revision);
  const library = gameToolsLibrary;
  const [filter, setFilter] = react.useState("all");
  const [query, setQuery] = react.useState("");
  const [shown, setShown] = react.useState(GameToolsChunk);
  const [games, setGames] = react.useState(() => readSteamLibraryGames());
  react.useEffect(() => {
    context.report(null);
    void request(GameToolsPatchId, "refreshLibrary", {}).catch(() => undefined);
  }, []);

  const matches = (name: string) => name.toLowerCase().includes(query.trim().toLowerCase());
  const steamNames = new Map<number, any>((games ?? []).map((game) => [game.appId, game]));
  const open = (appId: number) => navigateSteamRoute(`${GameToolsRoute}/${appId}`, steamInteractionWindow());
  const art = (appId: number) =>
    renderSteamUiArt(react, { images: steamAppHeaderImages(appId), lazy: true });

  const setFilterTo = (next: string) => {
    setFilter(next);
    setShown(GameToolsChunk);
  };
  const toolbar = renderSteamUiToolbar(
    ui,
    renderSteamUiTool(
      ui,
      null,
      h(ui.textField, {
        label: "Search games",
        value: query,
        onChange: (event) => {
          setQuery(event?.target?.value ?? "");
          setShown(GameToolsChunk);
        },
      }),
      true,
    ),
    renderSteamUiTool(
      ui,
      "Show",
      renderSteamUiChips(ui, [
        { label: games ? `All ${games.length}` : "All", selected: filter === "all", onClick: () => setFilterTo("all") },
        { label: `With tools ${library?.withTools ?? 0}`, selected: filter === "tools", onClick: () => setFilterTo("tools") },
        { label: `Updates ${library?.updates ?? 0}`, selected: filter === "updates", onClick: () => setFilterTo("updates") },
      ]),
    ),
  );

  const content: any[] = [toolbar];
  if (library?.error) content.push(renderSteamUiBanner(ui, { text: library.error, error: true }));

  // Games with tools, from the host's list.
  const listed = library?.games ?? [];
  if (!library || library.loading || listed.length || filter !== "all") {
    const rows = listed
      .filter((game) => matches(steamNames.get(game.appId)?.name ?? game.name))
      .filter((game) => filter !== "updates" || (game.tools ?? []).some((tool) => tool.status === "updateAvailable"));
    const section: any[] = [renderSteamUiHeader(ui, { title: "With tools" })];
    if (!library || library.loading) {
      section.push(renderSteamUiEmpty(ui, { kind: "loading", text: "Reading the games that have tools…" }));
    } else if (!rows.length) {
      section.push(
        renderSteamUiEmpty(ui, {
          kind: listed.length && query.trim() ? "noMatch" : "empty",
          text: !listed.length
            ? "No game has a tool yet. Choose a game below to add one."
            : filter === "updates" && !query.trim()
              ? "No update is known yet. Open a game and use Check for updates to look again."
              : "No game with tools matches the search.",
        }),
      );
    } else {
      section.push(
        h(
          ui.focusable,
          { key: "with-tools", className: "steam-ui-kit-list", "flow-children": "column" },
          ...rows.map((game) => {
            const tags: any[] = [];
            for (const tool of game.tools ?? []) {
              if (!tool.defaultChannel) tags.push({ text: tool.channelLabel, tone: "info" });
              tags.push(gameToolsSummaryTag(tool));
            }
            return renderSteamUiSelectRow(ui, {
              key: `tools-${game.appId}`,
              title: steamNames.get(game.appId)?.name ?? game.name,
              status: renderSteamUiTags(react, tags),
              detail: (game.tools ?? []).map((tool) => tool.detail).join(" · "),
              media: art(game.appId),
              description: "Open",
              onClick: () => open(game.appId),
            });
          }),
        ),
      );
    }
    content.push(h("div", { key: "with-tools", className: "wsgm-game-tools-section" }, ...section));
  }

  // Every game in the Steam library.
  if (filter === "all") {
    const section: any[] = [renderSteamUiHeader(ui, { title: "All games" })];
    if (!games) {
      section.push(
        renderSteamUiEmpty(ui, {
          kind: "error",
          text: "Steam could not list the games.",
          action: { label: "Try again", onClick: () => setGames(readSteamLibraryGames()) },
        }),
      );
    } else {
      const found = games.filter((game) => matches(game.name));
      const summaries = new Map<number, any>(listed.map((game) => [game.appId, game]));
      if (!found.length) {
        section.push(
          renderSteamUiEmpty(ui, {
            kind: games.length ? "noMatch" : "empty",
            text: games.length ? "No games match the search." : "Your Steam library has no games.",
          }),
        );
      } else {
        section.push(
          h(
            ui.focusable,
            { key: "all-games", className: "steam-ui-kit-list columns", "flow-children": "grid" },
            ...found.slice(0, shown).map((game) => {
              const tools = summaries.get(game.appId)?.tools ?? [];
              return renderSteamUiSelectRow(ui, {
                key: `game-${game.appId}`,
                title: game.name,
                detail: tools.length
                  ? tools.map((tool) => `${tool.toolName} ${tool.version}`).join(", ")
                  : game.shortcut
                    ? "Non-Steam game · no tools"
                    : "No tools",
                media: art(game.appId),
                description: "Open",
                onClick: () => open(game.appId),
              });
            }),
          ),
        );
        if (found.length > shown) {
          section.push(
            renderSteamUiMore(ui, {
              label: `Load More (${shown} of ${found.length})`,
              onClick: () => setShown(shown + GameToolsChunk),
            }),
          );
        }
      }
    }
    content.push(h("div", { key: "all-games", className: "wsgm-game-tools-section" }, ...section));
  }

  return renderSteamUiPane(
    ui,
    { key: "list", className: "wsgm-game-tools-root" },
    renderSteamUiHero(ui, {
      key: "hero",
      title: "Game Tools",
      detail: `Add upscaler mods such as OptiScaler to a game's own folder. ${GameToolsPurpose}`,
    }),
    h("div", { className: "wsgm-game-tools-body" }, h("div", { className: "wsgm-game-tools-main" }, ...content)),
  );
}

/**
 * Renders the Game Tools page: the game list without an AppID, one game's page with one.
 * @param context Registered page accessors for Steam components, latest state and publication refusal.
 * @param page The routed page descriptor carrying the optional AppID parameter.
 * @returns The page React tree, including loading, unavailable and refusal states.
 */
function GameToolsPage({ context, page }: any) {
  const h = context.react().createElement;
  const ui = context.ui();
  const raw = page?.params?.appid;
  if (raw === undefined || raw === null || raw === "") return h(GameToolsList, { key: "list", context });
  const appId = Number(raw);
  if (!Number.isInteger(appId) || appId <= 0 || appId > 0xffffffff) {
    return renderSteamUiEmpty(ui, { kind: "error", text: "No game was supplied." });
  }
  // Keyed by the game, so another game's page starts without this one's drafts or level.
  return h(GameToolsGame, { key: `game-${appId}`, context, appId, published: context.state() });
}

const gameToolsPage = registerSteamPage({
  template: "game-tools",
  gate: "gameTools",
  patchId: GameToolsPatchId,
  title: "Game Tools",
  id: "wsgm-game-tools",
  style: gameToolsStyles,
  loading: "Reading this game…",
  // The page asks for its game itself and draws until the host publishes it.
  waitForState: false,
  // The host's error and notice belong to the game they were published for, so the page draws them
  // under its hero; the frame keeps only the page's own refusals.
  banner: false,
  dismiss: null,
  // The game list publication is followed while the gate holds.
  prepare: () => {
    try {
      gameToolsLibraryEnd = subscribe(GameToolsLibraryId, (next: any) => {
        gameToolsLibrary = next ?? null;
        gameToolsLibraryStore.changed();
      });
    } catch {
      gameToolsLibraryEnd = null;
    }
    return null;
  },
  release: () => {
    endSubscription(gameToolsLibraryEnd);
    gameToolsLibraryEnd = null;
    gameToolsLibrary = null;
    gameToolsLibraryStore.changed();
  },
  status: () => ({
    appId: gameToolsPage.state()?.appId ?? 0,
    libraryGames: gameToolsLibrary?.games?.length ?? 0,
  }),
  Page: GameToolsPage,
});
