using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

public sealed partial class EmulatorManagerView
{
    // Core rows each list adds per Load more; RetroArch publishes a few hundred.
    private const int CoreChunk = 40;
    private string _coreInstallation = "";
    private string _coreSearch = "";
    private int _readyShown = CoreChunk;

    private void AddCores(StackPanel body, EmulatorShown shown)
    {
        var frontends = shown.Snapshot.Installations.Where(item => item.DataPolicy.HasCores).ToArray();
        if (frontends.Length == 0)
        {
            body.Children.Add(Tagged(Caption("RetroArch is not installed. Its cores are listed here once it is."),
                "emu.cores.empty"));
            body.Children.Add(Tagged(CommandStrip(Command("emu.cores.get", "Get RetroArch", () =>
            {
                _catalogSearch = "RetroArch";
                SelectView("catalog", false);
            }, true)), "emu.cores.get-strip"));
            return;
        }

        var installation = frontends.FirstOrDefault(item => item.Id == _coreInstallation) ?? frontends[0];
        var id = installation.Id;
        if (frontends.Length > 1)
        {
            body.Children.Add(Tagged(ChoiceRow("RetroArch installation",
                frontends.Select(item => (item.Id, item.Name + " " + item.Version + " · " + item.Channel)).ToArray(),
                id, value =>
                {
                    _coreInstallation = value;
                    CurrentLevel?.Invoke();
                }), "emu.cores.installation"));
        }

        var status = shown.Page.CoreStatus.Where(item => item.InstallationId == id)
            .ToDictionary(item => item.CoreId, StringComparer.Ordinal);

        bool NeedsFiles(EmulatorCore core)
        {
            return status.GetValueOrDefault(core.Id)?.Missing == true;
        }

        var needing = installation.Cores.Count(NeedsFiles);
        body.Children.Add(Tagged(Row("Search cores or systems",
            _coreSearch.Length > 0 ? _coreSearch : "Search by core or system name", Icons.ListLines,
            EditCoreSearch), "emu.cores.search"));
        var update = Command("emu.cores.update", "Update installed cores", shown.Progress.Busy || !installation.Managed
            ? null
            : () => Run(token => _service!.UpdateEmulatorAsync(id, token), "update cores"));
        var summary = Compact(Caption(EmulatorPresentation.Count(installation.Cores.Length, "installed", "installed")
                                      + " · " + EmulatorPresentation.Count(needing, "needs files", "need files")));
        summary.VerticalAlignment = VerticalAlignment.Center;
        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12, Tag = "emu.cores.toolbar"
        };
        toolbar.Children.Add(summary);
        var strip = CommandStrip(update);
        Grid.SetColumn(strip, 1);
        toolbar.Children.Add(strip);
        body.Children.Add(toolbar);

        var cores = installation.Cores.Where(core => Matches(_coreSearch, core.Name, core.Id,
                string.Join(" ", core.Systems), string.Join(" ", core.Systems.Select(EmulatorPresentation.SystemName))))
            .OrderBy(core => core.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (cores.Length == 0)
        {
            body.Children.Add(Tagged(Caption("No core matches the search."), "emu.cores.none"));
            return;
        }

        var list = new StackPanel { Tag = "emu.cores.list" };
        var attention = cores.Where(NeedsFiles).ToArray();
        if (attention.Length > 0)
        {
            list.Children.Add(Tagged(Eyebrow("Needs attention"), "emu.cores.attention"));
            foreach (var core in attention)
            {
                list.Children.Add(CoreRow(installation, core, status.GetValueOrDefault(core.Id)));
            }
        }

        var ready = cores.Where(core => !NeedsFiles(core)).ToArray();
        if (ready.Length > 0)
        {
            list.Children.Add(Tagged(Eyebrow("Ready"), "emu.cores.ready"));
            foreach (var core in ready.Take(_readyShown))
            {
                list.Children.Add(CoreRow(installation, core, null));
            }
        }

        body.Children.Add(Card(list, "emu.cores.card:" + id));
        if (ready.Length > _readyShown)
        {
            body.Children.Add(PagedRows.More(this, "emu.cores.more", $"{_readyShown} of {ready.Length} ready cores",
                "core:" + ready[_readyShown].Id, () =>
                {
                    _readyShown += CoreChunk;
                    CurrentLevel?.Invoke();
                }));
        }
    }

    /// <summary>Asks for the core search, from its row or from Y.</summary>
    private void EditCoreSearch()
    {
        EditText("Search cores or systems", _coreSearch, 120, value =>
        {
            _coreSearch = value;
            _readyShown = CoreChunk;
        });
    }

    private Control CoreRow(EmulatorInstallation installation, EmulatorCore core, EmulatorCoreStatus? missing)
    {
        var systems = string.Join(" · ", core.Systems.Select(EmulatorPresentation.SystemName));
        var detail = missing is { MissingFiles.Length: > 0 }
            ? (systems.Length > 0 ? systems + " · " : "") + "needs "
                                                          + EmulatorPresentation.JoinAnd(missing.MissingFiles)
            : core.MetadataMissing
                ? (systems.Length > 0 ? systems + " · " : "")
                  + "core information missing, so required files can't be checked"
                : systems;
        Control chip = missing is not null
            ? Chip("Needs files", "warn")
            : core.MetadataMissing
                ? Chip("No details", "")
                : Chip("Ready", "good");
        var row = Tagged(new OverlayMediaRow(core.Name, detail, null, null, [chip],
            () => Open("core:" + core.Id, () => RenderCore(installation.Id, core.Id))), "core:" + core.Id);
        row.Classes.Add("flat");
        if (missing is null)
        {
            return row;
        }

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Tag = "core.line:" + core.Id
        };
        grid.Children.Add(row);
        var bios = Command("core.bios:" + core.Id, "BIOS and firmware", () => SelectView("bios", false));
        bios.VerticalAlignment = VerticalAlignment.Center;
        bios.Margin = default;
        Grid.SetColumn(bios, 1);
        grid.Children.Add(bios);
        return grid;
    }

    /// <summary>One core's fixed facts: its file, file types, system mapping and declared files.</summary>
    private void RenderCore(string installationId, string coreId)
    {
        SecondaryHint = "";
        var installation = ReadShown()?.Snapshot.Installations.FirstOrDefault(item => item.Id == installationId);
        var core = installation?.Cores.FirstOrDefault(item => item.Id == coreId);
        if (core is null)
        {
            Back();
            return;
        }

        var details = NewStack("");
        details.Children.Add(Caption("Emulators  ›  RetroArch cores  ›  " + core.Name));
        details.Children.Add(new TextBlock
        {
            Text = core.Name, FontSize = 20, FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        details.Children.Add(Caption(core.Path));
        details.Children.Add(Caption("File types: " + (core.Extensions.Length > 0
            ? string.Join(", ", core.Extensions)
            : "not declared")));
        details.Children.Add(Caption(core.MetadataMissing
            ? "Upstream core information is missing. Choose its system manually on the ROM source."
            : "Systems: " + string.Join(", ", core.Systems.Select(EmulatorPresentation.SystemName))));
        List<string> required = [.. core.RequiredFiles];
        foreach (var file in required)
        {
            details.Children.Add(Caption("Required: " + file));
        }

        details.Children.Add(Tagged(CommandStrip(Command("core.back", "Back to cores", () => Back()),
            Command("core.bios", "BIOS and firmware", () => SelectView("bios", false))), "core.actions"));
        // Fixed facts about the core: the manager's progress must not redraw them.
        SetStaticContent(details);
    }
}
