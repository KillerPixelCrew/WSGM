// Both surfaces use LaunchActionService; this page supplies intent and the cog menu's exact identity.
const LaunchActionsPatchId = "wsgm.launch-actions";

function LaunchActionsPage({ context, page }: any) {
  const react = context.react();
  const h = react.createElement;
  const ui = context.ui();
  const state = context.state();
  const appId = Number(page?.params?.appid ?? 0);
  const [error, setError] = react.useState("");
  const [busy, setBusy] = react.useState(false);
  const [argumentsValue, setArguments] = react.useState("");
  const alive = react.useRef(null);
  const inFlight = react.useRef(false);

  react.useEffect(() => {
    const abort = new AbortController();
    alive.current = abort;
    setError("");
    setBusy(false);
    inFlight.current = false;
    setArguments("");
    if (appId > 0 && appId <= 0xffffffff) {
      void request(LaunchActionsPatchId, "open", { appId }).catch((failure) => {
        if (!abort.signal.aborted) setError(failure instanceof Error ? failure.message : String(failure));
      });
    }
    return () => abort.abort();
  }, [appId]);

  const act = async (command: string, payload: any) => {
    const operation = alive.current;
    if (operation?.signal.aborted || inFlight.current) return;
    inFlight.current = true;
    setBusy(true);
    setError("");
    try {
      await request(LaunchActionsPatchId, command, { appId, ...payload });
    } catch (failure) {
      if (!operation?.signal.aborted) setError(failure instanceof Error ? failure.message : String(failure));
    } finally {
      if (!operation?.signal.aborted) {
        inFlight.current = false;
        setBusy(false);
      }
    }
  };

  if (!appId)
    return h(
      "p",
      { role: "alert" },
      "This launch action has no game identity. Open it from the game's cog menu.",
    );
  if (!state || Number(state.appId) !== appId) {
    return h(
      "p",
      { role: "status" },
      error || context.refusal() || "Reading this game's launch configuration…",
    );
  }

  const confirm = (label: string, command: string, payload: any) => {
    const origin = alive.current;
    if (
      !showSteamUiConfirm(ui, {
        title: label,
        text: `${state.name}: replace the current launch action? ${state.canRestore ? "The recorded original will remain available." : "The original will be recorded before anything changes."}`,
        confirmLabel: label,
        onConfirm: () => {
          if (!origin?.signal.aborted) void act(command, payload);
        },
      })
    )
      setError("Steam's confirmation dialog is unavailable.");
  };
  const custom = async () => {
    const origin = alive.current;
    const args = argumentsValue;
    try {
      const path = await showSteamFilePicker(ui, {
        title: `Custom launch action for ${state.name}`,
        mode: "file",
        extensions: [".exe", ".cmd", ".bat", ".ps1"],
        signal: origin?.signal,
      });
      if (path && !origin?.signal.aborted)
        confirm("Replace launch action", "custom", { path, arguments: args });
    } catch (failure) {
      if (!origin?.signal.aborted) setError(failure instanceof Error ? failure.message : String(failure));
    }
  };
  const unavailable = busy || !!state.error;
  return h(
    ui.focusable,
    { className: "steam-ui-kit-page", "flow-children": "column", style: { overflowY: "auto" } },
    steamUiKitStyle(react),
    h(
      "div",
      { className: "steam-ui-kit-pane", style: { padding: "24px 48px 72px" } },
      renderSteamUiHeader(ui, {
        title: "Launch Actions",
        detail: `${state.name} · ${appId} · ${state.isShortcut ? "Non-Steam shortcut" : "Steam game"}`,
      }),
      h("p", { role: "status" }, `Current: ${state.ownership}`),
      h(
        "p",
        null,
        state.canRestore
          ? "Original launch action recorded. Restore is available."
          : "No restoration snapshot recorded.",
      ),
      state.externallyChanged
        ? h(
            "p",
            { role: "status" },
            "Launch fields differ from WSGM's saved action. The recorded original is still retained.",
          )
        : null,
      !state.wrapperAvailable
        ? h(
            "p",
            { role: "alert" },
            "Launch wrapper missing. Repair the installation before applying wrapper actions.",
          )
        : null,
      h("p", null, state.inputRoute),
      state.target
        ? h("p", { style: { overflowWrap: "anywhere" } }, `Target: ${state.target}`)
        : null,
      h(
        "p",
        { style: { overflowWrap: "anywhere" } },
        `Launch options: ${state.options || "(empty)"}`,
      ),
      error || state.error || state.notice
        ? renderSteamUiBanner(ui, {
            text: error || state.error || state.notice,
            error: !!(error || state.error),
            onDismiss: () => {
              setError("");
              if (state.notice) void act("open", {});
            },
          })
        : null,
      renderSteamUiActions(
        ui,
        (state.actions || []).map((action) => ({
          id: `wrapper-${action.mode}`,
          label: action.label,
          disabled: unavailable || !state.wrapperAvailable,
          onClick: () => confirm(action.label, "apply", { mode: action.mode }),
        })),
      ),
      h(ui.textField, {
        label: "Custom action arguments (optional)",
        value: argumentsValue,
        disabled: busy,
        onChange: (event) => setArguments(event?.target?.value ?? ""),
      }),
      renderSteamUiActions(ui, [
        {
          id: "custom",
          label: "Custom launch action…",
          disabled: unavailable,
          onClick: () => void custom(),
        },
        {
          id: "restore",
          label: "Restore original launch action",
          disabled: unavailable || !state.canRestore,
          onClick: () => confirm("Restore original launch action", "apply", { mode: 0 }),
        },
        { id: "refresh", label: "Refresh", disabled: busy, onClick: () => void act("open", {}) },
      ]),
    ),
  );
}

registerSteamPage({
  template: "launch-actions",
  gate: "launchActions",
  patchId: LaunchActionsPatchId,
  components: resolveSteamUiComponents,
  required: [
    "react",
    "focusable",
    "dialogButton",
    "dialogButtonPrimary",
    "textField",
    "modalRoot",
    "showModal",
  ],
  Page: LaunchActionsPage,
});
