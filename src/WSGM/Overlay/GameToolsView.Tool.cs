using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using SteamUiToolkit;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

public sealed partial class GameToolsView
{
    private static readonly string[] InstallSteps =
        ["Download", "Check the package", "Back up and copy", "Apply settings"];

    private Border ToolCard(GameToolsState state, GameToolsTool tool, string game)
    {
        var key = "tool:" + tool.Id;
        var busy = state.Busy;
        var working = busy?.ToolId == tool.Id;
        var idle = busy is null && !state.Loading;
        var installed = tool.InstalledVersion is not null;
        var channel = DraftChannel(tool);
        var body = new StackPanel { Spacing = 0 };
        body.Children.Add(Header(state, tool, channel, working, idle, game));
        AddNotes(body, tool, key);
        if (working)
        {
            body.Children.Add(ProgressPanel(state, busy!));
        }

        var rows = new StackPanel { Spacing = 0, Tag = key + ".rows" };
        var editable = idle && tool.CanEditOptions;
        if (tool.Channels.Count > 1 && (tool.ShowOptions || installed))
        {
            // The channel is a draft for the primary action, so it locks with the commands that would use it.
            AddRow(rows,
                ChannelRow(tool, channel, idle && (tool.CanEditOptions || tool.Actions.Contains("switchChannel"))));
        }

        if (tool.ShowOptions)
        {
            AddRow(rows, ProxyRow(tool, editable, installed));
            foreach (var setting in tool.Settings)
            {
                AddRow(rows, SettingRow(tool, setting, editable && setting.Available,
                    installed && tool.Actions.Contains("configure")));
            }

            foreach (var addon in tool.Addons)
            {
                AddRow(rows, AddonRow(tool, addon, idle && addon.CanChange, installed));
            }
        }

        if (rows.Children.Count > 0)
        {
            body.Children.Add(rows);
        }

        if (!working && Secondary(tool, channel, idle) is { } secondary)
        {
            body.Children.Add(secondary);
        }

        body.Children.Add(Footer(tool));
        return Card(body, key + ":" + _wide);
    }

    private Border UnavailableCard(GameToolsTool tool)
    {
        var body = new StackPanel { Spacing = 0 };
        var info = new StackPanel { Spacing = 6 };
        info.Children.Add(TitleLine(tool.Name, [Chip("Unavailable", "")], null));
        info.Children.Add(Caption(tool.StatusText));
        body.Children.Add(HeaderGrid(info, null));
        body.Children.Add(Footer(tool));
        return Card(body, "tool:" + tool.Id + ":unavailable");
    }

    // ---- Header: icon, name, status, latest version, description and the one primary action ----

    private Grid Header(GameToolsState state, GameToolsTool tool, GameToolsChannel channel, bool working, bool idle,
        string game)
    {
        List<Control> chips = [];
        if (working && state.Busy is { } busy)
        {
            chips.Add(Chip(Working(busy), ""));
        }
        else
        {
            switch (tool.Status)
            {
                case "notInstalled":
                    chips.Add(Chip("Not installed", ""));
                    break;
                case "installed":
                case "updateAvailable":
                    chips.Add(Chip("Installed " + tool.InstalledVersion, "good"));
                    if (tool.Status == "updateAvailable")
                    {
                        chips.Add(Chip("Update " + ShortLatest(tool.Channels.First(item => item.Current))
                                                 + " available", "warn"));
                    }

                    break;
                case "modified":
                    chips.Add(Chip(tool.Actions.Contains("repair") ? "Needs repair" : "Interrupted", "warn"));
                    break;
                case "conflict":
                    chips.Add(Chip("Installed outside WSGM", "warn"));
                    break;
                default:
                    chips.Add(Chip("Unavailable", ""));
                    break;
            }
        }

        var shownChannel = tool.InstalledVersion is not null && !working
            ? tool.Channels.FirstOrDefault(item => item.Current) ?? channel
            : channel;
        if (shownChannel.Id != tool.Channels[0].Id)
        {
            chips.Add(Chip(shownChannel.Label, "info"));
        }

        var latest = channel.CheckingLatest
            ? "Checking for the latest release…"
            : channel.LatestLabel is { } label
                ? "Latest " + label
                : channel.LatestError ?? "";
        var info = new StackPanel { Spacing = 6 };
        info.Children.Add(TitleLine(tool.Name, chips, working ? null : latest));
        info.Children.Add(Caption(Description(tool, working)));
        var primary = working ? null : Primary(state, tool, channel, idle, game);
        return HeaderGrid(info, primary);
    }

    private Grid HeaderGrid(Control info, Control? primary)
    {
        var tile = new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(8), Classes = { "badge" },
            VerticalAlignment = VerticalAlignment.Top, Padding = default,
            Child = new Path
            {
                Data = Icons.Wrench, Width = 22, Height = 22, Stretch = Stretch.Uniform, StrokeThickness = 1.8,
                StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, HorizontalAlignment =
                    HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        if (tile.Child is Path icon)
        {
            icon.Bind(Shape.StrokeProperty, this.GetResourceObservable("HcAccentBrush"));
        }

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 14, RowSpacing = 12,
            Margin = new Thickness(0, 0, 0, 12)
        };
        grid.Children.Add(tile);
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);
        if (primary is not null)
        {
            if (_wide)
            {
                primary.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetColumn(primary, 2);
            }
            else
            {
                grid.RowDefinitions = new RowDefinitions("Auto,Auto");
                primary.HorizontalAlignment = HorizontalAlignment.Left;
                Grid.SetRow(primary, 1);
                Grid.SetColumn(primary, 1);
                Grid.SetColumnSpan(primary, 2);
            }

            primary.Margin = default;
            grid.Children.Add(primary);
        }

        return grid;
    }

    private static WrapPanel TitleLine(string name, IReadOnlyList<Control> chips, string? latest)
    {
        var line = new WrapPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock
        {
            Text = name, FontSize = 18, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 4)
        });
        foreach (var chip in chips)
        {
            chip.Margin = new Thickness(0, 0, 8, 4);
            line.Children.Add(chip);
        }

        if (latest is { Length: > 0 })
        {
            var caption = Caption(latest);
            caption.VerticalAlignment = VerticalAlignment.Center;
            caption.Margin = new Thickness(2, 0, 0, 4);
            line.Children.Add(caption);
        }

        return line;
    }

    private static string Description(GameToolsTool tool, bool working)
    {
        if (tool.InstalledVersion is null || working)
        {
            return tool.Description;
        }

        var files = tool.Files;
        List<string> parts = [];
        if (tool.InstalledLabel is { } label && label != tool.InstalledVersion)
        {
            parts.Add(label);
        }

        var from = files.TargetDirectory.Length > 0 ? " from " + files.TargetDirectory.Replace('/', '\\') : "";
        parts.Add((parts.Count == 0 ? "Loaded as " : "loaded as ") + tool.ProxyName + from);
        parts.Add(Count(files.Owned, "file") + " owned by WSGM");
        if (files.Replaced > 0)
        {
            parts.Add(Count(files.Replaced, "game file") + " backed up");
        }

        return string.Join(" · ", parts);
    }

    private static string Count(int count, string noun)
    {
        return count.ToString(CultureInfo.InvariantCulture) + " " + noun + (count == 1 ? "" : "s");
    }

    private static string Working(GameToolsBusy busy)
    {
        var verb = busy.Operation switch
        {
            "install" or "replaceExisting" => "Installing",
            "update" => "Updating to",
            "switchChannel" => "Switching to",
            "repair" => "Repairing",
            "configure" => "Saving settings",
            "remove" => "Removing",
            "addAddon" => "Adding plugin",
            "removeAddon" => "Removing plugin",
            _ => "Working"
        };
        return busy.Version is { Length: > 0 } version
               && busy.Operation is not ("configure" or "remove" or "addAddon" or "removeAddon")
            ? verb + " " + version
            : verb;
    }

    private static string ShortLatest(GameToolsChannel channel)
    {
        return channel.BuildDate is { } date
               && DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None,
                   out var day)
            ? day.ToString("d MMM", CultureInfo.InvariantCulture) + " build"
            : channel.LatestVersion ?? "";
    }

    private Control? Primary(GameToolsState state, GameToolsTool tool, GameToolsChannel channel, bool idle,
        string game)
    {
        var key = "tool:" + tool.Id;
        var installed = tool.InstalledVersion is not null;
        var otherChannel = installed && tool.Channels.FirstOrDefault(item => item.Current)?.Id != channel.Id;
        string label;
        Action? action;
        if (tool.Actions.Contains("replaceExisting"))
        {
            label = "Replace existing installation…";
            action = () => ConfirmReplace(state, tool, game);
        }
        else if (tool.Status == "notInstalled")
        {
            var named = channel.Id == tool.Channels[0].Id ? "" : " " + channel.Label.ToLowerInvariant();
            label = "Install " + tool.Name + named + (channel.LatestVersion is { } version ? " " + version : "");
            action = tool.Actions.Contains("install") ? () => StartInstall(state, tool, game) : null;
        }
        else if (otherChannel)
        {
            label = "Switch to " + channel.Label.ToLowerInvariant()
                                 + (channel.LatestVersion is { } version ? " " + version : "");
            action = tool.Actions.Contains("switchChannel") && channel.LatestVersion is not null
                ? () => StartSwitch(state, tool, channel, game)
                : null;
        }
        else if (tool.Actions.Contains("update"))
        {
            var current = tool.Channels.FirstOrDefault(item => item.Current) ?? channel;
            label = "Update to " + (current.BuildDate is not null ? "the " + ShortLatest(current) : tool.LatestVersion);
            action = () => StartUpdate(state, tool, game);
        }
        else if (tool.Status == "modified" && tool.Actions.Contains("repair"))
        {
            label = "Repair";
            action = () => RunTool((service, token) => service.RepairAsync(_appId, tool.Id, token), "repair");
        }
        else
        {
            return null;
        }

        var button = Command(key + ".primary", label, idle ? action : null, true);
        button.MinWidth = 160;
        button.Padding = new Thickness(16, 6);
        return button;
    }

    // ---- Warnings, conflicts and the reason a tool can't be installed ----

    private static void AddNotes(StackPanel body, GameToolsTool tool, string key)
    {
        var notes = new StackPanel { Spacing = 8, Tag = key + ".notes" };
        if (tool.IncompatibleReason is { Length: > 0 } incompatible && tool.Status == "notInstalled")
        {
            notes.Children.Add(Banner(key + ".incompatible", FAInfoBarSeverity.Informational, null, incompatible));
        }

        if (tool.Status == "modified")
        {
            notes.Children.Add(Banner(key + ".status", FAInfoBarSeverity.Warning, null, tool.StatusText));
        }

        if (tool.Files.Missing.Count > 0 || tool.Files.Modified.Count > 0)
        {
            notes.Children.Add(Tagged(Caption("Missing or changed: "
                                              + string.Join(", ", tool.Files.Missing.Concat(tool.Files.Modified))),
                key + ".files"));
        }

        foreach (var conflict in tool.Conflicts)
        {
            notes.Children.Add(Tagged(Caption($"{conflict.KindLabel}: {conflict.Path}. {conflict.Message}"),
                key + ".conflict:" + conflict.Path));
        }

        for (var index = 0; index < tool.Warnings.Count; index++)
        {
            notes.Children.Add(Banner(key + ".warning:" + index, FAInfoBarSeverity.Warning, null,
                tool.Warnings[index]));
        }

        if (notes.Children.Count > 0)
        {
            notes.Margin = new Thickness(0, 0, 0, 12);
            body.Children.Add(notes);
        }
    }

    // ---- The in-card progress panel ----

    private Border ProgressPanel(GameToolsState state, GameToolsBusy busy)
    {
        var panel = new StackPanel { Spacing = 14 };
        var installs = busy.Operation is "install" or "replaceExisting" or "update" or "switchChannel" or "repair"
            or "addAddon" or "removeAddon";
        if (installs)
        {
            panel.Children.Add(Steps(busy.Phase));
        }

        panel.Children.Add(new OverlayProgress("game.busy", ProgressOf(state), "game.cancel", true));
        var noun = busy.Operation switch
        {
            "update" => "update",
            "switchChannel" => "switch",
            "repair" => "repair",
            "addAddon" or "removeAddon" => "change",
            _ => "install"
        };
        panel.Children.Add(Caption(!installs
            ? "WSGM finishes this on its own. Leaving this page doesn't stop it."
            : busy.Cancellable
                ? "Nothing in the game folder has changed yet, so cancelling now leaves the game as it was. Once "
                  + "files are being copied, the " + noun + " finishes or is rolled back."
                : "Files are being written. The " + noun + " finishes or is rolled back on its own, and leaving "
                  + "this page doesn't stop it."));
        return new Border
        {
            Child = panel, Classes = { "device-group" }, Margin = new Thickness(0, 0, 0, 12),
            Tag = "tool:" + busy.ToolId + ".progress"
        };
    }

    private static WrapPanel Steps(string phase)
    {
        return StepChips(InstallSteps, phase switch
        {
            GameToolPhase.Download => 0,
            GameToolPhase.Verify => 1,
            GameToolPhase.Copy => 2,
            GameToolPhase.Settings => 3,
            GameToolPhase.Rescan => 4,
            _ => -1
        });
    }

    // ---- Value rows: release channel, load name and the tool's settings ----

    private static void AddRow(StackPanel rows, Control row)
    {
        if (rows.Children.Count > 0)
        {
            rows.Children.Add(new Border { Height = 1, Classes = { "section-divider" } });
        }

        rows.Children.Add(row);
    }

    private static Grid ValueRow(string key, string title, string description, Control editor)
    {
        var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock
            { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (description.Length > 0)
        {
            var caption = Caption(description);
            caption.Margin = default;
            labels.Children.Add(caption);
        }

        editor.VerticalAlignment = VerticalAlignment.Center;
        editor.Tag = key + ".editor";
        AutomationProperties.SetName(editor, title);
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16, MinHeight = 52,
            Margin = new Thickness(0, 6), Tag = key
        };
        row.Children.Add(labels);
        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);
        return row;
    }

    private static OverlayChoice<string> Choice(IReadOnlyList<(string Value, string Label)> options, string value,
        bool enabled, Action<string> commit)
    {
        return new OverlayChoice<string>(options, value, commit)
        {
            MinWidth = 180, MinHeight = 40, IsEnabled = enabled, HorizontalAlignment = HorizontalAlignment.Right
        };
    }

    private Grid ChannelRow(GameToolsTool tool, GameToolsChannel channel, bool enabled)
    {
        var key = "tool:" + tool.Id + ".channel";
        var options = tool.Channels.Select(item => (item.Id, item.Label)).ToList();
        var editor = Choice(options, channel.Id, enabled, chosen =>
        {
            if (chosen == channel.Id || chosen.Length == 0)
            {
                return;
            }

            // Choosing a channel changes the primary action only; installing or switching is its own command.
            _draftChannel[tool.Id] = chosen;
            CurrentLevel?.Invoke();
        });
        return ValueRow(key, "Release channel", channel.Description, editor);
    }

    private Grid ProxyRow(GameToolsTool tool, bool enabled, bool installed)
    {
        var key = "tool:" + tool.Id + ".proxy";
        var current = ProxyFor(tool);
        var options = tool.ProxyOptions.Where(option => option.Available || option.Name == current)
            .Select(option => (option.Name, option.Name)).ToList();
        var hint = tool.ProxyOptions.FirstOrDefault(option => option.Name == current)?.Hint ?? "";
        var taken = tool.ProxyOptions.Where(option => !option.Available && option.Name != current)
            .Select(option => option.Name + (option.Conflict is { } conflict ? " (" + conflict.TrimEnd('.') + ")" : ""))
            .ToArray();
        var description = string.Join(" ", new[]
        {
            installed ? "Choosing another name renames the installed file." : hint,
            taken.Length > 0 ? "Not offered: " + string.Join(", ", taken) + "." : ""
        }.Where(part => part.Length > 0));
        var editor = Choice(options, current, enabled, chosen =>
        {
            if (chosen == current || chosen.Length == 0)
            {
                return;
            }

            if (installed)
            {
                RunTool((service, token) => service.ConfigureAsync(_appId, tool.Id, chosen,
                    new Dictionary<string, string>(), token), "rename");
                return;
            }

            _draftProxy[tool.Id] = chosen;
            CurrentLevel?.Invoke();
        });
        return ValueRow(key, "Load as", description, editor);
    }

    private Grid SettingRow(GameToolsTool tool, GameToolsSetting setting, bool enabled, bool applyNow)
    {
        var key = "tool:" + tool.Id + ".setting:" + setting.Id;
        var id = setting.Id;
        var value = applyNow
            ? setting.Value
            : _draftSettings.GetValueOrDefault(tool.Id)?.GetValueOrDefault(id) ?? setting.Value;
        var description = string.Join(" ", new[] { setting.Description, setting.Note ?? "" }
            .Where(part => part.Length > 0));
        Control editor;
        // A plain Off/On choice is a switch; any other choice, such as Automatic or Off, stays a dropdown.
        if (setting.Options.Count == 2 && setting.Options.Any(option => option is { Value: "on", Label: "On" })
                                       && setting.Options.Any(option => option is { Value: "off", Label: "Off" })
                                       && value is "on" or "off")
        {
            var toggle = new ToggleSwitch
            {
                IsChecked = value == "on", IsEnabled = enabled, OnContent = null, OffContent = null,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            toggle.IsCheckedChanged += (_, _) => Commit(toggle.IsChecked == true ? "on" : "off");
            editor = toggle;
        }
        else
        {
            var options = setting.Options.Select(option => (option.Value, option.Label)).ToList();
            if (!options.Any(option => option.Value == value))
            {
                // The file holds a value no option names; show it as is instead of claiming a default.
                options.Insert(0, (value, setting.IniValue is { Length: > 0 } raw ? "Custom (" + raw + ")" : "Custom"));
            }

            editor = Choice(options, value, enabled, Commit);
        }

        return ValueRow(key, setting.Label, description, editor);

        void Commit(string chosen)
        {
            if (chosen == value || chosen.Length == 0)
            {
                return;
            }

            if (applyNow)
            {
                RunTool((service, token) => service.ConfigureAsync(_appId, tool.Id, null,
                    new Dictionary<string, string> { [id] = chosen }, token), "configure");
                return;
            }

            if (!_draftSettings.TryGetValue(tool.Id, out var draft))
            {
                draft = new Dictionary<string, string>(StringComparer.Ordinal);
                _draftSettings[tool.Id] = draft;
            }

            draft[id] = chosen;
            CurrentLevel?.Invoke();
        }
    }

    // An add-on is a switch: a draft that goes out with Install, or added and removed at once afterwards.
    private Grid AddonRow(GameToolsTool tool, GameToolsAddon addon, bool enabled, bool installed)
    {
        var key = "tool:" + tool.Id + ".addon:" + addon.Id;
        var on = AddonOn(tool, addon, installed);
        var description = string.Join(" ", new[] { addon.Description, addon.Text, addon.Note ?? "" }
            .Where(part => part.Length > 0));
        var toggle = new ToggleSwitch
        {
            IsChecked = on, IsEnabled = enabled, OnContent = null, OffContent = null,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        toggle.IsCheckedChanged += (_, _) =>
        {
            var chosen = toggle.IsChecked == true;
            if (chosen == on)
            {
                return;
            }

            if (installed)
            {
                RunTool((service, token) => service.SetAddonAsync(_appId, tool.Id, addon.Id, chosen, token),
                    chosen ? "add " + addon.Name : "remove " + addon.Name);
                return;
            }

            if (!_draftAddons.TryGetValue(tool.Id, out var draft))
            {
                draft = new Dictionary<string, bool>(StringComparer.Ordinal);
                _draftAddons[tool.Id] = draft;
            }

            draft[addon.Id] = chosen;
            CurrentLevel?.Invoke();
        };
        return ValueRow(key, addon.Name, description, toggle);
    }

    /// <summary>Whether a card shows an add-on on: installed, else the user's draft, else its recommendation.</summary>
    private bool AddonOn(GameToolsTool tool, GameToolsAddon addon, bool installed)
    {
        return installed
            ? addon.Installed
            : _draftAddons.GetValueOrDefault(tool.Id)?.TryGetValue(addon.Id, out var draft) == true
                ? draft
                : addon.Recommended;
    }

    // ---- Supporting actions and the footer ----

    private Grid? Secondary(GameToolsTool tool, GameToolsChannel channel, bool idle)
    {
        var key = "tool:" + tool.Id;
        List<Control> left = [];
        var installed = tool.InstalledVersion is not null;
        var checking = tool.CheckingLatest;
        if (installed || channel.LatestVersion is null)
        {
            left.Add(Command(key + ".check-updates", checking ? "Checking…" : "Check for updates",
                idle && !checking
                    ? () => RunTool((service, token) => service.CheckUpdatesAsync(_appId, token), "check updates")
                    : null));
        }

        if (tool.Actions.Contains("repair") && tool.Status != "modified")
        {
            left.Add(Command(key + ".repair", "Repair", idle
                ? () => RunTool((service, token) => service.RepairAsync(_appId, tool.Id, token), "repair")
                : null));
        }

        foreach (var addon in tool.Addons.Where(addon => addon is { Installed: true, UpdateAvailable: true }))
        {
            left.Add(Command(key + ".addon-update:" + addon.Id, "Update " + addon.Name, idle && addon.CanChange
                ? () => RunTool((service, token) => service.SetAddonAsync(_appId, tool.Id, addon.Id, true, token),
                    "update " + addon.Name)
                : null));
        }

        Control? remove = null;
        if (tool.Actions.Contains("remove"))
        {
            var button = Command(key + ".remove", "Remove " + tool.Name + "…", idle
                ? () => ConfirmRemove(tool)
                : null);
            button.Classes.Add("danger");
            remove = button;
        }

        if (left.Count == 0 && remove is null)
        {
            return null;
        }

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 12, 0, 0),
            Tag = key + ".actions"
        };
        var strip = CommandStrip([.. left]);
        row.Children.Add(strip);
        if (remove is not null)
        {
            remove.Margin = new Thickness(0, 0, 0, 8);
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
        }

        return row;
    }

    private static TextBlock Footer(GameToolsTool tool)
    {
        var homepage = tool.Homepage.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? tool.Homepage["https://".Length..]
            : tool.Homepage;
        var footer = Caption(string.Join("  ·  ", new[] { tool.License, homepage, tool.Notice }
            .Where(part => part.Length > 0)));
        footer.Margin = new Thickness(2, 12, 2, 0);
        return Tagged(footer, "tool:" + tool.Id + ".about");
    }

    // ---- Commands and their confirmations ----

    private void StartInstall(GameToolsState state, GameToolsTool tool, string game)
    {
        var acknowledged = state.AntiCheat.Count > 0;
        var request = InstallRequest(tool, acknowledged, false);
        if (!acknowledged)
        {
            RunTool((service, token) => service.InstallAsync(request, token), "install");
            return;
        }

        var found = string.Join(", ", state.AntiCheat);
        Confirm("Install " + tool.Name + " for " + game + " anyway?",
        [
            found + (state.AntiCheat.Count == 1 ? " was" : " were") + " found in this game's folder. Third-party files "
            + "in a game folder can get an online account banned.",
            "Only continue if you play this game offline. You can remove " + tool.Name + " again from this page."
        ], "Install anyway", service => service.InstallAsync(request, default));
    }

    private void ConfirmReplace(GameToolsState state, GameToolsTool tool, string game)
    {
        var request = InstallRequest(tool, state.AntiCheat.Count > 0, true);
        List<string> paragraphs =
        [
            "WSGM backs up the " + tool.Name + " files that were installed in " + game + " outside WSGM, removes "
            + "them and installs its own copy. Removing it later restores the backed-up files."
        ];
        if (state.AntiCheat.Count > 0)
        {
            paragraphs.Add(string.Join(", ", state.AntiCheat) + " was found in this game's folder. Only continue if "
                                                              + "you play this game offline.");
        }

        Confirm("Replace the existing " + tool.Name + " installation?", paragraphs, "Replace",
            service => service.InstallAsync(request, default));
    }

    private void StartUpdate(GameToolsState state, GameToolsTool tool, string game)
    {
        var appId = _appId;
        if (!tool.UpdateChangesConfig)
        {
            RunTool((service, token) => service.UpdateAsync(appId, tool.Id, false, token), "update");
            return;
        }

        Confirm("Update " + tool.Name + " and replace its settings file?",
        [
            "This version changes the format of " + tool.Name + "'s settings file. WSGM replaces it in " + game
            + " with the new default and applies the settings on this page again.",
            "Changes made in " + tool.Name + "'s own in-game menu are lost."
        ], "Update", service => service.UpdateAsync(appId, tool.Id, true, default));
    }

    private void StartSwitch(GameToolsState state, GameToolsTool tool, GameToolsChannel channel, string game)
    {
        var appId = _appId;
        var target = channel.Id;
        if (!channel.ChangesConfig)
        {
            RunTool((service, token) => service.SwitchChannelAsync(appId, tool.Id, target, false, token),
                "switch channel");
            return;
        }

        Confirm("Switch " + tool.Name + " to " + channel.Label.ToLowerInvariant() + " "
                + channel.LatestVersion + "?",
        [
            channel.Label + " " + channel.LatestVersion + " uses a different settings file format. WSGM replaces "
            + tool.Name + "'s settings file in " + game + " with the new default and applies the settings on this "
            + "page again.",
            "Changes made in " + tool.Name + "'s own in-game menu are lost."
        ], "Switch", service => service.SwitchChannelAsync(appId, tool.Id, target, true, default));
    }

    private void ConfirmRemove(GameToolsTool tool)
    {
        var appId = _appId;
        var game = CurrentState() is { } state ? GameName(state) : _gameName;
        var files = tool.Files;
        List<string> paragraphs =
        [
            "WSGM deletes the " + Count(files.Owned, "file") + " it added to " + game + ", including "
            + tool.Name + "'s settings file, and the logs " + tool.Name + " wrote."
        ];
        paragraphs.Add(files.Replaced > 0
            ? "It restores the " + Count(files.Replaced, "game file") + " it backed up. Files that changed after "
              + "installation are left in place and named afterwards."
            : "No game file was replaced, so nothing needs restoring. Files that changed after installation are "
              + "left in place and named afterwards.");
        Confirm("Remove " + tool.Name + " from " + game + "?", paragraphs, "Remove",
            service => service.RemoveAsync(appId, tool.Id, default));
    }

    /// <summary>
    ///     Asks in the Overlay's in-window surface, with Cancel focused, and runs the command only when the user
    ///     accepts; a refusal is shown on the page.
    /// </summary>
    private void Confirm(string title, IReadOnlyList<string> paragraphs, string accept,
        Func<IGameToolsSource, Task<SteamUiCommandResult>> command)
    {
        if (_service is null || TopLevel.GetTopLevel(this) is not OverlayWindow window)
        {
            return;
        }

        var content = new StackPanel { Spacing = 14, Tag = "confirm" };
        foreach (var paragraph in paragraphs)
        {
            content.Children.Add(new TextBlock { Text = paragraph, TextWrapping = TextWrapping.Wrap, LineHeight = 22 });
        }

        var cancel = Command("confirm.cancel", "Cancel", () => window.Surfaces.CloseActive());
        var confirm = Command("confirm.accept", accept, () =>
        {
            window.Surfaces.CloseActive();
            RunTool((service, _) => command(service), "confirm");
        });
        confirm.Classes.Add("danger");
        foreach (var button in new[] { cancel, confirm })
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
        buttons.Children.Add(confirm);
        content.Children.Add(buttons);
        window.Surfaces.ShowConfirm(content, title, cancel);
    }

    private GameToolsInstallRequest InstallRequest(GameToolsTool tool, bool acknowledgeRisk, bool replaceExisting)
    {
        var settings = _draftSettings.TryGetValue(tool.Id, out var draft)
            ? new Dictionary<string, string>(draft, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        // Send what the page shows: a draft that is no longer offered falls back to the service's name.
        var shown = ProxyFor(tool);
        var proxy = shown == tool.ProxyName ? null : shown;
        var channel = DraftChannel(tool).Id;

        // Every add-on the card shows goes out as shown, so a recommendation the user left on is installed too.
        var addons = tool.Addons.ToDictionary(addon => addon.Id, addon => AddonOn(tool, addon, false),
            StringComparer.Ordinal);
        return new GameToolsInstallRequest(_appId, tool.Id, proxy, settings, acknowledgeRisk, replaceExisting,
            channel == tool.Channels[0].Id ? null : channel, addons.Count > 0 ? addons : null);
    }

    /// <summary>The channel the card shows: the user's draft while offered, otherwise the one the tool follows.</summary>
    private GameToolsChannel DraftChannel(GameToolsTool tool)
    {
        if (_draftChannel.TryGetValue(tool.Id, out var draft)
            && tool.Channels.FirstOrDefault(channel => channel.Id == draft) is { } chosen)
        {
            return chosen;
        }

        return tool.Channels.FirstOrDefault(channel => channel.Current)
               ?? tool.Channels.FirstOrDefault()
               ?? new GameToolsChannel(tool.Channel, tool.Channel, "", true, tool.LatestVersion, tool.LatestLabel, null,
                   tool.LatestError, tool.CheckingLatest, false);
    }

    /// <summary>The draft name while not installed and still offered, otherwise the service's name.</summary>
    private string ProxyFor(GameToolsTool tool)
    {
        if (tool.InstalledVersion is null && _draftProxy.TryGetValue(tool.Id, out var draft)
                                          && tool.ProxyOptions.Any(option => option.Name == draft && option.Available))
        {
            return draft;
        }

        return tool.ProxyName;
    }
}
