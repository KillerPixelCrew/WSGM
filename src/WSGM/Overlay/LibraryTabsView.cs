using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using SteamUiToolkit;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     The gamepad-driven custom-tab builder, hosted as a Tools sub-view of the
///     overlay (the <c>PanelFormat</c> idiom; scaffolding in <see cref="OverlaySubView" />).
///     The view is one projection of <see cref="LibraryManagerService" />, which Steam's Library
///     Tab Manager page projects too: the tabs, the strip order, the pickers' Steam data and every
///     save go through it, and a change made in Steam redraws the list here. A tab is injected into
///     Steam's own tab strip by <see cref="SteamLibraryTabs" /> and its membership is a fake
///     in-memory collection — no Steam collection is ever created, and user/SRM ones are never
///     touched. Card libraries are managed by the separate <see cref="CardManagerView" />.
/// </summary>
public sealed class LibraryTabsView : OverlaySubView
{
    // ---- Level: filter type picker ----

    // The service's table, so both surfaces offer the same kinds in the same order.
    private static readonly (FilterKind Kind, string Label, string Desc)[] FilterKinds =
    [
        .. LibraryTabFilterKind.All.Select(kind =>
            (Enum.Parse<FilterKind>(kind.Kind), kind.Label, kind.Description))
    ];

    // The last Steam data the pickers read, for naming a filter's collection and tag.
    private IReadOnlyList<LibraryCollectionChoice>? _collections;
    private CustomTabConfig _editing = new();

    // ---- Level: tab editor ----

    private CustomTabConfig? _editingOriginal;
    private bool _listShown;

    private FilterNode? _replacingFilter;
    private LibraryManagerService? _service;
    private IReadOnlyList<LibraryTagChoice>? _tags;

    /// <inheritdoc />
    protected override string LogScope => "Library tabs";

    private LibraryManagerService Service =>
        _service ?? throw new InvalidOperationException("The session library manager is unavailable.");

    // The custom tabs and the strip as the service last read them.
    private SteamLibraryTabsState State => Service.ReadTabsState();

    /// <summary>Attaches the view to the session's shared manager, or detaches it with null.</summary>
    /// <param name="service">The session's service, borrowed; null when the overlay closes or the session has none.</param>
    internal void Attach(LibraryManagerService? service)
    {
        if (ReferenceEquals(_service, service))
        {
            return;
        }

        if (_service is not null)
        {
            _service.Changed -= OnServiceChanged;
        }

        _service = service;
        if (service is not null)
        {
            service.Changed += OnServiceChanged;
        }
        else
        {
            Leave();
        }
    }

    /// <summary>
    ///     Loads config and renders the root tab list. Called by the overlay when
    ///     the sub-view opens.
    /// </summary>
    public void Open()
    {
        NavigationStack.Clear();
        CurrentLevel = null;
        if (_service is null)
        {
            RenderMessage("Library Tabs", "Library tab management is unavailable in this session.");
            return;
        }

        _tags = null;
        _collections = null;
        var generation = ++NavigationGeneration;
        RenderLoading("Library Tabs");
        _ = RunSafelyAsync(LoadAndRenderAsync(generation), "open");
    }

    /// <inheritdoc />
    internal override void Leave()
    {
        _listShown = false;
        base.Leave();
    }

    private async Task LoadAndRenderAsync(int generation)
    {
        // The first open waits for the reading; later ones draw the last one at once and let a
        // fresh reading redraw the list through Changed.
        if (!State.Loaded)
        {
            await Service.RefreshAsync();
        }
        else
        {
            Service.RequestRefresh();
        }

        if (generation != NavigationGeneration)
        {
            return;
        }

        Navigate(RenderTabList);
    }

    // A change from Steam's page or from a sync: the list redraws in place, keeping the finger on
    // the same tab. Deeper levels redraw after their own operation.
    private void OnServiceChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_service is not null && _listShown && IsVisible && Content is not null)
            {
                RenderTabList(FocusedTag());
            }
        });
    }

    private string? FocusedTag()
    {
        return TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is ActionButton { Tag: string tag }
            ? tag
            : null;
    }

    // ---- Level: tab list ----

    private void RenderTabList()
    {
        RenderTabList(null);
    }

    private void RenderTabList(string? focusTag)
    {
        _listShown = true;
        var state = State;
        var stack = NewStack("Library Tabs");
        stack.Children.Add(Caption("Tabs appear in Steam's library and update automatically as "
                                   + "you make changes."));

        var create = PrimaryRow("New Tab", "Build a tab from filters", Icons.FolderPlus,
            () => OpenTabEditor(null));
        create.Tag = "new";
        stack.Children.Add(create);
        var order = Row("Tab Order & Steam Tabs", "Reorder the strip, hide Steam's own tabs",
            Icons.Reorder, OpenTabOrder);
        order.Tag = "order";
        stack.Children.Add(order);

        if (state.Tabs.Count > 0)
        {
            stack.Children.Add(SectionLabel("YOUR TABS"));
            foreach (var tab in state.Tabs)
            {
                var t = tab;
                var detail = t.Enabled ? $"{t.FilterTree.Children.Count} filters" : "disabled";
                var row = Row(string.IsNullOrWhiteSpace(t.Name) ? "(unnamed)" : t.Name,
                    detail, Icons.Wrench, () => OpenTabEditor(t));
                row.Tag = t.Id;
                stack.Children.Add(row);
            }
        }

        SetContent(stack);
        if (focusTag is null)
        {
            return;
        }

        // Posted after SetContent's own first-row focus post, so it wins.
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var child in stack.Children)
            {
                if (child is ActionButton { Tag: string tag } button && tag == focusTag)
                {
                    button.Focus(NavigationMethod.Directional);
                    return;
                }
            }
        });
    }

    private void OpenTabOrder()
    {
        _listShown = false;
        Navigate(RenderTabOrder);
    }

    private void RenderTabOrder()
    {
        var stack = NewStack("Tab Order");
        stack.Children.Add(Caption("Top to bottom here is left to right in Steam. Steam's own "
                                   + "tabs can be hidden; WSGM tabs disappear when disabled in their editors."));
        foreach (var entry in State.Order)
        {
            var e = entry;
            var kind = KindLabel(e);
            stack.Children.Add(Row(e.Title, e.Hidden ? kind + " · hidden" : kind, Icons.Reorder,
                () => Navigate(() => RenderTabOrderActions(e.Key))));
        }

        SetContent(stack);
    }

    private static string KindLabel(LibraryTabOrderView entry)
    {
        return entry.Kind switch
        {
            "native" => "Steam tab",
            "card" => "Card tab",
            _ => "Custom tab"
        };
    }

    private void RenderTabOrderActions(string key, string? focusTitle = null)
    {
        var order = State.Order;
        var index = order.ToList().FindIndex(e => string.Equals(e.Key, key, StringComparison.Ordinal));
        if (index < 0)
        {
            Back();
            return;
        }

        var entry = order[index];
        var stack = NewStack(entry.Title);
        stack.Children.Add(Caption($"Position {index + 1} of {order.Count}"
                                   + (entry.Hidden ? " · hidden" : "")));
        stack.Children.Add(Row("Move up", "Earlier in the strip", Icons.ArrowUp,
            index > 0 ? () => MoveOrderEntry(key, -1) : null));
        stack.Children.Add(Row("Move down", "Later in the strip", Icons.ArrowDown,
            index + 1 < order.Count ? () => MoveOrderEntry(key, +1) : null));
        if (entry.Kind == "native")
        {
            stack.Children.Add(entry.Hidden
                ? PrimaryRow("Show tab", "Put this Steam tab back in the strip", Icons.Play,
                    () => SetNativeHidden(key, false))
                : DangerRow("Hide tab", "Remove this Steam tab from the strip", Icons.Close,
                    () => SetNativeHidden(key, true)));
        }

        stack.Children.Add(Row("Done", "Back to the list", Icons.ExitFullscreen, () => Back()));
        SetContent(stack);
        if (focusTitle is not null)
        {
            // Posted after SetContent's own first-row focus post, so it wins — repeated
            // move presses keep the finger on the same action instead of jumping to the
            // top row every re-render.
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var child in stack.Children)
                {
                    if (child is not ActionButton { IsEffectivelyEnabled: true } button
                        || button.Title != focusTitle)
                    {
                        continue;
                    }

                    button.Focus(NavigationMethod.Directional);
                    return;
                }
            });
        }
    }

    // The service applies a move at once and writes it in press order, so repeated presses keep
    // the finger on the same action while Steam's strip follows.
    private void MoveOrderEntry(string key, int delta)
    {
        _ = RunSafelyAsync(ApplyOrderChangeAsync(
            Service.MoveTabAsync(key, delta), key, delta < 0 ? "Move up" : "Move down"), "move");
    }

    private void SetNativeHidden(string key, bool hidden)
    {
        _ = RunSafelyAsync(ApplyOrderChangeAsync(
            Service.SetNativeTabHiddenAsync(key, hidden), key, hidden ? "Show tab" : "Hide tab"), "hide");
    }

    private async Task ApplyOrderChangeAsync(Task<SteamUiCommandResult> change, string key, string focusTitle)
    {
        var result = await change;
        if (!result.Succeeded)
        {
            Toast(result.Error ?? "Could not change the order.");
            return;
        }

        Replace(() => RenderTabOrderActions(key, focusTitle));
    }

    private void OpenTabEditor(CustomTabConfig? existing)
    {
        _listShown = false;
        _editingOriginal = existing;
        _editing = existing is null ? new CustomTabConfig { Id = Guid.NewGuid().ToString("N") } : Clone(existing);
        Navigate(RenderTabEditor);
    }

    private void RenderTabEditor()
    {
        var stack = NewStack(_editingOriginal is null ? "New Tab" : "Edit Tab");

        stack.Children.Add(Row("Name", string.IsNullOrWhiteSpace(_editing.Name) ? "(required)" : _editing.Name,
            Icons.CopyDoc, () => EditText("Tab name", _editing.Name, 0, v => { _editing.Name = v.Trim(); })));

        stack.Children.Add(ChoiceRow("Match",
            [(FilterMode.And, "All filters (AND)"), (FilterMode.Or, "Any filter (OR)")],
            _editing.FilterTree.Mode, value => _editing.FilterTree.Mode = value));

        stack.Children.Add(ChoiceRow("Include", CategoryChoices(_editing.Categories),
            _editing.Categories, value => _editing.Categories = value));

        stack.Children.Add(SectionLabel("FILTERS"));
        var filters = _editing.FilterTree.Children;
        if (filters.Count == 0)
        {
            stack.Children.Add(Caption("No filters yet — add one below."));
        }
        else
        {
            foreach (var node in filters.ToList())
            {
                var n = node;
                var valid = LibraryFilter.IsValid(n) ? "" : "  ⚠ incomplete";
                stack.Children.Add(Row(DescribeFilter(n), FilterKindLabel(n.Kind) + valid,
                    Icons.Wrench, () => OpenFilterEditor(n)));
            }
        }

        stack.Children.Add(Row("Add filter", "Choose a filter type", Icons.FolderPlus,
            () => OpenFilterPicker(null)));

        stack.Children.Add(SectionLabel(""));
        stack.Children.Add(PrimaryRow("Save tab", "Materialize as a Steam tab", Icons.Play, SaveTab));
        if (_editingOriginal is not null)
        {
            stack.Children.Add(DangerRow("Delete tab", "Remove this tab from Steam's library",
                Icons.Close, DeleteTab));
        }

        stack.Children.Add(Row("Cancel", "Discard changes", Icons.ExitFullscreen, () => Back()));

        SetContent(stack);
    }

    private void SaveTab()
    {
        _ = RunSafelyAsync(SaveTabAsync(), "save");
    }

    private async Task SaveTabAsync()
    {
        // The service validates the name and the filters and assigns a new tab its place; a
        // refusal names what is missing and the editor stays open.
        var result = await Service.SaveTabAsync(_editing);
        if (!result.Succeeded)
        {
            Toast(result.Error ?? "Could not save the tab. Try again.");
            return;
        }

        // Drop back to the list, then materialize in the background.
        NavigationStack.Clear();
        Replace(RenderTabList);
    }

    private void DeleteTab()
    {
        _ = RunSafelyAsync(DeleteTabAsync(), "delete");
    }

    private async Task DeleteTabAsync()
    {
        if (_editingOriginal is null)
        {
            return;
        }

        var result = await Service.DeleteTabAsync(_editingOriginal.Id);
        if (!result.Succeeded)
        {
            Toast(result.Error ?? "Could not delete the tab. Try again.");
        }

        NavigationStack.Clear();
        Replace(RenderTabList);
    }

    private void OpenFilterPicker(FilterNode? replacing)
    {
        _replacingFilter = replacing;
        Navigate(RenderFilterPicker);
    }

    private void RenderFilterPicker()
    {
        var stack = NewStack("Add Filter");
        foreach (var (kind, label, desc) in FilterKinds)
        {
            var k = kind;
            stack.Children.Add(Row(label, desc, Icons.Wrench, () => PickFilterKind(k)));
        }

        SetContent(stack);
    }

    private void PickFilterKind(FilterKind kind)
    {
        var node = new FilterNode { Kind = kind };
        if (kind == FilterKind.Merge)
        {
            node.Children.Add(new FilterNode { Kind = FilterKind.Installed });
        }

        if (_replacingFilter is not null)
        {
            var list = _editing.FilterTree.Children;
            var idx = list.IndexOf(_replacingFilter);
            if (idx >= 0)
            {
                list[idx] = node;
            }

            PopIfAny();
        }
        else
        {
            _editing.FilterTree.Children.Add(node);
        }

        // Replace the picker level with the editor for the new node.
        CurrentLevel = () => RenderFilterEditor(node);
        RenderFilterEditor(node);
    }

    // ---- Level: filter editor ----

    private void OpenFilterEditor(FilterNode node)
    {
        Navigate(() => RenderFilterEditor(node));
    }

    private void RenderFilterEditor(FilterNode node)
    {
        var stack = NewStack(FilterKindLabel(node.Kind));

        BuildFilterParams(stack, node);

        if (LibraryFilter.CanInvert(node.Kind))
        {
            stack.Children.Add(ChoiceRow("Result", [(false, "Normal"), (true, "Inverted (NOT)")],
                node.Inverted, value => node.Inverted = value));
        }

        stack.Children.Add(SectionLabel(""));
        stack.Children.Add(Row("Change type", "Pick a different filter", Icons.Wrench,
            () => OpenFilterPicker(node)));
        stack.Children.Add(DangerRow("Remove filter", "Delete this filter", Icons.Close, () =>
        {
            RemoveNode(_editing.FilterTree, node);
            Back();
        }));
        stack.Children.Add(PrimaryRow("Done", "Back to the tab", Icons.Play, () => Back()));

        SetContent(stack);
    }

    private void BuildFilterParams(StackPanel stack, FilterNode node)
    {
        // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
        switch (node.Kind)
        {
            case FilterKind.Installed:
                stack.Children.Add(ChoiceRow("State", [(false, "Not installed"), (true, "Installed")],
                    node.BoolValue, value => node.BoolValue = value));
                break;

            case FilterKind.Platform:
                stack.Children.Add(ChoiceRow("Platform",
                    [(PlatformKind.Steam, "Steam"), (PlatformKind.NonSteam, "Non-Steam")],
                    node.Platform, value => node.Platform = value));
                break;

            case FilterKind.Regex:
                stack.Children.Add(Row("Pattern", string.IsNullOrEmpty(node.Pattern)
                    ? "(required)"
                    : node.Pattern, Icons.CopyDoc, () =>
                    EditText("Title pattern", node.Pattern, 0, v => { node.Pattern = v; })));
                break;

            case FilterKind.Tag:
                stack.Children.Add(Row("Tags", node.TagIds.Count == 0
                        ? "(choose one or more)"
                        : $"{node.TagIds.Count} selected", Icons.Wrench,
                    () => OpenTagPicker(node)));
                stack.Children.Add(ChoiceRow("Match",
                    [(FilterMode.And, "All tags (AND)"), (FilterMode.Or, "Any tag (OR)")],
                    node.Mode, value => node.Mode = value));
                break;

            case FilterKind.Collection:
                stack.Children.Add(Row("Collection", string.IsNullOrEmpty(node.CollectionId)
                        ? "(choose one)"
                        : CollectionName(node.CollectionId), Icons.Wrench,
                    () => OpenCollectionPicker(node)));
                break;

            case FilterKind.Whitelist:
            case FilterKind.Blacklist:
                stack.Children.Add(Row("Games", node.AppIds.Count == 0
                        ? "(choose games)"
                        : $"{node.AppIds.Count} selected", Icons.Wrench,
                    () => OpenGamePicker(node)));
                break;

            case FilterKind.ReviewScore:
                stack.Children.Add(ChoiceRow("Source",
                    [(ReviewScoreType.SteamPercent, "Steam %"), (ReviewScoreType.Metacritic, "Metacritic")],
                    node.ScoreType, value => node.ScoreType = value));
                AddCondition(stack, node);
                AddStepper(stack, "Score", node.Threshold, 0, 100, 5,
                    v =>
                    {
                        node.Threshold = v;
                        Replace(() => RenderFilterEditor(node));
                    });
                break;

            case FilterKind.TimePlayed:
                AddCondition(stack, node);
                stack.Children.Add(ChoiceRow("Units",
                    [(TimeUnit.Minutes, "Minutes"), (TimeUnit.Hours, "Hours"), (TimeUnit.Days, "Days")],
                    node.Units, value => node.Units = value));
                AddStepper(stack, "Amount", node.Threshold, 0, 1000, 1,
                    v =>
                    {
                        node.Threshold = v;
                        Replace(() => RenderFilterEditor(node));
                    });
                break;

            case FilterKind.SizeOnDisk:
                AddCondition(stack, node);
                AddStepper(stack, "Size (GB)", node.Threshold, 0, 2000, 5,
                    v =>
                    {
                        node.Threshold = v;
                        Replace(() => RenderFilterEditor(node));
                    });
                break;

            case FilterKind.ReleaseDate:
            case FilterKind.LastPlayed:
                AddCondition(stack, node);
                AddStepper(stack, "Days ago", node.DaysAgo, 0, 3650, 30,
                    v =>
                    {
                        node.DaysAgo = (int)v;
                        node.Year = 0;
                        Replace(() => RenderFilterEditor(node));
                    });
                stack.Children.Add(Caption("0 days = use no date. (Absolute dates: edit config.)"));
                break;

            case FilterKind.SdCard:
                stack.Children.Add(ChoiceRow("Card", CardScopeChoices(node),
                    (node.CardScope, node.CardScope == SdCardScope.Specific ? node.ContentId : ""), value =>
                    {
                        node.CardScope = value.Item1;
                        node.ContentId = value.Item2;
                    }));
                break;

            case FilterKind.Merge:
                stack.Children.Add(ChoiceRow("Match", [(FilterMode.And, "All (AND)"), (FilterMode.Or, "Any (OR)")],
                    node.Mode, value => node.Mode = value));
                stack.Children.Add(SectionLabel("GROUP FILTERS"));
                foreach (var child in node.Children.ToList())
                {
                    var c = child;
                    stack.Children.Add(Row(DescribeFilter(c), FilterKindLabel(c.Kind), Icons.Wrench,
                        () => OpenChildEditor(node, c)));
                }

                stack.Children.Add(Row("Add to group", "Nested filter", Icons.FolderPlus,
                    () => OpenChildPicker(node)));
                break;
        }
    }

    private void AddCondition(StackPanel stack, FilterNode node)
    {
        stack.Children.Add(ChoiceRow("Condition",
            [(ThresholdCondition.Above, "At or above"), (ThresholdCondition.Below, "Below")],
            node.Condition, value => node.Condition = value));
    }

    // ---- Merge sub-group editing (one nesting level; re-uses the same editor) ----

    private void OpenChildPicker(FilterNode group)
    {
        Navigate(() =>
        {
            var stack = NewStack("Add to Group");
            foreach (var (kind, label, desc) in FilterKinds.Where(f => f.Kind != FilterKind.Merge))
            {
                var k = kind;
                stack.Children.Add(Row(label, desc, Icons.Wrench, () =>
                {
                    var child = new FilterNode { Kind = k };
                    group.Children.Add(child);
                    CurrentLevel = () => RenderChildEditor(group, child);
                    RenderChildEditor(group, child);
                }));
            }

            SetContent(stack);
        });
    }

    private void OpenChildEditor(FilterNode group, FilterNode child)
    {
        Navigate(() => RenderChildEditor(group, child));
    }

    private void RenderChildEditor(FilterNode group, FilterNode child)
    {
        var stack = NewStack(FilterKindLabel(child.Kind));
        BuildFilterParams(stack, child);
        if (LibraryFilter.CanInvert(child.Kind))
        {
            stack.Children.Add(ChoiceRow("Result", [(false, "Normal"), (true, "Inverted (NOT)")],
                child.Inverted, value => child.Inverted = value));
        }

        stack.Children.Add(SectionLabel(""));
        stack.Children.Add(DangerRow("Remove", "Delete from group", Icons.Close, () =>
        {
            group.Children.Remove(child);
            Back();
        }));
        stack.Children.Add(PrimaryRow("Done", "Back to group", Icons.Play, () => Back()));
        SetContent(stack);
    }

    // ---- Pickers (async data) ----

    private void OpenTagPicker(FilterNode node)
    {
        _ = RunSafelyAsync(OpenTagPickerAsync(node), "tag picker");
    }

    private async Task OpenTagPickerAsync(FilterNode node)
    {
        Navigate(() => RenderLoading("Tags"));
        var generation = NavigationGeneration;
        // A failed read draws the picker with what the service last read, and says why.
        var picker = await Service.LoadTagsAsync();
        if (generation != NavigationGeneration)
        {
            return;
        }

        if (picker.Error is not null)
        {
            Toast(picker.Error);
        }

        var tags = picker.Items;
        if (picker.Loaded)
        {
            _tags = tags;
        }

        var selected = new HashSet<long>(node.TagIds.Select(static id => (long)id));
        Replace(() => RenderMultiSelect("Tags", tags.Select(t => ((long)t.TagId, $"{t.Name} ({t.Count})")),
            selected, () =>
            {
                node.TagIds = [.. selected.Select(static id => checked((int)id))];
                Back();
            }));
    }

    private void OpenGamePicker(FilterNode node)
    {
        _ = RunSafelyAsync(OpenGamePickerAsync(node), "game picker");
    }

    private async Task OpenGamePickerAsync(FilterNode node)
    {
        Navigate(() => RenderLoading("Games"));
        var generation = NavigationGeneration;
        var picker = await Service.LoadGamesAsync();
        if (generation != NavigationGeneration)
        {
            return;
        }

        // A failed read draws what the service last read, and says why.
        if (picker.Error is not null)
        {
            Toast(picker.Error);
        }

        var games = picker.Items;
        // Ids compare unsigned, so a shortcut saved signed by an older build still shows as selected.
        var selected = new HashSet<long>(node.AppIds.Select(static id => (long)SteamApps.NormalizeAppId(id)));
        Replace(() => RenderMultiSelect("Games", games.Select(g => (g.AppId, g.Name)), selected, () =>
        {
            node.AppIds = [.. selected];
            Back();
        }));
    }

    private void OpenCollectionPicker(FilterNode node)
    {
        _ = RunSafelyAsync(OpenCollectionPickerAsync(node), "collection picker");
    }

    private async Task OpenCollectionPickerAsync(FilterNode node)
    {
        Navigate(() => RenderLoading("Collections"));
        var generation = NavigationGeneration;
        var picker = await Service.LoadCollectionsAsync();
        if (generation != NavigationGeneration)
        {
            return;
        }

        // A failed read draws what the service last read, and says why.
        if (picker.Error is not null)
        {
            Toast(picker.Error);
        }

        var collections = picker.Items;
        if (picker.Loaded)
        {
            _collections = collections;
        }

        Replace(() =>
        {
            var stack = NewStack("Collection");
            foreach (var col in collections)
            {
                var c = col;
                stack.Children.Add(Row(c.Name, $"{c.GameCount} games", Icons.Wrench, () =>
                {
                    node.CollectionId = c.Id;
                    Back();
                }));
            }

            if (collections.Count == 0)
            {
                stack.Children.Add(Caption("No collections found — is Steam open?"));
            }

            SetContent(stack);
        });
    }

    private void RenderMultiSelect(string title, IEnumerable<(long Id, string Label)> items,
        HashSet<long> selected, Action onDone, int page = 0)
    {
        const int pageSize = 200;
        var all = items.ToList();
        var pageCount = Math.Max(1, (all.Count + pageSize - 1) / pageSize);
        page = Math.Clamp(page, 0, pageCount - 1);
        var stack = NewStack(title);
        stack.Children.Add(PrimaryRow("Done", $"{selected.Count} selected", Icons.Play, onDone));
        if (pageCount > 1)
        {
            var currentPage = page;
            stack.Children.Add(Row($"Page {page + 1} of {pageCount}", $"{all.Count} entries · previous",
                Icons.Restart, page > 0
                    ? () => Replace(() => RenderMultiSelect(title, all, selected, onDone, currentPage - 1))
                    : null));
            if (page + 1 < pageCount)
            {
                stack.Children.Add(Row("Next page",
                    $"Entries {(page + 1) * pageSize + 1}–{Math.Min(all.Count, (page + 2) * pageSize)}",
                    Icons.Play, () => Replace(() => RenderMultiSelect(title, all, selected, onDone, currentPage + 1))));
            }
        }

        foreach (var (id, label) in all.Skip(page * pageSize).Take(pageSize))
        {
            var itemId = id;
            var check = selected.Contains(itemId) ? "✓ " : "";
            var row = Row(check + label, "", null, null);
            row.Click += (_, _) =>
            {
                if (!selected.Add(itemId))
                {
                    selected.Remove(itemId);
                }

                row.Title = (selected.Contains(itemId) ? "✓ " : "") + label;
            };
            stack.Children.Add(row);
        }

        SetContent(stack);
    }

    // ---- Tab-side builders ----

    private static void AddStepper(StackPanel stack, string label, double value, double min, double max,
        double step, Action<double> onChange)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        var text = new TextBlock
        {
            Text = $"{label}: {value.ToString("0.##", CultureInfo.InvariantCulture)}",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };
        Grid.SetColumn(text, 0);
        var minus = new Button { Content = "−", Width = 46, Margin = new Thickness(4, 0, 4, 0) };
        Grid.SetColumn(minus, 1);
        minus.Click += (_, _) => onChange(Math.Clamp(value - step, min, max));
        var plus = new Button { Content = "+", Width = 46 };
        Grid.SetColumn(plus, 2);
        plus.Click += (_, _) => onChange(Math.Clamp(value + step, min, max));
        row.Children.Add(text);
        row.Children.Add(minus);
        row.Children.Add(plus);
        stack.Children.Add(row);
    }

    // ---- Value helpers ----

    private List<((SdCardScope Scope, string ContentId) Value, string Label)> CardScopeChoices(FilterNode node)
    {
        var choices = new List<((SdCardScope Scope, string ContentId) Value, string Label)>
        {
            ((SdCardScope.Inserted, ""), "Currently inserted"),
            ((SdCardScope.Any, ""), "Any tracked card")
        };
        choices.AddRange(State.Cards.Select(card => ((SdCardScope.Specific, card.ContentId), card.Name)));
        if (node.CardScope == SdCardScope.Specific && choices.All(choice => choice.Value.ContentId != node.ContentId))
        {
            choices.Add(((SdCardScope.Specific, node.ContentId), "Unavailable card"));
        }

        return choices;
    }

    private static void RemoveNode(FilterNode group, FilterNode node)
    {
        group.Children.Remove(node);
    }

    private static IReadOnlyList<(int Value, string Label)> CategoryChoices(int current)
    {
        var games = (int)LibraryFilterCategories.Games;
        var software = games | (int)LibraryFilterCategories.Software;
        var hidden = software | (int)LibraryFilterCategories.Hidden;
        var choices = new List<(int Value, string Label)>
        {
            (games, CategoriesLabel((LibraryFilterCategories)games)),
            (software, CategoriesLabel((LibraryFilterCategories)software)),
            (hidden, CategoriesLabel((LibraryFilterCategories)hidden))
        };
        if (choices.All(choice => choice.Value != current))
        {
            choices.Add((current, CategoriesLabel((LibraryFilterCategories)current)));
        }

        return choices;
    }

    private static string CategoriesLabel(LibraryFilterCategories c)
    {
        var parts = new List<string>();
        if (c.HasFlag(LibraryFilterCategories.Games))
        {
            parts.Add("Games");
        }

        if (c.HasFlag(LibraryFilterCategories.Software))
        {
            parts.Add("Software");
        }

        if (c.HasFlag(LibraryFilterCategories.Music))
        {
            parts.Add("Music");
        }

        if (parts.Count == 0)
        {
            parts.Add("Games");
        }

        if (c.HasFlag(LibraryFilterCategories.Hidden))
        {
            parts.Add("+Hidden");
        }

        return string.Join(", ", parts);
    }

    private string CollectionName(string id)
    {
        return _collections?.FirstOrDefault(c => c.Id == id)?.Name ?? "selected";
    }

    private string CardName(string contentId)
    {
        return State.Cards.FirstOrDefault(c => c.ContentId == contentId)?.Name ?? "a card";
    }

    private static string FilterKindLabel(FilterKind kind)
    {
        return FilterKinds.FirstOrDefault(f => f.Kind == kind).Label ?? kind.ToString();
    }

    private string DescribeFilter(FilterNode node)
    {
        var prefix = node.Inverted && LibraryFilter.CanInvert(node.Kind) ? "NOT " : "";
        return prefix + node.Kind switch
        {
            FilterKind.Tag => node.TagIds.Count == 1 && _tags is not null
                ? _tags.FirstOrDefault(t => t.TagId == node.TagIds[0])?.Name ?? "Tag"
                : $"Tags ({node.TagIds.Count})",
            FilterKind.Installed => node.BoolValue ? "Installed" : "Not installed",
            FilterKind.Collection => CollectionName(node.CollectionId),
            FilterKind.Regex => $"Title ~ {node.Pattern}",
            FilterKind.SdCard => node.CardScope switch
            {
                SdCardScope.Specific => $"On {CardName(node.ContentId)}",
                SdCardScope.Any => "On any card",
                _ => "On inserted card"
            },
            FilterKind.TimePlayed => $"Playtime {Cond(node)} {node.Threshold:0.##}",
            FilterKind.SizeOnDisk => $"Size {Cond(node)} {node.Threshold:0.##} GB",
            FilterKind.ReviewScore => $"Score {Cond(node)} {node.Threshold:0}",
            FilterKind.ReleaseDate => $"Released {(node.DaysAgo > 0 ? $"< {node.DaysAgo}d ago" : "date")}",
            FilterKind.LastPlayed => $"Played {(node.DaysAgo > 0 ? $"< {node.DaysAgo}d ago" : "date")}",
            FilterKind.Platform => node.Platform == PlatformKind.Steam ? "Steam" : "Non-Steam",
            FilterKind.Whitelist => $"Whitelist ({node.AppIds.Count})",
            FilterKind.Blacklist => $"Blacklist ({node.AppIds.Count})",
            FilterKind.Merge => $"Group ({node.Children.Count})",
            _ => node.Kind.ToString()
        };
    }

    private static string Cond(FilterNode node)
    {
        return node.Condition == ThresholdCondition.Above ? "≥" : "<";
    }

    private static CustomTabConfig Clone(CustomTabConfig t)
    {
        return new CustomTabConfig
        {
            Id = t.Id,
            Name = t.Name,
            Enabled = t.Enabled,
            Position = t.Position,
            Categories = t.Categories,
            FilterTree = t.FilterTree.Clone()
        };
    }
}
