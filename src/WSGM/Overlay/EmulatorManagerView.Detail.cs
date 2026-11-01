using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

public sealed partial class EmulatorManagerView
{
    private readonly Dictionary<string, string> _channelDraft = new(StringComparer.Ordinal);

    // ---- An installed emulator ----

    private void RenderInstallation(string installationId)
    {
        var shown = ReadShown();
        var installation = shown?.Snapshot.Installations.FirstOrDefault(item => item.Id == installationId);
        var item = shown?.Page.Installed.FirstOrDefault(entry => entry.Id == installationId);
        if (shown is null || installation is null || item is null)
        {
            RenderMessage("Emulators", "That emulator is no longer installed.");
            return;
        }

        var body = NewStack("");
        body.Children.Add(Tagged(Caption("Emulators  ›  " + installation.Name), "emu.crumb"));
        var working = WorksOn(shown.Progress, installationId);
        if (!working)
        {
            body.Children.Add(Progress(shown));
        }

        body.Children.Add(Hero(shown, installation, item, working));
        var setup = SetupCard(shown, installation, item);
        var steam = SteamCard(shown, installation);
        if (_wide)
        {
            body.Children.Add(CardGrid("emu.detail.cards:" + installationId, [setup, steam], 2));
        }
        else
        {
            body.Children.Add(setup);
            body.Children.Add(steam);
        }

        body.Children.Add(Maintenance(shown, installation));
        SetEmulatorContent(body, shown);
        RestoreFocus();
    }

    private Border Hero(EmulatorShown shown, EmulatorInstallation installation, EmulatorListItem item, bool working)
    {
        var id = installation.Id;
        var busy = shown.Progress.Busy;
        var offer = EmulatorPresentation.Offer(shown.Snapshot, installation);
        var newer = installation.Managed && offer is { Error.Length: 0, ReleaseId.Length: > 0 }
                                         && offer.ReleaseId != installation.ReleaseId;
        var title = new WrapPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock
        {
            Text = installation.Name, FontSize = _wide ? 22 : 20, FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 4)
        });
        var systems = installation.Cores.Length > 0
            ? [item.Summary]
            : installation.Systems.Select(EmulatorPresentation.SystemName).Take(3);
        foreach (var system in systems)
        {
            var chip = Chip(system, "");
            chip.Margin = new Thickness(0, 0, 6, 4);
            title.Children.Add(chip);
        }

        if (newer)
        {
            var available = Chip(offer!.ReleaseId == installation.IgnoredReleaseId
                ? offer.Version + " skipped"
                : offer.Version + " available", "warn");
            available.Margin = new Thickness(0, 0, 6, 4);
            title.Children.Add(available);
        }

        var info = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(title);
        var path = Compact(Caption(string.Join(" · ", new[]
        {
            item.VersionLine, installation.Managed ? "managed by WSGM" : "WSGM doesn't update it",
            Path.GetDirectoryName(installation.ExecutablePath) ?? installation.Root
        }.Where(part => part.Length > 0))));
        path.TextWrapping = TextWrapping.NoWrap;
        path.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(path, installation.ExecutablePath);
        info.Children.Add(path);
        if (offer is { Error.Length: > 0 })
        {
            info.Children.Add(Compact(Caption("Could not check for updates: " + offer.Error)));
        }

        var actions = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (newer && !working)
        {
            var update = Command("emu.update", "Update to " + offer!.Version, busy
                ? null
                : () => Run(token => _service!.UpdateEmulatorAsync(id, token), "update"), true);
            update.MinWidth = 160;
            update.Padding = new Thickness(16, 6);
            update.Margin = default;
            update.HorizontalAlignment = HorizontalAlignment.Stretch;
            actions.Children.Add(update);
        }

        List<Control> secondary = [];
        if (offer is { NotesUrl.Length: > 0 })
        {
            secondary.Add(Command("emu.notes", "Release notes",
                () => Run(token => _service!.OpenEmulatorReleaseNotesAsync(offer.DefinitionId, offer.Channel,
                    offer.Architecture, token), "release notes")));
        }

        if (newer && offer!.ReleaseId != installation.IgnoredReleaseId && !working)
        {
            var release = offer.ReleaseId;
            secondary.Add(Command("emu.skip", "Skip version", busy
                ? null
                : () => Run(token => _service!.IgnoreEmulatorVersionAsync(id, release, token), "skip")));
        }

        if (secondary.Count > 0)
        {
            var strip = CommandStrip([.. secondary]);
            strip.HorizontalAlignment = _wide ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            actions.Children.Add(strip);
        }

        var stack = new StackPanel();
        stack.Children.Add(HeroGrid(Icon(item.AppId, _wide ? 72 : 56), info,
            actions.Children.Count > 0 ? actions : null));
        if (working)
        {
            stack.Children.Add(WorkPanel(shown));
        }

        return Card(stack, "emu.hero:" + id + ":" + _wide + ":" + working);
    }

    /// <summary>Icon, the name and its facts, and the actions at the right when wide or under the name when narrow.</summary>
    private Grid HeroGrid(Control icon, Control info, Control? actions)
    {
        var grid = new Grid
            { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 16, RowSpacing = 10 };
        icon.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(icon);
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);
        if (actions is null)
        {
            return grid;
        }

        if (_wide)
        {
            actions.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(actions, 2);
        }
        else
        {
            grid.RowDefinitions = new RowDefinitions("Auto,Auto");
            actions.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetRow(actions, 1);
            Grid.SetColumn(actions, 1);
            Grid.SetColumnSpan(actions, 2);
        }

        grid.Children.Add(actions);
        return grid;
    }

    /// <summary>The setup checklist: each prerequisite with Choose file, the runtimes, and Recheck.</summary>
    private Border SetupCard(EmulatorShown shown, EmulatorInstallation installation, EmulatorListItem item)
    {
        var id = installation.Id;
        var busy = shown.Progress.Busy;
        var body = new StackPanel();
        var missing = item.Setup.Any(entry => entry is { Required: true, Ready: false });
        body.Children.Add(CardTitle("emu.setup.title", "Setup", item.SetupLabel.Length > 0
            ? Chip(item.SetupLabel, missing || item.SetupLabel == "Needs setup" ? "warn" : "good")
            : null));
        if (item.Setup.Length == 0)
        {
            body.Children.Add(Caption(installation.MissingRequirements.Length > 0
                ? string.Join(" ", installation.MissingRequirements)
                : "Nothing to set up. Its games start once a ROM source uses it."));
        }

        foreach (var entry in item.Setup)
        {
            var kind = entry.Kind;
            Control? command;
            if (kind == "runtime")
            {
                command = entry.Ready
                    ? null
                    : Command("emu.setup.runtime", "Install runtimes", busy
                        ? null
                        : () => Run(token => _service!.InstallEmulatorRuntimeDependenciesAsync(
                            installation.DefinitionId, token), "runtimes"));
            }
            else
            {
                var choose = Command("emu.setup.file:" + kind, entry.Ready ? "Replace…" : "Choose file…", busy
                    ? null
                    : () => _ = RunSafelyAsync(ChooseAsync(false, entry.Extensions, path =>
                        Run(token => _service!.ConfigureEmulatorPrerequisiteAsync(id, path, kind, token),
                            "prerequisite")), "prerequisite file"));
                AutomationProperties.SetName(choose, (entry.Ready ? "Replace " : "Choose ") + entry.Name);
                if (entry.AllowDirectory)
                {
                    var folder = Command("emu.setup.folder:" + kind, "Choose folder…", busy
                        ? null
                        : () => _ = RunSafelyAsync(ChooseAsync(true, [], path =>
                            Run(token => _service!.ConfigureEmulatorPrerequisiteAsync(id, path, kind, token),
                                "prerequisite")), "prerequisite folder"));
                    AutomationProperties.SetName(folder, "Choose a folder for " + entry.Name);
                    command = CommandStrip(choose, folder);
                }
                else
                {
                    command = choose;
                }
            }

            if (command is WrapPanel wrap)
            {
                wrap.Margin = new Thickness(0, 8, 0, 0);
            }

            body.Children.Add(Divider());
            body.Children.Add(Line("emu.setup:" + kind, entry.Name + (entry.Required ? "" : " (optional)"),
                entry.Detail, command, StateMark(entry.Ready, !entry.Required)));
        }

        body.Children.Add(Divider());
        var recheck = Command("emu.setup.recheck", "Recheck", busy ? null : () => Run(_service!.RefreshEmulatorsAsync));
        List<Control> commands = [recheck];
        if (installation.DataPolicy.Prerequisites.Any(rule => rule.Kind is "bios" or "firmware" or "keys" or "system"))
        {
            commands.Add(Command("emu.setup.bios", "BIOS and firmware", () => SelectView("bios", false)));
        }

        var footer = new StackPanel { Spacing = 6, Margin = new Thickness(0, 10, 0, 0), Tag = "emu.setup.footer" };
        footer.Children.Add(Compact(Caption("Finished the emulator's own first-run setup? Recheck reads its files "
                                            + "again.")));
        footer.Children.Add(CommandStrip([.. commands]));
        body.Children.Add(footer);
        return Card(body, "emu.setup.card:" + id);
    }

    /// <summary>The emulator's own Steam shortcut, its launch mode and what in the library uses it.</summary>
    private Border SteamCard(EmulatorShown shown, EmulatorInstallation installation)
    {
        var id = installation.Id;
        var page = shown.Page;
        var busy = shown.Progress.Busy;
        var body = new StackPanel();
        body.Children.Add(CardTitle("emu.steam.title", "In Steam", null));
        var toggle = new ToggleSwitch
        {
            IsChecked = installation.SteamShortcutEnabled, IsEnabled = !busy, OnContent = null, OffContent = null,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        toggle.IsCheckedChanged += (_, _) =>
        {
            var enabled = toggle.IsChecked == true;
            if (enabled != installation.SteamShortcutEnabled)
            {
                Run(token => _service!.SetEmulatorShortcutAsync(id, enabled, installation.SteamShortcutLaunchMode,
                    token), "shortcut");
            }
        };
        AutomationProperties.SetName(toggle, "Show " + installation.Name + " in Steam");
        body.Children.Add(Divider());
        body.Children.Add(Line("emu.steam.shortcut", "Show " + installation.Name + " in Steam",
            "Adds the emulator itself as a non-Steam game", toggle));
        var mode = new OverlayChoice<int>(page.LaunchModes.Select(entry => (entry.Value, entry.Label)).ToArray(),
            installation.SteamShortcutLaunchMode, value =>
            {
                if (value != installation.SteamShortcutLaunchMode)
                {
                    Run(token => _service!.SetEmulatorShortcutAsync(id, installation.SteamShortcutEnabled, value,
                        token), "launch mode");
                }
            })
        {
            MinWidth = 200, MinHeight = 40, IsEnabled = !busy, HorizontalAlignment = HorizontalAlignment.Right
        };
        AutomationProperties.SetName(mode, "Launch mode");
        body.Children.Add(Divider());
        body.Children.Add(Line("emu.steam.mode", "Launch mode",
            "With Give the game the controller, the emulator reads the controller directly.", mode));
        body.Children.Add(Divider());
        var dependency = page.Dependencies.FirstOrDefault(entry => entry.InstallationId == id);
        var usage = Caption(dependency?.Summary ?? "Not used by a ROM source yet.");
        usage.Margin = new Thickness(2, 10, 2, 0);
        body.Children.Add(Tagged(usage, "emu.steam.usage"));
        if (page.ShortcutStatus.Length > 0)
        {
            body.Children.Add(Tagged(Compact(Caption(page.ShortcutStatus)), "emu.steam.status"));
        }

        return Card(body, "emu.steam.card:" + id);
    }

    /// <summary>Repair and the RetroArch core list at the left, Remove kept apart at the right.</summary>
    private Grid Maintenance(EmulatorShown shown, EmulatorInstallation installation)
    {
        var id = installation.Id;
        var busy = shown.Progress.Busy;
        List<Control> left = [];
        if (installation.Managed)
        {
            left.Add(Command("emu.repair", "Repair installation", busy
                ? null
                : () => Run(token => _service!.RepairEmulatorAsync(id, token), "repair")));
        }

        if (installation.Cores.Length > 0)
        {
            left.Add(Command("emu.cores", "RetroArch cores", () =>
            {
                _coreInstallation = id;
                SelectView("cores", false);
            }));
        }

        var remove = Command("emu.remove", (installation.Managed ? "Remove " : "Forget ") + installation.Name + "…",
            busy ? null : () => ConfirmRemove(shown, installation));
        remove.Classes.Add("danger");
        remove.Margin = new Thickness(0, 0, 0, 8);
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6, 0, 0),
            Tag = "emu.maintenance"
        };
        row.Children.Add(CommandStrip([.. left]));
        Grid.SetColumn(remove, 1);
        row.Children.Add(remove);
        return row;
    }

    /// <summary>
    ///     Asks before removing in the in-window surface, with Cancel focused, naming what goes and what stays: the
    ///     program files, the data, the emulator's own shortcut and the imported games.
    /// </summary>
    private void ConfirmRemove(EmulatorShown shown, EmulatorInstallation installation)
    {
        if (_service is null || TopLevel.GetTopLevel(this) is not OverlayWindow window)
        {
            return;
        }

        var id = installation.Id;
        var dependency = shown.Page.Dependencies.FirstOrDefault(entry => entry.InstallationId == id);
        List<string> paragraphs = installation.Managed
            ?
            [
                "WSGM deletes " + installation.Name + "'s program files from " + installation.Root + ".",
                "Its settings, saves and the BIOS or firmware you supplied stay in " + installation.DataPath
                + ", and so do your ROMs."
            ]
            :
            [
                "WSGM stops using your installation at " + installation.ExecutablePath
                                                         + ". Its files and data stay as they are."
            ];
        paragraphs.Add((installation.SteamShortcutEnabled
                           ? "Its own shortcut is removed from Steam. "
                           : "")
                       + (dependency?.Summary ?? "")
                       + " Imported games stay in Steam and need another emulator before they start.");
        var title = (installation.Managed ? "Remove " : "Forget ") + installation.Name + "?";
        var content = new StackPanel { Spacing = 14, Tag = "confirm" };
        foreach (var paragraph in paragraphs)
        {
            content.Children.Add(new TextBlock
                { Text = paragraph.Trim(), TextWrapping = TextWrapping.Wrap, LineHeight = 22 });
        }

        var cancel = Command("confirm.cancel", "Cancel", () => window.Surfaces.CloseActive());
        var accept = Command("confirm.accept", installation.Managed ? "Remove" : "Forget", () =>
        {
            window.Surfaces.CloseActive();
            _ = RunCommandAsync(token => _service!.RemoveEmulatorAsync(id, token), "remove", () =>
            {
                // The emulator is gone: return to the list it was opened from.
                if (NavigationStack.Count > 0)
                {
                    Back();
                }
            });
        });
        accept.Classes.Add("danger");
        foreach (var button in new[] { cancel, accept })
        {
            button.MinWidth = 120;
            button.Padding = new Thickness(16, 6);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0)
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        content.Children.Add(buttons);
        window.Surfaces.ShowConfirm(content, title, cancel);
    }

    // ---- A catalogue emulator ----

    private void RenderDefinition(string definitionId)
    {
        var shown = ReadShown();
        var definition = shown?.Snapshot.Definitions.FirstOrDefault(item => item.Id == definitionId);
        if (shown is null || definition is null)
        {
            RenderMessage("Emulators", "That emulator is no longer listed.");
            return;
        }

        var snapshot = shown.Snapshot;
        var busy = shown.Progress.Busy;
        var channel = _channelDraft.TryGetValue(definitionId, out var draft) &&
                      Enumerable.Contains(definition.Channels, draft)
            ? draft
            : definition.Channels.FirstOrDefault() ?? "";
        var offer = snapshot.Offers.FirstOrDefault(item =>
            item.DefinitionId == definitionId && item.Channel == channel);
        var working = WorksOn(shown.Progress, definitionId);
        var body = NewStack("");
        body.Children.Add(Tagged(Caption("Emulators  ›  Get emulators  ›  " + definition.Name), "emu.crumb"));
        if (!working)
        {
            body.Children.Add(Progress(shown));
        }

        var title = new WrapPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock
        {
            Text = definition.Name, FontSize = _wide ? 22 : 20, FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 4)
        });
        foreach (var system in definition.Systems.Select(EmulatorPresentation.SystemName).Take(4))
        {
            var chip = Chip(system, "");
            chip.Margin = new Thickness(0, 0, 6, 4);
            title.Children.Add(chip);
        }

        var info = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(title);
        info.Children.Add(Compact(Caption(offer is null
            ? "Check for updates to read the latest release."
            : offer.Error.Length > 0
                ? offer.Error
                : "Latest " + offer.Version + (definition.Channels.Length > 1 ? " (" + channel + ")" : "")
                  + " · from " + definition.Source)));
        var actions = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (!working)
        {
            var install = Command("emu.install", "Install " + definition.Name
                                                            + (channel != definition.Channels.FirstOrDefault()
                                                                ? " " + channel
                                                                : ""), busy
                ? null
                : () => Run(token => _service!.InstallEmulatorAsync(definitionId, channel, token), "install"), true);
            install.MinWidth = 160;
            install.Padding = new Thickness(16, 6);
            install.Margin = default;
            install.HorizontalAlignment = HorizontalAlignment.Stretch;
            actions.Children.Add(install);
        }

        if (offer is { NotesUrl.Length: > 0 })
        {
            var notes = CommandStrip(Command("emu.notes", "Release notes",
                () => Run(token => _service!.OpenEmulatorReleaseNotesAsync(offer.DefinitionId, offer.Channel,
                    offer.Architecture, token), "release notes")));
            notes.HorizontalAlignment = _wide ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            actions.Children.Add(notes);
        }

        var hero = new StackPanel();
        hero.Children.Add(HeroGrid(Icon(0, _wide ? 72 : 56), info, actions.Children.Count > 0 ? actions : null));
        if (definition.Channels.Length > 1)
        {
            var choice = new OverlayChoice<string>([.. definition.Channels.Select(item => (item, item))], channel,
                value =>
                {
                    _channelDraft[definitionId] = value;
                    CurrentLevel?.Invoke();
                }) { MinWidth = 200, MinHeight = 40, IsEnabled = !busy && !working };
            AutomationProperties.SetName(choice, "Release channel");
            hero.Children.Add(Divider());
            hero.Children.Add(Line("emu.channel", "Release channel",
                "Install follows this channel's releases; settings and saves are shared.", choice));
        }

        if (working)
        {
            hero.Children.Add(WorkPanel(shown));
        }

        body.Children.Add(Card(hero, "emu.definition.hero:" + definitionId + ":" + _wide + ":" + working));
        body.Children.Add(BeforeYouPlay(definition));
        body.Children.Add(External(definition, busy));
        foreach (var installation in snapshot.Installations.Where(item => item.DefinitionId == definitionId))
        {
            var installed = installation.Id;
            var row = Row(installation.Name + " " + installation.Version,
                installation.Channel + " · " + (installation.Managed ? "managed by WSGM" : "your own installation"),
                Icons.ListLines,
                () => Open("emu.definition.installed:" + installed, () => RenderInstallation(installed)));
            body.Children.Add(Tagged(row, "emu.definition.installed:" + installed));
        }

        SetEmulatorContent(body, shown);
        RestoreFocus();
    }

    /// <summary>What the emulator needs besides its program, from its reviewed prerequisites and runtimes.</summary>
    private Border BeforeYouPlay(EmulatorDefinition definition)
    {
        var body = new StackPanel();
        body.Children.Add(CardTitle("emu.requires.title", "Before you play", null));
        if (definition.Prerequisites.Length == 0 && !definition.DataPolicy.RequiresVisualCpp)
        {
            body.Children.Add(Caption("Nothing else is needed. Its games start once a ROM source uses it."));
        }

        foreach (var prerequisite in definition.Prerequisites)
        {
            body.Children.Add(Divider());
            body.Children.Add(Line("emu.requires:" + prerequisite.Kind, prerequisite.Name,
                prerequisite.Description, Chip(prerequisite.Required ? "Required" : "Optional",
                    prerequisite.Required ? "warn" : "")));
        }

        if (definition.DataPolicy.RequiresVisualCpp || definition.Id == "model2")
        {
            body.Children.Add(Divider());
            body.Children.Add(Line("emu.requires.runtime", "Microsoft runtimes",
                "Installs verified Microsoft components; Windows may ask for permission.",
                Command("emu.runtimes", "Install required runtimes", ReadShown()?.Progress.Busy == true
                    ? null
                    : () => Run(token => _service!.InstallEmulatorRuntimeDependenciesAsync(definition.Id, token),
                        "runtimes"))));
        }

        if (definition.Prerequisites.Length > 0)
        {
            body.Children.Add(Divider());
            var strip = CommandStrip(Command("emu.requires.bios", "BIOS and firmware",
                () => SelectView("bios", false)));
            strip.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(Line("emu.requires.footer", "Add these before or after installing",
                "WSGM links your BIOS folder into the emulator when it installs.", strip));
        }

        return Card(body, "emu.requires:" + definition.Id);
    }

    /// <summary>Registers a copy the user installed themselves, without taking over its files or updates.</summary>
    private Border External(EmulatorDefinition definition, bool busy)
    {
        var id = definition.Id;
        var body = new StackPanel();
        var use = Command("emu.external", "Use my installation…", busy
            ? null
            : () => _ = RunSafelyAsync(ChooseAsync(false, [".exe"], executable =>
                    Run(token => _service!.UseExternalEmulatorAsync(id, executable, token), "use installation")),
                "choose emulator"));
        body.Children.Add(Line("emu.external.line", "Already installed it yourself?",
            "Choose its program file. WSGM uses it as it is and doesn't move, update or change its files.", use));
        return Card(body, "emu.external.card:" + id);
    }
}
