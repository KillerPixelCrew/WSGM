type BridgeConfiguration = Readonly<{
  version: number;
  namespace: string;
  binding: string;
  contextGeneration: number;
  documentGeneration: number;
  maximumPending: number;
  timeoutMilliseconds: number;
  allowed: Readonly<Record<string, readonly string[]>>;
}>;

declare const __WSGM_CONFIGURATION_JSON__: BridgeConfiguration;

// This file is a script, not a module, so the interface merges with the global
// Window directly; a `declare global` block would need a module context.
interface Window {
  // Steam's own webpack chunk registry. Untyped by Steam, and the only route to
  // the module runtime the native components are installed into.
  webpackChunksteamui: unknown[];
  [key: string]: any;
}

// @wsgm-bundle-start
(() => {
  "use strict";
  const config: BridgeConfiguration = __WSGM_CONFIGURATION_JSON__;
  const prior = window[config.namespace];
  if (
    prior &&
    prior.version === config.version &&
    prior.contextGeneration === config.contextGeneration &&
    prior.documentGeneration === config.documentGeneration &&
    prior.nativeComponents &&
    typeof prior.nativeComponents.install === "function"
  ) {
    return JSON.stringify({ ok: true, reused: true, version: prior.version });
  }
  if (prior && typeof prior.dispose === "function") prior.dispose("generation replaced");

  const pending = new Map();
  const subscribers = new Map();
  const latestStates = new Map();
  const nativeComponents = createNativeComponentHost();
  let nextSequence = 0;
  let disposed = false;

  const allowed = (patchId, command) => {
    const commands = config.allowed[patchId];
    return Array.isArray(commands) && commands.includes(command);
  };
  const send = (envelope) => {
    if (disposed) throw new Error("WSGM bridge disposed");
    const binding = window[config.binding];
    if (typeof binding !== "function") throw new Error("WSGM Runtime binding unavailable");
    binding(JSON.stringify(envelope));
  };
  const request = (patchId, command, payload, actionGeneration) => {
    if (!allowed(patchId, command)) return Promise.reject(new Error("command not allowlisted"));
    if (pending.size >= config.maximumPending) return Promise.reject(new Error("bridge busy"));
    const sequence = ++nextSequence;
    const envelope = {
      version: config.version,
      type: "request",
      patchId,
      command,
      sequence,
      actionGeneration,
      contextGeneration: config.contextGeneration,
      documentGeneration: config.documentGeneration,
      payload: payload ?? null,
    };
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        pending.delete(sequence);
        try {
          send({ ...envelope, type: "cancel" });
        } catch {}
        reject(new Error("WSGM bridge request timed out"));
      }, config.timeoutMilliseconds);
      pending.set(sequence, { resolve, reject, timer, patchId, command });
      try {
        send(envelope);
      } catch (error) {
        clearTimeout(timer);
        pending.delete(sequence);
        reject(error);
      }
    });
  };
  const subscribe = (patchId, callback) => {
    if (!Object.hasOwn(config.allowed, patchId) || typeof callback !== "function")
      throw new Error("subscription not allowlisted");
    let set = subscribers.get(patchId);
    if (!set) subscribers.set(patchId, (set = new Set()));
    set.add(callback);
    if (latestStates.has(patchId)) callback(latestStates.get(patchId));
    return () => set.delete(callback);
  };
  const deliver = (envelope) => {
    if (
      !envelope ||
      envelope.version !== config.version ||
      envelope.contextGeneration !== config.contextGeneration ||
      envelope.documentGeneration !== config.documentGeneration
    )
      return false;
    if (envelope.type === "response") {
      const item = pending.get(envelope.sequence);
      if (!item || item.patchId !== envelope.patchId || item.command !== envelope.command)
        return false;
      clearTimeout(item.timer);
      pending.delete(envelope.sequence);
      if (envelope.ok) item.resolve(envelope.payload);
      else item.reject(new Error(String(envelope.error || "command rejected")));
      return true;
    }
    if (envelope.type === "state") {
      if (!Object.hasOwn(config.allowed, envelope.patchId)) return false;
      latestStates.set(envelope.patchId, envelope.payload);
      const set = subscribers.get(envelope.patchId);
      if (!set) return true;
      for (const callback of [...set]) {
        try {
          callback(envelope.payload);
        } catch {}
      }
      return true;
    }
    return false;
  };
  const dispose = (reason) => {
    if (disposed) return;
    disposed = true;
    nativeComponents.dispose();
    for (const item of pending.values()) {
      clearTimeout(item.timer);
      item.reject(new Error(reason || "WSGM bridge disposed"));
    }
    pending.clear();
    subscribers.clear();
    latestStates.clear();
  };

  const bridge = Object.freeze({
    version: config.version,
    contextGeneration: config.contextGeneration,
    documentGeneration: config.documentGeneration,
    request,
    subscribe,
    deliver,
    dispose,
    nativeComponents: Object.freeze({
      install: nativeComponents.install,
      remove: nativeComponents.remove,
      status: nativeComponents.status,
    }),
  });
  Object.defineProperty(window, config.namespace, {
    value: bridge,
    configurable: true,
    enumerable: false,
    writable: false,
  });
  return JSON.stringify({ ok: true, reused: false, version: config.version });

  function createNativeComponentHost() {
    const registrations = new Map();
    const listeners = new Set<() => void>();
    const actionGenerations = new Map();
    let runtime;
    let controlRuntime;
    let tdpControl;
    let autoTdpControl;
    let frameLimitControl;
    let overlayLevelControl;
    let controllerControl;
    let performanceRoot;
    let originalUseMemo;
    let patchedUseMemo;
    let disposedHost = false;

    const definitions = Object.freeze({
      tdp: Object.freeze({
        patchId: "wsgm.native-qam.tdp",
        command: "setPrimaryLimit",
      }),
      autoTdp: Object.freeze({
        patchId: "wsgm.native-qam.auto-tdp",
        command: "setAutoTdp",
      }),
      frameLimit: Object.freeze({
        patchId: "wsgm.native-qam.frame-limit",
        command: "setFrameLimit",
      }),
      overlayLevel: Object.freeze({
        patchId: "wsgm.native-qam.overlay-level",
        command: "setOverlayLevel",
      }),
      controllerTarget: Object.freeze({
        patchId: "wsgm.native-qam.controller-target",
        command: "setControllerTarget",
      }),
    });

    const nextActionGeneration = (patchId) => {
      const next = (actionGenerations.get(patchId) || 0) + 1;
      actionGenerations.set(patchId, next);
      return next;
    };
    const notify = () => {
      for (const listener of [...listeners]) {
        try {
          listener();
        } catch {}
      }
    };
    const subscribeHost = (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    };
    const uniqueFactory = (requiredTokens) => {
      const matches = Object.entries(runtime.m).filter(([, factory]) => {
        const source = String(factory);
        return requiredTokens.every((token) => source.includes(token));
      });
      return matches.length === 1 ? matches[0] : null;
    };
    const uniqueFunction = (exports, requiredTokens) => {
      const matches = Object.values(exports).filter(
        (value) =>
          typeof value === "function" &&
          requiredTokens.every((token) => String(value).includes(token)),
      );
      return matches.length === 1 ? matches[0] : null;
    };
    const uniqueObject = (exports, predicate) => {
      const matches = Object.values(exports).filter(
        (value) => value && typeof value === "object" && predicate(value),
      );
      return matches.length === 1 ? matches[0] : null;
    };
    const getRuntime = () => {
      let found;
      window.webpackChunksteamui.push([
        ["wsgm_native_components_" + Date.now()],
        {},
        (value) => {
          found = value;
        },
      ]);
      return found;
    };
    const createControlRuntime = () => {
      const reactFactory = uniqueFactory([
        "react.transitional.element",
        "useState",
        "cloneElement",
        "createElement",
      ]);
      const fieldsFactory = uniqueFactory([
        "DialogSlider_Container",
        "DropDownField",
        "SliderField",
      ]);
      const layoutFactory = uniqueFactory(["PanelSectionTitle", "PanelSectionRow", "spinner"]);
      const localizationFactory = uniqueFactory([
        "Attempting to localize token",
        "Unable to find localization token",
        "LocalizeString",
      ]);
      if (!reactFactory || !fieldsFactory || !layoutFactory || !localizationFactory) return null;

      const react = runtime(reactFactory[0]);
      const fields = runtime(fieldsFactory[0]);
      const layout = runtime(layoutFactory[0]);
      const localization = runtime(localizationFactory[0]);
      const slider = uniqueFunction(fields, [
        "onChangeComplete",
        "notchCount",
        "valueSuffix",
        "explainerTitle",
      ]);
      const dropdown = uniqueFunction(fields, [
        "contextMenuPositionOptions",
        "childrenContainerWidth",
        "menuLabel",
      ]);
      // Steam's own ToggleField, from the same module as the slider and dropdown above. Selected by
      // the two markers of its class body rather than by its export name, which is minified and
      // changes with every client build. Live-verified 2026-08-29: exactly one export matches, and
      // the provider that names the module's fields lists that same class as ToggleField.
      const toggle = uniqueFunction(fields, ["OnToggleChange", "this.Toggle()"]);
      const section = uniqueFunction(layout, ["PanelSectionTitle", "spinner"]);
      const row = uniqueObject(
        layout,
        (value) => value.$$typeof && typeof value.render === "function",
      );
      const localize = uniqueFunction(localization, ["LocalizeString(e)", "void 0===r?e"]);
      if (!slider || !dropdown || !section || !row || !localize) return null;
      // The toggle is deliberately not in that guard. It arrived after the other four, so a client
      // whose toggle cannot be found still gets every control that does not need one, rather than
      // losing the whole native surface.
      return { react, slider, dropdown, toggle, section, row, localize };
    };
    const normalizeText = (value) => (typeof value === "string" ? value.slice(0, 240) : "");
    const normalizeAutoTdpState = (value) => {
      if (!value || typeof value !== "object" || typeof value.available !== "boolean") return null;
      if (typeof value.enabled !== "boolean" || typeof value.controlling !== "boolean") return null;
      // The watts figure is only ever a display detail beside the switch, so a value outside the
      // range any power limit uses is dropped rather than rejecting the whole state and taking the
      // switch away with it.
      const watts =
        typeof value.watts === "number" &&
        Number.isInteger(value.watts) &&
        value.watts >= 1 &&
        value.watts <= 200
          ? value.watts
          : null;
      return Object.freeze({
        available: value.available,
        enabled: value.enabled,
        controlling: value.controlling,
        watts,
        progress: normalizeText(value.progress),
        statusText: normalizeText(value.statusText),
      });
    };
    const normalizeTdpState = (value) => {
      if (!value || typeof value !== "object" || typeof value.available !== "boolean") return null;
      if (!value.available) {
        return Object.freeze({
          available: false,
          minimumWatts: null,
          maximumWatts: null,
          stepWatts: null,
          desiredWatts: null,
          observedWatts: null,
          progress: normalizeText(value.progress),
          statusText: normalizeText(value.statusText),
        });
      }
      const min = Number(value.minimumWatts);
      const max = Number(value.maximumWatts);
      const step = Number(value.stepWatts);
      const desired = typeof value.desiredWatts === "number" ? value.desiredWatts : null;
      const observed = typeof value.observedWatts === "number" ? value.observedWatts : null;
      if (
        !Number.isInteger(min) ||
        !Number.isInteger(max) ||
        !Number.isInteger(step) ||
        min < 1 ||
        max > 200 ||
        min >= max ||
        step < 1 ||
        step > max - min ||
        (desired !== null && (!Number.isInteger(desired) || desired < min || desired > max)) ||
        (observed !== null && (!Number.isInteger(observed) || observed < min || observed > max))
      )
        return null;
      return Object.freeze({
        available: value.available,
        minimumWatts: min,
        maximumWatts: max,
        stepWatts: step,
        desiredWatts: desired,
        observedWatts: observed,
        progress: normalizeText(value.progress),
        statusText: normalizeText(value.statusText),
      });
    };
    const normalizeControllerState = (value) => {
      if (!value || typeof value !== "object" || typeof value.available !== "boolean") return null;
      if (!Array.isArray(value.targets) || value.targets.length > 8) return null;
      const targets: Readonly<{ id: string; label: string; available: boolean }>[] = [];
      const ids = new Set();
      for (const item of value.targets) {
        if (!item || typeof item !== "object") return null;
        const id = normalizeText(item.id);
        const label = normalizeText(item.label);
        if (!/^[a-z0-9._-]{1,64}$/.test(id) || !label || ids.has(id)) return null;
        ids.add(id);
        targets.push(Object.freeze({ id, label, available: item.available !== false }));
      }
      const selectedTarget = normalizeText(value.selectedTarget);
      const observedTarget = normalizeText(value.observedTarget);
      if (
        (selectedTarget && !ids.has(selectedTarget)) ||
        (observedTarget && !ids.has(observedTarget))
      )
        return null;
      return Object.freeze({
        available: value.available,
        targets: Object.freeze(targets),
        selectedTarget,
        observedTarget,
        progress: normalizeText(value.progress),
        statusText: normalizeText(value.statusText),
        applicationRestartRequired: value.applicationRestartRequired === true,
      });
    };
    const validEnum = (value, allowed) =>
      typeof value === "string" && allowed.includes(value) ? value : null;
    const normalizePerformanceCommon = (value) => {
      if (!value || typeof value !== "object" || typeof value.available !== "boolean") return null;
      const readbackQuality = validEnum(value.readbackQuality, [
        "unavailable",
        "verified",
        "applied-unverified",
        "stale",
      ]);
      const policyLayer = validEnum(value.policyLayer, ["none", "global", "application"]);
      const adapterAvailability = validEnum(value.adapterAvailability, [
        "unknown",
        "not-installed",
        "not-running",
        "incompatible",
        "adapter-unavailable",
        "ready",
        "degraded",
      ]);
      const progress = validEnum(value.progress, [
        "idle",
        "queued",
        "applying",
        "succeeded-verified",
        "applied-unverified",
        "rejected",
        "timed-out",
        "indeterminate",
        "failed",
        "external-change",
      ]);
      if (!readbackQuality || !policyLayer || !adapterAvailability || !progress) return null;
      return Object.freeze({
        available: value.available,
        supportsReadback: value.supportsReadback === true,
        readbackQuality,
        policyLayer,
        applicationTargetAvailable: value.applicationTargetAvailable === true,
        targetProfile: normalizeText(value.targetProfile),
        adapterAvailability,
        progress,
        fault: normalizeText(value.fault),
        statusText: normalizeText(value.statusText),
      });
    };
    const normalizeFrameLimitState = (value) => {
      const common = normalizePerformanceCommon(value);
      if (!common) return null;
      const minimumFps = value.minimumFps === null ? null : Number(value.minimumFps);
      const maximumFps = value.maximumFps === null ? null : Number(value.maximumFps);
      const desiredFps = value.desiredFps === null ? null : Number(value.desiredFps);
      const observedFps = value.observedFps === null ? null : Number(value.observedFps);
      // The bounds are a pair: either both are present or neither is. Rejecting a
      // half-populated range here rather than inside the big test below is also what
      // lets the rest of it treat maximumFps as a number.
      if ((minimumFps === null) !== (maximumFps === null)) return null;
      if (
        (minimumFps !== null &&
          maximumFps !== null &&
          (!Number.isInteger(minimumFps) ||
            !Number.isInteger(maximumFps) ||
            minimumFps < 0 ||
            maximumFps < minimumFps ||
            maximumFps > 1000)) ||
        (desiredFps !== null &&
          (!Number.isInteger(desiredFps) ||
            minimumFps === null ||
            maximumFps === null ||
            desiredFps < minimumFps ||
            desiredFps > maximumFps)) ||
        (observedFps !== null &&
          (!Number.isInteger(observedFps) ||
            minimumFps === null ||
            maximumFps === null ||
            observedFps < minimumFps ||
            observedFps > maximumFps)) ||
        (common.available && minimumFps === null)
      )
        return null;
      return Object.freeze({ ...common, minimumFps, maximumFps, desiredFps, observedFps });
    };
    const normalizeOverlayLevelState = (value) => {
      const common = normalizePerformanceCommon(value);
      if (!common || !Array.isArray(value.levels) || value.levels.length > 5) return null;
      const levels: number[] = [];
      for (const item of value.levels) {
        const level = Number(item);
        if (!Number.isInteger(level) || level < 0 || level > 4 || levels.includes(level))
          return null;
        levels.push(level);
      }
      levels.sort((left, right) => left - right);
      const desiredLevel = value.desiredLevel === null ? null : Number(value.desiredLevel);
      const observedLevel = value.observedLevel === null ? null : Number(value.observedLevel);
      if (
        (desiredLevel !== null &&
          (!Number.isInteger(desiredLevel) || !levels.includes(desiredLevel))) ||
        (observedLevel !== null &&
          (!Number.isInteger(observedLevel) || !levels.includes(observedLevel))) ||
        (common.available && levels.length === 0)
      )
        return null;
      return Object.freeze({
        ...common,
        levels: Object.freeze(levels),
        desiredLevel,
        observedLevel,
      });
    };
    const useSemanticState = (controlRuntime, kind, normalize) => {
      const definition = definitions[kind];
      const [state, setState] = controlRuntime.react.useState(null);
      controlRuntime.react.useEffect(
        () => subscribe(definition.patchId, (value) => setState(normalize(value))),
        [],
      );
      return state;
    };
    const isBusy = (progress) =>
      progress === "queued" || progress === "applying" || progress === "replacing";
    // Steam's localizer returns the token itself when it has no string for it, which is truthy and
    // would render "#QuickAccess_..." as a label. Live-verified 2026-08-29: a known token localizes,
    // an unknown one comes straight back. That matters for anything WSGM adds — Valve has no token
    // for a WSGM feature — and it also protects the Valve-token calls if one is ever retired.
    const localizeOr = (controlRuntime, token, fallback) => {
      const text = controlRuntime.localize(token);
      return typeof text === "string" && text.length > 0 && text[0] !== "#" ? text : fallback;
    };
    const createTdpControl = (controlRuntime) =>
      function WsgmNativeTdpControl() {
        const state = useSemanticState(controlRuntime, "tdp", normalizeTdpState);
        if (!state || !state.available) return null;
        const value = state.observedWatts ?? state.desiredWatts;
        if (value === null) return null;
        const definition = definitions.tdp;
        const setValue = (watts) => {
          if (!Number.isInteger(watts) || watts < state.minimumWatts || watts > state.maximumWatts)
            return;
          void request(
            definition.patchId,
            definition.command,
            { watts },
            nextActionGeneration(definition.patchId),
          ).catch(() => {});
        };
        return controlRuntime.react.createElement(controlRuntime.slider, {
          label: controlRuntime.localize("#QuickAccess_Tab_Perf_TDPLimitEnabled"),
          explainer: controlRuntime.localize("#QuickAccess_Tab_Perf_TDPLimit_Explainer"),
          explainerTitle: controlRuntime.localize("#QuickAccess_Tab_Perf_TDPLimitEnabled"),
          valueSuffix: controlRuntime.localize("#QuickAccess_Tab_Perf_TDPLimitUnits"),
          min: state.minimumWatts,
          max: state.maximumWatts,
          step: state.stepWatts,
          value,
          showValue: true,
          showBookendLabels: true,
          disabled: isBusy(state.progress),
          description: state.statusText || undefined,
          onChange: () => {},
          onChangeComplete: setValue,
        });
      };
    const createAutoTdpControl = (controlRuntime) =>
      function WsgmNativeAutoTdpControl() {
        const state = useSemanticState(controlRuntime, "autoTdp", normalizeAutoTdpState);
        if (!state || !state.available || !controlRuntime.toggle) return null;
        const definition = definitions.autoTdp;
        const setEnabled = (enabled) => {
          if (typeof enabled !== "boolean" || enabled === state.enabled) return;
          void request(
            definition.patchId,
            definition.command,
            { enabled },
            nextActionGeneration(definition.patchId),
          ).catch(() => {});
        };
        // While controlling, the watts AutoTDP settled on go in the description: a user watching the
        // slider move needs to see that something is driving it, and what it decided.
        const description =
          state.controlling && state.watts !== null
            ? state.watts + " W · " + state.statusText
            : state.statusText;
        return controlRuntime.react.createElement(controlRuntime.toggle, {
          label: localizeOr(controlRuntime, "#QuickAccess_Tab_Perf_AutoTDP", "Automatic TDP"),
          description: description || undefined,
          checked: state.enabled,
          // Controlled, so the switch shows the stored setting rather than its own click. A command
          // that does not land leaves the switch where the setting actually is instead of showing a
          // change that did not happen.
          controlled: true,
          disabled: isBusy(state.progress),
          onChange: setEnabled,
        });
      };
    const createControllerControl = (controlRuntime) =>
      function WsgmNativeControllerTargetControl() {
        const state = useSemanticState(
          controlRuntime,
          "controllerTarget",
          normalizeControllerState,
        );
        if (!state || !state.available) return null;
        const options = state.targets
          .filter((target) => target.available)
          .map((target) => ({ data: target.id, label: target.label }));
        const selected = state.observedTarget || state.selectedTarget;
        if (!options.some((option) => option.data === selected)) return null;
        const definition = definitions.controllerTarget;
        const setTarget = (option) => {
          if (!option || !options.some((candidate) => candidate.data === option.data)) return;
          void request(
            definition.patchId,
            definition.command,
            { target: option.data },
            nextActionGeneration(definition.patchId),
          ).catch(() => {});
        };
        const restart = state.applicationRestartRequired
          ? " Restart the application to rebind."
          : "";
        return controlRuntime.react.createElement(controlRuntime.dropdown, {
          label: controlRuntime.localize("#QuickAccess_Tab_Settings_Section_Controller_Title"),
          rgOptions: options,
          selectedOption: selected,
          onChange: setTarget,
          disabled: isBusy(state.progress) || options.length < 2,
          description: (state.statusText || "") + restart || undefined,
          layout: "below",
        });
      };
    const createFrameLimitControl = (controlRuntime) =>
      function WsgmNativeFrameLimitControl() {
        const state = useSemanticState(controlRuntime, "frameLimit", normalizeFrameLimitState);
        if (!state || !state.available) return null;
        const value = state.observedFps ?? state.desiredFps;
        if (value === null) return null;
        const definition = definitions.frameLimit;
        const setValue = (nextValue) => {
          if (
            !Number.isInteger(nextValue) ||
            nextValue < state.minimumFps ||
            nextValue > state.maximumFps
          )
            return;
          void request(
            definition.patchId,
            definition.command,
            { value: nextValue, persistence: "automatic" },
            nextActionGeneration(definition.patchId),
          ).catch(() => {});
        };
        return controlRuntime.react.createElement(controlRuntime.slider, {
          label: controlRuntime.localize("#QuickAccess_Tab_Perf_FramerateLimit"),
          min: state.minimumFps,
          max: state.maximumFps,
          step: 1,
          value,
          valueSuffix: " FPS",
          showValue: true,
          showBookendLabels: true,
          disabled: isBusy(state.progress),
          description: state.fault || state.statusText || undefined,
          onChange: () => {},
          onChangeComplete: setValue,
        });
      };
    const createOverlayLevelControl = (controlRuntime) =>
      function WsgmNativeOverlayLevelControl() {
        const state = useSemanticState(controlRuntime, "overlayLevel", normalizeOverlayLevelState);
        if (!state || !state.available) return null;
        const selected = state.observedLevel ?? state.desiredLevel;
        if (selected === null || !state.levels.includes(selected)) return null;
        const options = state.levels.map((level) => ({
          data: level,
          label: level === 0 ? "Off" : level === 1 ? "On" : String(level),
        }));
        const definition = definitions.overlayLevel;
        const setValue = (option) => {
          if (!option || !state.levels.includes(option.data)) return;
          void request(
            definition.patchId,
            definition.command,
            { value: option.data, persistence: "automatic" },
            nextActionGeneration(definition.patchId),
          ).catch(() => {});
        };
        return controlRuntime.react.createElement(controlRuntime.dropdown, {
          label: controlRuntime.localize("#QuickAccess_Tab_Perf_PerfOverlayLevel"),
          rgOptions: options,
          selectedOption: selected,
          onChange: setValue,
          disabled: isBusy(state.progress) || options.length < 2,
          description: state.fault || state.statusText || undefined,
          layout: "below",
        });
      };
    const appendControls = (controlRuntime, tree) => {
      // Rendered React elements from Steam's own untyped runtime.
      const controls: unknown[] = [];
      if (registrations.has("tdp")) {
        controls.push(
          controlRuntime.react.createElement(
            controlRuntime.row,
            { key: "wsgm-native-qam-tdp" },
            controlRuntime.react.createElement(tdpControl),
          ),
        );
      }
      // Straight after the power limit, because AutoTDP is what moves it. A user who sees the
      // slider change on its own finds the explanation in the next row rather than hunting for it.
      if (registrations.has("autoTdp")) {
        controls.push(
          controlRuntime.react.createElement(
            controlRuntime.row,
            { key: "wsgm-native-qam-auto-tdp" },
            controlRuntime.react.createElement(autoTdpControl),
          ),
        );
      }
      if (registrations.has("frameLimit")) {
        controls.push(
          controlRuntime.react.createElement(
            controlRuntime.row,
            { key: "wsgm-native-qam-frame-limit" },
            controlRuntime.react.createElement(frameLimitControl),
          ),
        );
      }
      if (registrations.has("overlayLevel")) {
        controls.push(
          controlRuntime.react.createElement(
            controlRuntime.row,
            { key: "wsgm-native-qam-overlay-level" },
            controlRuntime.react.createElement(overlayLevelControl),
          ),
        );
      }
      if (registrations.has("controllerTarget")) {
        controls.push(
          controlRuntime.react.createElement(
            controlRuntime.row,
            { key: "wsgm-native-qam-controller-target" },
            controlRuntime.react.createElement(controllerControl),
          ),
        );
      }
      if (!controls.length) return tree;
      let inserted = false;
      const visit = (element, depth) => {
        if (inserted || depth > 8 || !controlRuntime.react.isValidElement(element)) return element;
        if (element.type === controlRuntime.section) {
          inserted = true;
          const children = controlRuntime.react.Children.toArray(element.props.children);
          return controlRuntime.react.cloneElement(element, {}, ...children, ...controls);
        }
        const children = controlRuntime.react.Children.toArray(element.props.children);
        if (!children.length) return element;
        let changed = false;
        const next = children.map((child) => {
          const replacement = visit(child, depth + 1);
          changed ||= replacement !== child;
          return replacement;
        });
        return changed ? controlRuntime.react.cloneElement(element, {}, ...next) : element;
      };
      return visit(tree, 0);
    };
    const ensurePatched = () => {
      if (
        controlRuntime &&
        performanceRoot &&
        patchedUseMemo &&
        controlRuntime.react.useMemo === patchedUseMemo
      )
        return true;
      runtime = getRuntime();
      if (!runtime || !runtime.m) return false;
      const performanceFactory = uniqueFactory([
        "#QuickAccess_Tab_Perf_Common_Settings",
        "#QuickAccess_Tab_Perf_BatteryTimeRemaining",
        "TS.ON_FRAME",
      ]);
      controlRuntime = createControlRuntime();
      if (!performanceFactory || !controlRuntime) return false;
      performanceRoot = uniqueFunction(runtime(performanceFactory[0]), ["TS.ON_FRAME", "return"]);
      if (!performanceRoot) return false;
      tdpControl = createTdpControl(controlRuntime);
      autoTdpControl = createAutoTdpControl(controlRuntime);
      frameLimitControl = createFrameLimitControl(controlRuntime);
      overlayLevelControl = createOverlayLevelControl(controlRuntime);
      controllerControl = createControllerControl(controlRuntime);
      function WsgmNativeQamPerformanceRoot(props) {
        const [, setRevision] = controlRuntime.react.useState(0);
        controlRuntime.react.useEffect(
          () => subscribeHost(() => setRevision((value) => value + 1)),
          [],
        );
        return appendControls(controlRuntime, performanceRoot(props));
      }
      originalUseMemo = controlRuntime.react.useMemo;
      patchedUseMemo = function WsgmNativeQamUseMemo(factory, dependencies) {
        const value = originalUseMemo(factory, dependencies);
        if (!Array.isArray(value)) return value;
        const matches = value.filter(
          (item) =>
            item &&
            typeof item === "object" &&
            controlRuntime.react.isValidElement(item.panel) &&
            item.panel.type === performanceRoot,
        );
        if (matches.length !== 1) return value;
        return value.map((item) => {
          if (item !== matches[0]) return item;
          const panel = controlRuntime.react.createElement(WsgmNativeQamPerformanceRoot, {
            ...item.panel.props,
            key: item.panel.key ?? "wsgm-native-qam-performance-root",
          });
          return { ...item, panel };
        });
      };
      controlRuntime.react.useMemo = patchedUseMemo;
      return controlRuntime.react.useMemo === patchedUseMemo;
    };
    const install = (kind) => {
      if (disposedHost || !Object.hasOwn(definitions, kind))
        return { ok: false, error: "component is not allowlisted" };
      if (!ensurePatched())
        return {
          ok: false,
          error: "native performance root was already initialized or incompatible",
        };
      registrations.set(kind, definitions[kind].patchId);
      notify();
      return { ok: true, kind, registered: true, hostVersion: 1 };
    };
    const remove = (kind) => {
      if (!Object.hasOwn(definitions, kind)) return { ok: true, absent: true };
      registrations.delete(kind);
      actionGenerations.delete(definitions[kind].patchId);
      notify();
      if (
        !registrations.size &&
        controlRuntime &&
        originalUseMemo &&
        controlRuntime.react.useMemo === patchedUseMemo
      ) {
        controlRuntime.react.useMemo = originalUseMemo;
      }
      return { ok: true, kind, registered: false };
    };
    const status = (kind) => ({
      ok: Object.hasOwn(definitions, kind),
      kind,
      registered: registrations.has(kind),
      hostVersion: 1,
      performanceRootWrapped:
        !!controlRuntime && !!patchedUseMemo && controlRuntime.react.useMemo === patchedUseMemo,
    });
    const disposeHostResources = () => {
      disposedHost = true;
      registrations.clear();
      actionGenerations.clear();
      notify();
      listeners.clear();
      if (controlRuntime && originalUseMemo && controlRuntime.react.useMemo === patchedUseMemo)
        controlRuntime.react.useMemo = originalUseMemo;
    };
    return { install, remove, status, dispose: disposeHostResources };
  }
})();
