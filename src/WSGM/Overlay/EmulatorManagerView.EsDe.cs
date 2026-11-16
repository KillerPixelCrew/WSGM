using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using WSGM.Shell;

namespace WSGM.Overlay;

public sealed partial class EmulatorManagerView
{
    /// <summary>
    ///     ES-DE's library in place of an emulator's setup: what it shows, what holds a system back, and each ROM
    ///     folder with whether Library Importer also offers it to Steam.
    /// </summary>
    private Border EsDeCard(EmulatorShown shown)
    {
        var esDe = shown.Page.EsDe;
        var busy = shown.Progress.Busy;
        var body = new StackPanel();
        body.Children.Add(CardTitle("emu.esde.title", "Library", esDe.Summary.Length > 0
            ? Chip(esDe.Summary, esDe.Systems.Any(system => system.Problem.Length == 0) ? "good" : "warn")
            : null));
        body.Children.Add(Tagged(Caption("ES-DE shows the games in your ROM folders and starts them with the "
                                         + "emulators installed here. Your System defaults come first; choose "
                                         + "another emulator for a system or a game in ES-DE."),
            "emu.esde.intro"));
        if (esDe.Status.Length > 0)
        {
            var status = Line("emu.esde.status", esDe.Tone == "bad" ? "Not set up" : "Needs attention", esDe.Status,
                null, StateMark(false, false));
            body.Children.Add(status);
        }

        if (!esDe.Synced)
        {
            body.Children.Add(Tagged(Caption("Setting up ES-DE from your ROM folders…"), "emu.esde.pending"));
        }
        else if (esDe.Folders.Length == 0)
        {
            body.Children.Add(Divider());
            body.Children.Add(Tagged(Caption("No ROM folders yet. Add them in Library Importer, under Sources; "
                                             + "ES-DE shows their games once they're added."), "emu.esde.empty"));
        }
        else
        {
            body.Children.Add(Eyebrow("Systems"));
            foreach (var system in esDe.Systems)
            {
                body.Children.Add(Divider());
                body.Children.Add(Line("emu.esde.system:" + system.SystemId, system.Name, system.Detail, null,
                    StateMark(system.Problem.Length == 0, false)));
            }

            body.Children.Add(Eyebrow("ROM folders"));
            body.Children.Add(Tagged(Compact(Caption("Switch a folder off to keep its games in ES-DE only. Games "
                                                     + "already in Steam stay until you remove them in Library "
                                                     + "Importer.")), "emu.esde.folders.intro"));
            foreach (var folder in esDe.Folders)
            {
                body.Children.Add(Divider());
                body.Children.Add(FolderLine(folder, busy));
            }
        }

        body.Children.Add(Divider());
        var commands = CommandStrip(
            Command("emu.esde.sync", "Sync now", () => Run(_service!.SyncEsDeAsync, "ES-DE sync")),
            Command("emu.esde.defaults", "System defaults", () => SelectView("defaults", false)));
        var footer = new StackPanel { Spacing = 6, Margin = new Thickness(0, 10, 0, 0), Tag = "emu.esde.footer" };
        if (esDe.RomFolder.Length > 0)
        {
            var folder = Compact(Caption("ES-DE reads games through " + esDe.RomFolder));
            ToolTip.SetTip(folder, esDe.RomFolder);
            footer.Children.Add(folder);
        }

        footer.Children.Add(commands);
        body.Children.Add(footer);
        return Card(body, "emu.esde.card");
    }

    /// <summary>One ROM folder: its systems in ES-DE and the switch that offers it to Steam through Library Importer.</summary>
    private Grid FolderLine(EsDeFolderRow folder, bool busy)
    {
        var sourceId = folder.SourceId;
        var toggle = new ToggleSwitch
        {
            IsChecked = folder.InSteam, IsEnabled = !busy, OnContent = null, OffContent = null,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        toggle.IsCheckedChanged += (_, _) =>
        {
            var enabled = toggle.IsChecked == true;
            if (enabled != folder.InSteam)
            {
                Run(token => _service!.SetRomSourceInSteamAsync(sourceId, enabled, token), "ROM folder");
            }
        };
        AutomationProperties.SetName(toggle, "Offer " + folder.Name + " to Steam in Library Importer");
        var detail = folder.Systems + " · " + (folder.InSteam ? "also in Library Importer" : "ES-DE only");
        var line = Line("emu.esde.folder:" + sourceId, folder.Name, detail, toggle);
        ToolTip.SetTip(line, folder.Location);
        return line;
    }
}
