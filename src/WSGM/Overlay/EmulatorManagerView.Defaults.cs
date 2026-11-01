using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WSGM.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

public sealed partial class EmulatorManagerView
{
    private void AddDefaults(StackPanel body, EmulatorShown shown)
    {
        var page = shown.Page;
        body.Children.Add(Tagged(Caption("The emulator each system's games use unless a ROM source or a game picks "
                                         + "its own. Changing one leaves existing choices on sources and games alone."),
            "emu.defaults.intro"));
        var used = page.Systems.Where(system => system.InLibrary).ToArray();
        var table = new StackPanel { Tag = "emu.defaults.table" };
        table.Children.Add(DefaultsHeading());
        if (used.Length == 0)
        {
            table.Children.Add(Divider());
            table.Children.Add(Tagged(Caption("No ROM source uses a system yet. Add one in the Library Importer, or "
                                              + "set a default below for when you do."), "emu.defaults.empty"));
        }

        foreach (var system in used)
        {
            table.Children.Add(Divider());
            table.Children.Add(DefaultRow(shown, system));
        }

        body.Children.Add(Card(table, "emu.defaults.card:" + _wide));
        var others = new StackPanel { Spacing = 0 };
        foreach (var system in page.Systems.Where(system => !system.InLibrary))
        {
            others.Children.Add(DefaultRow(shown, system));
        }

        var section = new CollapsibleSection("Other systems", others)
        {
            Summary = "Systems none of your ROM sources use yet"
        };
        section.Heading.Tag = "emu.defaults.others";
        body.Children.Add(section);
    }

    private Grid DefaultsHeading()
    {
        var heading = DefaultGrid("emu.defaults.heading");
        heading.MinHeight = 32;
        string[] labels = ["System", "Emulator", "Core"];
        for (var index = 0; index < labels.Length; index++)
        {
            var label = Eyebrow(labels[index]);
            label.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(label, index);
            heading.Children.Add(label);
        }

        return heading;
    }

    private Grid DefaultGrid(string key)
    {
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(_wide ? "*,240,240" : "*,190,170"), ColumnSpacing = 14,
            MinHeight = 52, Margin = new Thickness(0, 4), Tag = key
        };
    }

    /// <summary>
    ///     An installation's name in the narrow emulator column; the full label with version and ownership only when
    ///     two compatible installations share the name.
    /// </summary>
    private static string ChoiceLabel(EmulatorShown shown, IReadOnlyList<RomEmulatorChoice> choices,
        RomEmulatorChoice choice)
    {
        var name = shown.Snapshot.Installations.FirstOrDefault(item => item.Id == choice.Id)?.Name;
        return name is null || choices.Count(other =>
            shown.Snapshot.Installations.FirstOrDefault(item => item.Id == other.Id)?.Name == name) > 1
            ? choice.Label
            : name;
    }

    private Grid DefaultRow(EmulatorShown shown, EmulatorSystemRow system)
    {
        var systemId = system.SystemId;
        var busy = shown.Progress.Busy;
        var preference = shown.Snapshot.SystemPreferences.FirstOrDefault(item => item.SystemId == systemId);
        var choices = shown.Page.Choices.FirstOrDefault(item => item.SystemId == systemId)?.Installations ?? [];
        var row = DefaultGrid("default:" + systemId);
        var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock
            { Text = system.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        var summary = Compact(Caption(system.Summary));
        if (system.NeedsEmulator)
        {
            summary.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("HcWarningBrush"));
        }

        labels.Children.Add(summary);
        row.Children.Add(labels);
        var emulator = Tagged(new OverlayChoice<string>(
                [("", "No default"), .. choices.Select(item => (item.Id, ChoiceLabel(shown, choices, item)))],
                preference?.InstallationId ?? "",
                value => Run(token => _service!.SetPreferredEmulatorAsync(systemId, value,
                    choices.FirstOrDefault(item => item.Id == value)?.DefaultCoreId ?? "", token)))
            {
                IsEnabled = !busy, HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center
            },
            "default.emulator:" + systemId);
        AutomationProperties.SetName(emulator, system.Name + " emulator");
        Grid.SetColumn(emulator, 1);
        row.Children.Add(emulator);
        var selected = choices.FirstOrDefault(item => item.Id == preference?.InstallationId);
        Control core;
        if (system.NeedsEmulator)
        {
            var get = Command("default.get:" + systemId, "Get an emulator", () => FindEmulatorFor(system.Name));
            AutomationProperties.SetName(get, "Get an emulator for " + system.Name);
            get.Margin = default;
            core = get;
        }
        else if (selected?.RequiresCore == true)
        {
            core = Tagged(new OverlayChoice<string>([.. selected.Cores.Select(item => (item.Id, item.Label))],
                    preference?.CoreId ?? selected.DefaultCoreId,
                    value => Run(token => _service!.SetPreferredEmulatorAsync(systemId, selected.Id, value, token)))
                {
                    IsEnabled = !busy, HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Center
                },
                "default.core:" + systemId);
            AutomationProperties.SetName(core, system.Name + " core");
        }
        else
        {
            core = Compact(Caption(selected is null ? "No emulator chosen" : "Standalone"));
        }

        core.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(core, 2);
        row.Children.Add(core);
        return row;
    }
}
