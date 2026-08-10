using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>The gamepad-driven custom-tab builder + SD-card manager, hosted as a Tools
/// sub-view of the overlay (the <c>PanelFormat</c> idiom). Self-drawing (like
/// <see cref="OnScreenKeyboard"/>): each navigation level rebuilds <see cref="ContentControl.Content"/>,
/// and every interactive element is a <see cref="Button"/> so D-pad navigation and A/B
/// work with no extra focus plumbing. All Steam contact goes through
/// <see cref="SteamCollections"/> / <see cref="LibraryTabManager"/>; membership is
/// materialized as WSGM-owned collections, never touching user/SRM ones.</summary>
public sealed class LibraryTabsView : UserControl
{
    private LibraryTabManager _manager = new();
    private AppConfig _config = new();

    // Navigation: a stack of render thunks. Push goes deeper; Back pops.
    private readonly Stack<Action> _stack = new();
    private Action? _current;

    // Lazily-loaded, cached Steam data for the pickers.
    private IReadOnlyList<SteamCollections.AppInfo>? _games;
    private IReadOnlyList<SteamCollections.TagInfo>? _tags;
    private IReadOnlyList<SteamCollectionInfo>? _collections;

    /// <summary>Raised when the user backs out of the top level (the overlay then
    /// returns to the Tools list).</summary>
    public event Action? CloseRequested;

    /// <summary>Loads config and renders the root tab list. Called by the overlay when
    /// the sub-view opens.</summary>
    /// <param name="manager">The shared library-tab manager.</param>
    public void Open(LibraryTabManager manager)
    {
        _manager = manager;
        _config = LibraryTabManager.LoadConfig();
        _stack.Clear();
        _current = null;
        _games = null;
        _tags = null;
        _collections = null;
        Navigate(RenderTabList);
    }

    /// <summary>Handles a Back/B press: pops one level, or requests close at the top.
    /// Returns true when it consumed the press.</summary>
    public bool Back()
    {
        if (_stack.Count == 0)
        {
            CloseRequested?.Invoke();
            return true;
        }
        _current = _stack.Pop();
        _current();
        return true;
    }

    private void Navigate(Action render)
    {
        if (_current is not null)
        {
            _stack.Push(_current);
        }
        _current = render;
        render();
    }

    private void Replace(Action render)
    {
        _current = render;
        render();
    }

    private void PopIfAny()
    {
        if (_stack.Count > 0)
        {
            _stack.Pop();
        }
    }

    // ---- Level: tab list ----

    private void RenderTabList()
    {
        var stack = NewStack("Library Tabs");
        stack.Children.Add(Caption("Tabs appear in Steam's library and update automatically as "
            + "you make changes."));

        stack.Children.Add(PrimaryRow("New Tab", "Build a tab from filters", Icons.FolderPlus,
            () => OpenTabEditor(null)));
        stack.Children.Add(Row("Card Manager", "Rename, hide, and view SD-card libraries",
            Icons.SdCard, RenderCardList));

        if (_config.CustomTabs.Count > 0)
        {
            stack.Children.Add(SectionLabel("YOUR TABS"));
            foreach (var tab in _config.CustomTabs.OrderBy(t => t.Position).ToList())
            {
                var t = tab;
                var state = t.Enabled ? $"{t.FilterTree?.Children.Count ?? 0} filters" : "disabled";
                stack.Children.Add(Row(string.IsNullOrWhiteSpace(t.Name) ? "(unnamed)" : t.Name,
                    state, Icons.Wrench, () => OpenTabEditor(t)));
            }
        }

        SetContent(stack);
    }

    // ---- Level: tab editor ----

    private CustomTabConfig? _editingOriginal;
    private CustomTabConfig _editing = new();

    private void OpenTabEditor(CustomTabConfig? existing)
    {
        _editingOriginal = existing;
        _editing = existing is null ? new CustomTabConfig() : Clone(existing);
        _editing.FilterTree ??= new FilterNode { Kind = FilterKind.Merge };
        Navigate(RenderTabEditor);
    }

    private void RenderTabEditor()
    {
        var stack = NewStack(_editingOriginal is null ? "New Tab" : "Edit Tab");

        stack.Children.Add(Row("Name", string.IsNullOrWhiteSpace(_editing.Name) ? "(required)" : _editing.Name,
            Icons.CopyDoc, () => EditText("Tab name", _editing.Name, 40, v =>
            {
                _editing.Name = v.Trim();
                Back();
            })));

        stack.Children.Add(CycleRow("Match", _editing.FilterTree!.Mode == FilterMode.And
            ? "All filters (AND)" : "Any filter (OR)", () =>
        {
            _editing.FilterTree!.Mode = _editing.FilterTree.Mode == FilterMode.And
                ? FilterMode.Or : FilterMode.And;
            Replace(RenderTabEditor);
        }));

        stack.Children.Add(CycleRow("Include", CategoriesLabel((LibraryFilter.Categories)_editing.Categories),
            () =>
        {
            _editing.Categories = NextCategories(_editing.Categories);
            Replace(RenderTabEditor);
        }));

        stack.Children.Add(SectionLabel("FILTERS"));
        var filters = _editing.FilterTree!.Children;
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
            stack.Children.Add(DangerRow("Delete tab", "Remove this tab and its Steam collection",
                Icons.Close, DeleteTab));
        }
        stack.Children.Add(Row("Cancel", "Discard changes", Icons.ExitFullscreen, () => Back()));

        SetContent(stack);
    }

    private async void SaveTab()
    {
        if (string.IsNullOrWhiteSpace(_editing.Name))
        {
            Toast("A tab needs a name.");
            return;
        }
        if (_editing.FilterTree!.Children.Count == 0 || !_editing.FilterTree.Children.All(LibraryFilter.IsValid))
        {
            Toast("Finish every filter first (no ⚠).");
            return;
        }

        if (_editingOriginal is null)
        {
            _editing.Position = _config.CustomTabs.Count == 0
                ? 0 : _config.CustomTabs.Max(t => t.Position) + 1;
            _config.CustomTabs.Add(_editing);
        }
        else
        {
            var index = _config.CustomTabs.IndexOf(_editingOriginal);
            _editing.Position = _editingOriginal.Position;
            _editing.CollectionId = _editingOriginal.CollectionId;
            if (index >= 0)
            {
                _config.CustomTabs[index] = _editing;
            }
        }

        if (!await TryPersistTabsAsync("save"))
        {
            return;
        }
        // Drop back to the list, then materialize in the background.
        _stack.Clear();
        Replace(RenderTabList);
        _ = SyncQuietly();
    }

    private async void DeleteTab()
    {
        if (_editingOriginal is null)
        {
            return;
        }
        _config.CustomTabs.Remove(_editingOriginal);
        if (!await TryPersistTabsAsync("delete"))
        {
            return;
        }
        _stack.Clear();
        Replace(RenderTabList);
        _ = SyncQuietly();
    }

    private Task PersistTabsAsync()
    {
        var tabs = _config.CustomTabs;
        return LibraryTabManager.MutateConfigAsync<object?>(cfg =>
        {
            cfg.CustomTabs = tabs;
            return null;
        });
    }

    private async Task<bool> TryPersistTabsAsync(string operation)
    {
        try
        {
            await PersistTabsAsync();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Library tab {operation} failed: {ex.Message}");
            _config = LibraryTabManager.LoadConfig();
            Toast($"Could not {operation} the tab. Try again.");
            _stack.Clear();
            Replace(RenderTabList);
            return false;
        }
    }

    private async Task SyncQuietly()
    {
        try
        {
            var summary = await _manager.SyncAllAsync();
            Log.Info($"Library tabs (builder): {summary}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Library-tab sync failed: {ex.Message}");
        }
    }

    // ---- Level: filter type picker ----

    private static readonly (FilterKind Kind, string Label, string Desc)[] FilterKinds =
    [
        (FilterKind.Tag, "Tag / Genre", "Games with a store tag"),
        (FilterKind.Installed, "Installed", "Installed or not"),
        (FilterKind.Collection, "Collection", "In a Steam collection"),
        (FilterKind.Regex, "Title", "Title matches a pattern"),
        (FilterKind.SdCard, "SD Card", "Installed on a card"),
        (FilterKind.TimePlayed, "Playtime", "Above/below hours played"),
        (FilterKind.SizeOnDisk, "Size", "Above/below install size"),
        (FilterKind.ReviewScore, "Review score", "Above/below a score"),
        (FilterKind.ReleaseDate, "Release date", "Before/after a date"),
        (FilterKind.LastPlayed, "Last played", "Before/after a date"),
        (FilterKind.Platform, "Platform", "Steam or non-Steam"),
        (FilterKind.Whitelist, "Whitelist", "Only these games"),
        (FilterKind.Blacklist, "Blacklist", "Exclude these games"),
        (FilterKind.Merge, "Merge group", "Nested AND/OR of filters"),
    ];

    private FilterNode? _replacingFilter;

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
            var list = _editing.FilterTree!.Children;
            var idx = list.IndexOf(_replacingFilter);
            if (idx >= 0)
            {
                list[idx] = node;
            }
            PopIfAny();
        }
        else
        {
            _editing.FilterTree!.Children.Add(node);
        }
        // Replace the picker level with the editor for the new node.
        _current = () => RenderFilterEditor(node);
        RenderFilterEditor(node);
    }

    // ---- Level: filter editor ----

    private void OpenFilterEditor(FilterNode node) => Navigate(() => RenderFilterEditor(node));

    private void RenderFilterEditor(FilterNode node)
    {
        var stack = NewStack(FilterKindLabel(node.Kind));

        BuildFilterParams(stack, node);

        if (LibraryFilter.CanInvert(node.Kind))
        {
            stack.Children.Add(CycleRow("Result", node.Inverted ? "Inverted (NOT)" : "Normal", () =>
            {
                node.Inverted = !node.Inverted;
                Replace(() => RenderFilterEditor(node));
            }));
        }

        stack.Children.Add(SectionLabel(""));
        stack.Children.Add(Row("Change type", "Pick a different filter", Icons.Wrench,
            () => OpenFilterPicker(node)));
        stack.Children.Add(DangerRow("Remove filter", "Delete this filter", Icons.Close, () =>
        {
            RemoveNode(_editing.FilterTree!, node);
            Back();
        }));
        stack.Children.Add(PrimaryRow("Done", "Back to the tab", Icons.Play, () => Back()));

        SetContent(stack);
    }

    private void BuildFilterParams(StackPanel stack, FilterNode node)
    {
        switch (node.Kind)
        {
            case FilterKind.Installed:
                stack.Children.Add(CycleRow("State", node.BoolValue ? "Installed" : "Not installed",
                    () => { node.BoolValue = !node.BoolValue; Replace(() => RenderFilterEditor(node)); }));
                break;

            case FilterKind.Platform:
                stack.Children.Add(CycleRow("Platform", node.Platform == PlatformKind.Steam
                    ? "Steam" : "Non-Steam", () =>
                {
                    node.Platform = node.Platform == PlatformKind.Steam
                        ? PlatformKind.NonSteam : PlatformKind.Steam;
                    Replace(() => RenderFilterEditor(node));
                }));
                break;

            case FilterKind.Regex:
                stack.Children.Add(Row("Pattern", string.IsNullOrEmpty(node.Pattern)
                    ? "(required)" : node.Pattern, Icons.CopyDoc, () =>
                    EditText("Title pattern", node.Pattern, 64, v =>
                    {
                        node.Pattern = v;
                        Back();
                    })));
                break;

            case FilterKind.Tag:
                stack.Children.Add(Row("Tags", node.TagIds.Count == 0
                    ? "(choose one or more)" : $"{node.TagIds.Count} selected", Icons.Wrench,
                    () => OpenTagPicker(node)));
                stack.Children.Add(CycleRow("Match", node.Mode == FilterMode.And
                    ? "All tags (AND)" : "Any tag (OR)", () =>
                {
                    node.Mode = node.Mode == FilterMode.And ? FilterMode.Or : FilterMode.And;
                    Replace(() => RenderFilterEditor(node));
                }));
                break;

            case FilterKind.Collection:
                stack.Children.Add(Row("Collection", string.IsNullOrEmpty(node.CollectionId)
                    ? "(choose one)" : CollectionName(node.CollectionId), Icons.Wrench,
                    () => OpenCollectionPicker(node)));
                break;

            case FilterKind.Whitelist:
            case FilterKind.Blacklist:
                stack.Children.Add(Row("Games", node.AppIds.Count == 0
                    ? "(choose games)" : $"{node.AppIds.Count} selected", Icons.Wrench,
                    () => OpenGamePicker(node)));
                break;

            case FilterKind.ReviewScore:
                stack.Children.Add(CycleRow("Source", node.ScoreType == ReviewScoreType.SteamPercent
                    ? "Steam %" : "Metacritic", () =>
                {
                    node.ScoreType = node.ScoreType == ReviewScoreType.SteamPercent
                        ? ReviewScoreType.Metacritic : ReviewScoreType.SteamPercent;
                    Replace(() => RenderFilterEditor(node));
                }));
                AddCondition(stack, node);
                AddStepper(stack, "Score", node.Threshold, 0, 100, 5,
                    v => { node.Threshold = v; Replace(() => RenderFilterEditor(node)); });
                break;

            case FilterKind.TimePlayed:
                AddCondition(stack, node);
                stack.Children.Add(CycleRow("Units", node.Units switch
                {
                    TimeUnit.Minutes => "Minutes",
                    TimeUnit.Days => "Days",
                    _ => "Hours",
                }, () =>
                {
                    node.Units = node.Units switch
                    {
                        TimeUnit.Minutes => TimeUnit.Hours,
                        TimeUnit.Hours => TimeUnit.Days,
                        _ => TimeUnit.Minutes,
                    };
                    Replace(() => RenderFilterEditor(node));
                }));
                AddStepper(stack, "Amount", node.Threshold, 0, 1000, 1,
                    v => { node.Threshold = v; Replace(() => RenderFilterEditor(node)); });
                break;

            case FilterKind.SizeOnDisk:
                AddCondition(stack, node);
                AddStepper(stack, "Size (GB)", node.Threshold, 0, 2000, 5,
                    v => { node.Threshold = v; Replace(() => RenderFilterEditor(node)); });
                break;

            case FilterKind.ReleaseDate:
            case FilterKind.LastPlayed:
                AddCondition(stack, node);
                AddStepper(stack, "Days ago", node.DaysAgo, 0, 3650, 30,
                    v => { node.DaysAgo = (int)v; node.Year = 0; Replace(() => RenderFilterEditor(node)); });
                stack.Children.Add(Caption("0 days = use no date. (Absolute dates: edit config.)"));
                break;

            case FilterKind.SdCard:
                stack.Children.Add(CycleRow("Card", node.CardScope switch
                {
                    SdCardScope.Inserted => "Currently inserted",
                    SdCardScope.Any => "Any tracked card",
                    _ => CardName(node.ContentId),
                }, () => CycleCardScope(node)));
                break;

            case FilterKind.Merge:
                stack.Children.Add(CycleRow("Match", node.Mode == FilterMode.And
                    ? "All (AND)" : "Any (OR)", () =>
                {
                    node.Mode = node.Mode == FilterMode.And ? FilterMode.Or : FilterMode.And;
                    Replace(() => RenderFilterEditor(node));
                }));
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
        => stack.Children.Add(CycleRow("Condition", node.Condition == ThresholdCondition.Above
            ? "At or above" : "Below", () =>
        {
            node.Condition = node.Condition == ThresholdCondition.Above
                ? ThresholdCondition.Below : ThresholdCondition.Above;
            Replace(() => RenderFilterEditor(node));
        }));

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
                    _current = () => RenderChildEditor(group, child);
                    RenderChildEditor(group, child);
                }));
            }
            SetContent(stack);
        });
    }

    private void OpenChildEditor(FilterNode group, FilterNode child)
        => Navigate(() => RenderChildEditor(group, child));

    private void RenderChildEditor(FilterNode group, FilterNode child)
    {
        var stack = NewStack(FilterKindLabel(child.Kind));
        BuildFilterParams(stack, child);
        if (LibraryFilter.CanInvert(child.Kind))
        {
            stack.Children.Add(CycleRow("Result", child.Inverted ? "Inverted (NOT)" : "Normal", () =>
            {
                child.Inverted = !child.Inverted;
                Replace(() => RenderChildEditor(group, child));
            }));
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

    private async void OpenTagPicker(FilterNode node)
    {
        Navigate(() => RenderLoading("Tags"));
        _tags ??= await SteamCollections.GetLibraryTagsAsync();
        var selected = new HashSet<long>(node.TagIds.Select(static id => (long)id));
        Replace(() => RenderMultiSelect("Tags", _tags!.Select(t => ((long)t.TagId, $"{t.Name} ({t.Count})")),
            selected, () =>
        {
            node.TagIds = selected.Select(static id => checked((int)id)).ToList();
            Back();
        }));
    }

    private async void OpenGamePicker(FilterNode node)
    {
        Navigate(() => RenderLoading("Games"));
        _games ??= await SteamCollections.GetGamesAsync();
        var selected = new HashSet<long>(node.AppIds);
        Replace(() => RenderMultiSelect("Games", _games!.Select(g => (g.AppId, g.Name)), selected, () =>
        {
            node.AppIds = selected.ToList();
            Back();
        }));
    }

    private async void OpenCollectionPicker(FilterNode node)
    {
        Navigate(() => RenderLoading("Collections"));
        _collections ??= await SteamCollections.ListAsync();
        Replace(() =>
        {
            var stack = NewStack("Collection");
            foreach (var col in _collections!)
            {
                var c = col;
                stack.Children.Add(Row(c.Name, $"{c.AppIds.Count} games", Icons.Wrench, () =>
                {
                    node.CollectionId = c.Id;
                    Back();
                }));
            }
            if (_collections!.Count == 0)
            {
                stack.Children.Add(Caption("No collections found — is Steam open?"));
            }
            SetContent(stack);
        });
    }

    private void RenderMultiSelect(string title, IEnumerable<(long Id, string Label)> items,
        HashSet<long> selected, Action onDone)
    {
        var stack = NewStack(title);
        stack.Children.Add(PrimaryRow("Done", $"{selected.Count} selected", Icons.Play, onDone));
        foreach (var (id, label) in items.Take(400))
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

    // ---- Level: card manager ----

    private async void RenderCardList()
    {
        SetContent(NewStack("Card Manager").Also(s => s.Children.Add(Caption("Scanning cards…"))));
        IReadOnlyList<LibraryTabManager.CardView> cards;
        try
        {
            cards = await _manager.ListCardsAsync();
            _config = LibraryTabManager.LoadConfig();
        }
        catch (Exception ex)
        {
            Log.Warn($"Card list failed: {ex.Message}");
            cards = Array.Empty<LibraryTabManager.CardView>();
        }
        var stack = NewStack("Card Manager");
        if (cards.Count == 0)
        {
            stack.Children.Add(Caption("No SD-card libraries tracked yet. Format or add one first."));
        }
        foreach (var card in cards)
        {
            var c = card;
            var marker = c.Inserted ? "★ inserted" : "⦸ ejected";
            var state = $"{c.GameCount} games · {marker}" + (c.Enabled ? "" : " · tab off")
                + (c.Hidden ? " · hidden" : "");
            stack.Children.Add(Row(c.Name, state, Icons.SdCard, () => RenderCardEditor(c)));
        }
        stack.Children.Add(SectionLabel(""));
        stack.Children.Add(Row("Back", "Return to tabs", Icons.ExitFullscreen, () => Back()));
        SetContent(stack);
    }

    private void RenderCardEditor(LibraryTabManager.CardView card)
    {
        Navigate(() =>
        {
            var stack = NewStack(card.Name);
            stack.Children.Add(Caption(card.Inserted ? "Currently inserted." : "Not inserted (remembered)."));
            stack.Children.Add(Row("Rename", card.Name, Icons.CopyDoc, () =>
                EditText("Card name", card.Name, 40, async v =>
                {
                    await _manager.RenameCardAsync(card.ContentId, v);
                    // Drop the text-entry level and the card editor, landing on a fresh
                    // card list (with the tab list still underneath).
                    PopIfAny();
                    PopIfAny();
                    Replace(RenderCardList);
                    _ = SyncQuietly();
                })));
            stack.Children.Add(CycleRow("Steam tab", card.Enabled ? "On" : "Off", async () =>
            {
                await _manager.SetCardEnabledAsync(card.ContentId, !card.Enabled);
                PopIfAny();
                Replace(RenderCardList);
                _ = SyncQuietly();
            }));
            stack.Children.Add(CycleRow("Hidden", card.Hidden ? "Yes" : "No", async () =>
            {
                await _manager.SetCardHiddenAsync(card.ContentId, !card.Hidden);
                PopIfAny();
                Replace(RenderCardList);
                _ = SyncQuietly();
            }));
            stack.Children.Add(Row("View games", $"{card.GameCount} installed", Icons.Grid4,
                () => OpenGameList(card)));
            stack.Children.Add(SectionLabel(""));
            stack.Children.Add(DangerRow("Forget card", "Remove its tab and tracking", Icons.Close,
                async () =>
                {
                    await _manager.ForgetCardAsync(card.ContentId);
                    PopIfAny();
                    Replace(RenderCardList);
                    _ = SyncQuietly();
                }));
            stack.Children.Add(Row("Back", "Return to cards", Icons.ExitFullscreen, () => Back()));
            SetContent(stack);
        });
    }

    private async void OpenGameList(LibraryTabManager.CardView card)
    {
        Navigate(() => RenderLoading(card.Name));
        _games ??= await SteamCollections.GetGamesAsync();
        var names = _games!.ToDictionary(g => g.AppId, g => g.Name);
        Replace(() =>
        {
            var stack = NewStack($"{card.Name} — Games");
            if (card.AppIds.Count == 0)
            {
                stack.Children.Add(Caption("No games recorded on this card."));
            }
            foreach (var id in card.AppIds)
            {
                stack.Children.Add(Row(names.TryGetValue(id, out var nm) ? nm
                    : id.ToString(CultureInfo.InvariantCulture), "", null, null));
            }
            stack.Children.Add(SectionLabel(""));
            stack.Children.Add(Row("Back", "Return", Icons.ExitFullscreen, () => Back()));
            SetContent(stack);
        });
    }

    // ---- Text entry (name / regex / rename) ----

    private void EditText(string title, string current, int maxLen, Action<string> onAccept)
    {
        // Prefer the separate keyboard window beside the sidebar (game mode); fall back
        // to an inline keyboard screen if none is available.
        if (KeyboardService.Request(title, current, v => onAccept(v ?? "")))
        {
            return;
        }
        Navigate(() =>
        {
            var stack = NewStack(title);
            var box = new TextBox { Text = current, MaxLength = maxLen, Margin = new Avalonia.Thickness(0, 0, 0, 6) };
            stack.Children.Add(box);
            var keyboard = new OnScreenKeyboard { Target = box };
            keyboard.Accepted += (_, _) => { onAccept(box.Text ?? ""); };
            stack.Children.Add(keyboard);
            stack.Children.Add(PrimaryRow("Accept", "Save this text", Icons.Play,
                () => onAccept(box.Text ?? "")));
            stack.Children.Add(Row("Cancel", "Discard", Icons.ExitFullscreen, () => Back()));
            SetContent(stack);
        });
    }

    // ---- Shared builders ----

    private StackPanel NewStack(string heading)
    {
        var stack = new StackPanel { Spacing = 4 };
        if (!string.IsNullOrEmpty(heading))
        {
            stack.Children.Add(new TextBlock
            {
                Text = heading,
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                Margin = new Avalonia.Thickness(0, 0, 0, 4),
            });
        }
        return stack;
    }

    private void RenderLoading(string title)
    {
        var stack = NewStack(title);
        stack.Children.Add(Caption("Loading from Steam…"));
        SetContent(stack);
    }

    private CardButton Row(string title, string desc, Geometry? icon, Action? onClick)
    {
        var button = new CardButton { Title = title, Description = desc, IconGeometry = icon };
        if (onClick is not null)
        {
            button.Click += (_, _) => onClick();
        }
        return button;
    }

    private CardButton PrimaryRow(string title, string desc, Geometry? icon, Action onClick)
    {
        var button = Row(title, desc, icon, onClick);
        button.Classes.Add("primary");
        return button;
    }

    private CardButton DangerRow(string title, string desc, Geometry? icon, Action onClick)
    {
        var button = Row(title, desc, icon, onClick);
        button.Classes.Add("danger");
        return button;
    }

    private CardButton CycleRow(string label, string value, Action onClick)
        => Row(label, value, Icons.Restart, onClick).Also(b => b.TrailingText = "↔");

    private TextBlock Caption(string text) => new()
    {
        Text = text,
        Classes = { "caption" },
        TextWrapping = TextWrapping.Wrap,
        Margin = new Avalonia.Thickness(2, 0, 2, 4),
    };

    private TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        Classes = { "eyebrow" },
        Margin = new Avalonia.Thickness(2, 6, 2, 2),
    };

    private void AddStepper(StackPanel stack, string label, double value, double min, double max,
        double step, Action<double> onChange)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        var text = new TextBlock
        {
            Text = $"{label}: {value.ToString("0.##", CultureInfo.InvariantCulture)}",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(4, 0, 0, 0),
        };
        Grid.SetColumn(text, 0);
        var minus = new Button { Content = "−", Width = 46, Margin = new Avalonia.Thickness(4, 0, 4, 0) };
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

    // No inner ScrollViewer: the overlay's ContentScroller owns scrolling and its
    // GotFocus→BringIntoView keeps the focused control (incl. keyboard keys) on screen.
    private void SetContent(StackPanel stack)
    {
        Content = stack;
        FocusFirst(stack);
    }

    private void FocusFirst(StackPanel stack) => Dispatcher.UIThread.Post(() =>
    {
        foreach (var child in stack.Children)
        {
            if (child is Button { IsEffectivelyEnabled: true } b)
            {
                b.Focus(NavigationMethod.Directional);
                return;
            }
            if (child is Grid grid)
            {
                foreach (var gc in grid.Children)
                {
                    if (gc is Button gb)
                    {
                        gb.Focus(NavigationMethod.Directional);
                        return;
                    }
                }
            }
        }
    });

    private void Toast(string message) => Log.Info($"Library tabs: {message}");

    // ---- Value helpers ----

    private void CycleCardScope(FilterNode node)
    {
        var cards = _config.CardLibraries;
        // Inserted → Any → each specific card → Inserted.
        switch (node.CardScope)
        {
            case SdCardScope.Inserted:
                node.CardScope = SdCardScope.Any;
                break;
            case SdCardScope.Any:
                if (cards.Count > 0)
                {
                    node.CardScope = SdCardScope.Specific;
                    node.ContentId = cards[0].ContentId;
                }
                else
                {
                    node.CardScope = SdCardScope.Inserted;
                }
                break;
            default:
                var idx = cards.FindIndex(c => c.ContentId == node.ContentId);
                if (idx < 0 || idx + 1 >= cards.Count)
                {
                    node.CardScope = SdCardScope.Inserted;
                    node.ContentId = "";
                }
                else
                {
                    node.ContentId = cards[idx + 1].ContentId;
                }
                break;
        }
        Replace(() => RenderFilterEditor(node));
    }

    private static void RemoveNode(FilterNode group, FilterNode node) => group.Children.Remove(node);

    private static int NextCategories(int current)
    {
        // Cycle a few useful presets rather than exposing the full bitfield.
        var g = (int)LibraryFilter.Categories.Games;
        var gs = g | (int)LibraryFilter.Categories.Software;
        var gsh = gs | (int)LibraryFilter.Categories.Hidden;
        if (current == g)
        {
            return gs;
        }
        return current == gs ? gsh : g;
    }

    private static string CategoriesLabel(LibraryFilter.Categories c)
    {
        var parts = new List<string>();
        if (c.HasFlag(LibraryFilter.Categories.Games))
        {
            parts.Add("Games");
        }
        if (c.HasFlag(LibraryFilter.Categories.Software))
        {
            parts.Add("Software");
        }
        if (c.HasFlag(LibraryFilter.Categories.Music))
        {
            parts.Add("Music");
        }
        if (parts.Count == 0)
        {
            parts.Add("Games");
        }
        if (c.HasFlag(LibraryFilter.Categories.Hidden))
        {
            parts.Add("+Hidden");
        }
        return string.Join(", ", parts);
    }

    private string CollectionName(string id)
        => _collections?.FirstOrDefault(c => c.Id == id)?.Name ?? "selected";

    private string CardName(string contentId)
        => _config.CardLibraries.FirstOrDefault(c => c.ContentId == contentId)?.Name ?? "a card";

    private static string FilterKindLabel(FilterKind kind)
        => FilterKinds.FirstOrDefault(f => f.Kind == kind).Label ?? kind.ToString();

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
            FilterKind.SdCard => node.CardScope == SdCardScope.Specific
                ? $"On {CardName(node.ContentId)}"
                : node.CardScope == SdCardScope.Any ? "On any card" : "On inserted card",
            FilterKind.TimePlayed => $"Playtime {Cond(node)} {node.Threshold:0.##}",
            FilterKind.SizeOnDisk => $"Size {Cond(node)} {node.Threshold:0.##} GB",
            FilterKind.ReviewScore => $"Score {Cond(node)} {node.Threshold:0}",
            FilterKind.ReleaseDate => $"Released {(node.DaysAgo > 0 ? $"< {node.DaysAgo}d ago" : "date")}",
            FilterKind.LastPlayed => $"Played {(node.DaysAgo > 0 ? $"< {node.DaysAgo}d ago" : "date")}",
            FilterKind.Platform => node.Platform == PlatformKind.Steam ? "Steam" : "Non-Steam",
            FilterKind.Whitelist => $"Whitelist ({node.AppIds.Count})",
            FilterKind.Blacklist => $"Blacklist ({node.AppIds.Count})",
            FilterKind.Merge => $"Group ({node.Children.Count})",
            _ => node.Kind.ToString(),
        };
    }

    private static string Cond(FilterNode node) => node.Condition == ThresholdCondition.Above ? "≥" : "<";

    private static CustomTabConfig Clone(CustomTabConfig t) => new()
    {
        Id = t.Id,
        Name = t.Name,
        Enabled = t.Enabled,
        Position = t.Position,
        Categories = t.Categories,
        CollectionId = t.CollectionId,
        FilterTree = t.FilterTree?.Clone() ?? new FilterNode { Kind = FilterKind.Merge },
    };
}

/// <summary>Tiny fluent helper so builders can configure-and-return in one expression.</summary>
internal static class FluentExtensions
{
    public static T Also<T>(this T value, Action<T> configure)
    {
        configure(value);
        return value;
    }
}
