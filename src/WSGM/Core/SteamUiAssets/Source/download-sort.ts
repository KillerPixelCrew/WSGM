// Name / Size / Type sort buttons in the header of Big Picture's download queue ("Up Next"),
// reordering the queue through Steam's own SteamClient.Downloads.SetQueueIndex.
//
// Every shape decision here is a device-verified finding: the Focusable requirement, the JSX-runtime
// injection point, the tight component predicates, the whole-pending-list scope and the unknown-size
// ranking are in docs/steam-cef.md §12. Re-probe with tools/WsgmLibTest/run-prod-sort.mjs before
// shipping a change here.
//
// The header is intercepted through the toolkit's shared JSX-runtime claim rather than by wrapping
// jsx and jsxs here: the library stat on a game's page claims the same runtime, and two wrappers
// would each hand back the other on removal.
function createWsgmDownloadSort() {
  const patchId = "wsgm.download-sort";
  const transformName = "wsgm.download-sort";
  const sectionToken = "#Downloads_Section_Current";
  const ReactExportTokens = ["react.transitional.element", "useState", "cloneElement", "createElement"];
  const FocusableTokens = ['"flow-children"', "onActivate:", "focusClassName", "focusWithinClassName"];
  const DownloadProgressTokens = ["k_EAppUpdateProgress_Preallocating=", "k_EAppUpdateProgress_Download="];

  let installed = false;
  let runtime: any = null;
  let react: any = null;
  let focusable: any = null;
  let downloadIndex: number | undefined;
  let state = { key: null as string | null, dir: 1, busy: false };
  let lastError = "";

  const modules = () => getWebpackRuntime("download-sort");
  const sourceOf = (value) => {
    try {
      const fn = typeof value === "function" ? value : value && value.render ? value.render : null;
      return fn ? Function.prototype.toString.call(fn) : "";
    } catch {
      return "";
    }
  };

  const scan = () => {
    if (react && focusable && downloadIndex !== undefined) return;
    const resolve = modules().resolve;
    const found = resolve(ReactExportTokens);
    if (!found || !found.createElement || !found.useMemo || !found.version) throw new Error("React exports unavailable");
    const focusables = Object.values(resolve(FocusableTokens)).filter((value) => {
      const source = sourceOf(value);
      return (
        typeof value === "function" &&
        source.length < 1500 &&
        source.indexOf("class") !== 0 &&
        FocusableTokens.every((token) => source.includes(token))
      );
    });
    if (focusables.length !== 1) throw new Error("Focusable export is absent or ambiguous");
    const enums = Object.values(resolve(DownloadProgressTokens)).filter(
      (value: any) => value && typeof value === "object" && Number.isInteger(value.k_EAppUpdateProgress_Download),
    );
    if (enums.length !== 1) throw new Error("Download progress enum is absent or ambiguous");
    react = found;
    focusable = focusables[0];
    downloadIndex = (enums[0] as any).k_EAppUpdateProgress_Download;
  };

  // Bytes LEFT to download, not the total: the queue is about what is still coming down the wire.
  // Returns -1 for "Steam has not planned this app yet" (every bytes_total still 0, which is what a
  // freshly restarted client reports for a queued-but-not-started app) so those can be parked
  // instead of being ranked as the smallest download.
  const bytesLeft = (transfer) => {
    let total = 0;
    let done = 0;
    for (const info of transfer.update_type_info || []) {
      const progress = info.progress && info.progress[downloadIndex as number];
      if (!progress) continue;
      total += progress.bytes_total || 0;
      done += progress.bytes_in_progress || 0;
    }
    if (total <= 0) return -1;
    return Math.max(0, total - done);
  };
  const nameOf = (transfer) => {
    const overview = window.appStore && window.appStore.GetAppOverviewByAppID(transfer.appid);
    return (overview && overview.display_name ? overview.display_name : String(transfer.appid)).toLocaleLowerCase();
  };
  const kindOf = (transfer) => (transfer.buildid === 0 ? 0 : 1);
  // Direction is applied INSIDE each comparator: items with an unknown size must stay at the end in
  // both directions, which an outer sign flip cannot express.
  const keys = [
    { id: "name", label: "NAME", cmp: (a, b, dir) => dir * nameOf(a).localeCompare(nameOf(b)) },
    {
      id: "size",
      label: "SIZE",
      cmp: (a, b, dir) => {
        const x = bytesLeft(a);
        const y = bytesLeft(b);
        if (x < 0 && y < 0) return nameOf(a).localeCompare(nameOf(b));
        if (x < 0) return 1;
        if (y < 0) return -1;
        return dir * (x - y) || nameOf(a).localeCompare(nameOf(b));
      },
    },
    {
      id: "type",
      label: "TYPE",
      cmp: (a, b, dir) => dir * (kindOf(a) - kindOf(b)) || nameOf(a).localeCompare(nameOf(b)),
    },
  ];

  // The whole pending list, not just the running queue: scheduled and unqueued entries are part of
  // what the user sees on the page, so they are sorted in with everything else. Assigning them a
  // queue index is what dragging them into the queue does in Steam's own UI.
  const pendingTransfers = () => {
    const store = window.downloadsStore;
    if (!store) return [];
    const seen: Record<string, number> = {};
    const out: any[] = [];
    const add = (list) => {
      for (const transfer of list || []) {
        if (!transfer || transfer.completed) continue;
        if (seen[transfer.appid]) continue;
        seen[transfer.appid] = 1;
        out.push(transfer);
      }
    };
    add(store.QueuedTransfers);
    add(store.UnqueuedTransfers);
    add(store.ScheduledTransfers);
    // Stable starting point: queued entries keep their order, everything unqueued follows in the
    // order Steam listed it.
    return out.sort((a, b) => {
      const x = a.queue_index < 0 ? 1e9 : a.queue_index;
      const y = b.queue_index < 0 ? 1e9 : b.queue_index;
      return x - y;
    });
  };

  // The page has no log that reaches wsgm.log, so a run's refusals go to the host as the patch's one
  // command. Only a bridge replaced mid-run loses the report, and then the console is the one place
  // left to say so.
  const report = (refusal) => {
    const say = (error) => {
      console.warn(
        "WSGM download sort: Steam refused " +
          refusal.refused +
          " of " +
          refusal.total +
          " queue positions (first: " +
          refusal.first +
          "); the report did not reach WSGM: " +
          String((error && error.message) || error),
      );
    };
    try {
      request(patchId, "refused", refusal).catch(say);
    } catch (error) {
      say(error);
    }
  };

  // A best-effort repaint of the queue's storage-keyed list sections after the order changed under
  // them. The walk recurses on child and sibling alike, so depth counts every earlier sibling as
  // well; maxDepth keeps that recursion far inside the engine's stack. The download page has few
  // such sections, and maxUpdates stops the walk from repainting every storage-keyed component
  // elsewhere in the popup.
  const rerender = () => {
    const maxDepth = 500;
    const maxUpdates = 12;
    try {
      const manager = window.g_PopupManager;
      if (!manager) return;
      for (const popup of Array.from(manager.GetPopups()) as any[]) {
        const popupDocument = popup.m_popup && popup.m_popup.document;
        if (!popupDocument) continue;
        const row = popupDocument.querySelector("[data-rbd-draggable-id]");
        if (!row) continue;
        const fiberKey = Object.keys(row).filter((key) => key.indexOf("__reactFiber$") === 0)[0];
        if (!fiberKey) continue;
        let fiber = row[fiberKey];
        while (fiber.return) fiber = fiber.return;
        let seen = 0;
        const visit = (node, depth) => {
          if (!node || depth > maxDepth || seen > maxUpdates) return;
          const type = node.type;
          if (node.stateNode && typeof type === "function" && type.prototype && type.prototype.GetStorageKey) {
            try {
              node.stateNode.forceUpdate();
              seen++;
            } catch {
              // A section that cannot repaint now repaints on Steam's own next render.
            }
          }
          visit(node.child, depth + 1);
          visit(node.sibling, depth + 1);
        };
        visit(fiber, 0);
      }
    } catch {
      // Best effort: the order is already applied, and the page catches up on its own.
    }
  };

  const applySort = (keyId) => {
    const current = state;
    if (current.busy) return;
    current.dir = current.key === keyId ? -current.dir : 1;
    current.key = keyId;
    current.busy = true;
    rerender();
    const items = pendingTransfers();
    if (items.length < 2) {
      current.busy = false;
      rerender();
      return;
    }
    // Always renumber from 0: the list includes unqueued and scheduled entries whose queue_index is
    // -1, so seeding from items[0] could hand SetQueueIndex a negative index.
    const start = 0;
    const definition = keys.filter((key) => key.id === keyId)[0];
    const sorted = items.slice().sort((a, b) => definition.cmp(a, b, current.dir));
    // One SetQueueIndex per pace, as on the device pass (docs/steam-cef.md §12): a fifty-entry
    // re-queue takes about 6 s with the buttons dimmed. A rejected index does not stop the run; the
    // run ends by telling WSGM how many Steam refused.
    const paceMs = 120;
    let index = 0;
    let failed = 0;
    let firstError = "";
    const fail = (error) => {
      failed++;
      if (!firstError) firstError = String((error && error.message) || error);
    };
    const step = () => {
      if (index >= sorted.length) {
        current.busy = false;
        rerender();
        if (failed) report({ refused: failed, total: sorted.length, first: firstError });
        return;
      }
      try {
        const moved = window.SteamClient.Downloads.SetQueueIndex(
          sorted[index].appid,
          start + index,
          window.downloadsStore.CurrentViewingRemoteClientID,
        );
        if (moved && typeof moved.catch === "function") moved.catch(fail);
      } catch (error) {
        fail(error);
      }
      index++;
      setTimeout(step, paceMs);
    };
    step();
  };

  const sortBar = () => {
    const current = state;
    const children = [
      react.createElement(
        "span",
        { key: "cap", style: { fontSize: "11px", letterSpacing: ".5px", color: "#8ba6b8", marginRight: "2px" } },
        "SORT:",
      ),
    ];
    keys.forEach((key) => {
      const on = current.key === key.id;
      children.push(
        react.createElement(
          focusable,
          {
            key: key.id,
            onActivate: () => applySort(key.id),
            style: {
              font: "inherit",
              fontSize: "11px",
              letterSpacing: ".5px",
              lineHeight: "1",
              padding: "5px 9px",
              border: "1px solid " + (on ? "rgba(103,193,245,.55)" : "rgba(255,255,255,.18)"),
              borderRadius: "2px",
              background: on ? "rgba(103,193,245,.20)" : "rgba(255,255,255,.07)",
              color: on ? "#67c1f5" : "#c6d4df",
              cursor: "pointer",
              opacity: current.busy ? 0.5 : 1,
            },
          },
          key.label + (on ? (current.dir > 0 ? " ↑" : " ↓") : ""),
        ),
      );
    });
    return react.createElement(
      focusable,
      {
        key: "wsgm-sort",
        "flow-children": "row",
        style: { display: "flex", alignItems: "center", gap: "6px", flex: "0 0 auto", paddingLeft: "12px" },
      },
      children,
    );
  };

  // One element transform on the shared JSX-runtime claim: the queue header, and nothing else, comes
  // back inside a row with the sort bar. Undefined leaves every other element to the runtime.
  const transform = (create, type, props, key) => {
    if (!(props && props.sectionTitle === sectionToken && props.count !== undefined && props.labelId !== undefined)) {
      return undefined;
    }
    const header = create(type, { ...props, style: { ...props.style, flex: "1 1 auto", minWidth: 0 } }, key);
    // paddingRight matches the header's own 16px gutter so the bar lines up with the right edge of
    // the rows, not the window edge.
    return react.createElement(
      "div",
      { style: { display: "flex", alignItems: "center", width: "100%", paddingRight: "16px", boxSizing: "border-box" } },
      header,
      sortBar(),
    );
  };

  const install = () => {
    if (installed) return { ok: true, installed: true };
    try {
      scan();
      runtime = modules().resolve([...JsxRuntimeTokens]);
    } catch (error: any) {
      lastError = String((error && error.stack) || error);
      return { ok: false, error: lastError };
    }
    const registered = interceptElements(runtime, transformName, transform);
    if (!registered.ok) {
      lastError = registered.error || "element transform refused";
      return { ok: false, error: lastError };
    }
    installed = true;
    lastError = "";
    rerender();
    return { ok: true, installed: true };
  };

  // The transform is released before the gate forgets it is installed, so a failed release is
  // retried by the next remove rather than left in Steam behind an "absent" answer.
  const remove = () => {
    if (!installed) return { ok: true, absent: true };
    const released = releaseElements(runtime, transformName);
    if (!released.ok) {
      lastError = released.error || "download sort release failed";
      return { ok: false, error: lastError };
    }
    installed = false;
    runtime = null;
    state = { key: null, dir: 1, busy: false };
    rerender();
    return { ok: true, removed: true };
  };

  const status = () => ({
    ok: true,
    installed,
    registered: !!runtime && elementsIntercepted(runtime, transformName),
    sorting: state.busy,
    lastError,
  });

  return { install, remove, status, applySort };
}

registerGate("wsgmDownloadSort", createWsgmDownloadSort());
