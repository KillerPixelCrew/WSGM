// The native submenu and Overlay share LaunchActionService and its exact-AppID commands.
const LaunchActionsPatchId = "wsgm.launch-actions";
const launchActionsMenuStore = createLocalStore();
let launchActionsMenuUi: any = null;
let launchActionsMenuState: any = null;
let launchActionsMenuRefusal = "";
let launchActionsMenuLifetime: AbortController | null = null;

function LaunchActionsMenu({ native }: any) {
  const { react, itemType, submenuType, appId, currentAppId } = native;
  const h = react.createElement;
  react.useSyncExternalStore(
    launchActionsMenuStore.subscribe,
    launchActionsMenuStore.revision,
    launchActionsMenuStore.revision,
  );
  const [error, setError] = react.useState("");
  const [busy, setBusy] = react.useState(false);
  const pending = react.useRef(false);
  const lifetime = launchActionsMenuLifetime;
  const ui = launchActionsMenuUi;
  const active = () => !!ui && lifetime === launchActionsMenuLifetime && !lifetime?.signal.aborted;
  const selected = () => active() && currentAppId() === appId;
  const state = Number(launchActionsMenuState?.appId) === appId ? launchActionsMenuState : null;
  const report = (failure) => {
    if (!active()) return;
    const message = failure instanceof Error ? failure.message : String(failure);
    setError(message);
    // Native action selection closes the menu. Keep a refusal visible in its owning dialog too.
    showSteamUiConfirm(ui, {
      title: "Launch Actions",
      text: message,
      confirmLabel: "OK",
      onConfirm: () => {},
    });
  };
  const act = async (command, payload) => {
    if (!active() || pending.current) return;
    pending.current = true;
    setBusy(true);
    setError("");
    try {
      await request(LaunchActionsPatchId, command, { appId, ...payload });
    } catch (failure) {
      report(failure);
    } finally {
      pending.current = false;
      if (active()) setBusy(false);
    }
  };
  react.useEffect(() => {
    if (selected()) void act("open", {});
  }, [appId, lifetime]);

  const row = (id, label, onSelected?, disabled = false) =>
    h(
      itemType,
      {
        key: id,
        disabled: disabled || !ui,
        onSelected,
      },
      label,
    );
  if (!ui) return row("unavailable", "Launch Actions are unavailable", undefined, true);
  const confirm = (label, command, payload) => {
    if (!active()) return;
    if (
      !showSteamUiConfirm(ui, {
        title: label,
        text: `${state.name} · ${appId}: replace the current launch action? ${state.canRestore ? "The recorded original will remain available." : "The original will be recorded before anything changes."}`,
        confirmLabel: label,
        onConfirm: () => {
          if (active()) void act(command, payload);
        },
      })
    )
      report("Steam's confirmation dialog is unavailable.");
  };
  const custom = async (argumentsValue = "") => {
    if (!active()) return;
    try {
      const path = await showSteamFilePicker(ui, {
        title: `Custom launch action for ${state.name} · ${appId}`,
        mode: "file",
        extensions: [".exe", ".cmd", ".bat", ".ps1"],
        signal: lifetime?.signal,
      });
      if (path && active())
        confirm("Replace launch action", "custom", { path, arguments: argumentsValue });
    } catch (failure) {
      report(failure);
    }
  };
  const unavailable = busy || !!state?.error || !state;
  const details = [
    row("identity", state ? `${state.name} · ${appId}` : `Game ${appId}`, undefined, true),
  ];
  if (state) {
    details.push(row("current", `Current: ${state.ownership}`, undefined, true));
    details.push(
      row(
        "snapshot",
        state.canRestore ? "Original launch action recorded" : "No restoration snapshot recorded",
        undefined,
        true,
      ),
    );
    details.push(row("input-route", state.inputRoute, undefined, true));
    if (state.externallyChanged)
      details.push(
        row(
          "changed",
          "Launch fields changed outside WSGM; recorded original retained",
          undefined,
          true,
        ),
      );
    if (state.target) details.push(row("target", `Target: ${state.target}`, undefined, true));
    details.push(row("options", `Launch options: ${state.options || "(empty)"}`, undefined, true));
    if (!state.wrapperAvailable)
      details.push(
        row("wrapper", "Launch wrapper missing. Repair the installation.", undefined, true),
      );
  }
  const rows = [
    h(submenuType, { key: "configuration", label: "Current launch configuration" }, ...details),
  ];
  const reason =
    error ||
    launchActionsMenuRefusal ||
    state?.error ||
    state?.notice ||
    (!state ? "Reading this game's launch configuration…" : "");
  if (reason) rows.push(row("status", reason, undefined, true));
  for (const action of state?.actions || [])
    rows.push(
      row(
        `wrapper-${action.mode}`,
        action.label,
        () => {
          if (selected()) confirm(action.label, "apply", { mode: action.mode });
        },
        unavailable || !state.wrapperAvailable,
      ),
    );
  rows.push(
    row(
      "custom",
      "Custom executable…",
      () => {
        if (selected()) void custom();
      },
      unavailable,
    ),
  );
  rows.push(
    row(
      "custom-arguments",
      "Custom executable with arguments…",
      () => {
        if (!selected()) return;
        if (
          !showSteamUiPrompt(ui, {
            title: `Custom launch arguments · ${state.name}`,
            label: "Arguments",
            text: "Enter arguments, then choose the executable. Use Custom executable for no arguments.",
            confirmLabel: "Choose executable…",
            onConfirm: (value) => {
              if (active()) void custom(value);
            },
          })
        )
          report("Steam's text prompt is unavailable.");
      },
      unavailable,
    ),
  );
  rows.push(
    row(
      "restore",
      "Restore original launch action",
      () => {
        if (selected()) confirm("Restore original launch action", "apply", { mode: 0 });
      },
      unavailable || !state?.canRestore,
    ),
  );
  rows.push(
    row(
      "refresh",
      "Refresh",
      () => {
        if (selected()) void act("open", {});
      },
      busy,
    ),
  );
  return h(submenuType, { label: native.label }, ...rows);
}

registerSteamGameContextMenuRenderer("launch-actions", (native) =>
  native.react.createElement(LaunchActionsMenu, { key: native.key, native }),
);
registerGate(
  "launchActions",
  (() => {
    let unsubscribe: (() => void) | null = null;
    let unsubscribeRefusal: (() => void) | null = null;
    let lastError = "";
    return {
      install: () => {
        if (launchActionsMenuLifetime) return { ok: true, alreadyInstalled: true };
        try {
          const ui = resolveSteamUiComponents(getWebpackRuntime("launch-actions"));
          const missing = [
            "react",
            "focusable",
            "dialogButton",
            "textField",
            "modalRoot",
            "showModal",
          ].filter((key) => !ui?.[key]);
          if (missing.length)
            return {
              ok: false,
              error: (lastError = `Native Steam components unavailable: ${missing.join(", ")}`),
            };
          launchActionsMenuUi = ui;
          launchActionsMenuLifetime = new AbortController();
          unsubscribe = subscribe(LaunchActionsPatchId, (state) => {
            launchActionsMenuState = state;
            launchActionsMenuStore.changed();
          });
          unsubscribeRefusal = subscribeRefusal(LaunchActionsPatchId, (reason) => {
            launchActionsMenuRefusal = reason || "";
            launchActionsMenuStore.changed();
          });
          launchActionsMenuStore.changed();
          return { ok: true, installed: true };
        } catch (failure) {
          return { ok: false, error: (lastError = String(failure)) };
        }
      },
      remove: () => {
        launchActionsMenuLifetime?.abort();
        launchActionsMenuLifetime = null;
        unsubscribe = endSubscription(unsubscribe);
        unsubscribeRefusal = endSubscription(unsubscribeRefusal);
        launchActionsMenuUi = null;
        launchActionsMenuState = null;
        launchActionsMenuRefusal = "";
        launchActionsMenuStore.changed();
        return { ok: true, removed: true };
      },
      status: () => ({
        installed: !!launchActionsMenuLifetime,
        resolved: !!launchActionsMenuUi,
        subscribed: !!unsubscribe,
        lastError,
      }),
    };
  })(),
);
