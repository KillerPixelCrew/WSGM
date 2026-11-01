// Emulator Manager presentation: five tabs (Installed, Get emulators, RetroArch cores, System
// defaults, BIOS and firmware) and a detail level per emulator. Packages, BIOS files, preferences and
// shortcuts belong to EmulatorService; this page draws its one page projection (`wsgm.emulators`)
// and progress (`wsgm.emulators.progress`) and sends its exact commands. The Overlay's
// EmulatorManagerView is the same task over the same state, actions and wording.
const EmulatorTabs = [
  { id: "installed", title: "Installed" },
  { id: "catalog", title: "Get emulators" },
  { id: "cores", title: "RetroArch cores" },
  { id: "defaults", title: "System defaults" },
  { id: "bios", title: "BIOS and firmware" },
];
const EmulatorPurpose =
  "Install, update and set up the emulators your ROM sources use. WSGM keeps each one portable and adds it to Steam.";
const EmulatorInstallSteps = ["Find release", "Download", "Extract", "Check startup"];
const EmulatorStepOf: Record<string, number> = { find: 0, download: 1, extract: 2, check: 3 };
// The operations whose steps, meter and Stop show inside the emulator they work on.
const EmulatorItemWork = ["install", "update", "repair", "updateAll"];
// Ready cores each list adds per Load More; RetroArch publishes a few hundred.
const EmulatorCoreChunk = 40;

// Layout glue under the page's root: where the kit's elements go, not how they look.
const emulatorStyles = `
#wsgm-emulators { overflow-wrap: anywhere; }
#wsgm-emulators .wsgm-emu-root { display: flex; flex-direction: column; width: 100%; max-width: 1360px; min-width: 0; box-sizing: border-box; margin-inline: auto; padding: 16px 40px 72px; gap: 16px; }
#wsgm-emulators .wsgm-emu-head { display: flex; align-items: flex-end; justify-content: space-between; flex-wrap: wrap; gap: 12px 24px; min-width: 0; }
#wsgm-emulators .wsgm-emu-head > :first-child { flex: 1 1 360px; min-width: 0; }
#wsgm-emulators .wsgm-emu-level { display: flex; flex-direction: column; min-width: 0; padding-bottom: 72px; }
#wsgm-emulators .wsgm-emu-banners { display: flex; flex-direction: column; gap: 8px; min-width: 0; padding: 16px 40px 0; }
#wsgm-emulators .wsgm-emu-body { display: flex; align-items: flex-start; gap: 28px; min-width: 0; padding: 20px 40px 0; }
#wsgm-emulators .wsgm-emu-main { flex: 1 1 auto; min-width: 0; display: flex; flex-direction: column; gap: 16px; }
#wsgm-emulators .wsgm-emu-section { display: flex; flex-direction: column; gap: 8px; min-width: 0; }
#wsgm-emulators .wsgm-emu-remove { padding-top: 14px; border-top: 1px solid hsla(0, 0%, 100%, 0.1); }
#wsgm-emulators .wsgm-emu-choices { display: flex; flex-wrap: wrap; gap: 8px 12px; min-width: 0; }
#wsgm-emulators .wsgm-emu-choices > * { width: 240px; max-width: 100%; }
#wsgm-emulators .wsgm-emu-row-actions { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: 8px; min-width: 0; }
#wsgm-emulators .wsgm-emu-row-actions .DialogButton { width: auto; min-width: 0; }
#wsgm-emulators .wsgm-emu-toolbar { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 8px 16px; min-width: 0; }
@media (max-width: 760px) {
  #wsgm-emulators .wsgm-emu-root { padding: 12px 20px 72px; }
  #wsgm-emulators .wsgm-emu-body { flex-direction: column; align-items: stretch; padding: 16px 20px 0; }
  #wsgm-emulators .wsgm-emu-banners { padding: 12px 20px 0; }
}
`;

// What the page keeps across a tab change, which remounts a tab's content: the searches, the family
// filter, the RetroArch installation the core list shows and how far it is paged, a channel picked
// per catalogue emulator and which folded groups are open. The Overlay keeps the same for its next
// visit. A link from one tab to another sets what the other one opens with.
const emulatorMemory = {
  catalogSearch: "",
  family: "all",
  coreInstallation: "",
  coreSearch: "",
  coresShown: EmulatorCoreChunk,
  channels: {} as Record<string, string>,
  otherSystems: false,
  otherBios: false,
  reset() {
    this.catalogSearch = "";
    this.family = "all";
    this.coreInstallation = "";
    this.coreSearch = "";
    this.coresShown = EmulatorCoreChunk;
    this.channels = {};
    this.otherSystems = false;
    this.otherBios = false;
  },
};

/** Whether the text is found in any of the values, ignoring case, as the Overlay searches. */
const emulatorMatches = (search: string, ...values: string[]) =>
  !search || values.some((value) => String(value ?? "").toLowerCase().includes(search.toLowerCase()));

/** "1 core", "2 cores". */
const emulatorCount = (count: number, noun: string, plural = `${noun}s`) =>
  `${count} ${count === 1 ? noun : plural}`;

/** "A", "A and B", "A, B and C". */
const emulatorJoinAnd = (names: string[]) =>
  names.length < 2 ? (names[0] ?? "") : `${names.slice(0, -1).join(", ")} and ${names.at(-1)}`;

/** A status chip of the projection as a tag, in the tone the Overlay's chip has. */
const emulatorChipTag = (chip: any) => ({
  text: chip.label,
  tone: ({ action: "update", warn: "update", good: "success", bad: "danger" } as any)[chip.tone] ?? "neutral",
});

/** The running operation's byte count: "12 MB of 31 MB", "12 MB" or nothing. */
const emulatorAmount = (progress: any) =>
  progress.bytes > 0
    ? progress.total > 0
      ? `${wsgmFormatBytes(progress.bytes)} of ${wsgmFormatBytes(progress.total)}`
      : wsgmFormatBytes(progress.bytes)
    : "";

/**
 * The manager's running work through the kit's progress, with Stop. Inside an emulator's own detail
 * it lists the install transaction's four steps and says what stopping keeps, as the Overlay's work
 * panel does; elsewhere it is the page's one progress line.
 * @param ui Resolved components.
 * @param progress The progress publication.
 * @param inline Whether it is drawn inside the emulator it works on.
 * @returns The progress block.
 */
const emulatorProgressBlock = (ui: any, progress: any, inline: boolean) => {
  const installing = progress.operation === "install";
  return renderSteamUiProgress(ui, {
    phase: progress.status || "Working…",
    done: progress.total > 0 ? progress.bytes : null,
    total: progress.total > 0 ? progress.total : null,
    count: emulatorAmount(progress) || null,
    steps: inline ? EmulatorInstallSteps : null,
    step: inline ? (EmulatorStepOf[progress.phase] ?? -1) : null,
    note: inline
      ? installing
        ? "Stopping leaves nothing installed. The emulator is added only after its startup check passes, and leaving this page doesn't stop it."
        : "Stopping keeps the active version. The new one replaces it only after its startup check passes, and leaving this page doesn't stop it."
      : installing
        ? "Install nothing"
        : "Keep the active installation",
    onCancel: () => void emulatorAct("cancel", {}),
    cancelLabel: "Stop operation",
  });
};

/** An emulator's icon: its Steam shortcut's icon, or the controller glyph while it has none. */
const emulatorIcon = (react: any, appId: number, size: number, label?: string) =>
  renderSteamUiIconTile(react, {
    images: appId > 0 ? steamAppIconImages(appId) : [],
    glyph: "controller",
    size,
    label,
  });

/** A system's name from the importer's systems, or its id when the importer does not list it. */
const emulatorSystemName = (state: any, id: string) =>
  (state?.romSystems ?? []).find((item: any) => item.id === id)?.name ?? id;

/** The offer for an installation's channel and architecture, as the Overlay reads it. */
const emulatorOffer = (state: any, installation: any) =>
  (state?.emulators?.offers ?? []).find(
    (offer: any) =>
      offer.definitionId === installation.definitionId &&
      offer.channel === installation.channel &&
      offer.architecture === installation.architecture,
  ) ?? null;

/** The folder an installation's program is in. */
const emulatorFolder = (installation: any) => {
  const path = String(installation.executablePath ?? "");
  const slash = Math.max(path.lastIndexOf("\\"), path.lastIndexOf("/"));
  return slash > 0 ? path.slice(0, slash) : installation.root || "";
};

/**
 * What removing an installation deletes and keeps, as the Overlay's confirmation words it: the
 * program files, the data, the emulator's own shortcut and the imported games.
 */
const emulatorRemoveText = (installation: any, dependency: any) => {
  const paragraphs = installation.managed
    ? [
        `WSGM deletes ${installation.name}'s program files from ${installation.root}.`,
        `Its settings, saves and the BIOS or firmware you supplied stay in ${installation.dataPath}, and so do your ROMs.`,
      ]
    : [`WSGM stops using your installation at ${installation.executablePath}. Its files and data stay as they are.`];
  paragraphs.push(
    (
      (installation.steamShortcutEnabled ? "Its own shortcut is removed from Steam. " : "") +
      (dependency?.summary ?? "") +
      " Imported games stay in Steam and need another emulator before they start."
    ).trim(),
  );
  return paragraphs.join("\n\n");
};

/**
 * One tab of the Emulators page and the levels opened from it: an installed emulator, a catalogue
 * emulator and a RetroArch core. B leaves a level for the one it was opened from.
 * @param props The frame's context, the host's state and the active tab.
 * @returns The tab's content.
 */
function EmulatorTab({ context, state, tab }: any) {
  const ui = context.ui();
  const react = ui.react;
  const h = react.createElement;
  const progress = useEmulatorProgress();
  // The levels opened over the tab, each with the control Back returns focus to.
  const [stack, setStack] = react.useState([] as { kind: string; id: string; focus: string }[]);
  const [returnTo, setReturnTo] = react.useState("");
  const [, setRedraw] = react.useState(0);
  const lifetime = react.useRef(null as AbortController | null);
  react.useEffect(() => {
    lifetime.current = new AbortController();
    return () => lifetime.current?.abort();
  }, []);

  const manager = state?.emulators ?? {};
  const definitions: any[] = manager.definitions ?? [];
  const installations: any[] = manager.installations ?? [];
  const listed: any[] = state?.installed ?? [];
  const busy = !!progress.busy;
  const level = stack[stack.length - 1] ?? { kind: "home", id: "", focus: "" };
  const redraw = () => setRedraw((value: number) => value + 1);
  const act = (command: string, payload: any = {}) => void emulatorAct(command, payload);
  const open = (kind: string, id: string, focus: string) => {
    setReturnTo("");
    setStack([...stack, { kind, id, focus }]);
  };
  const back = () => {
    if (!stack.length) return;
    setReturnTo(stack[stack.length - 1].focus);
    setStack(stack.slice(0, -1));
  };
  // A link to another tab: the frame remounts that tab's content, which opens on its own list.
  const toTab = (id: string) => context.setTab(id);
  const toBios = () => toTab("bios");
  const pick = (title: string, mode: "file" | "folder", extensions: readonly string[], chosen: (path: string) => void) =>
    void showSteamFilePicker(ui, { title, mode, extensions: [...extensions], signal: lifetime.current?.signal }).then(
      (path: string | null) => {
        if (path && !lifetime.current?.signal.aborted) chosen(path);
      },
    );
  // A row of commands, or nothing when none applies, so an empty row is no stop for the controller.
  const actions = (list: any[]) => {
    const shown = list.filter(Boolean);
    return shown.length ? renderSteamUiActions(ui, shown, { compact: true }) : null;
  };
  // A supporting line; a div, since the page frame styles every paragraph as body text.
  const muted = (text: string, key?: string) => h("div", { key, className: "steam-ui-kit-muted" }, text);
  const section = (key: string, title: string, detail: string, ...children: any[]) =>
    h(
      ui.focusable,
      { key, className: "wsgm-emu-section", "flow-children": "column" },
      renderSteamUiHeader(ui, { title, detail: detail || undefined }),
      ...children,
    );
  // A native field row: its name and description, an optional state mark, and the controls at its end.
  const field = (key: string, name: string, description: string, value: any, icon?: any, indent = false) =>
    h(ui.valueField, {
      key,
      name,
      description: description || undefined,
      icon,
      indentLevel: indent ? 1 : undefined,
      focusable: false,
      value: value ?? null,
    });
  const rowActions = (list: any[]) =>
    h(
      ui.focusable,
      { className: "wsgm-emu-row-actions", "flow-children": "row" },
      ...list.filter(Boolean).map((action) =>
        h(
          action.primary ? ui.dialogButtonPrimary : ui.dialogButton,
          {
            key: action.id,
            disabled: !!action.disabled,
            "aria-disabled": !!action.disabled,
            "aria-label": action.name,
            onClick: () => {
              if (!action.disabled) action.onClick();
            },
          },
          action.label,
        ),
      ),
    );

  // ---- Levels ----

  const detail = (key: string, hero: any, banners: any[], main: any[], side: any = null) =>
    react.cloneElement(
      renderSteamUiLevel(
        ui,
        { className: "wsgm-emu-level", onBack: back },
        hero,
        banners.length ? h("div", { className: "wsgm-emu-banners" }, ...banners) : null,
        h(
          ui.focusable,
          { className: "wsgm-emu-body", "flow-children": "row" },
          h(ui.focusable, { className: "wsgm-emu-main", "flow-children": "column" }, ...main),
          side,
        ),
      ),
      { key },
    );
  const missing = (text: string) =>
    react.cloneElement(
      renderSteamUiLevel(
        ui,
        { className: "wsgm-emu-level", onBack: back },
        h(
          "div",
          { className: "wsgm-emu-banners" },
          renderSteamUiEmpty(ui, { kind: "unavailable", text, action: { label: "Back", onClick: back } }),
        ),
      ),
      { key: `${tab}:${level.kind}:${level.id}:gone` },
    );

  // An installed emulator: its update, the setup checklist, In Steam, the supporting actions and
  // Remove set apart, with the installation beside them.
  const installationLevel = (id: string) => {
    const installation = installations.find((item) => item.id === id);
    const item = listed.find((entry) => entry.id === id);
    if (!installation || !item) return missing("That emulator is no longer installed.");
    const offer = emulatorOffer(state, installation);
    const newer = installation.managed && offer && !offer.error && offer.releaseId && offer.releaseId !== installation.releaseId;
    const skipped = newer && offer.releaseId === installation.ignoredReleaseId;
    const working = busy && progress.targetId === id && EmulatorItemWork.includes(progress.operation);
    const dependency = (state.dependencies ?? []).find((entry: any) => entry.installationId === id);
    const notes = offer?.notesUrl
      ? {
          id: "notes",
          label: "Release notes",
          onClick: () =>
            act("openEmulatorReleaseNotes", {
              definitionId: offer.definitionId,
              channel: offer.channel,
              architecture: offer.architecture,
            }),
        }
      : null;
    const systems = installation.cores?.length
      ? [item.summary]
      : (installation.systems ?? []).slice(0, 3).map((system: string) => emulatorSystemName(state, system));
    const hero = renderSteamUiHero(ui, {
      key: "hero",
      eyebrow: "Emulators",
      title: installation.name,
      icon: emulatorIcon(react, item.appId ?? 0, 84, installation.name),
      tags: [
        ...systems.map((text: string) => ({ text })),
        newer ? { text: skipped ? `${offer.version} skipped` : `${offer.version} available`, tone: "update" } : null,
      ],
      detail: [
        item.versionLine,
        installation.managed ? "managed by WSGM" : "WSGM doesn't update it",
        emulatorFolder(installation),
      ]
        .filter(Boolean)
        .join(" · "),
    });
    const banners = offer?.error
      ? [renderSteamUiBanner(ui, { key: "offer", text: `Could not check for updates: ${offer.error}`, error: true })]
      : [];

    const main: any[] = [];
    if (working) {
      main.push(emulatorProgressBlock(ui, progress, true));
      if (notes) main.push(actions([notes]));
    } else {
      main.push(
        renderSteamUiStatus(ui, [progress.status && !busy ? { key: "progress", text: progress.status } : null]),
        busy ? emulatorProgressBlock(ui, progress, false) : null,
        actions([
          newer
            ? {
                id: "update",
                label: `Update to ${offer.version}`,
                primary: true,
                disabled: busy,
                onClick: () => act("updateEmulator", { installationId: id }),
              }
            : null,
          notes,
          newer && !skipped
            ? {
                id: "skip",
                label: "Skip version",
                disabled: busy,
                onClick: () => act("ignoreEmulatorVersion", { installationId: id, releaseId: offer.releaseId }),
              }
            : null,
        ]),
      );
    }

    // The setup checklist: each prerequisite with Choose file, the runtimes, and Recheck.
    const setup: any[] = item.setup ?? [];
    const configure = (kind: string, path: string) =>
      act("configureEmulatorPrerequisite", { installationId: id, path, kind });
    const setupRows = setup.map((entry: any) => {
      let value: any = null;
      if (entry.kind === "runtime") {
        value = entry.ready
          ? null
          : rowActions([
              {
                id: "runtime",
                label: "Install runtimes",
                disabled: busy,
                onClick: () => act("installEmulatorRuntimes", { definitionId: installation.definitionId }),
              },
            ]);
      } else {
        value = rowActions([
          {
            id: "file",
            label: entry.ready ? "Replace…" : "Choose file…",
            name: `${entry.ready ? "Replace" : "Choose"} ${entry.name}`,
            disabled: busy,
            onClick: () => pick(`Choose ${entry.name}`, "file", entry.extensions ?? [], (path) => configure(entry.kind, path)),
          },
          entry.allowDirectory
            ? {
                id: "folder",
                label: "Choose folder…",
                name: `Choose a folder for ${entry.name}`,
                disabled: busy,
                onClick: () => pick(`Choose a folder for ${entry.name}`, "folder", [], (path) => configure(entry.kind, path)),
              }
            : null,
        ]);
      }
      const mark = entry.ready ? "ready" : entry.required ? "missing" : "optional";
      return field(
        `setup:${entry.kind}`,
        entry.name + (entry.required ? "" : " (optional)"),
        entry.detail,
        value,
        renderSteamUiStateMark(react, { state: mark, label: entry.ready ? "Ready" : entry.required ? "Missing" : "Optional" }),
      );
    });
    const firmware = (installation.dataPolicy?.prerequisites ?? []).some((rule: any) =>
      ["bios", "firmware", "keys", "system"].includes(rule.kind),
    );
    main.push(
      section(
        "setup",
        "Setup",
        item.setupLabel ?? "",
        setupRows.length
          ? h(ui.settingsSection, null, ...setupRows)
          : muted(
              (installation.missingRequirements ?? []).length
                ? installation.missingRequirements.join(" ")
                : "Nothing to set up. Its games start once a ROM source uses it.",
            ),
        muted("Finished the emulator's own first-run setup? Recheck reads its files again."),
        actions([
          { id: "recheck", label: "Recheck", disabled: busy, onClick: () => act("refreshEmulators") },
          firmware ? { id: "bios", label: "BIOS and firmware", onClick: toBios } : null,
        ]),
      ),
    );

    // The emulator's own Steam shortcut, its launch mode and what in the library uses it.
    const launchMode = installation.steamShortcutLaunchMode ?? 2;
    const shortcut = installation.steamShortcutEnabled !== false;
    main.push(
      section(
        "steam",
        "In Steam",
        "",
        h(
          ui.settingsSection,
          null,
          h(ui.toggleField, {
            key: "shortcut",
            label: `Show ${installation.name} in Steam`,
            description: "Adds the emulator itself as a non-Steam game",
            controlled: true,
            checked: shortcut,
            disabled: busy,
            onChange: (value: boolean) =>
              !!value !== shortcut &&
              act("setEmulatorShortcut", { installationId: id, enabled: !!value, launchMode }),
          }),
          h(ui.dropdown, {
            key: "mode",
            label: "Launch mode",
            description: "With Give the game the controller, the emulator reads the controller directly.",
            rgOptions: (state.launchModes ?? []).map((mode: any) => ({ data: mode.value, label: mode.label })),
            selectedOption: launchMode,
            disabled: busy,
            onChange: (option: any) =>
              option &&
              option.data !== launchMode &&
              act("setEmulatorShortcut", { installationId: id, enabled: shortcut, launchMode: option.data }),
          }),
        ),
        muted(dependency?.summary ?? "Not used by a ROM source yet."),
        renderSteamUiStatus(ui, [state.shortcutStatus ? { key: "shortcut", text: state.shortcutStatus } : null]),
      ),
    );

    // Repair and the core list, then Remove kept apart.
    main.push(
      actions([
        installation.managed
          ? { id: "repair", label: "Repair installation", disabled: busy, onClick: () => act("repairEmulator", { installationId: id }) }
          : null,
        installation.cores?.length
          ? {
              id: "cores",
              label: "RetroArch cores",
              onClick: () => {
                emulatorMemory.coreInstallation = id;
                toTab("cores");
              },
            }
          : null,
      ]),
      h(
        "div",
        { key: "remove", className: "wsgm-emu-remove" },
        actions([
          {
            id: "remove",
            label: `${installation.managed ? "Remove" : "Forget"} ${installation.name}…`,
            disabled: busy,
            onClick: () => {
              const shown = showSteamUiConfirm(ui, {
                title: `${installation.managed ? "Remove" : "Forget"} ${installation.name}?`,
                text: emulatorRemoveText(installation, dependency),
                confirmLabel: installation.managed ? "Remove" : "Forget",
                focusCancel: true,
                onConfirm: () => {
                  context.report(null);
                  void emulatorPage.send("removeEmulator", { installationId: id }).then(
                    () => {
                      // The emulator is gone: return to the list it was opened from.
                      if (!lifetime.current?.signal.aborted) back();
                    },
                    (failure: any) => context.report(failure),
                  );
                },
              });
              if (!shown) context.report("Steam's confirmation dialog is unavailable.");
            },
          },
        ]),
      ),
    );

    const side = renderSteamUiSidePanel(ui, {
      key: "installation",
      label: "Installation",
      sections: [
        {
          key: "owner",
          value: installation.managed ? "Managed by WSGM" : "Your own installation",
          detail: dependency?.summary ?? "Not used by a ROM source yet.",
        },
        { key: "program", label: "Program", detail: emulatorFolder(installation), rule: true },
        installation.dataPath ? { key: "data", label: "Settings and saves", detail: installation.dataPath } : null,
      ],
    });
    return detail(`${tab}:installation:${id}`, hero, banners, main, side);
  };

  // A catalogue emulator: its release, channel, what it needs before play, and Use my installation.
  const definitionLevel = (id: string) => {
    const definition = definitions.find((item) => item.id === id);
    if (!definition) return missing("That emulator is no longer listed.");
    const channels: string[] = definition.channels ?? [];
    const draft = emulatorMemory.channels[id];
    const channel = channels.includes(draft) ? draft : (channels[0] ?? "");
    const offer = (manager.offers ?? []).find((item: any) => item.definitionId === id && item.channel === channel) ?? null;
    const working = busy && progress.targetId === id && EmulatorItemWork.includes(progress.operation);
    const hero = renderSteamUiHero(ui, {
      key: "hero",
      eyebrow: "Get emulators",
      title: definition.name,
      icon: emulatorIcon(react, 0, 84, definition.name),
      tags: (definition.systems ?? []).slice(0, 4).map((system: string) => ({ text: emulatorSystemName(state, system) })),
      detail: !offer
        ? "Check for updates to read the latest release."
        : offer.error
          ? offer.error
          : `Latest ${offer.version}${channels.length > 1 ? ` (${channel})` : ""} · from ${definition.source}`,
    });
    const main: any[] = [];
    const notes = offer?.notesUrl
      ? {
          id: "notes",
          label: "Release notes",
          onClick: () =>
            act("openEmulatorReleaseNotes", {
              definitionId: offer.definitionId,
              channel: offer.channel,
              architecture: offer.architecture,
            }),
        }
      : null;
    if (working) main.push(emulatorProgressBlock(ui, progress, true));
    else {
      main.push(
        renderSteamUiStatus(ui, [progress.status && !busy ? { key: "progress", text: progress.status } : null]),
        busy ? emulatorProgressBlock(ui, progress, false) : null,
      );
    }
    main.push(
      actions([
        working
          ? null
          : {
              id: "install",
              label: `Install ${definition.name}${channel !== channels[0] ? ` ${channel}` : ""}`,
              primary: true,
              disabled: busy || !channel,
              onClick: () => act("installEmulator", { definitionId: id, channel }),
            },
        notes,
      ]),
    );
    if (channels.length > 1) {
      main.push(
        h(
          ui.settingsSection,
          { key: "channel" },
          h(ui.dropdown, {
            label: "Release channel",
            description: "Install follows this channel's releases; settings and saves are shared.",
            rgOptions: channels.map((value) => ({ data: value, label: value })),
            selectedOption: channel,
            disabled: busy || working,
            onChange: (option: any) => {
              if (!option) return;
              emulatorMemory.channels[id] = option.data;
              redraw();
            },
          }),
        ),
      );
    }

    // What it needs besides its program, from its reviewed prerequisites and runtimes.
    const prerequisites: any[] = definition.prerequisites ?? [];
    const runtimes = !!definition.dataPolicy?.requiresVisualCpp || definition.id === "model2";
    const needs = [
      ...prerequisites.map((rule) =>
        field(
          `requires:${rule.kind}`,
          rule.name,
          rule.description,
          renderSteamUiTag(react, { text: rule.required ? "Required" : "Optional", tone: rule.required ? "update" : "neutral" }),
        ),
      ),
      runtimes
        ? field(
            "requires:runtime",
            "Microsoft runtimes",
            "Installs verified Microsoft components; Windows may ask for permission.",
            rowActions([
              {
                id: "runtimes",
                label: "Install required runtimes",
                disabled: busy,
                onClick: () => act("installEmulatorRuntimes", { definitionId: id }),
              },
            ]),
          )
        : null,
      prerequisites.length
        ? field(
            "requires:bios",
            "Add these before or after installing",
            "WSGM links your BIOS folder into the emulator when it installs.",
            rowActions([{ id: "bios", label: "BIOS and firmware", onClick: toBios }]),
          )
        : null,
    ].filter(Boolean);
    main.push(
      section(
        "requires",
        "Before you play",
        "",
        needs.length
          ? h(ui.settingsSection, null, ...needs)
          : muted("Nothing else is needed. Its games start once a ROM source uses it."),
      ),
      section(
        "external",
        "Already installed it yourself?",
        "",
        h(
          ui.settingsSection,
          null,
          field(
            "external",
            "Use my installation",
            "Choose its program file. WSGM uses it as it is and doesn't move, update or change its files.",
            rowActions([
              {
                id: "external",
                label: "Use my installation…",
                disabled: busy,
                onClick: () =>
                  pick(`Choose ${definition.name}'s program`, "file", [".exe"], (path) =>
                    act("useExternalEmulator", { definitionId: id, path }),
                  ),
              },
            ]),
          ),
        ),
      ),
    );
    const copies = installations.filter((item) => item.definitionId === id);
    if (copies.length) {
      main.push(
        h(
          ui.focusable,
          { key: "copies", className: "steam-ui-kit-list", "flow-children": "column" },
          ...copies.map((copy) =>
            renderSteamUiSelectRow(ui, {
              key: copy.id,
              title: `${copy.name} ${copy.version}`.trim(),
              detail: `${copy.channel} · ${copy.managed ? "managed by WSGM" : "your own installation"}`,
              autoFocus: returnTo === `copy:${copy.id}`,
              onClick: () => open("installation", copy.id, `copy:${copy.id}`),
            }),
          ),
        ),
      );
    }
    return detail(`${tab}:definition:${id}`, hero, [], main);
  };

  // One core's fixed facts: its file, file types, system mapping and declared files.
  const coreLevel = (key: string) => {
    const [installationId, coreId] = key.split("\n");
    const core = installations.find((item) => item.id === installationId)?.cores?.find((item: any) => item.id === coreId);
    if (!core) return missing("That core is no longer installed.");
    const systems = (core.systems ?? []).map((system: string) => emulatorSystemName(state, system));
    return react.cloneElement(
      renderSteamUiLevel(
        ui,
        { className: "wsgm-emu-root", onBack: back },
        h(
          ui.focusable,
          { className: "wsgm-emu-section", "flow-children": "column" },
          renderSteamUiHeader(ui, { page: true, title: core.name, detail: core.path }),
          h("p", null, `File types: ${(core.extensions ?? []).length ? core.extensions.join(", ") : "not declared"}`),
          h(
            "p",
            null,
            core.metadataMissing
              ? "Upstream core information is missing. Choose its system manually on the ROM source."
              : `Systems: ${systems.join(", ")}`,
          ),
          ...(core.requiredFiles ?? []).map((file: string) => h("p", { key: file }, `Required: ${file}`)),
          actions([
            { id: "back", label: "Back to cores", onClick: back },
            { id: "bios", label: "BIOS and firmware", onClick: toBios },
          ]),
        ),
      ),
      { key: `${tab}:core:${key}` },
    );
  };

  if (level.kind === "installation") return installationLevel(level.id);
  if (level.kind === "definition") return definitionLevel(level.id);
  if (level.kind === "core") return coreLevel(level.id);

  // ---- The tab's own list ----

  const view: any[] = [];

  if (tab === "installed") {
    const updateAll = state.updateAll;
    const updates: any[] = state.updates ?? [];
    if (updateAll?.running) {
      view.push(
        renderSteamUiBanner(ui, {
          key: "update-all",
          text: `Updating ${Math.min(updateAll.done + 1, updateAll.total)} of ${updateAll.total}: ${updateAll.current}`,
        }),
      );
    } else {
      if (updateAll?.outcome) {
        const failed = (updateAll.failed ?? []).length > 0;
        view.push(
          renderSteamUiBanner(ui, {
            key: "update-all",
            text: updateAll.outcome,
            error: failed,
            success: !failed && !updateAll.cancelled,
          }),
        );
      }
      if (updates.length) {
        view.push(
          renderSteamUiBanner(ui, {
            key: "updates",
            text: state.updatesSummary,
            actions: [
              {
                id: "update-all",
                label: updates.length === 1 ? `Update ${updates[0].name}` : updates.length === 2 ? "Update both" : "Update all",
                primary: true,
                disabled: busy,
                onClick: () => act("updateAllEmulators"),
              },
            ],
          }),
        );
      }
    }
    if (!listed.length) {
      view.push(
        manager.initialized
          ? renderSteamUiEmpty(ui, {
              kind: "empty",
              title: "No emulators installed",
              text: "Get one from the catalogue, or open one there and choose Use my installation for a copy you already have.",
              action: { label: "Get emulators", onClick: () => toTab("catalog") },
            })
          : renderSteamUiEmpty(ui, { kind: "loading", text: "Reading the installed emulators…" }),
      );
    } else {
      view.push(
        renderSteamUiGrid(
          ui,
          listed.map((item) =>
            renderSteamUiCard(ui, {
              key: item.id,
              icon: emulatorIcon(react, item.appId ?? 0, 56),
              title: item.name,
              subtitle: item.versionLine,
              meta: item.summary ? [item.summary] : [],
              tags: (item.chips ?? []).map(emulatorChipTag),
              autoFocus: returnTo === `installed:${item.id}`,
              onActivate: () => open("installation", item.id, `installed:${item.id}`),
            }),
          ),
        ),
      );
    }
  } else if (tab === "catalog") {
    const catalog: any[] = state.catalog ?? [];
    const search = emulatorMemory.catalogSearch;
    const family = emulatorMemory.family;
    const searched = catalog.filter((item) => emulatorMatches(search, item.name, item.systems));
    const shown = searched.filter((item) => family === "all" || (item.families ?? []).includes(family));
    const setFamily = (value: string) => {
      emulatorMemory.family = value;
      redraw();
    };
    view.push(
      renderSteamUiToolbar(
        ui,
        renderSteamUiTool(
          ui,
          null,
          h(ui.textField, {
            label: "Search emulators or systems",
            placeholder: "Search by emulator or system name",
            value: search,
            onChange: (event: any) => {
              emulatorMemory.catalogSearch = event?.target?.value ?? "";
              redraw();
            },
          }),
          true,
        ),
      ),
      renderSteamUiChips(ui, [
        { label: `All (${searched.length})`, selected: family === "all", onClick: () => setFamily("all") },
        ...(state.families ?? []).map((name: string) => ({
          label: `${name} (${searched.filter((item) => (item.families ?? []).includes(name)).length})`,
          selected: family === name,
          onClick: () => setFamily(name),
        })),
      ]),
    );
    if (!shown.length) {
      view.push(
        catalog.length
          ? renderSteamUiEmpty(ui, {
              kind: "noMatch",
              text: "No emulator matches the search. Clear it or choose All.",
              action: {
                label: "Clear search",
                onClick: () => {
                  emulatorMemory.catalogSearch = "";
                  emulatorMemory.family = "all";
                  redraw();
                },
              },
            })
          : renderSteamUiEmpty(ui, { kind: "error", text: "The emulator catalogue could not be read." }),
      );
    } else {
      const channelOf = (id: string) => definitions.find((item) => item.id === id)?.channels?.[0] ?? "";
      view.push(
        renderSteamUiGrid(
          ui,
          shown.map((item) =>
            renderSteamUiCard(ui, {
              key: item.definitionId,
              icon: emulatorIcon(react, 0, 56),
              title: item.name,
              subtitle: item.installedVersion
                ? `${item.installedVersion} installed`
                : item.latestVersion
                  ? `Latest ${item.latestVersion}`
                  : "",
              meta: [item.systems, item.requirement].filter(Boolean),
              tags: item.installedVersion ? [{ text: "Installed", tone: "success" }] : [],
              description: "Details",
              secondary: item.installedVersion
                ? null
                : {
                    label: "Install",
                    disabled: busy || !channelOf(item.definitionId),
                    onActivate: () =>
                      act("installEmulator", { definitionId: item.definitionId, channel: channelOf(item.definitionId) }),
                  },
              autoFocus: returnTo === `catalog:${item.definitionId}`,
              onActivate: () => open("definition", item.definitionId, `catalog:${item.definitionId}`),
            }),
          ),
        ),
        muted("Already have one installed yourself? Open its card and choose Use my installation.", "external"),
      );
    }
  } else if (tab === "cores") {
    const frontends = installations.filter((item) => item.dataPolicy?.hasCores);
    if (!frontends.length) {
      view.push(
        renderSteamUiEmpty(ui, {
          kind: "empty",
          title: "RetroArch is not installed",
          text: "Its cores are listed here once it is.",
          action: {
            label: "Get RetroArch",
            onClick: () => {
              emulatorMemory.catalogSearch = "RetroArch";
              emulatorMemory.family = "all";
              toTab("catalog");
            },
          },
        }),
      );
    } else {
      const installation = frontends.find((item) => item.id === emulatorMemory.coreInstallation) ?? frontends[0];
      const id = installation.id;
      const status = new Map(
        (state.coreStatus ?? []).filter((item: any) => item.installationId === id).map((item: any) => [item.coreId, item]),
      );
      const needsFiles = (core: any) => (status.get(core.id) as any)?.missing === true;
      const cores: any[] = installation.cores ?? [];
      const search = emulatorMemory.coreSearch;
      const found = cores
        .filter((core) =>
          emulatorMatches(
            search,
            core.name,
            core.id,
            (core.systems ?? []).join(" "),
            (core.systems ?? []).map((system: string) => emulatorSystemName(state, system)).join(" "),
          ),
        )
        .sort((left, right) => left.name.localeCompare(right.name, undefined, { sensitivity: "base" }));
      const tools: any[] = [];
      if (frontends.length > 1) {
        tools.push(
          renderSteamUiTool(
            ui,
            null,
            renderSteamUiChoice(ui, {
              label: "RetroArch installation",
              rgOptions: frontends.map((item) => ({ data: item.id, label: `${item.name} ${item.version} · ${item.channel}` })),
              selectedOption: id,
              onChange: (option: any) => {
                if (!option) return;
                emulatorMemory.coreInstallation = option.data;
                emulatorMemory.coresShown = EmulatorCoreChunk;
                redraw();
              },
            }),
          ),
        );
      }
      tools.push(
        renderSteamUiTool(
          ui,
          null,
          h(ui.textField, {
            label: "Search cores or systems",
            placeholder: "Search by core or system name",
            value: search,
            onChange: (event: any) => {
              emulatorMemory.coreSearch = event?.target?.value ?? "";
              emulatorMemory.coresShown = EmulatorCoreChunk;
              redraw();
            },
          }),
          true,
        ),
      );
      view.push(
        renderSteamUiToolbar(ui, ...tools),
        h(
          ui.focusable,
          { key: "summary", className: "wsgm-emu-toolbar", "flow-children": "row" },
          muted(
            `${emulatorCount(cores.length, "installed", "installed")} · ${emulatorCount(cores.filter(needsFiles).length, "needs files", "need files")}`,
          ),
          actions([
            {
              id: "update-cores",
              label: "Update installed cores",
              disabled: busy || !installation.managed,
              onClick: () => act("updateEmulator", { installationId: id }),
            },
          ]),
        ),
      );
      const row = (core: any) => {
        const missingFiles: string[] = (status.get(core.id) as any)?.missingFiles ?? [];
        const systems = (core.systems ?? []).map((system: string) => emulatorSystemName(state, system)).join(" · ");
        const prefix = systems ? `${systems} · ` : "";
        const attention = needsFiles(core);
        return renderSteamUiSelectRow(ui, {
          key: core.id,
          title: core.name,
          detail: attention && missingFiles.length
            ? `${prefix}needs ${emulatorJoinAnd(missingFiles)}`
            : core.metadataMissing
              ? `${prefix}core information missing, so required files can't be checked`
              : systems,
          status: renderSteamUiTags(react, [
            attention
              ? { text: "Needs files", tone: "update" }
              : core.metadataMissing
                ? { text: "No details" }
                : { text: "Ready", tone: "success" },
          ]),
          description: "Core details",
          secondary: attention ? { label: "BIOS and firmware", onActivate: toBios } : null,
          autoFocus: returnTo === `core:${core.id}`,
          onClick: () => open("core", `${id}\n${core.id}`, `core:${core.id}`),
        });
      };
      if (!found.length) {
        view.push(
          renderSteamUiEmpty(ui, {
            kind: "noMatch",
            text: "No core matches the search.",
            action: {
              label: "Clear search",
              onClick: () => {
                emulatorMemory.coreSearch = "";
                redraw();
              },
            },
          }),
        );
      } else {
        const attention = found.filter(needsFiles);
        const ready = found.filter((core) => !needsFiles(core));
        if (attention.length) {
          view.push(
            h(
              ui.focusable,
              { key: "attention", className: "wsgm-emu-section", "flow-children": "column" },
              renderSteamUiHeader(ui, { title: "Needs attention" }),
              h(ui.focusable, { className: "steam-ui-kit-list", "flow-children": "column" }, ...attention.map(row)),
            ),
          );
        }
        if (ready.length) {
          view.push(
            h(
              ui.focusable,
              { key: "ready", className: "wsgm-emu-section", "flow-children": "column" },
              renderSteamUiHeader(ui, { title: "Ready" }),
              h(
                ui.focusable,
                { className: "steam-ui-kit-list", "flow-children": "column" },
                ...ready.slice(0, emulatorMemory.coresShown).map(row),
              ),
            ),
          );
          if (ready.length > emulatorMemory.coresShown) {
            view.push(
              renderSteamUiMore(ui, {
                label: `Load More (${emulatorMemory.coresShown} of ${ready.length} ready cores)`,
                onClick: () => {
                  emulatorMemory.coresShown += EmulatorCoreChunk;
                  redraw();
                },
              }),
            );
          }
        }
      }
    }
  } else if (tab === "defaults") {
    const systems: any[] = state.systems ?? [];
    const installationName = (id: string) => installations.find((item) => item.id === id)?.name;
    const defaultRow = (system: any) => {
      const systemId = system.systemId;
      const preference = (manager.systemPreferences ?? []).find((item: any) => item.systemId === systemId);
      const choices: any[] = (state.choices ?? []).find((item: any) => item.systemId === systemId)?.installations ?? [];
      // An installation's name, with its version and ownership only when two compatible ones share it.
      const label = (choice: any) => {
        const name = installationName(choice.id);
        return !name || choices.filter((other) => installationName(other.id) === name).length > 1 ? choice.label : name;
      };
      const selected = choices.find((item) => item.id === preference?.installationId);
      const emulator = renderSteamUiChoice(ui, {
        label: `${system.name} emulator`,
        showLabel: false,
        rgOptions: [{ data: "", label: "No default" }, ...choices.map((item) => ({ data: item.id, label: label(item) }))],
        selectedOption: preference?.installationId ?? "",
        disabled: busy,
        onChange: (option: any) =>
          option &&
          act("setPreferredEmulator", {
            systemId,
            installationId: option.data,
            coreId: choices.find((item) => item.id === option.data)?.defaultCoreId ?? "",
          }),
      });
      const core = system.needsEmulator
        ? rowActions([
            {
              id: "get",
              label: "Get an emulator",
              name: `Get an emulator for ${system.name}`,
              onClick: () => {
                emulatorMemory.catalogSearch = system.name;
                emulatorMemory.family = "all";
                toTab("catalog");
              },
            },
          ])
        : selected?.requiresCore
          ? renderSteamUiChoice(ui, {
              label: `${system.name} core`,
              showLabel: false,
              rgOptions: (selected.cores ?? []).map((item: any) => ({ data: item.id, label: item.label })),
              selectedOption: preference?.coreId || selected.defaultCoreId,
              disabled: busy,
              onChange: (option: any) =>
                option &&
                act("setPreferredEmulator", { systemId, installationId: selected.id, coreId: option.data }),
            })
          : muted(selected ? "Standalone" : "No emulator chosen");
      return field(
        `default:${systemId}`,
        system.name,
        system.summary,
        h(ui.focusable, { className: "wsgm-emu-choices", "flow-children": "row" }, emulator, core),
      );
    };
    const used = systems.filter((system) => system.inLibrary);
    const others = systems.filter((system) => !system.inLibrary);
    view.push(
      h(
        "p",
        { key: "intro" },
        "The emulator each system's games use unless a ROM source or a game picks its own. Changing one leaves existing choices on sources and games alone.",
      ),
      used.length
        ? h(ui.settingsSection, { key: "used" }, ...used.map(defaultRow))
        : muted(
            "No ROM source uses a system yet. Add one in the Library Importer, or set a default below for when you do.",
            "empty",
          ),
    );
    if (others.length) {
      view.push(
        renderSteamUiGroup(
          ui,
          {
            key: "others",
            title: "Other systems",
            detail: "Systems none of your ROM sources use yet",
            collapsed: !emulatorMemory.otherSystems,
            onToggle: () => {
              emulatorMemory.otherSystems = !emulatorMemory.otherSystems;
              redraw();
            },
          },
          h(ui.settingsSection, null, ...others.map(defaultRow)),
        ),
      );
    }
  } else if (tab === "bios") {
    const summary = state.biosSummary ?? { found: 0, needed: 0, label: "", systems: [] };
    const bios = state.bios ?? { folder: "", checked: false, systems: [] };
    const addFiles = (systemId: string, folder: boolean, title: string) =>
      pick(title, folder ? "folder" : "file", [], (path) => act("addBiosFiles", { path, systemId }));
    view.push(
      h(
        ui.focusable,
        { key: "folder", className: "wsgm-emu-section", "flow-children": "column" },
        renderSteamUiHeader(ui, { title: "Shared BIOS folder", detail: bios.folder || "Not chosen yet" }),
        renderSteamUiTags(react, [
          {
            text: summary.label,
            tone: summary.needed > 0 && summary.found < summary.needed ? "update" : summary.needed > 0 ? "success" : "neutral",
          },
        ]),
        actions([
          { id: "add", label: "Add BIOS files…", disabled: busy, onClick: () => addFiles("", false, "Choose local BIOS files") },
          { id: "add-folder", label: "Add a folder…", disabled: busy, onClick: () => addFiles("", true, "Choose local BIOS folder") },
          {
            id: "folder",
            label: "Change folder…",
            disabled: busy,
            onClick: () => pick("Choose BIOS folder", "folder", [], (path) => act("setBiosFolder", { path })),
          },
          { id: "verify", label: "Check files", disabled: busy, onClick: () => act("verifyBios") },
        ]),
      ),
      h(
        "p",
        { key: "intro" },
        "Installed emulators and cores read their files from this one folder, linked into each emulator's own location. WSGM checks every file against the known good dumps where a checksum is published.",
      ),
    );
    const checklist = (system: any) => {
      const id = system.systemId;
      const rows: any[] = [];
      const setups = installations.filter(
        (item) =>
          (item.systems ?? []).includes(id) &&
          (item.dataPolicy?.prerequisites ?? []).some((rule: any) => rule.nativeInstaller),
      );
      rows.push(
        field(
          `bios:${id}`,
          system.name,
          (system.usedBy || "No installed emulator uses it") + (system.optional ? " · optional" : ""),
          rowActions([
            { id: "add", label: "Add files…", disabled: busy, onClick: () => addFiles(id, false, `Choose ${system.name} files`) },
            {
              id: "relink",
              label: "Relink emulators",
              disabled: busy || !system.usedBy,
              onClick: () => act("relinkBios", { systemId: id }),
            },
            ...setups.map((item) => ({
              id: `setup:${item.id}`,
              label: `Set up ${item.name}`,
              onClick: () => open("installation", item.id, `bios:${id}`),
            })),
          ]),
        ),
      );
      for (const file of system.items ?? []) {
        const ready = file.state === "verified" || file.state === "present";
        const value =
          !ready && !file.optional && file.state === "missing"
            ? rowActions([
                {
                  id: "choose",
                  label: "Choose file…",
                  name: `Choose ${file.label}`,
                  disabled: busy,
                  onClick: () => addFiles(id, false, `Choose ${file.label}`),
                },
              ])
            : renderSteamUiTag(react, {
                text: file.stateLabel,
                tone: file.state === "verified" ? "success" : file.state === "wrongFile" ? "danger" : "neutral",
              });
        rows.push(
          field(
            `bios:${id}:${file.label}`,
            file.label,
            ready || file.state === "wrongFile" ? "" : file.stateLabel,
            value,
            renderSteamUiStateMark(react, {
              state: ready ? "ready" : file.optional ? "optional" : "missing",
              label: file.stateLabel,
            }),
            true,
          ),
        );
      }
      for (const link of (bios.systems ?? []).find((item: any) => item.id === id)?.links ?? []) {
        rows.push(field(`bios:${id}:link:${link}`, link, "", null, undefined, true));
      }
      return rows;
    };
    const systems: any[] = summary.systems ?? [];
    if (!systems.length) {
      view.push(
        renderSteamUiEmpty(ui, {
          kind: "empty",
          text: bios.checked
            ? "No supported emulator needs BIOS or firmware files."
            : "BIOS files have not been checked yet. Choose Check files.",
        }),
      );
    } else {
      const used = systems.filter((system) => system.usedBy);
      const others = systems.filter((system) => !system.usedBy);
      view.push(
        used.length
          ? h(ui.settingsSection, { key: "used" }, ...used.flatMap(checklist))
          : muted("No installed emulator needs these files yet. The systems below list what each one will need.", "unused"),
      );
      if (others.length) {
        view.push(
          renderSteamUiGroup(
            ui,
            {
              key: "others",
              title: "Systems without an installed emulator",
              detail: "Add their files now, and emulators you install later use them",
              collapsed: !emulatorMemory.otherBios,
              onToggle: () => {
                emulatorMemory.otherBios = !emulatorMemory.otherBios;
                redraw();
              },
            },
            h(ui.settingsSection, null, ...others.flatMap(checklist)),
          ),
        );
      }
    }
  }

  // The page's purpose, Check for updates, the running work or its last result, then the view.
  const refreshing = busy && progress.operation === "refresh";
  return renderSteamUiPane(
    ui,
    { key: `${tab}:home`, className: "wsgm-emu-root" },
    h(
      ui.focusable,
      { className: "wsgm-emu-head", "flow-children": "row" },
      renderSteamUiHeader(ui, { page: true, title: "Emulators", detail: EmulatorPurpose }),
      actions([
        {
          id: "refresh",
          label: refreshing ? "Checking…" : "Check for updates",
          disabled: busy,
          onClick: () => act("refreshEmulators"),
        },
      ]),
    ),
    busy
      ? emulatorProgressBlock(ui, progress, false)
      : renderSteamUiStatus(ui, [progress.status ? { key: "progress", text: progress.status } : null]),
    ...view,
  );
}
