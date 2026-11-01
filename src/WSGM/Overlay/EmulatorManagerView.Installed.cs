using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using WSGM.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

public sealed partial class EmulatorManagerView
{
    private string _catalogFamily = "all";
    private string _catalogSearch = "";

    // ---- Installed ----

    private void AddInstalled(StackPanel body, EmulatorShown shown)
    {
        var page = shown.Page;
        AddUpdateBanner(body, shown);
        if (page.Installed.Length == 0)
        {
            body.Children.Add(Tagged(Caption(page.Emulators.Initialized
                ? "No emulator is installed yet. Get one from the catalogue, or open one there and choose Use my "
                  + "installation for a copy you already have."
                : "Reading the installed emulators…"), "emu.installed.empty"));
            body.Children.Add(Tagged(CommandStrip(Command("emu.installed.get", "Get emulators",
                () => SelectView("catalog", true), true)), "emu.installed.get-strip"));
            return;
        }

        var cards = page.Installed.Select(item =>
        {
            var id = item.Id;
            var tag = "emu.installed:" + id;
            var line = new StackPanel { Spacing = 4 };
            line.Children.Add(Compact(Caption(item.VersionLine)));
            line.Children.Add(Compact(Caption(item.Summary)));
            var chips = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            foreach (var chip in item.Chips)
            {
                var badge = Chip(chip.Label, chip.Tone);
                badge.Margin = new Thickness(0, 0, 6, 4);
                chips.Children.Add(badge);
            }

            line.Children.Add(chips);
            var row = new OverlayMediaRow(item.Name, "", Icon(item.AppId, 56), line, [],
                () => Open(tag, () => RenderInstallation(id)));
            row.Padding = new Thickness(12, 10);
            row.VerticalAlignment = VerticalAlignment.Stretch;
            return (Control)Tagged(row, tag);
        }).ToArray();
        body.Children.Add(CardGrid("emu.installed.grid:" + string.Join(",", page.Installed.Select(item => item.Id)),
            cards, _wide ? 3 : 2));
    }

    private static TextBlock Compact(TextBlock caption)
    {
        caption.Margin = default;
        return caption;
    }

    /// <summary>
    ///     The waiting updates with Update all, Update all's progress while it runs, and its result once it ends,
    ///     worded as a partial failure when one update failed.
    /// </summary>
    private void AddUpdateBanner(StackPanel body, EmulatorShown shown)
    {
        var page = shown.Page;
        if (page.UpdateAll is { Running: true } running)
        {
            body.Children.Add(Banner("emu.update-all.running", FAInfoBarSeverity.Informational, null,
                $"Updating {Math.Min(running.Done + 1, running.Total)} of {running.Total}: {running.Current}"));
            return;
        }

        if (page.UpdateAll is { Outcome.Length: > 0 } result)
        {
            body.Children.Add(Banner("emu.update-all.result",
                result.Failed.Count > 0 || result.Cancelled ? FAInfoBarSeverity.Warning : FAInfoBarSeverity.Success,
                null, result.Outcome));
        }

        if (page.Updates.Length == 0)
        {
            return;
        }

        var label = page.Updates.Length switch
        {
            1 => "Update " + page.Updates[0].Name,
            2 => "Update both",
            _ => "Update all"
        };
        var update = Command("emu.update-all", label, shown.Progress.Busy
            ? null
            : () => Run(_service!.UpdateAllEmulatorsAsync, "update all"), true);
        update.Padding = new Thickness(16, 6);
        update.Margin = default;
        update.VerticalAlignment = VerticalAlignment.Center;
        var text = new TextBlock
        {
            Text = page.UpdatesSummary, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };
        grid.Children.Add(text);
        Grid.SetColumn(update, 1);
        grid.Children.Add(update);
        body.Children.Add(Card(grid, "emu.updates"));
    }

    // ---- Get emulators ----

    private void AddCatalog(StackPanel body, EmulatorShown shown)
    {
        var page = shown.Page;
        body.Children.Add(Tagged(Row("Search emulators or systems",
                _catalogSearch.Length > 0 ? _catalogSearch : "Search by emulator or system name", Icons.ListLines,
                () => EditText("Search emulators or systems", _catalogSearch, 120, value => _catalogSearch = value)),
            "emu.catalog.search"));
        var searched = page.Catalog.Where(item => Matches(_catalogSearch, item.Name, item.Systems)).ToArray();
        List<Control> filters =
        [
            Command("emu.family:all", $"All ({searched.Length})", () => Family("all"), _catalogFamily == "all")
        ];
        filters.AddRange(page.Families.Select(family => (Control)Command("emu.family:" + family,
            $"{family} ({searched.Count(item => item.Families.Contains(family))})", () => Family(family),
            _catalogFamily == family)));
        body.Children.Add(Tagged(CommandStrip([.. filters]), "emu.catalog.filters"));
        var listed = searched.Where(item => _catalogFamily == "all" || item.Families.Contains(_catalogFamily))
            .ToArray();
        if (listed.Length == 0)
        {
            body.Children.Add(Tagged(Caption(page.Catalog.Length == 0
                ? "The emulator catalogue could not be read."
                : "No emulator matches the search. Clear it or choose All."), "emu.catalog.empty"));
            return;
        }

        var cards = listed.Select(item => CatalogCard(item, shown)).ToArray();
        body.Children.Add(CardGrid("emu.catalog.grid:" + string.Join(",", listed.Select(item => item.DefinitionId)),
            cards, _wide ? 2 : 1));
        body.Children.Add(Tagged(Caption("Already have one installed yourself? Open its card and choose Use my "
                                         + "installation."), "emu.catalog.external"));
        return;

        void Family(string family)
        {
            _catalogFamily = family;
            CurrentLevel?.Invoke();
        }
    }

    private Control CatalogCard(EmulatorCatalogItem item, EmulatorShown shown)
    {
        var id = item.DefinitionId;
        var tag = "emu.catalog:" + id;
        var line = Compact(Caption(item.Systems + (item.InstalledVersion.Length > 0
            ? " · " + item.InstalledVersion + " installed"
            : item.LatestVersion.Length > 0
                ? " · latest " + item.LatestVersion
                : "")));
        List<Control> chips = item.InstalledVersion.Length > 0 ? [Chip("Installed", "good")] : [];
        var row = Tagged(new OverlayMediaRow(item.Name, item.Requirement, Icon(0, 44), line, chips,
            () => Open(tag, () => RenderDefinition(id))), tag);
        row.Classes.Add("flat");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(row);
        if (item.InstalledVersion.Length == 0)
        {
            var channel = shown.Snapshot.Definitions.FirstOrDefault(definition => definition.Id == id)?.Channels
                .FirstOrDefault() ?? "";
            var install = Command(tag + ".install", "Install", shown.Progress.Busy || channel.Length == 0
                ? null
                : () => Run(token => _service!.InstallEmulatorAsync(id, channel, token), "install"));
            install.VerticalAlignment = VerticalAlignment.Center;
            install.Padding = new Thickness(14, 6);
            install.Margin = new Thickness(0, 0, 4, 0);
            AutomationProperties.SetName(install, "Install " + item.Name);
            Grid.SetColumn(install, 1);
            grid.Children.Add(install);
        }

        var card = Card(grid, tag + ".card");
        card.Padding = new Thickness(4);
        return card;
    }

    /// <summary>Opens Get emulators searched for one system, from a system that has games and no emulator.</summary>
    private void FindEmulatorFor(string systemName)
    {
        _catalogSearch = systemName;
        _catalogFamily = "all";
        SelectView("catalog", false);
    }
}
