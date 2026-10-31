// Notes presentation in Steam: the same structured pages the WSGM overlay edits, drawn in Big Picture and in
// a game's Steam overlay. NotesService owns every page, revision, image and screenshot; this page draws its
// state and sends exact commands carrying the revision they were made against. Nothing is stored in the
// page: a reload of Steam's frontend loses nothing and creates nothing.
const NotesPatchId = "wsgm.notes";
const NotesPageWidth = 1280;

const notesPalette = [
  ["#FFFFFF", "White"],
  ["#FFD93D", "Yellow"],
  ["#FF5A5A", "Red"],
  ["#5BD46B", "Green"],
  ["#4DA3FF", "Blue"],
  ["#111111", "Black"],
];
const notesWidths = [
  [3, "Fine"],
  [6, "Medium"],
  [12, "Bold"],
];

// The page's own layout: where the kit's elements go, not how they look.
const notesStyles = `
#wsgm-notes .wsgm-notes-list { display: flex; flex-direction: column; gap: 8px; }
#wsgm-notes .wsgm-notes-list .DialogButton { width: 100%; min-width: 0; height: auto; padding: 10px 14px; text-align: left; }
#wsgm-notes .wsgm-notes-row-title { font-size: 16px; font-weight: 600; color: #fff; }
#wsgm-notes .wsgm-notes-row-detail { font-size: 13px; color: #8b929a; overflow-wrap: anywhere; }
#wsgm-notes .wsgm-notes-surface { position: relative; width: 100%; overflow: hidden; border-radius: 6px; background: #1d2128; margin: 8px 0; }
#wsgm-notes .wsgm-notes-surface[data-mode="ink"] { touch-action: none; outline: 2px solid var(--gpSystemBlue, #1a9fff); }
#wsgm-notes .wsgm-notes-text { position: absolute; box-sizing: border-box; white-space: pre-wrap; overflow-wrap: anywhere; color: #fff; background: rgba(255,255,255,.06); border-radius: 6px; }
#wsgm-notes .wsgm-notes-image { position: absolute; object-fit: fill; user-select: none; -webkit-user-drag: none; }
#wsgm-notes .wsgm-notes-image-missing { position: absolute; display: flex; align-items: center; justify-content: center; color: #8b929a; background: rgba(255,255,255,.08); }
#wsgm-notes .wsgm-notes-selected { outline: 3px solid var(--gpSystemBlue, #1a9fff); }
#wsgm-notes .wsgm-notes-ink { position: absolute; left: 0; top: 0; width: 100%; height: 100%; pointer-events: none; }
#wsgm-notes .wsgm-notes-surface[data-mode="ink"] .wsgm-notes-ink { pointer-events: auto; }
#wsgm-notes .wsgm-notes-editor { width: 100%; min-height: 180px; box-sizing: border-box; font-size: 18px; resize: vertical; }
#wsgm-notes .wsgm-notes-swatch { display: inline-block; width: 14px; height: 14px; border-radius: 7px; margin-right: 6px; vertical-align: middle; border: 1px solid rgba(255,255,255,.4); }
#wsgm-notes .wsgm-notes-preview { height:100%; box-sizing:border-box; padding:24px; background:linear-gradient(145deg,#273340,#18212c); color:#dce4ec; font-size:18px; line-height:1.6; overflow:hidden; white-space:pre-wrap; overflow-wrap:anywhere; }
#wsgm-notes .wsgm-notes-preview p { margin:0; display:-webkit-box; -webkit-line-clamp:5; -webkit-box-orient:vertical; overflow:hidden; }
#wsgm-notes .wsgm-notes-preview small { display:block; color:#9aafc3; font-size:11px; letter-spacing:.12em; margin-bottom:16px; }
#wsgm-notes .wsgm-notes-canvas-scroll { height:clamp(180px,calc(100vh - 380px),620px); overflow:auto; overscroll-behavior:contain; border:1px solid rgba(255,255,255,.12); border-radius:8px; background:#1d2128; }
#wsgm-notes .wsgm-notes-canvas-scroll.ink { height:clamp(160px,calc(100vh - 460px),540px); }
#wsgm-notes .wsgm-notes-canvas-scroll .wsgm-notes-surface { margin:0; border-radius:0; }
#wsgm-notes .wsgm-notes-canvas-empty { position:absolute; top:100px; left:10%; right:10%; text-align:center; color:#9aafc3; font-size:18px; pointer-events:none; }
#wsgm-notes .wsgm-notes-canvas-empty strong { display:block; font-size:24px; color:#fff; margin-bottom:12px; }
#wsgm-notes .wsgm-notes-ink-options { display:flex; flex-wrap:wrap; gap:12px; align-items:end; }
#wsgm-notes .wsgm-notes-ink-options .steam-ui-kit-choice { flex:1 1 160px; min-width:0; }
#wsgm-notes .wsgm-notes-status { color:#9aa7b5; font-size:12px; margin-top:10px; }
`;

// The editor's revision-checked command sender. A refusal resolves to its reason; the host marks a refusal
// caused by a newer revision with a fixed prefix, which is how a conflict is told from any other refusal.
const NotesConflictPrefix = "Conflict: ";
const notesSend = (command: string, payload: any = {}) =>
  request(NotesPatchId, command, payload).then(
    (answer: any) => ({ ok: true, answer, error: "", conflict: false }),
    (error) => {
      const reason = String(error?.message ?? error ?? "The change was refused.");
      const conflict = reason.startsWith(NotesConflictPrefix);
      return {
        ok: false,
        answer: null,
        error: conflict ? reason.slice(NotesConflictPrefix.length) : reason,
        conflict,
      };
    },
  );

/**
 * The Steam window a page element is drawn in: Big Picture's main window or one game's overlay window.
 * @param element An element of the page.
 * @returns The window instance, or null when it cannot be told.
 */
const notesWindowOf = (element) => {
  const view = element?.ownerDocument?.defaultView;
  const windows = window.SteamUIStore?.WindowStore?.SteamUIWindows;
  if (!view || !Array.isArray(windows)) return null;
  return (
    windows.find((candidate) => {
      try {
        return candidate?.BrowserWindow === view;
      } catch {
        return false;
      }
    }) ?? null
  );
};

/**
 * What the page's browser can do, measured rather than assumed: Steam's CEF is older than current Chromium.
 * @param view The page's window object.
 * @returns The capabilities, including whether ink authoring is offered.
 */
const notesCapabilities = (view) => {
  const doc = view?.document;
  const pointer = typeof view?.PointerEvent === "function";
  const coalesced =
    pointer && typeof view.PointerEvent.prototype?.getCoalescedEvents === "function";
  const touchAction = !!view?.CSS?.supports?.("touch-action", "none");
  const svg = !!doc?.createElementNS?.("http://www.w3.org/2000/svg", "svg")?.createSVGPoint;
  const capture = pointer && typeof view.Element?.prototype?.setPointerCapture === "function";
  const resize = typeof view?.ResizeObserver === "function";
  return {
    pointer,
    coalesced,
    touchAction,
    svg,
    capture,
    resize,
    chrome: String(view?.navigator?.userAgent ?? "").match(/Chrome\/[\d.]+/)?.[0] ?? "unknown",
    ink: pointer && touchAction && svg && capture,
  };
};

// Steam's own virtual-keyboard input hook, the one its text fields and its Notes editor attach to their
// input elements: focus makes the element the keyboard's target and activation opens the keyboard in the
// window it belongs to, Big Picture or a game overlay. Found by its source, never by module id.
let notesKeyboardHook: any = undefined;
const notesResolveKeyboardHook = () => {
  if (notesKeyboardHook !== undefined) return notesKeyboardHook;
  try {
    notesKeyboardHook = getWebpackRuntime("notes").exported(
      ["TakeFocusAndShowKeyboard", "BIsElementValidForInput", "CreateVirtualKeyboardRef"],
      (value) => typeof value === "function" && value.length === 2,
    );
  } catch {
    notesKeyboardHook = null;
  }
  return notesKeyboardHook;
};

const notesContentHeight = (page) => {
  let bottom = (NotesPageWidth * 10) / 16;
  for (const element of page?.elements ?? [])
    bottom = Math.max(bottom, element.y + element.height + 240);
  for (const stroke of page?.strokes ?? []) {
    for (let index = 1; index < stroke.points.length; index += 2)
      bottom = Math.max(bottom, stroke.points[index] + 240);
  }
  return bottom;
};

const notesNewId = () => {
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (value) => value.toString(16).padStart(2, "0")).join("");
};

const notesDistance = (ax, ay, bx, by) => Math.hypot(ax - bx, ay - by);
const notesSegmentDistance = (px, py, ax, ay, bx, by) => {
  const dx = bx - ax;
  const dy = by - ay;
  const length = dx * dx + dy * dy;
  if (length <= 0) return notesDistance(px, py, ax, ay);
  const t = Math.max(0, Math.min(1, ((px - ax) * dx + (py - ay) * dy) / length));
  return notesDistance(px, py, ax + t * dx, ay + t * dy);
};

// The same stroke eraser as the overlay's: a touched stroke goes whole, so undo restores it whole.
const notesStrokesAt = (page, x, y, radius) => {
  const hits: string[] = [];
  for (const stroke of page?.strokes ?? []) {
    const reach = radius + stroke.width / 2;
    const p = stroke.points;
    if (p.length === 2) {
      if (notesDistance(x, y, p[0], p[1]) <= reach) hits.push(stroke.id);
      continue;
    }
    for (let index = 2; index + 1 < p.length; index += 2) {
      if (notesSegmentDistance(x, y, p[index - 2], p[index - 1], p[index], p[index + 1]) <= reach) {
        hits.push(stroke.id);
        break;
      }
    }
  }
  return hits;
};

// Drops points closer than a quarter of the stroke width, keeping the first and last.
const notesSimplify = (points: number[], width: number) => {
  const kept: number[] = [];
  const minimum = Math.max(0.5, width / 4);
  const count = points.length / 2;
  for (let index = 0; index < count; index++) {
    const x = points[index * 2];
    const y = points[index * 2 + 1];
    const last = index === count - 1;
    if (
      kept.length >= 2 &&
      !last &&
      notesDistance(x, y, kept[kept.length - 2], kept[kept.length - 1]) < minimum
    )
      continue;
    kept.push(Math.round(x * 10) / 10, Math.round(y * 10) / 10);
  }
  return kept;
};

const notesPolyline = (points: number[]) => {
  const parts: string[] = [];
  for (let index = 0; index + 1 < points.length; index += 2)
    parts.push(`${points[index]},${points[index + 1]}`);
  if (points.length === 2) parts.push(`${points[0] + 0.01},${points[1]}`);
  return parts.join(" ");
};

const notesFirstLine = (text) => {
  const line = String(text ?? "")
    .split("\n", 1)[0]
    .trim();
  return line.length === 0 ? "Empty text" : line.length > 80 ? `${line.slice(0, 80)}…` : line;
};

// Images arrive by request, one per asset, and live only in memory while their page is open.
/**
 * Draws one page: text blocks and images positioned on it, and the vector ink above them.
 * @param props Page, mode, tool settings, selection, image loader and the edit callbacks.
 * @returns The scaled page surface.
 */
function NotesSurface(props: any) {
  const react = props.ui.react;
  const h = react.createElement;
  const { page, mode, tool, color, width } = props;
  const images: Map<string, string | null> = props.images;
  const assets = [
    ...new Set<string>(
      page.elements.filter((element) => element.kind === "image").map((element) => element.asset),
    ),
  ]
    .sort()
    .join("|");
  const surfaceRef = react.useRef(null);
  const liveRef = react.useRef(null);
  const draw = react.useRef(null);
  const [scale, setScale] = react.useState(0.5);
  const [, setImagesRevision] = react.useState(0);
  const [drag, setDrag] = react.useState(null);
  react.useEffect(() => {
    const element = surfaceRef.current;
    const view = element?.ownerDocument?.defaultView;
    if (!element || typeof view?.ResizeObserver !== "function") return undefined;
    const observer = new view.ResizeObserver(() =>
      setScale(element.clientWidth / NotesPageWidth || 0.5),
    );
    observer.observe(element);
    setScale(element.clientWidth / NotesPageWidth || 0.5);
    return () => observer.disconnect();
  }, []);
  react.useEffect(() => {
    let live = true;
    const abort = new AbortController();
    const pending = new Set<string>();
    for (const element of page.elements) {
      if (element.kind !== "image" || images.has(element.asset)) continue;
      pending.add(element.asset);
      images.set(element.asset, null);
      void request(NotesPatchId, "image", { asset: element.asset }, undefined, abort.signal).then(
        (answer: any) => {
          if (!live) return;
          pending.delete(element.asset);
          images.set(element.asset, answer?.dataUrl ?? null);
          setImagesRevision((value) => value + 1);
        },
        () => {
          if (live) images.delete(element.asset);
        },
      );
    }
    return () => {
      live = false;
      abort.abort();
      for (const asset of pending) if (images.get(asset) === null) images.delete(asset);
    };
  }, [page.id, assets]);

  const height = notesContentHeight(page);
  const toPage = (event) => {
    const rect = surfaceRef.current.getBoundingClientRect();
    return [(event.clientX - rect.left) / scale, (event.clientY - rect.top) / scale];
  };
  const inkDown = (event) => {
    if (mode !== "ink" || !props.inkCapable) return;
    event.preventDefault();
    event.stopPropagation();
    event.currentTarget.setPointerCapture?.(event.pointerId);
    const [x, y] = toPage(event);
    if (tool === "eraser") {
      draw.current = { erase: new Set(notesStrokesAt(page, x, y, 14 / scale)) };
    } else {
      draw.current = { points: [x, y] };
    }
    if (liveRef.current) liveRef.current.setAttribute("points", notesPolyline([x, y]));
  };
  const inkMove = (event) => {
    const current = draw.current;
    if (!current) return;
    event.preventDefault();
    event.stopPropagation();
    // Every sample the digitizer produced since the last event, where the browser reports them.
    const samples =
      typeof event.getCoalescedEvents === "function" ? event.getCoalescedEvents() : [event];
    for (const sample of samples.length ? samples : [event]) {
      const [x, y] = toPage(sample);
      if (current.erase)
        for (const id of notesStrokesAt(page, x, y, 14 / scale)) current.erase.add(id);
      else current.points.push(x, y);
    }
    if (current.points && liveRef.current)
      liveRef.current.setAttribute("points", notesPolyline(current.points));
  };
  const inkUp = (event) => {
    const current = draw.current;
    draw.current = null;
    if (!current) return;
    event.preventDefault();
    event.stopPropagation();
    if (liveRef.current) liveRef.current.setAttribute("points", "");
    if (current.erase) {
      if (current.erase.size) props.onErase([...current.erase]);
      return;
    }
    const strokeWidth = tool === "highlighter" ? width * 3 : width;
    props.onStroke({
      id: notesNewId(),
      tool: tool === "highlighter" ? "highlighter" : "pen",
      color,
      width: strokeWidth,
      points: notesSimplify(current.points, strokeWidth),
      pressure: [],
    });
  };
  const elementDown = (event, element) => {
    if (mode === "ink") return;
    event.stopPropagation();
    if (mode === "text" && element.kind === "text") {
      props.onEditText(element);
      return;
    }
    props.onSelect(element.id);
    if (mode !== "navigate") return;
    event.currentTarget.setPointerCapture?.(event.pointerId);
    setDrag({
      id: element.id,
      startX: event.clientX,
      startY: event.clientY,
      x: element.x,
      y: element.y,
      moved: false,
    });
  };
  const elementMove = (event) => {
    if (!drag) return;
    const dx = (event.clientX - drag.startX) / scale;
    const dy = (event.clientY - drag.startY) / scale;
    setDrag({ ...drag, dx, dy, moved: drag.moved || Math.abs(dx) + Math.abs(dy) > 6 / scale });
  };
  const elementUp = (element) => {
    if (drag?.id === element.id && drag.moved) {
      props.onPlace(
        element,
        Math.max(0, drag.x + drag.dx),
        Math.max(0, drag.y + drag.dy),
        element.width,
        element.height,
      );
    }
    setDrag(null);
  };
  const surfaceClick = (event) => {
    if (mode !== "text" || event.target !== surfaceRef.current) return;
    const [x, y] = toPage(event);
    props.onAddText(x, y);
  };

  const children = page.elements.map((element) => {
    const moving = drag?.id === element.id && drag.moved;
    const x = moving ? Math.max(0, drag.x + drag.dx) : element.x;
    const y = moving ? Math.max(0, drag.y + drag.dy) : element.y;
    const style = {
      left: `${x * scale}px`,
      top: `${y * scale}px`,
      width: `${element.width * scale}px`,
      minHeight: element.kind === "text" ? `${element.height * scale}px` : undefined,
      height: element.kind === "image" ? `${element.height * scale}px` : undefined,
      fontSize: `${24 * scale}px`,
      padding: element.kind === "text" ? `${8 * scale}px ${12 * scale}px` : undefined,
      touchAction: mode === "navigate" ? "none" : undefined,
    };
    const common = {
      key: element.id,
      style,
      onPointerDown: (event) => elementDown(event, element),
      onPointerMove: elementMove,
      onPointerUp: () => elementUp(element),
      onPointerCancel: () => setDrag(null),
    };
    const selected = element.id === props.selected && mode !== "ink" ? " wsgm-notes-selected" : "";
    if (element.kind === "image") {
      const url = images.get(element.asset);
      return url
        ? h("img", {
            ...common,
            className: `wsgm-notes-image${selected}`,
            src: url,
            alt: "Screenshot",
            draggable: false,
          })
        : h(
            "div",
            { ...common, className: `wsgm-notes-image-missing${selected}` },
            url === null ? "Loading…" : "Image missing",
          );
    }
    // Plain text, never markup: React escapes it, so note content cannot run as web content.
    return h("div", { ...common, className: `wsgm-notes-text${selected}` }, element.text || " ");
  });

  return h(
    "div",
    {
      ref: surfaceRef,
      className: "wsgm-notes-surface",
      "data-mode": mode,
      style: { height: `${height * scale}px` },
      onClick: surfaceClick,
    },
    ...children,
    !page.elements.length && !page.strokes.length && mode !== "ink"
      ? h(
          "div",
          { className: "wsgm-notes-canvas-empty" },
          h("strong", null, "Your page starts here"),
          "Add text, draw in Ink mode or insert a game screenshot.",
        )
      : null,
    h(
      "svg",
      {
        className: "wsgm-notes-ink",
        viewBox: `0 0 ${NotesPageWidth} ${height}`,
        preserveAspectRatio: "none",
        onPointerDown: inkDown,
        onPointerMove: inkMove,
        onPointerUp: inkUp,
        onPointerCancel: () => {
          draw.current = null;
          liveRef.current?.setAttribute("points", "");
        },
      },
      ...page.strokes.map((stroke) =>
        h("polyline", {
          key: stroke.id,
          points: notesPolyline(stroke.points),
          fill: "none",
          stroke: stroke.color,
          strokeWidth: stroke.width,
          strokeOpacity: stroke.tool === "highlighter" ? 0.4 : 1,
          strokeLinecap: "round",
          strokeLinejoin: "round",
        }),
      ),
      h("polyline", {
        ref: liveRef,
        fill: "none",
        stroke: color,
        strokeWidth: tool === "highlighter" ? width * 3 : width,
        strokeOpacity: tool === "highlighter" ? 0.4 : 1,
        strokeLinecap: "round",
        strokeLinejoin: "round",
      }),
    ),
  );
}

/**
 * The multiline text editor: a textarea wired to Steam's own keyboard where the client exposes it, so the
 * controller's keyboard appears in the window the page is in. A physical keyboard types directly.
 * @param props Resolved components, the draft text and the save, discard and change callbacks.
 * @returns The editor level.
 */
function NotesTextEditor(props: any) {
  const ui = props.ui;
  const react = ui.react;
  const h = react.createElement;
  const hook = props.keyboardHook;
  const imperative = react.useRef(null);
  // The hook is chosen once per page install, so the hook order never changes between renders.
  const keyboardRef = hook ? hook({}, imperative) : null;
  const [text, setText] = react.useState(props.initial ?? "");
  const change = (value) => {
    setText(value);
    props.onChange(value);
  };
  return renderSteamUiLevel(
    ui,
    { bounded: true, onBack: props.onDiscard },
    renderSteamUiHeader(ui, {
      page: true,
      title: props.title,
      detail: "Enter starts a new line. Save keeps it on the page.",
    }),
    h(
      ui.focusable,
      {
        className: "DialogInput_Wrapper",
        onActivate: () => imperative.current?.TakeFocusAndShowKeyboard?.(),
        onOKActionDescription: "Type",
      },
      h("textarea", {
        ref: keyboardRef ?? undefined,
        className: "DialogTextInputBase wsgm-notes-editor",
        value: text,
        autoFocus: true,
        onChange: (event) => change(event?.target?.value ?? ""),
      }),
    ),
    renderSteamUiActions(
      ui,
      [
        { id: "save", label: "Save", primary: true, onClick: () => props.onSave(text) },
        { id: "discard", label: "Discard", onClick: props.onDiscard },
      ],
      { compact: true },
    ),
  );
}

/**
 * The capture sheet: the countdown choice and start, or the pending countdown and its cancel.
 * @param props Resolved components, published state, the page and close callback.
 * @returns The level.
 */
function NotesCaptureLevel({ ui, state, page, onBack }: any) {
  const react = ui.react;
  const h = react.createElement;
  const capture = state.capture;
  const [seconds, setSeconds] = react.useState(state.countdown);
  if (capture.phase !== "idle") {
    return renderSteamUiLevel(
      ui,
      { bounded: true, onBack },
      renderSteamUiHeader(ui, {
        page: true,
        title: "Screenshot pending",
        detail: capture.phase === "countdown" ? `${capture.remaining} s left` : "Capturing…",
      }),
      renderSteamUiActions(
        ui,
        [
          {
            id: "cancel",
            label: "Cancel screenshot",
            primary: true,
            onClick: () => void notesSend("cancelCapture"),
          },
        ],
        {
          compact: true,
        },
      ),
    );
  }
  const choices: any[] = [];
  for (let value = 5; value <= 10; value++) {
    choices.push({
      id: `s${value}`,
      label: `${value} s`,
      primary: value === seconds,
      onClick: () => setSeconds(value),
    });
  }
  return renderSteamUiLevel(
    ui,
    { bounded: true, onBack },
    renderSteamUiHeader(ui, { page: true, title: "Add screenshot", detail: page.title }),
    renderSteamUiBox(
      react,
      "How it works",
      h(
        "p",
        null,
        "Steam's overlay closes and the game comes back. Use the countdown to unpause, open the map or move the camera. The game's own window is captured when it ends, and this page opens again with the screenshot added.",
      ),
    ),
    !state.captureAvailable
      ? renderSteamUiEmpty(ui, {
          kind: "unavailable",
          text: "Screenshots are taken from a game's Steam overlay. Open Notes over a running game.",
        })
      : null,
    renderSteamUiToolbar(
      ui,
      renderSteamUiTool(
        ui,
        "Countdown",
        renderSteamUiActions(ui, choices, { compact: true }),
        true,
      ),
    ),
    renderSteamUiActions(
      ui,
      [
        {
          id: "start",
          label: "Start countdown",
          primary: true,
          disabled: !state.captureAvailable,
          onClick: () => void notesSend("capture", { pageId: page.id, seconds }).then(onBack),
        },
        { id: "cancel", label: "Cancel", onClick: onBack },
      ],
      { compact: true },
    ),
  );
}

/**
 * One page: its surface, mode and tool bars, the selection's actions and its element list.
 * @param props Resolved components, published state, the page, ink capability and navigation callbacks.
 * @returns The page level.
 */
function NotesPageLevel(props: any) {
  const { ui, state, page, inkCapable, keyboardHook, onBack } = props;
  const react = ui.react;
  const h = react.createElement;
  const [mode, setMode] = react.useState("navigate");
  const [tool, setTool] = react.useState("pen");
  const [color, setColor] = react.useState("#FFFFFF");
  const [width, setWidth] = react.useState(6);
  const [selected, setSelected] = react.useState(null);
  const [editor, setEditor] = react.useState(null);
  const [capturing, setCapturing] = react.useState(false);
  const [conflict, setConflict] = react.useState(null);
  const [panel, setPanel] = react.useState("");
  const viewport = react.useRef(null);
  const viewportWidth = react.useRef(640);
  const scroll = react.useRef(0);
  const undo = react.useRef([]);
  const redo = react.useRef([]);
  const images = react.useRef(new Map<string, string | null>());
  const [, setHistory] = react.useState(0);
  const readOnly = !!state.pageReadOnly;
  const revision = page.revision;

  react.useEffect(() => {
    // Leaving the page drops its images and its undo history.
    return () => {
      images.current.clear();
    };
  }, [page.id]);
  react.useEffect(() => {
    if (state.capture?.insertedElementId && state.capture.pageId === page.id)
      setSelected(state.capture.insertedElementId);
  }, [state.capture?.insertedElementId]);

  // Sends an edit made against `base`. A refusal on a page whose revision moved is a conflict: the page
  // already shows the newest version, and the caller decides whether to offer reapplying.
  const edit = async (command: string, payload: any, base: number, onConflict?: () => void) => {
    const result: any = await notesSend(command, { pageId: page.id, revision: base, ...payload });
    if (result.ok) {
      setConflict(null);
      return result;
    }
    if (result.conflict) {
      undo.current = [];
      redo.current = [];
      setHistory((value) => value + 1);
      if (onConflict) onConflict();
      else
        setConflict(
          "This page changed in the WSGM overlay. It now shows the newest version; nothing was overwritten.",
        );
    } else {
      setConflict(result.error);
    }
    return result;
  };
  const report = (result) => {
    if (!result.ok) setConflict(result.error);
  };
  const openPanel = (value) => {
    scroll.current = viewport.current?.scrollTop ?? scroll.current;
    setPanel(value);
  };
  const pushUndo = (step) => {
    undo.current = [...undo.current, step];
    redo.current = [];
    setHistory((value) => value + 1);
  };
  const applyStep = async (step, reverse) => {
    const remove = step.added === reverse;
    return remove
      ? edit("removeStrokes", { strokeIds: step.strokes.map((stroke) => stroke.id) }, revision)
      : edit(
          "restoreStrokes",
          {
            strokes: step.strokes,
            indices: step.indices ?? step.strokes.map((_, i) => page.strokes.length + i),
          },
          revision,
        );
  };
  const doUndo = async () => {
    const step = undo.current[undo.current.length - 1];
    if (!step) return;
    if ((await applyStep(step, true)).ok) {
      undo.current = undo.current.slice(0, -1);
      redo.current = [...redo.current, step];
      setHistory((value) => value + 1);
    }
  };
  const doRedo = async () => {
    const step = redo.current[redo.current.length - 1];
    if (!step) return;
    if ((await applyStep(step, false)).ok) {
      redo.current = redo.current.slice(0, -1);
      undo.current = [...undo.current, step];
      setHistory((value) => value + 1);
    }
  };
  const onStroke = async (stroke) => {
    const base = revision;
    const result = await edit("addStroke", { stroke }, base, () =>
      setConflict(
        "This page changed in the WSGM overlay while you drew. Your stroke was not saved; draw it again on the newest version.",
      ),
    );
    if (result.ok) pushUndo({ strokes: [stroke], indices: null, added: true });
  };
  const onErase = async (ids: string[]) => {
    const removed: any[] = [];
    const indices: number[] = [];
    page.strokes.forEach((stroke, index) => {
      if (ids.includes(stroke.id)) {
        removed.push(stroke);
        indices.push(index);
      }
    });
    if (!removed.length) return;
    const result = await edit("removeStrokes", { strokeIds: ids }, revision);
    if (result.ok) pushUndo({ strokes: removed, indices, added: false });
  };
  const place = (element, x, y, w, hgt) =>
    void edit("place", { elementId: element.id, x, y, width: w, height: hgt }, revision);
  const openEditor = (element) =>
    setEditor({
      elementId: element?.id ?? null,
      base: revision,
      text: element?.text ?? "",
      x: 40,
      y: 40,
      ...element?.at,
    });

  if (editor) {
    const save = async (text) => {
      const command = editor.elementId ? "setText" : "addText";
      const payload = editor.elementId
        ? { elementId: editor.elementId, text }
        : { x: editor.x, y: editor.y, text };
      const result = await edit(command, payload, editor.base, () =>
        setEditor({ ...editor, text, conflict: true }),
      );
      if (result.ok) {
        setEditor(null);
        if (result.answer?.id) setSelected(result.answer.id);
      }
    };
    if (editor.conflict) {
      const exists =
        !editor.elementId || page.elements.some((element) => element.id === editor.elementId);
      const actions: any[] = [];
      if (exists) {
        actions.push({
          id: "apply",
          label: editor.elementId ? "Replace with my text" : "Add my text to the newest version",
          primary: true,
          onClick: () => setEditor({ ...editor, base: revision, conflict: false, retry: true }),
        });
      }
      actions.push({
        id: "copy",
        label: "Add my text as a new block",
        onClick: () =>
          setEditor({ ...editor, elementId: null, base: revision, conflict: false, retry: true }),
      });
      actions.push({ id: "discard", label: "Discard my text", onClick: () => setEditor(null) });
      return renderSteamUiLevel(
        ui,
        { bounded: true, onBack: () => setEditor(null) },
        renderSteamUiHeader(ui, {
          page: true,
          title: "Changed in the WSGM overlay",
          detail: "Nothing was overwritten.",
        }),
        renderSteamUiBox(react, "Your text", h("p", null, notesFirstLine(editor.text))),
        renderSteamUiActions(ui, actions, { compact: true }),
      );
    }
    return h(NotesTextEditor, {
      key: `${editor.elementId ?? "new"}:${editor.base}:${editor.retry ? 1 : 0}`,
      ui,
      keyboardHook,
      title: editor.elementId ? "Edit text" : "New text",
      initial: editor.text,
      onChange: (text) => (editor.text = text),
      onSave: save,
      onDiscard: () => setEditor(null),
    });
  }
  if (capturing || state.capture?.phase !== "idle") {
    return h(NotesCaptureLevel, { ui, state, page, onBack: () => setCapturing(false) });
  }

  const element = page.elements.find((item) => item.id === selected) ?? null;
  const modeActions = [
    {
      id: "navigate",
      label: "Navigate",
      primary: mode === "navigate",
      onClick: () => setMode("navigate"),
    },
    {
      id: "text",
      label: "Text",
      primary: mode === "text",
      disabled: readOnly,
      onClick: () => setMode("text"),
    },
    {
      id: "ink",
      label: inkCapable ? "Ink" : "Ink (WSGM overlay only)",
      primary: mode === "ink",
      disabled: readOnly || !inkCapable,
      onClick: () => setMode("ink"),
    },
  ];
  if (!readOnly)
    modeActions.push(
      {
        id: "addText",
        label: "Add text",
        primary: false,
        disabled: false,
        onClick: () => openEditor(null),
      },
      {
        id: "capture",
        label: "Add screenshot…",
        primary: false,
        disabled: false,
        onClick: () => setCapturing(true),
      },
    );
  const toolbars: any[] = [renderSteamUiActions(ui, modeActions, { compact: true })];
  if (mode === "ink" && !readOnly) {
    const history = renderSteamUiActions(
      ui,
      [
        {
          id: "undo",
          label: "Undo",
          disabled: undo.current.length === 0,
          onClick: () => void doUndo(),
        },
        {
          id: "redo",
          label: "Redo",
          disabled: redo.current.length === 0,
          onClick: () => void doRedo(),
        },
        {
          id: "clear",
          label: "Clear ink…",
          disabled: page.strokes.length === 0,
          onClick: () =>
            showSteamUiConfirm(ui, {
              title: "Clear ink",
              text: "Remove every drawing on this page? Text and screenshots stay. Undo can bring it back.",
              confirmLabel: "Clear",
              onConfirm: () => {
                const strokes = page.strokes;
                void edit("clearInk", {}, revision).then((result) => {
                  if (result.ok)
                    pushUndo({ strokes, indices: strokes.map((_, i) => i), added: false });
                });
              },
            }),
        },
      ],
      { compact: true },
    );
    toolbars.push(
      h(
        ui.focusable,
        { className: "wsgm-notes-ink-options", "flow-children": "row" },
        renderSteamUiChoice(ui, {
          label: "Tool",
          selectedOption: tool,
          rgOptions: [
            { data: "pen", label: "Pen" },
            { data: "highlighter", label: "Highlighter" },
            { data: "eraser", label: "Eraser" },
          ],
          onChange: (option) => setTool(option.data),
        }),
        renderSteamUiChoice(ui, {
          label: "Width",
          selectedOption: width,
          rgOptions: notesWidths.map(([data, label]) => ({ data, label: String(label) })),
          onChange: (option) => setWidth(option.data),
        }),
        renderSteamUiChoice(ui, {
          label: "Color",
          selectedOption: color,
          rgOptions: notesPalette.map(([data, label]) => ({ data, label: String(label) })),
          onChange: (option) => {
            setColor(option.data);
            if (tool === "eraser") setTool("pen");
          },
        }),
        history,
      ),
    );
  }
  const pageActions = readOnly
    ? null
    : renderSteamUiActions(
        ui,
        [
          {
            id: "rename",
            label: "Rename…",
            onClick: () =>
              showSteamUiPrompt(ui, {
                title: "Rename page",
                label: "Title",
                initial: page.title,
                confirmLabel: "Rename",
                onConfirm: (title) => void edit("rename", { title }, revision),
              }),
          },
          {
            id: "pin",
            label: page.pinned ? "Unpin" : "Pin",
            onClick: () =>
              void notesSend("pin", { pageId: page.id, pinned: !page.pinned }).then(report),
          },
          {
            id: "moveUp",
            label: "Move page up",
            onClick: () => void notesSend("move", { pageId: page.id, delta: -1 }).then(report),
          },
          {
            id: "moveDown",
            label: "Move page down",
            onClick: () => void notesSend("move", { pageId: page.id, delta: 1 }).then(report),
          },
          {
            id: "deletePage",
            label: "Delete page…",
            onClick: () =>
              showSteamUiConfirm(ui, {
                title: "Delete page",
                text: `Delete “${page.title}”? This removes its text, drawings and screenshots and cannot be undone.`,
                confirmLabel: "Delete",
                onConfirm: () =>
                  void notesSend("deletePage", { pageId: page.id, revision }).then(report),
              }),
          },
        ],
        { compact: true },
      );
  const selection: any[] = [];
  if (element && !readOnly) {
    const nudge = (dx, dy, factor) =>
      place(
        element,
        Math.max(0, element.x + dx),
        Math.max(0, element.y + dy),
        element.width * factor,
        element.height * factor,
      );
    selection.push(
      renderSteamUiHeader(ui, {
        title: element.kind === "image" ? "Selected screenshot" : "Selected text",
        sub: true,
      }),
      renderSteamUiActions(
        ui,
        [
          ...(element.kind === "text"
            ? [
                {
                  id: "edit",
                  label: "Edit text",
                  primary: true,
                  onClick: () => openEditor(element),
                },
              ]
            : []),
          { id: "left", label: "◀", onClick: () => nudge(-40, 0, 1) },
          { id: "up", label: "▲", onClick: () => nudge(0, -40, 1) },
          { id: "down", label: "▼", onClick: () => nudge(0, 40, 1) },
          { id: "right", label: "▶", onClick: () => nudge(40, 0, 1) },
          { id: "smaller", label: "Smaller", onClick: () => nudge(0, 0, 0.85) },
          { id: "bigger", label: "Bigger", onClick: () => nudge(0, 0, 1.15) },
          {
            id: "front",
            label: "To front",
            onClick: () => void edit("raise", { elementId: element.id }, revision),
          },
          {
            id: "delete",
            label: element.kind === "image" ? "Delete screenshot" : "Delete text",
            onClick: () =>
              showSteamUiConfirm(ui, {
                title: element.kind === "image" ? "Delete screenshot" : "Delete text",
                text: "Remove this item? The rest of the page stays.",
                confirmLabel: "Delete",
                onConfirm: () =>
                  void edit("remove", { elementId: element.id }, revision).then((result) => {
                    if (result.ok) setSelected(null);
                  }),
              }),
          },
        ],
        { compact: true },
      ),
    );
  }
  const list = page.elements.map((item) =>
    h(
      ui.dialogButton,
      {
        key: item.id,
        onClick: () => {
          setSelected(item.id);
          scroll.current = (Math.max(0, item.y - 40) * viewportWidth.current) / NotesPageWidth;
          setPanel("");
        },
      },
      h(
        "div",
        { className: "wsgm-notes-row-title" },
        `${item.id === selected ? "● " : ""}${item.kind === "image" ? `Screenshot ${item.pixelWidth}×${item.pixelHeight}` : notesFirstLine(item.text)}`,
      ),
    ),
  );
  if (panel)
    return renderSteamUiLevel(
      ui,
      { bounded: true, onBack: () => setPanel("") },
      renderSteamUiHeader(ui, {
        page: true,
        title:
          panel === "contents"
            ? "Page contents"
            : panel === "selection"
              ? "Arrange selection"
              : "Page options",
        detail: page.title,
      }),
      panel === "contents"
        ? list.length
          ? h(ui.focusable, { className: "wsgm-notes-list", "flow-children": "column" }, ...list)
          : renderSteamUiEmpty(ui, {
              kind: "empty",
              text: "No text or images yet. Return to add text, ink or a screenshot.",
            })
        : panel === "selection"
          ? selection
          : pageActions,
      renderSteamUiActions(
        ui,
        [{ id: "back", label: "Back to page", onClick: () => setPanel("") }],
        { compact: true },
      ),
    );
  return renderSteamUiLevel(
    ui,
    { bounded: true, onBack },
    renderSteamUiHeader(ui, {
      page: true,
      title: page.title,
      detail: `${state.scopeTitle}${readOnly ? " · read-only (saved by a newer WSGM)" : ""}`,
    }),
    conflict
      ? renderSteamUiBanner(ui, { text: conflict, error: true, onDismiss: () => setConflict(null) })
      : null,
    ...toolbars,
    h(
      "div",
      {
        className: "wsgm-notes-canvas-scroll" + (mode === "ink" ? " ink" : ""),
        ref: (element) => {
          viewport.current = element;
          if (element) {
            viewportWidth.current = element.clientWidth || viewportWidth.current;
            element.scrollTop = scroll.current;
          }
        },
        onScroll: (event) => {
          scroll.current = event.currentTarget.scrollTop;
        },
      },
      h(NotesSurface, {
        ui,
        page,
        images: images.current,
        mode,
        tool,
        color,
        width,
        selected,
        inkCapable,
        onStroke: (stroke) => void onStroke(stroke),
        onErase: (ids) => void onErase(ids),
        onSelect: (id) => setSelected(id),
        onPlace: place,
        onEditText: (item) => openEditor(item),
        onAddText: (x, y) => setEditor({ elementId: null, base: revision, text: "", x, y }),
      }),
    ),
    renderSteamUiActions(
      ui,
      [
        { id: "pages", label: "All pages", onClick: onBack },
        {
          id: "contents",
          label: `Page contents (${page.elements.length})`,
          onClick: () => openPanel("contents"),
        },
        { id: "options", label: "Page options", onClick: () => openPanel("options") },
        {
          id: "pageUp",
          label: "Page up",
          onClick: () =>
            viewport.current?.scrollBy({
              top: -Math.max(120, viewport.current.clientHeight * 0.8),
            }),
        },
        {
          id: "pageDown",
          label: "Page down",
          onClick: () =>
            viewport.current?.scrollBy({ top: Math.max(120, viewport.current.clientHeight * 0.8) }),
        },
        ...(element && !readOnly
          ? [
              ...(element.kind === "text"
                ? [
                    {
                      id: "editSelected",
                      label: "Edit selected text",
                      onClick: () => openEditor(element),
                    },
                  ]
                : []),
              { id: "arrange", label: "Arrange selection", onClick: () => openPanel("selection") },
            ]
          : []),
      ],
      { compact: true },
    ),
    h(
      "div",
      { className: "wsgm-notes-status" },
      `Saved on this PC · ${new Date(page.modified).toLocaleString()}`,
    ),
  );
}

/**
 * The notebook: its pages, a new page, and switching to Global or another game's notebook.
 * @param props Resolved components and published state.
 * @returns The notebook pane; its collection puts the focus back on the page it was left from.
 */
function NotesNotebookLevel({ ui, state }: any) {
  const h = ui.react.createElement;
  const game = state.game;
  const detail =
    state.scope === "global"
      ? game.state === "unknown"
        ? `Global notes. ${game.detail ?? ""} Nothing is saved to a game's notebook until it is known.`
        : game.state === "game"
          ? `Global notes. ${game.title} has its own notebook.`
          : "Global notes, not tied to any game."
      : state.scope === game.scope
        ? `Notes for ${game.title}, the game running now.`
        : "Notes for this game.";
  const create = () => void notesSend("create", { scope: state.scope });
  const card = (page) =>
    renderSteamUiCard(ui, {
      key: page.id,
      title: page.title,
      media: h(
        "div",
        { className: "wsgm-notes-preview" },
        h("small", null, page.readOnly ? "READ ONLY" : "NOTE PAGE"),
        h(
          "p",
          null,
          page.preview ||
            (page.images
              ? "Screenshots and annotations"
              : page.strokes
                ? "Handwritten notes"
                : "A fresh page for your next idea"),
        ),
      ),
      badge: page.pinned ? { text: "Pinned" } : page.readOnly ? { text: "Read only" } : null,
      meta: [
        [
          page.texts ? `${page.texts} text ${page.texts === 1 ? "block" : "blocks"}` : "",
          page.images ? `${page.images} ${page.images === 1 ? "image" : "images"}` : "",
          page.strokes ? "Ink" : "",
        ]
          .filter(Boolean)
          .join(" · ") || "Empty page",
        "Edited " + new Date(page.modified).toLocaleDateString(undefined, { dateStyle: "medium" }),
      ],
      onActivate: () => void notesSend("openPage", { pageId: page.id }),
    });
  return renderSteamUiPane(
    ui,
    { key: "notebook", bounded: true },
    renderSteamUiHeader(ui, { page: true, title: "Notes", detail }),
    renderSteamUiCollection(ui, {
      // One memory per notebook, so the focus returns to the page that was open in it.
      id: `wsgm-notes:${state.scope}`,
      items: state.pages,
      itemKey: (page) => page.id,
      render: card,
      header: renderSteamUiChoice(ui, {
        label: "Notebook",
        selectedOption: state.scope,
        rgOptions: state.notebooks.map((book) => ({
          data: book.scope,
          label: `${book.title}${book.scope === game.scope ? " · Running now" : ""} · ${book.pages} ${book.pages === 1 ? "page" : "pages"}`,
        })),
        onChange: (option) => void notesSend("selectScope", { scope: option.data }),
      }),
      actions: [{ id: "create", label: "New page", primary: true, onClick: create }],
      empty: {
        title: "Your notebook starts here",
        text: `Create a page in ${state.scopeTitle} for text, drawings and game screenshots. Changes stay on this PC.`,
        action: { label: "New page", onClick: create },
      },
    }),
  );
}

/**
 * Renders Notes from host state: the notebook, its page editor, and the capture and conflict levels.
 * @param props The frame's context and the host's published state.
 * @returns The level on screen, in an element that tells which Steam window the page is drawn in.
 */
function NotesPage({ context, state }: any) {
  const react = context.react();
  const h = react.createElement;
  const ui = context.ui();
  const rootRef = react.useRef(null);
  const [capabilities, setCapabilities] = react.useState(null);
  react.useEffect(() => {
    // Which window this page is in decides its game: a game's overlay names its own app and process.
    const instance = notesWindowOf(rootRef.current);
    const overlay = instance?.IsGamepadUIOverlayWindow?.() === true;
    const info = overlay ? instance.params?.browserInfo : null;
    const found = notesCapabilities(rootRef.current?.ownerDocument?.defaultView);
    setCapabilities(found);
    void notesSend("open", {
      appId: Number.isInteger(info?.m_unAppID) ? info.m_unAppID : 0,
      processId: Number.isInteger(info?.m_unPID) ? info.m_unPID : 0,
    });
    void notesSend("report", { capabilities: JSON.stringify({ ...found, overlay }) });
  }, []);
  let body;
  if (!state.loaded) {
    // The host reads Notes only once the page has said it opened.
    body = renderSteamUiEmpty(ui, { kind: "loading", text: "Opening your notes…" });
  } else if (state.page) {
    body = h(NotesPageLevel, {
      key: state.page.id,
      ui,
      state,
      page: state.page,
      inkCapable: !!capabilities?.ink,
      keyboardHook: notesResolveKeyboardHook(),
      onBack: () => void notesSend("closePage"),
    });
  } else {
    body = h(NotesNotebookLevel, {
      ui,
      state,
    });
  }
  return h("div", { ref: rootRef, className: "wsgm-notes-root" }, body);
}

// The host's message: an error, a notice, or how the last screenshot ended.
const notesBanner = (state) =>
  state.error
    ? { text: state.error, error: true }
    : state.notice
      ? { text: state.notice }
      : state.capture?.phase === "idle" && state.capture.outcome
        ? { text: state.capture.outcome, error: !!state.capture.outcomeIsError }
        : null;

const notesPage = registerSteamPage({
  template: "notes",
  gate: "notes",
  patchId: NotesPatchId,
  title: "Notes",
  id: "wsgm-notes",
  style: notesStyles,
  loading: "Opening your notes…",
  banner: notesBanner,
  status: () => ({
    loaded: !!notesPage.state()?.loaded,
    keyboard: notesKeyboardHook ? "hook" : notesKeyboardHook === null ? "none" : "unresolved",
  }),
  Page: NotesPage,
});
