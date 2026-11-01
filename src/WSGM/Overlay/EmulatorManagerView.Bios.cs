using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using WSGM.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

public sealed partial class EmulatorManagerView
{
    private void AddBios(StackPanel body, EmulatorShown shown)
    {
        var page = shown.Page;
        var summary = page.BiosSummary;
        body.Children.Add(FolderCard(shown));
        body.Children.Add(Tagged(Caption("Installed emulators and cores read their files from this one folder, linked "
                                         + "into each emulator's own location. WSGM checks every file against the "
                                         + "known good dumps where a checksum is published."), "bios.intro"));
        if (summary.Systems.Count == 0)
        {
            body.Children.Add(Tagged(Caption(page.Bios.Checked
                ? "No supported emulator needs BIOS or firmware files."
                : "BIOS files have not been checked yet. Choose Check files."), "bios.empty"));
            return;
        }

        var used = summary.Systems.Where(system => system.UsedBy.Length > 0).ToArray();
        if (used.Length > 0)
        {
            var list = new StackPanel { Tag = "bios.systems" };
            foreach (var system in used)
            {
                AddChecklist(list, shown, system);
            }

            body.Children.Add(Card(list, "bios.card"));
        }
        else
        {
            body.Children.Add(Tagged(Caption("No installed emulator needs these files yet. The systems below list what "
                                             + "each one will need."), "bios.unused"));
        }

        var others = new StackPanel { Tag = "bios.others.list" };
        foreach (var system in summary.Systems.Where(system => system.UsedBy.Length == 0))
        {
            AddChecklist(others, shown, system);
        }

        if (others.Children.Count == 0)
        {
            return;
        }

        var section = new CollapsibleSection("Systems without an installed emulator", others)
        {
            Summary = "Add their files now, and emulators you install later use them"
        };
        section.Heading.Tag = "bios.others";
        body.Children.Add(section);
    }

    /// <summary>The shared folder, how many needed files it holds, and the commands that fill it.</summary>
    private Border FolderCard(EmulatorShown shown)
    {
        var page = shown.Page;
        var busy = shown.Progress.Busy;
        var icon = new Path
        {
            Data = Icons.FolderPlus, Width = 22, Height = 22, Stretch = Stretch.Uniform, StrokeThickness = 1.6,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0)
        };
        icon.Bind(Shape.StrokeProperty, this.GetResourceObservable("HcAccentBrush"));
        var labels = new StackPanel { Spacing = 4 };
        var title = new WrapPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock
        {
            Text = "Shared BIOS folder", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 4)
        });
        var summary = page.BiosSummary;
        var chip = Chip(summary.Label, summary.Needed > 0 && summary.Found < summary.Needed ? "warn"
            : summary.Needed > 0 ? "good" : "");
        chip.Margin = new Thickness(0, 0, 0, 4);
        title.Children.Add(chip);
        labels.Children.Add(title);
        var folder = Compact(Caption(page.Bios.Folder.Length > 0 ? page.Bios.Folder : "Not chosen yet"));
        folder.TextWrapping = TextWrapping.NoWrap;
        folder.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(folder, page.Bios.Folder);
        labels.Children.Add(folder);
        var commands = CommandStrip(
            Command("bios.add", "Add BIOS files…", busy
                ? null
                : () => _ = RunSafelyAsync(AddFiles("", false), "BIOS files")),
            Command("bios.add.folder", "Add a folder…", busy
                ? null
                : () => _ = RunSafelyAsync(AddFiles("", true), "BIOS files")),
            Command("bios.folder", "Change folder…", busy
                ? null
                : () => _ = RunSafelyAsync(ChooseFolder(), "BIOS folder")),
            Command("bios.verify", "Check files", busy ? null : () => Run(_service!.VerifyBiosAsync)));
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(_wide ? "Auto,*,Auto" : "Auto,*"), ColumnSpacing = 14,
            RowDefinitions = new RowDefinitions(_wide ? "Auto" : "Auto,Auto"), RowSpacing = 10
        };
        grid.Children.Add(icon);
        Grid.SetColumn(labels, 1);
        grid.Children.Add(labels);
        commands.VerticalAlignment = VerticalAlignment.Center;
        commands.Margin = new Thickness(0, 8, 0, 0);
        if (_wide)
        {
            commands.MaxWidth = 380;
            Grid.SetColumn(commands, 2);
        }
        else
        {
            Grid.SetRow(commands, 1);
            Grid.SetColumn(commands, 1);
        }

        grid.Children.Add(commands);
        return Card(grid, "bios.folder.card:" + _wide);
    }

    private void AddChecklist(StackPanel list, EmulatorShown shown, EmulatorBiosChecklist system)
    {
        var id = system.SystemId;
        var busy = shown.Progress.Busy;
        if (list.Children.Count > 0)
        {
            list.Children.Add(Divider());
        }

        var detail = system.UsedBy.Length > 0 ? system.UsedBy : "No installed emulator uses it";
        if (system.Optional)
        {
            detail += " · optional";
        }

        var actions = CommandStrip(
            Command("bios.add:" + id, "Add files…", busy
                ? null
                : () => _ = RunSafelyAsync(AddFiles(id, false),
                    "BIOS files")),
            Command("bios.relink:" + id, "Relink emulators", busy || system.UsedBy.Length == 0
                ? null
                : () => Run(token => _service!.RelinkBiosAsync(id, token))));
        foreach (var installed in shown.Snapshot.Installations.Where(item => item.Systems.Contains(id)
                                                                             && item.DataPolicy.Prerequisites
                                                                                 .Any(rule => rule.NativeInstaller)))
        {
            var installationId = installed.Id;
            actions.Children.Add(Command("bios.firmware:" + installationId, "Set up " + installed.Name,
                () => Open("bios.firmware:" + installationId, () => RenderInstallation(installationId))));
            actions.Children[^1].Margin = new Thickness(0, 0, 8, 8);
        }

        AutomationProperties.SetName(actions, system.Name);
        list.Children.Add(Line("bios.system:" + id, system.Name, detail, actions));
        foreach (var item in system.Items)
        {
            var ready = item.State is "verified" or "present";
            Control trailing;
            if (!ready && !item.Optional && item.State == "missing")
            {
                var choose = Command("bios.choose:" + id + ":" + item.Label, "Choose file…", busy
                    ? null
                    : () => _ = RunSafelyAsync(AddFiles(id, false), "BIOS files"));
                AutomationProperties.SetName(choose, "Choose " + item.Label);
                trailing = choose;
            }
            else
            {
                trailing = Chip(item.StateLabel, item.State switch
                {
                    "verified" => "good",
                    "wrongFile" => "bad",
                    _ => ""
                });
            }

            var line = Line("bios.file:" + id + ":" + item.Label, item.Label,
                ready || item.State == "wrongFile" ? "" : item.StateLabel, trailing, StateMark(ready, item.Optional));
            line.MinHeight = 40;
            line.Margin = new Thickness(26, 0, 0, 0);
            list.Children.Add(line);
        }

        var shownLinks = shown.Page.Bios.Systems.FirstOrDefault(item => item.Id == id)?.Links ?? [];
        foreach (var link in shownLinks)
        {
            var caption = Caption(link);
            caption.Margin = new Thickness(28, 0, 2, 4);
            list.Children.Add(Tagged(caption, "bios.link:" + id + ":" + link));
        }
    }

    private async Task ChooseFolder()
    {
        var generation = NavigationGeneration;
        var path = await PickPathAsync(true);
        if (path is not null && generation == NavigationGeneration)
        {
            Run(token => _service!.SetBiosFolderAsync(path, token));
        }
    }

    private async Task AddFiles(string systemId, bool folder)
    {
        var generation = NavigationGeneration;
        var path = await PickPathAsync(folder);
        if (path is not null && generation == NavigationGeneration)
        {
            Run(token => _service!.AddBiosFilesAsync(path, systemId, token));
        }
    }
}
