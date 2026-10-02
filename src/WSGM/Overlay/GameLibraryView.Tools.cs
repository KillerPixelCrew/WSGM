using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using SteamUiToolkit;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

public sealed partial class GameLibraryView
{
    private static readonly (string Id, string Label)[] Assets =
        [("grid", "Capsule"), ("wide", "Wide Capsule"), ("hero", "Hero"), ("logo", "Logo"), ("icon", "Icon")];

    private string _asset = "grid";
    private string _extensions = ".lnk .url .exe";
    private string _fill = "Catalog";
    private string _folderPath = "";
    private bool _grid = true;
    private string _group = "";
    private string? _matchEntry;
    private string? _matchError;
    private GameLibraryMatchesAnswer? _matches;
    private string _search = "";
    private int _shown = 48;
    private string _sourceId = "";
    private bool _subfolders = true;

    private void ChooseMode(GameLibraryEntry entry, string mode)
    {
        if (mode == nameof(ImportMode.SteamIntegration) && entry.RequiresAcknowledgement && !entry.Acknowledged)
        {
            Navigate(() => RenderAcknowledge(entry.Id, entry.Name));
        }
        else
        {
            Run(token => _service!.SetModeAsync(entry.Id, mode, entry.Acknowledged, token));
        }
    }

    private void RenderAddFolder()
    {
        var body = NewStack("Add a shortcuts folder");
        body.Children.Add(Tagged(
            Row("Choose folder", _folderPath, Icons.Grid4, () => _ = RunSafelyAsync(ChooseAsync(), "folder picker")),
            "folder.choose"));
        body.Children.Add(ToggleRow("Include subfolders", _subfolders, value =>
        {
            _subfolders = value;
            RenderAddFolder();
        }));
        body.Children.Add(Tagged(Row("File types", _extensions, Icons.ListLines, () => EditText("File extensions",
            _extensions, 32,
            value =>
            {
                _extensions = value;
                RenderAddFolder();
            })), "folder.types"));
        body.Children.Add(Tagged(PrimaryRow("Add folder", "Titles are offered at the next scan", Icons.Grid4,
            _folderPath.Length == 0 ? null : () => _ = RunSafelyAsync(AddAsync(), "add folder")), "folder.add"));
        SetContent(body);
        return;

        async Task ChooseAsync()
        {
            var generation = _navigationGeneration;
            var path = await PickPathAsync(true);
            if (path is not null && generation == _navigationGeneration)
            {
                _folderPath = path;
                RenderAddFolder();
            }
        }

        async Task AddAsync()
        {
            var generation = _navigationGeneration;
            var types = _extensions.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim()).ToArray();
            var result = await _service!.AddFolderAsync(_folderPath, _subfolders, types, CancellationToken.None);
            if (generation != _navigationGeneration)
            {
                return;
            }

            if (result.Succeeded)
            {
                Back();
            }
            else
            {
                Toast(result.Error ?? "The folder could not be added.");
            }
        }
    }

    private void RenderReviewInto(StackPanel body, GameLibraryState state, bool importedOnly)
    {
        AddStatus(body, state);
        var tabs = new[]
        {
            ("", "All"), ("new", "New"), ("imported", "Imported"), ("attention", "Needs attention"),
            ("excluded", "Left out")
        };
        body.Children.Add(ToolTabs(importedOnly ? "imported" : _group, tabs.Select(tab => (tab.Item1, tab.Item2,
            (Action)(() =>
            {
                _group = tab.Item1;
                _shown = 48;
                Replace(() => RenderReview(false));
            }))).ToArray()));
        body.Children.Add(Tagged(Row("Search", _search, Icons.ListLines, () => EditText("Search titles", _search, 128,
            value =>
            {
                _search = value;
                _shown = 48;
                RenderReview(importedOnly);
            })), "review.search"));
        var sources = new List<(string, string)> { ("", "All sources") };
        sources.AddRange(state.Sources.Select(source => (source.Id, source.Name)));
        body.Children.Add(ChoiceRow("Source", sources, _sourceId, value =>
        {
            _sourceId = value;
            _shown = 48;
            RenderReview(importedOnly);
        }));
        body.Children.Add(ChoiceRow("Artwork slot", Assets.Select(asset => (asset.Id, asset.Label)).ToArray(), _asset,
            value =>
            {
                _asset = value;
                RenderReview(importedOnly);
            }));
        body.Children.Add(ToggleRow("Grid view", _grid, value =>
        {
            _grid = value;
            RenderReview(importedOnly);
        }));
        var group = importedOnly ? "imported" : _group;
        var entries = state.Entries.Where(entry => (group.Length == 0 || entry.Group == group)
                                                   && (_sourceId.Length == 0 || entry.SourceId == _sourceId) &&
                                                   entry.Name.Contains(_search, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var selectable = entries.Where(entry => entry.Selectable && entry.Action != "Remove").ToArray();
        var allSelected = selectable.Length > 0 && selectable.All(entry => entry.Selected);
        body.Children.Add(Tagged(Row(allSelected ? "Deselect visible" : "Select visible",
            "Applies only to this source, tab and search", Icons.ListLines,
            state.Loading || selectable.Length == 0
                ? null
                : () => Run(token => SelectVisibleAsync(selectable, !allSelected, token))), "review.select"));
        body.Children.Add(Tagged(PrimaryRow($"Apply {state.SelectedCount}", "Save the selected changes to Steam",
            Icons.Play,
            state.Loading || state.SelectedCount == 0 ? null : () => Run(_service!.ApplyAsync)), "apply"));
        body.Children.Add(Tagged(Row("All artwork", "Review and fill selected titles before applying", Icons.Palette,
            state.SelectedCount == 0 ? null : () => Navigate(RenderAllArtwork)), "all-artwork"));
        var cards = new WrapPanel();
        foreach (var entry in entries.Take(_shown))
        {
            var id = entry.Id;
            var slot = entry.Artwork.FirstOrDefault(slot => slot.Asset == _asset);
            var description = GameLibraryRows.Describe(entry) + (entry.Selected ? " · Selected" : "");
            if (_grid)
            {
                cards.Children.Add(PreviewCard(id, slot?.Thumb, entry.Name, description,
                    () => Navigate(() => RenderEntry(id))));
            }
            else
            {
                body.Children.Add(Tagged(Row(entry.Name, description, null, () => Navigate(() => RenderEntry(id))),
                    "entry:" + id));
            }
        }

        if (_grid)
        {
            body.Children.Add(cards);
        }

        if (entries.Length > _shown)
        {
            body.Children.Add(Tagged(Row("Load more", $"{_shown} of {entries.Length}", Icons.ArrowDown, () =>
            {
                _shown += 48;
                RenderReview(importedOnly);
            }), "review.more"));
        }

        if (entries.Length == 0)
        {
            body.Children.Add(Caption("No matching titles."));
        }
    }

    private async Task<SteamUiCommandResult> SelectVisibleAsync(GameLibraryEntry[] entries, bool selected,
        CancellationToken token)
    {
        foreach (var entry in entries)
        {
            if (entry.Selected == selected)
            {
                continue;
            }

            var result = await _service!.ToggleEntryAsync(entry.Id, token);
            if (!result.Succeeded)
            {
                return result;
            }
        }

        return SteamUiCommandResult.Applied;
    }

    private void RenderTitleArtwork(string id, string asset)
    {
        if (_service?.ReadState() is not { } state || state.Entries.FirstOrDefault(entry => entry.Id == id) is not
                { } entry)
        {
            Back();
            return;
        }

        var body = NewStack(entry.Name + " · Staged artwork");
        AddStatus(body, state);
        body.Children.Add(Caption("Changes are saved only when you apply this title to Steam."));
        body.Children.Add(ToolTabs(asset,
            Assets.Select(type => (type.Id, type.Label, (Action)(() => Replace(() => RenderTitleArtwork(id, type.Id)))))
                .ToArray()));
        var slot = entry.Artwork.FirstOrDefault(slot => slot.Asset == asset);
        if (slot is not null)
        {
            body.Children.Add(Caption($"Current: {slot.Kind} · {slot.Provider} · {slot.Index}/{slot.Count}"));
            body.Children.Add(new OverlayPreviewImage(slot.Thumb, 220)
                { Tag = "chosen:" + id + ":" + asset + ":" + slot.Thumb });
        }

        body.Children.Add(Tagged(
            Row("Previous candidate", "", Icons.ArrowLeft,
                state.Loading ? null : () => Run(token => _service.CycleArtworkAsync(id, asset, -1, token))),
            "art.prev"));
        body.Children.Add(Tagged(
            Row("Next candidate", "", Icons.ArrowDown,
                state.Loading ? null : () => Run(token => _service.CycleArtworkAsync(id, asset, 1, token))),
            "art.next"));
        body.Children.Add(Tagged(
            Row("Clear slot", "Stage no artwork for this slot", Icons.Close,
                state.Loading ? null : () => Run(token => _service.ClearArtworkAsync(id, asset, token))), "art.clear"));
        body.Children.Add(Tagged(
            Row("Search game match", entry.MatchName, Icons.ListLines, () => Navigate(() => RenderMatch(id))),
            "match"));
        var answer = _service.ReadArtworkOptions(id, asset);
        if (answer is not null)
        {
            body.Children.Add(Caption(answer.Status + (answer.Detail.Length == 0 ? "" : ": " + answer.Detail)));
            var cards = new WrapPanel();
            foreach (var option in answer.Options)
            {
                var url = option.Url;
                cards.Children.Add(PreviewCard(url, option.Thumb, option.Provider, $"{option.Width}×{option.Height}",
                    () => Run(token => _service.PickArtworkAsync(id, asset, url, token))));
            }

            body.Children.Add(cards);
        }

        if (entry.AppId > 0)
        {
            body.Children.Add(Tagged(Row("Edit current Steam artwork",
                "Applies immediately, independently of staged changes", Icons.Palette,
                () => ArtworkRequested?.Invoke(entry.AppId, entry.Name)), "art.current"));
        }

        SetContent(body);
    }

    private void RenderMatch(string id)
    {
        if (_service?.ReadState().Entries.FirstOrDefault(entry => entry.Id == id) is not { } entry)
        {
            Back();
            return;
        }

        var body = NewStack("Artwork game match");
        body.Children.Add(Caption(entry.MatchName.Length == 0 ? "Automatic match" : entry.MatchName));
        body.Children.Add(Tagged(Row("Search", entry.Name, Icons.ListLines, () => EditText("Find artwork game",
            entry.Name, 128,
            query => _ = RunSafelyAsync(SearchAsync(query), "match search"))), "match.search"));
        body.Children.Add(Tagged(
            Row("Use automatic match", "Discard the fixed match", Icons.Restart,
                () => Run(token => _service!.SetMatchAsync(id, "", "", "", token))), "match.auto"));
        if (_matchEntry == id)
        {
            if (_matchError is not null)
            {
                body.Children.Add(Caption(_matchError));
            }

            foreach (var match in _matches?.Matches ?? [])
            {
                var chosen = match;
                body.Children.Add(Tagged(Row(match.Name, match.ProviderName + (match.Exact ? " · Exact" : ""),
                        Icons.Grid4,
                        () => Run(token =>
                            _service!.SetMatchAsync(id, chosen.Provider, chosen.Id, chosen.Name, token))),
                    "match:" + match.Provider + ":" + match.Id));
            }
        }

        SetContent(body);
        return;

        async Task SearchAsync(string query)
        {
            var generation = _navigationGeneration;
            var result = await _service!.SearchMatchAsync(id, query, CancellationToken.None);
            if (generation != _navigationGeneration)
            {
                return;
            }

            _matchEntry = id;
            _matchError = result.Error;
            _matches = result.Payload is { } payload
                ? payload.Deserialize(GameLibraryJsonContext.Default.GameLibraryMatchesAnswer)
                : null;
            RenderMatch(id);
        }
    }

    private void RenderAllArtwork()
    {
        if (_service?.ReadState() is not { } state)
        {
            return;
        }

        var body = NewStack("All staged artwork");
        AddStatus(body, state);
        body.Children.Add(ChoiceRow("Fill from",
            new[] { ("Catalog", "Launcher first"), ("Providers", "Providers first") }, _fill, value =>
            {
                _fill = value;
                RenderAllArtwork();
            }));
        body.Children.Add(Tagged(
            Row("Fill all", "Replace staged picks for selected titles", Icons.Palette,
                state.Loading ? null : () => Run(token => _service.FillArtworkAsync(_fill, false, "", token))),
            "fill.all"));
        body.Children.Add(Tagged(
            Row("Fill empty slots", "Keep filled and existing artwork", Icons.Palette,
                state.Loading ? null : () => Run(token => _service.FillArtworkAsync(_fill, true, "", token))),
            "fill.empty"));
        body.Children.Add(Tagged(
            Row("Reset all", "Restore staged defaults for selected titles", Icons.Restart,
                state.Loading ? null : () => Run(_service.ResetArtworkAsync)), "fill.reset"));
        foreach (var type in Assets)
        {
            var asset = type.Id;
            body.Children.Add(Tagged(Row("Fill " + type.Label, "Selected titles only", Icons.Palette,
                    state.Loading ? null : () => Run(token => _service.FillArtworkAsync(_fill, false, asset, token))),
                "fill:" + asset));
        }

        foreach (var entry in state.Entries.Where(entry => entry.Selected).Take(_shown))
        {
            body.Children.Add(Caption(entry.Name));
            var row = new WrapPanel();
            foreach (var type in Assets)
            {
                var slot = entry.Artwork.FirstOrDefault(slot => slot.Asset == type.Id);
                var id = entry.Id;
                var asset = type.Id;
                row.Children.Add(PreviewCard(id + ":" + asset, slot?.Thumb, type.Label, slot?.Kind ?? "none",
                    () => Navigate(() => RenderTitleArtwork(id, asset))));
            }

            body.Children.Add(row);
        }

        if (state.SelectedCount > _shown)
        {
            body.Children.Add(Tagged(Row("Load more titles", "", Icons.ArrowDown, () =>
            {
                _shown += 48;
                RenderAllArtwork();
            }), "all.more"));
        }

        body.Children.Add(Tagged(PrimaryRow($"Apply {state.SelectedCount}", "Save selected changes", Icons.Play,
            state.Loading || state.SelectedCount == 0 ? null : () => Run(_service.ApplyAsync)), "apply"));
        SetContent(body);
    }
}
