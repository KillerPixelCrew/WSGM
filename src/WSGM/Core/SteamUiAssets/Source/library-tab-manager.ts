// Library Tab Manager presentation. Tab validation, placement, the strip order's writes and Steam's
// synchronization belong to LibraryManagerService; this page edits a draft of one tab and sends
// the whole tab back, and moves the strip one press at a time.
const LibraryTabsPatchId = "wsgm.library-tabs";

// The page's own layout: where the kit's elements go, not how they look.
const libraryTabsStyles = `
#wsgm-library-tabs .wsgm-tabs-list, #wsgm-library-tabs .steam-ui-kit-list { display: flex; flex-direction: column; gap: 8px; }
#wsgm-library-tabs .wsgm-tabs-list .DialogButton, #wsgm-library-tabs .steam-ui-kit-list .DialogButton { width: 100%; min-width: 0; height: auto; padding: 10px 14px; }
#wsgm-library-tabs .wsgm-tabs-stepper { display: flex; align-items: center; gap: 8px; }
#wsgm-library-tabs .wsgm-tabs-stepper span { min-width: 64px; text-align: center; }
#wsgm-library-tabs .steam-ui-kit-tool:not(.grow) { width: 200px; }
`;

const libraryTabsPageSize = 200;

/**
 * A fresh filter of one kind, every field in the shape the host publishes and reads back.
 * @param kind The FilterKind name.
 * @returns The node; a merge group starts with one Installed child, as the overlay's does.
 */
const libraryTabsNewFilter = (kind: string): any => ({
  kind,
  inverted: false,
  mode: "And",
  children: kind === "Merge" ? [libraryTabsNewFilter("Installed")] : [],
  collectionId: "",
  boolValue: true,
  tagIds: [],
  appIds: [],
  pattern: "",
  platform: "Steam",
  threshold: 0,
  condition: "Above",
  scoreType: "SteamPercent",
  units: "Hours",
  daysAgo: 0,
  year: 0,
  month: 0,
  day: 0,
  cardScope: "Inserted",
  contentId: "",
});

// A new tab, with no id: the host assigns one and its place in the list when it is saved.
const libraryTabsNewTab = () => ({
  id: "",
  name: "",
  enabled: true,
  position: 0,
  categories: 1,
  filterTree: { ...libraryTabsNewFilter("Merge"), children: [] },
});

// A hint while editing. The host's validation is the authority: it also refuses a title pattern
// its evaluator cannot run.
const libraryTabsIncomplete = (node): boolean => {
  switch (node.kind) {
    case "Collection":
      return !node.collectionId;
    case "Tag":
      return !(node.tagIds ?? []).length;
    case "Regex":
      return !node.pattern;
    case "Whitelist":
    case "Blacklist":
      return !(node.appIds ?? []).length;
    case "ReleaseDate":
    case "LastPlayed":
      return !(node.daysAgo > 0 || node.year > 0);
    case "SdCard":
      return node.cardScope === "Specific" && !node.contentId;
    case "Merge":
      return !(node.children ?? []).length || node.children.some(libraryTabsIncomplete);
    default:
      return false;
  }
};

const libraryTabsKindLabel = (state, kind: string) =>
  (state.filterKinds ?? []).find((entry) => entry.kind === kind)?.label ?? kind;

const libraryTabsCondition = (node) => (node.condition === "Above" ? "≥" : "<");

/**
 * One line naming a filter, the way the overlay's editor lists it.
 * @param state Published state, for tag, collection and card names.
 * @param node The filter.
 * @returns The description, led by NOT when inverted.
 */
const libraryTabsDescribe = (state, node): string => {
  const canInvert = (state.filterKinds ?? []).find((entry) => entry.kind === node.kind)?.canInvert;
  const prefix = node.inverted && canInvert ? "NOT " : "";
  const cardName = (id) => (state.cards ?? []).find((card) => card.contentId === id)?.name ?? "a card";
  const collectionName = (id) => (state.collections?.items ?? []).find((c) => c.id === id)?.name ?? "selected";
  const number = (value) => Number(value ?? 0).toLocaleString(undefined, { maximumFractionDigits: 2 });
  let text: string;
  switch (node.kind) {
    case "Tag":
      text =
        node.tagIds?.length === 1
          ? ((state.tags?.items ?? []).find((tag) => tag.tagId === node.tagIds[0])?.name ?? "Tag")
          : `Tags (${node.tagIds?.length ?? 0})`;
      break;
    case "Installed":
      text = node.boolValue ? "Installed" : "Not installed";
      break;
    case "Collection":
      text = collectionName(node.collectionId);
      break;
    case "Regex":
      text = `Title ~ ${node.pattern}`;
      break;
    case "SdCard":
      text = node.cardScope === "Specific" ? `On ${cardName(node.contentId)}` : node.cardScope === "Any" ? "On any card" : "On inserted card";
      break;
    case "TimePlayed":
      text = `Playtime ${libraryTabsCondition(node)} ${number(node.threshold)}`;
      break;
    case "SizeOnDisk":
      text = `Size ${libraryTabsCondition(node)} ${number(node.threshold)} GB`;
      break;
    case "ReviewScore":
      text = `Score ${libraryTabsCondition(node)} ${Math.round(node.threshold ?? 0)}`;
      break;
    case "ReleaseDate":
      text = `Released ${node.daysAgo > 0 ? `< ${node.daysAgo}d ago` : "date"}`;
      break;
    case "LastPlayed":
      text = `Played ${node.daysAgo > 0 ? `< ${node.daysAgo}d ago` : "date"}`;
      break;
    case "Platform":
      text = node.platform === "Steam" ? "Steam" : "Non-Steam";
      break;
    case "Whitelist":
      text = `Whitelist (${node.appIds?.length ?? 0})`;
      break;
    case "Blacklist":
      text = `Blacklist (${node.appIds?.length ?? 0})`;
      break;
    case "Merge":
      text = `Group (${node.children?.length ?? 0})`;
      break;
    default:
      text = String(node.kind);
      break;
  }
  return prefix + text;
};

// The category prefilter choices the overlay offers, as a bitfield: Games, +Software, +Hidden.
const libraryTabsCategoryChoices = (current: number) => {
  const games = 1;
  const software = games | 2;
  const hidden = software | 16;
  const label = (value: number) => {
    const parts: string[] = [];
    if (value & 1) parts.push("Games");
    if (value & 2) parts.push("Software");
    if (value & 8192) parts.push("Music");
    if (!parts.length) parts.push("Games");
    if (value & 16) parts.push("+Hidden");
    return parts.join(", ");
  };
  const choices = [games, software, hidden].map((value) => ({ value, label: label(value) }));
  if (!choices.some((choice) => choice.value === current)) choices.push({ value: current, label: label(current) });
  return choices;
};

// A value with − and + beside it, the overlay's stepper: a slider has no place for a count of days.
const libraryTabsStepper = (ui, key: string, label: string, value: number, min: number, max: number, step: number, onChange) => {
  const h = ui.react.createElement;
  const clamp = (next: number) => Math.min(max, Math.max(min, next));
  return h(ui.valueField, {
    key,
    name: label,
    focusable: false,
    value: h(
      ui.focusable,
      { className: "wsgm-tabs-stepper", "flow-children": "row" },
      h(ui.smallButton, { onClick: () => onChange(clamp(value - step)), disabled: value <= min }, "−"),
      h("span", null, Number(value).toLocaleString(undefined, { maximumFractionDigits: 2 })),
      h(ui.smallButton, { onClick: () => onChange(clamp(value + step)), disabled: value >= max }, "+"),
    ),
  });
};

const libraryTabsLevel = (ui, onBack, title: string, detail: string | undefined, ...children) =>
  renderSteamUiLevel(
    ui,
    { bounded: true, onBack },
    renderSteamUiHeader(ui, { page: true, title, detail }),
    ...children,
  );

/**
 * The root level: the builder's tabs, the way to the strip order and a new tab.
 * @param props Resolved components, published state and the open callbacks.
 * @returns The list pane; the collection puts the focus back on the tab it was left from.
 */
function LibraryTabsList({ ui, state, onNew, onOrder, onEdit }: any) {
  const tabs = state.tabs ?? [];
  return renderSteamUiPane(
    ui,
    { key: "list", bounded: true },
    renderSteamUiHeader(ui, {
      page: true,
      title: "Library Tab Manager",
      detail: "Tabs appear in Steam's library and update automatically as you make changes.",
    }),
    renderSteamUiCollection(ui, {
      id: "wsgm-library-tabs:list",
      layout: "list",
      items: tabs,
      itemKey: (tab) => tab.id,
      render: (tab) =>
        renderSteamUiSelectRow(ui, {
          key: tab.id,
          title: tab.name || "(unnamed)",
          detail: tab.enabled ? `${tab.filterTree?.children?.length ?? 0} filters` : "disabled",
          onClick: () => onEdit(tab),
        }),
      actions: [
        { id: "new", label: "New tab", primary: true, onClick: onNew },
        { id: "order", label: "Tab order & Steam tabs", onClick: onOrder },
      ],
      loading: !state.loaded,
      loadingText: "Reading your tabs…",
      empty: {
        title: "Make the library your own",
        text: "Create a tab from filters such as installed games, collections or SD cards. Use Tab order & Steam tabs to arrange the library strip.",
        action: { label: "New tab", onClick: onNew },
      },
    }),
  );
}

/**
 * The strip, top to bottom here for left to right in Steam, with a move and a hide/show per row.
 * @param props Resolved components, published state and the back callback.
 * @returns The nested level; Back returns to the list.
 */
function LibraryTabsOrder({ ui, state, act, onBack }: any) {
  const react = ui.react;
  const h = react.createElement;
  const order = state.order ?? [];
  const kindLabel = (entry) => (entry.kind === "native" ? "Steam tab" : entry.kind === "card" ? "Card tab" : "Custom tab");
  // Rows are keyed by the tab, so a move keeps the pressed button under the finger while the row
  // takes its new place.
  const rows = order.map((entry, index) =>
    h(ui.valueField, {
      key: entry.key,
      name: entry.title,
      description: entry.hidden ? `${kindLabel(entry)} · hidden` : kindLabel(entry),
      focusable: false,
      value: renderSteamUiActions(ui, [
        { id: "up", label: "Move up", disabled: index === 0 || !!state.busy,
          onClick: () => void act("moveTab", { key: entry.key, delta: -1 }) },
        { id: "down", label: "Move down", disabled: index === order.length - 1 || !!state.busy,
          onClick: () => void act("moveTab", { key: entry.key, delta: 1 }) },
        ...(entry.kind === "native" ? [{ id: "visibility", label: entry.hidden ? "Show" : "Hide",
          disabled: !!state.busy,
          onClick: () => void act("setNativeHidden", { key: entry.key, hidden: !entry.hidden }) }] : [])
      ], { compact: true }),
    }),
  );
  return libraryTabsLevel(
    ui,
    onBack,
    "Tab order",
    "Top to bottom here is left to right in Steam. Steam's own tabs can be hidden; WSGM tabs disappear when disabled in their editors.",
    order.length
      ? h(ui.settingsSection, { label: "Strip" }, ...rows)
      : renderSteamUiEmpty(ui, { kind: "loading", text: "Reading Steam's library strip…" }),
    renderSteamUiActions(ui, [{ id: "back", label: "Done", onClick: onBack }], { compact: true }),
  );
}

/**
 * The filter type list, for a new filter, a replacement or a group's child.
 * @param props Resolved components, published state, whether a group excludes nesting, the pick and back callbacks.
 * @returns The nested level.
 */
function LibraryTabsFilterPicker({ ui, state, inGroup, onPick, onBack }: any) {
  const h = ui.react.createElement;
  const kinds = (state.filterKinds ?? []).filter((kind) => !inGroup || kind.kind !== "Merge");
  return libraryTabsLevel(
    ui,
    onBack,
    inGroup ? "Add to group" : "Add filter",
    undefined,
    h(
      ui.focusable,
      { className: "wsgm-tabs-list", "flow-children": "column" },
      ...kinds.map((kind) =>
        renderSteamUiSelectRow(ui, { key: kind.kind, title: kind.label, detail: kind.description, onClick: () => onPick(kind.kind) }),
      ),
    ),
  );
}

/**
 * Picks several of a list, with a search and the overlay's page size, as the tag and game pickers do.
 * @param props Resolved components, the title, the published picker, how an item is labelled, the selected ids and the done callback.
 * @returns The nested level; Done and Back both keep the selection, which lives on the draft.
 */
function LibraryTabsMultiSelect({ ui, title, picker, labelOf, idOf, selected, onToggle, onRetry, onBack }: any) {
  const react = ui.react;
  const h = react.createElement;
  const [search, setSearch] = react.useState("");
  const [page, setPage] = react.useState(0);
  const items = (picker?.items ?? []).filter((item) => !search || labelOf(item).toLowerCase().includes(search.toLowerCase()));
  const pageCount = Math.max(1, Math.ceil(items.length / libraryTabsPageSize));
  const current = Math.min(page, pageCount - 1);
  const shown = items.slice(current * libraryTabsPageSize, (current + 1) * libraryTabsPageSize);
  const body = picker?.loading && !items.length
    ? renderSteamUiEmpty(ui, { kind: "loading", text: "Reading from Steam…" })
    : picker?.error && !items.length
      ? renderSteamUiEmpty(ui, { kind: "error", text: picker.error, action: { label: "Try again", onClick: onRetry } })
      : !items.length
        ? search
          ? renderSteamUiEmpty(ui, { kind: "noMatch", text: "Nothing matches." })
          : renderSteamUiEmpty(ui, { kind: "empty", text: "Nothing to pick from." })
        : h(
            ui.settingsSection,
            { label: `${selected.size} selected` },
            ...shown.map((item) =>
              h(ui.toggleField, {
                key: String(idOf(item)),
                label: labelOf(item),
                controlled: true,
                checked: selected.has(idOf(item)),
                onChange: () => onToggle(idOf(item)),
              }),
            ),
          );
  return libraryTabsLevel(
    ui,
    onBack,
    title,
    picker?.error && items.length ? picker.error : undefined,
    renderSteamUiToolbar(
      ui,
      renderSteamUiTool(
        ui,
        "Search",
        h(ui.textField, { value: search, onChange: (event) => { setSearch(event?.target?.value ?? ""); setPage(0); } }),
        true,
      ),
    ),
    body,
    renderSteamUiActions(ui, [
      ...(pageCount > 1
        ? [
            { id: "prev", label: `Page ${current + 1} of ${pageCount} · previous`, disabled: current === 0, onClick: () => setPage(current - 1) },
            { id: "next", label: "Next page", disabled: current + 1 >= pageCount, onClick: () => setPage(current + 1) },
          ]
        : []),
      { id: "done", label: "Done", primary: true, onClick: onBack },
    ], { compact: true }),
  );
}

/**
 * Picks one collection.
 * @param props Resolved components, the published picker, the pick and back callbacks.
 * @returns The nested level.
 */
function LibraryTabsCollectionPicker({ ui, picker, onPick, onRetry, onBack }: any) {
  const h = ui.react.createElement;
  const items = picker?.items ?? [];
  return libraryTabsLevel(
    ui,
    onBack,
    "Collection",
    picker?.error && items.length ? picker.error : undefined,
    picker?.loading && !items.length
      ? renderSteamUiEmpty(ui, { kind: "loading", text: "Reading from Steam…" })
      : picker?.error && !items.length
        ? renderSteamUiEmpty(ui, { kind: "error", text: picker.error, action: { label: "Try again", onClick: onRetry } })
        : !items.length
          ? renderSteamUiEmpty(ui, { kind: "empty", text: "No collections found — is Steam open?" })
          : h(
            ui.focusable,
            { className: "wsgm-tabs-list", "flow-children": "column" },
            ...items.map((item) =>
              renderSteamUiSelectRow(ui, { key: item.id, title: item.name, detail: `${item.gameCount} games`, onClick: () => onPick(item.id) }),
            ),
          ),
  );
}

/**
 * One filter's parameters, its NOT toggle and, for a group, its children.
 * @param props Resolved components, published state, the node, its group when nested, a redraw, and the navigation callbacks.
 * @returns The nested level; Done and Back both keep the edits, which live on the draft.
 */
function LibraryTabsFilterEditor({ ui, state, node, group, touch, onPickTags, onPickCollection, onPickGames, onChangeType, onAddChild, onEditChild, onRemove, onBack }: any) {
  const react = ui.react;
  const h = react.createElement;
  const set = (field: string, value) => {
    node[field] = value;
    touch();
  };
  const choice = (key: string, label: string, choices: { value: any; label: string }[], current, apply) =>
    renderSteamSettingRow(
      ui,
      { key, kind: "choice", label, text: current, choices },
      undefined,
      (_row, value) => apply(value),
      () => {},
    );
  const conditionRow = () =>
    choice("condition", "Condition", [{ value: "Above", label: "At or above" }, { value: "Below", label: "Below" }], node.condition, (v) => set("condition", v));
  const rows: any[] = [];
  switch (node.kind) {
    case "Installed":
      rows.push(choice("state", "State", [{ value: false, label: "Not installed" }, { value: true, label: "Installed" }], !!node.boolValue, (v) => set("boolValue", !!v)));
      break;
    case "Platform":
      rows.push(choice("platform", "Platform", [{ value: "Steam", label: "Steam" }, { value: "NonSteam", label: "Non-Steam" }], node.platform, (v) => set("platform", v)));
      break;
    case "Regex":
      rows.push(
        renderSteamSettingRow(
          ui,
          { key: "pattern", kind: "text", label: "Title pattern", text: node.pattern ?? "" },
          node.pattern ?? "",
          (_row, value) => set("pattern", String(value ?? "")),
          () => {},
        ),
      );
      break;
    case "Tag":
      rows.push(
        renderSteamSettingRow(
          ui,
          { key: "tags", kind: "action", label: "Tags", description: node.tagIds?.length ? `${node.tagIds.length} selected` : "Choose one or more", buttonLabel: "Choose…" },
          undefined,
          () => {},
          onPickTags,
        ),
      );
      rows.push(choice("tagMode", "Match", [{ value: "And", label: "All tags (AND)" }, { value: "Or", label: "Any tag (OR)" }], node.mode, (v) => set("mode", v)));
      break;
    case "Collection":
      rows.push(
        renderSteamSettingRow(
          ui,
          { key: "collection", kind: "action", label: "Collection", description: node.collectionId ? libraryTabsDescribe(state, { ...node, inverted: false }) : "Choose one", buttonLabel: "Choose…" },
          undefined,
          () => {},
          onPickCollection,
        ),
      );
      break;
    case "Whitelist":
    case "Blacklist":
      rows.push(
        renderSteamSettingRow(
          ui,
          { key: "games", kind: "action", label: "Games", description: node.appIds?.length ? `${node.appIds.length} selected` : "Choose games", buttonLabel: "Choose…" },
          undefined,
          () => {},
          onPickGames,
        ),
      );
      break;
    case "ReviewScore":
      rows.push(choice("source", "Source", [{ value: "SteamPercent", label: "Steam %" }, { value: "Metacritic", label: "Metacritic" }], node.scoreType, (v) => set("scoreType", v)));
      rows.push(conditionRow());
      rows.push(libraryTabsStepper(ui, "score", "Score", node.threshold ?? 0, 0, 100, 5, (v) => set("threshold", v)));
      break;
    case "TimePlayed":
      rows.push(conditionRow());
      rows.push(choice("units", "Units", [{ value: "Minutes", label: "Minutes" }, { value: "Hours", label: "Hours" }, { value: "Days", label: "Days" }], node.units, (v) => set("units", v)));
      rows.push(libraryTabsStepper(ui, "amount", "Amount", node.threshold ?? 0, 0, 1000, 1, (v) => set("threshold", v)));
      break;
    case "SizeOnDisk":
      rows.push(conditionRow());
      rows.push(libraryTabsStepper(ui, "size", "Size (GB)", node.threshold ?? 0, 0, 2000, 5, (v) => set("threshold", v)));
      break;
    case "ReleaseDate":
    case "LastPlayed":
      rows.push(conditionRow());
      rows.push(
        libraryTabsStepper(ui, "days", "Days ago", node.daysAgo ?? 0, 0, 3650, 30, (v) => {
          node.daysAgo = v;
          node.year = 0;
          touch();
        }),
      );
      rows.push(renderSteamSettingRow(ui, { key: "daysNote", kind: "note", label: "0 days = use no date", text: "Absolute dates: edit the configuration file." }, undefined, () => {}, () => {}));
      break;
    case "SdCard": {
      const cards = (state.cards ?? []).map((card) => ({ value: `Specific:${card.contentId}`, label: card.name }));
      const current = node.cardScope === "Specific" ? `Specific:${node.contentId}` : node.cardScope;
      const choices = [{ value: "Inserted", label: "Currently inserted" }, { value: "Any", label: "Any tracked card" }, ...cards];
      if (!choices.some((entry) => entry.value === current)) choices.push({ value: current, label: "Unavailable card" });
      rows.push(
        choice("card", "Card", choices, current, (v) => {
          const [scope, contentId] = String(v).split(":");
          node.cardScope = scope === "Specific" ? "Specific" : scope;
          node.contentId = scope === "Specific" ? (contentId ?? "") : "";
          touch();
        }),
      );
      break;
    }
    case "Merge":
      rows.push(choice("mode", "Match", [{ value: "And", label: "All (AND)" }, { value: "Or", label: "Any (OR)" }], node.mode, (v) => set("mode", v)));
      break;
    default:
      break;
  }
  const canInvert = (state.filterKinds ?? []).find((entry) => entry.kind === node.kind)?.canInvert;
  if (canInvert) {
    rows.push(choice("invert", "Result", [{ value: false, label: "Normal" }, { value: true, label: "Inverted (NOT)" }], !!node.inverted, (v) => set("inverted", !!v)));
  }
  const children =
    node.kind === "Merge"
      ? renderSteamUiGroup(
          ui,
          { key: "children", title: "Group filters" },
          h(
            ui.focusable,
            { className: "wsgm-tabs-list", "flow-children": "column" },
            ...(node.children ?? []).map((child, index) =>
              renderSteamUiSelectRow(ui, {
                key: `${index}:${child.kind}`,
                title: libraryTabsDescribe(state, child),
                detail: libraryTabsKindLabel(state, child.kind) + (libraryTabsIncomplete(child) ? " · ⚠ incomplete" : ""),
                onClick: () => onEditChild(child),
              }),
            ),
          ),
          renderSteamUiActions(ui, [{ id: "addChild", label: "Add to group", onClick: onAddChild }], { compact: true }),
        )
      : null;
  return libraryTabsLevel(
    ui,
    onBack,
    libraryTabsKindLabel(state, node.kind),
    group ? "Part of a group" : undefined,
    h(ui.settingsSection, { label: "Filter" }, ...rows),
    children,
    renderSteamUiActions(ui, [
      ...(group ? [] : [{ id: "changeType", label: "Change type", onClick: onChangeType }]),
      {
        id: "remove",
        label: group ? "Remove from group" : "Remove filter",
        onClick: () => showSteamUiConfirm(ui, { title: "Remove this filter?", text: libraryTabsDescribe(state, node), confirmLabel: "Remove", onConfirm: onRemove }),
      },
      { id: "done", label: "Done", primary: true, onClick: onBack },
    ], { compact: true }),
  );
}

/**
 * The tab itself: its name, match mode, categories and filters, and the save, delete and cancel actions.
 * @param props Resolved components, published state, the draft, whether it exists, a redraw, and the callbacks.
 * @returns The nested level; Back discards the draft like Cancel.
 */
function LibraryTabsEditor({ ui, state, draft, existing, touch, onAddFilter, onEditFilter, onSave, onDelete, onBack }: any) {
  const react = ui.react;
  const h = react.createElement;
  const filters = draft.filterTree.children ?? [];
  const change = (row, value) => {
    switch (row.key) {
      case "name":
        draft.name = String(value ?? "");
        break;
      case "mode":
        draft.filterTree.mode = value;
        break;
      case "categories":
        draft.categories = Number(value);
        break;
      default:
        return;
    }
    touch();
  };
  const rows = [
    renderSteamSettingRow(ui, { key: "name", kind: "text", label: "Name", description: draft.name ? undefined : "Required", text: draft.name }, draft.name, change, () => {}),
    renderSteamSettingRow(
      ui,
      { key: "mode", kind: "choice", label: "Match", text: draft.filterTree.mode, choices: [{ value: "And", label: "All filters (AND)" }, { value: "Or", label: "Any filter (OR)" }] },
      undefined,
      change,
      () => {},
    ),
    renderSteamSettingRow(
      ui,
      { key: "categories", kind: "choice", label: "Include", text: draft.categories, choices: libraryTabsCategoryChoices(draft.categories) },
      undefined,
      change,
      () => {},
    ),
  ];
  return libraryTabsLevel(
    ui,
    onBack,
    existing ? "Edit tab" : "New tab",
    undefined,
    h(ui.settingsSection, { label: "Tab" }, ...rows),
    renderSteamUiGroup(
      ui,
      { key: "filters", title: "Filters" },
      filters.length === 0
        ? renderSteamUiEmpty(ui, { kind: "empty", text: "No filters yet — add one below." })
        : h(
            ui.focusable,
            { className: "wsgm-tabs-list", "flow-children": "column" },
            ...filters.map((node, index) =>
              renderSteamUiSelectRow(ui, {
                key: `${index}:${node.kind}`,
                title: libraryTabsDescribe(state, node),
                detail: libraryTabsKindLabel(state, node.kind) + (libraryTabsIncomplete(node) ? " · ⚠ incomplete" : ""),
                onClick: () => onEditFilter(node),
              }),
            ),
          ),
      renderSteamUiActions(ui, [{ id: "add", label: "Add filter", onClick: onAddFilter }], { compact: true }),
    ),
    renderSteamUiActions(ui, [
      { id: "save", label: "Save tab", primary: true, disabled: !!state.busy, onClick: onSave },
      ...(existing
        ? [
            {
              id: "delete",
              label: "Delete tab",
              disabled: !!state.busy,
              onClick: () =>
                showSteamUiConfirm(ui, { title: `Delete ${draft.name || "this tab"}?`, text: "The tab leaves Steam's library strip.", confirmLabel: "Delete", onConfirm: onDelete }),
            },
          ]
        : []),
      { id: "cancel", label: "Cancel", onClick: onBack },
    ], { compact: true }),
  );
}

/**
 * Walks the tab list, the strip order, one tab's editor and its filter levels over host state,
 * with Back walking the levels up.
 * @param props The frame's context and the host's published state.
 * @returns The level on screen.
 */
function LibraryTabsPage({ context, state }: any) {
  const react = context.react();
  const h = react.createElement;
  const ui = context.ui();
  const act = context.act;
  // The levels the user walked into, root first; a reload of the asset starts at the list, so no
  // stale editor outlives the state it was opened from. The draft tab is edited in place and
  // redrawn through a counter, as the overlay's editor mutates its own copy.
  const [levels, setLevels] = react.useState([] as any[]);
  const [draft, setDraft] = react.useState(null as any);
  const [existing, setExisting] = react.useState(false);
  const [, redraw] = react.useState(0);
  // The tabs are read again whenever the page opens: another surface may have changed them.
  react.useEffect(() => {
    void act("refresh");
  }, []);
  const touch = () => redraw((value: number) => value + 1);
  const push = (level) => setLevels((stack) => [...stack, level]);
  const pop = () => setLevels((stack) => stack.slice(0, -1));
  const replaceTop = (level) => setLevels((stack) => [...stack.slice(0, -1), level]);
  // Back at the list, the collection puts the focus back on the tab it was left from.
  const toList = () => {
    setLevels([]);
    setDraft(null);
  };
  const openEditor = (tab) => {
    setDraft(tab ? JSON.parse(JSON.stringify(tab)) : libraryTabsNewTab());
    setExisting(!!tab);
    setLevels([{ kind: "editor" }]);
  };
  const listOf = (group) => (group ? group.children : draft.filterTree.children);
  const top = levels[levels.length - 1];
  let body;
  if (!top) {
    body = h(LibraryTabsList, {
      ui,
      state,
      onNew: () => openEditor(null),
      onOrder: () => setLevels([{ kind: "order" }]),
      onEdit: openEditor,
    });
  } else if (top.kind === "order") {
    body = h(LibraryTabsOrder, { ui, state, act, onBack: toList });
  } else if (!draft) {
    body = renderSteamUiEmpty(ui, { kind: "loading", text: "Loading…" });
  } else if (top.kind === "editor") {
    body = h(LibraryTabsEditor, {
      ui,
      state,
      draft,
      existing,
      touch,
      onAddFilter: () => push({ kind: "picker", replacing: null, group: null }),
      onEditFilter: (node) => push({ kind: "filter", node, group: null }),
      onSave: () =>
        void context.send("saveTab", { tab: draft }).then(
          toList,
          // The refusal names what is missing in the banner; the editor stays open with the draft.
          (error) => context.report(error),
        ),
      onDelete: () =>
        void context.send("deleteTab", { id: draft.id }).then(toList, (error) => context.report(error)),
      onBack: toList,
    });
  } else if (top.kind === "picker") {
    body = h(LibraryTabsFilterPicker, {
      ui,
      state,
      inGroup: !!top.group,
      onBack: pop,
      onPick: (kind: string) => {
        const node = libraryTabsNewFilter(kind);
        const list = listOf(top.group);
        const index = top.replacing ? list.indexOf(top.replacing) : -1;
        if (index >= 0) list[index] = node;
        else list.push(node);
        touch();
        // The picker becomes the new filter's editor, so Back from it returns to where the
        // picker was opened from.
        replaceTop({ kind: "filter", node, group: top.group });
      },
    });
  } else if (top.kind === "filter") {
    const node = top.node;
    body = h(LibraryTabsFilterEditor, {
      ui,
      state,
      node,
      group: top.group,
      touch,
      onBack: pop,
      onPickTags: () => {
        void act("loadTags");
        push({ kind: "tags", node });
      },
      onPickCollection: () => {
        void act("loadCollections");
        push({ kind: "collections", node });
      },
      onPickGames: () => {
        void act("loadGames");
        push({ kind: "games", node });
      },
      onChangeType: () => push({ kind: "picker", replacing: node, group: top.group }),
      onAddChild: () => push({ kind: "picker", replacing: null, group: node }),
      onEditChild: (child) => push({ kind: "filter", node: child, group: node }),
      onRemove: () => {
        const list = listOf(top.group);
        const index = list.indexOf(node);
        if (index >= 0) list.splice(index, 1);
        touch();
        pop();
      },
    });
  } else if (top.kind === "tags") {
    const node = top.node;
    const selected = new Set((node.tagIds ?? []).map(Number));
    body = h(LibraryTabsMultiSelect, {
      ui,
      title: "Tags",
      picker: state.tags,
      onRetry: () => void act("loadTags"),
      labelOf: (tag) => `${tag.name} (${tag.count})`,
      idOf: (tag) => Number(tag.tagId),
      selected,
      onToggle: (id: number) => {
        node.tagIds = selected.has(id) ? (node.tagIds ?? []).filter((entry) => Number(entry) !== id) : [...(node.tagIds ?? []), id];
        touch();
      },
      onBack: pop,
    });
  } else if (top.kind === "games") {
    const node = top.node;
    const selected = new Set((node.appIds ?? []).map(Number));
    body = h(LibraryTabsMultiSelect, {
      ui,
      title: "Games",
      picker: state.games,
      onRetry: () => void act("loadGames"),
      labelOf: (game) => game.name,
      idOf: (game) => Number(game.appId),
      selected,
      onToggle: (id: number) => {
        node.appIds = selected.has(id) ? (node.appIds ?? []).filter((entry) => Number(entry) !== id) : [...(node.appIds ?? []), id];
        touch();
      },
      onBack: pop,
    });
  } else if (top.kind === "collections") {
    const node = top.node;
    body = h(LibraryTabsCollectionPicker, {
      ui,
      picker: state.collections,
      onRetry: () => void act("loadCollections"),
      onBack: pop,
      onPick: (id: string) => {
        node.collectionId = id;
        touch();
        pop();
      },
    });
  }
  return body;
}

const libraryTabsPage = registerSteamPage({
  template: "library-tab-manager",
  gate: "libraryTabManager",
  patchId: LibraryTabsPatchId,
  title: "Library Tab Manager",
  id: "wsgm-library-tabs",
  style: libraryTabsStyles,
  loading: "Loading library tabs…",
  status: () => ({ tabs: libraryTabsPage.state()?.tabs?.length ?? 0 }),
  Page: LibraryTabsPage,
});
