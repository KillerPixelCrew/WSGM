using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

public sealed partial class GameToolsView
{
    private readonly Dictionary<string, string> _draftChannel = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _draftProxy = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _draftSettings = new(StringComparer.Ordinal);
    private uint _appId;
    private string _gameName = "";

    /// <summary>Opens one game's page above the list and starts a scan of its folder.</summary>
    /// <param name="appId">Steam AppID or non-Steam shortcut id.</param>
    /// <param name="name">The library name, shown until the scan reports one; may be empty.</param>
    private void OpenGame(uint appId, string name)
    {
        ClearGame();
        _appId = appId;
        _gameName = name;
        Navigate(RenderGame);
        _ = RunSafelyAsync(ScanAsync(appId), "scan");
    }

    private void ClearGame()
    {
        _appId = 0;
        _gameName = "";
        _draftSettings.Clear();
        _draftProxy.Clear();
        _draftChannel.Clear();
    }

    private async Task ScanAsync(uint appId)
    {
        if (_service is not { } service)
        {
            return;
        }

        var generation = NavigationGeneration;
        await Task.Run(() => service.ReadAsync(appId));
        // The service raised Changed as well; this redraw covers a page hidden behind a surface meanwhile.
        if (generation == NavigationGeneration && appId == _appId)
        {
            CurrentLevel?.Invoke();
        }
    }

    private GameToolsState? CurrentState()
    {
        return _service is { } service && _appId != 0 ? service.Peek(_appId) : null;
    }

    private string GameName(GameToolsState state)
    {
        // A placeholder state carries the AppID as its name until the scan reports the real one.
        var placeholder = state.Loading && state.Name == state.AppId.ToString(CultureInfo.InvariantCulture);
        return state.Name.Length > 0 && !placeholder
            ? state.Name
            : _gameName.Length > 0
                ? _gameName
                : state.Name;
    }

    private void RenderGame()
    {
        if (CurrentState() is not { } state)
        {
            RenderMessage("Game Tools", "Game Tools is not available in this session.");
            return;
        }

        var name = GameName(state);
        var body = NewStack("");
        body.Children.Add(Tagged(Caption("Game Tools  ›  " + name), "game.crumb"));
        if (state.Error is { Length: > 0 } error)
        {
            body.Children.Add(Banner("game.error", FAInfoBarSeverity.Error, null, error));
        }
        else if (state.Notice is { Length: > 0 } notice && state.Busy is null)
        {
            body.Children.Add(Banner("game.notice", FAInfoBarSeverity.Success, null, notice));
        }

        if (state.Availability != "ready" && state.Reason is { Length: > 0 } reason)
        {
            body.Children.Add(Banner("game.availability",
                state.Availability == "running" ? FAInfoBarSeverity.Warning : FAInfoBarSeverity.Informational,
                AvailabilityTitle(state.Availability), reason));
        }

        body.Children.Add(GameCard(state, name));
        var hasFolder = state.Availability is "ready" or "running";
        var cardProgress = false;
        if (state.Loading && state.Tools.Count == 0)
        {
            body.Children.Add(new OverlayProgress("game.busy", ProgressOf(state)));
            cardProgress = true;
        }
        else if (hasFolder)
        {
            if (state.AntiCheat.Count > 0)
            {
                body.Children.Add(Banner("game.anticheat", FAInfoBarSeverity.Error, AntiCheatTitle(state),
                    AntiCheatMessage(state)));
            }

            foreach (var tool in state.Tools)
            {
                cardProgress |= state.Busy?.ToolId == tool.Id;
                body.Children.Add(ToolCard(state, tool, name));
            }
        }
        else
        {
            foreach (var tool in state.Tools)
            {
                body.Children.Add(UnavailableCard(tool));
            }
        }

        if (!cardProgress)
        {
            // The level's one live progress element: the folder scan, or work for a tool this page doesn't draw.
            body.Children.Add(new OverlayProgress("game.busy", ProgressOf(state)));
        }

        var idle = state.Busy is null && !state.Loading;
        body.Children.Add(Tagged(CommandStrip(
            Command("game.refresh", "Scan the folder again", idle
                ? () => RunTool((service, token) => service.RefreshAsync(_appId, token), "refresh")
                : null),
            Command("game.back", "Back to games", () => Back())), "game.commands"));
        SetGameContent(body, state, ProgressOf);
    }

    /// <summary>
    ///     Sets a level of the open game: download ticks update its progress element in place, and only a structural
    ///     change of the game's state redraws it.
    /// </summary>
    /// <param name="body">The level.</param>
    /// <param name="state">The state the level was drawn from.</param>
    /// <param name="progress">The level's progress element state, or null for a level without one.</param>
    private void SetGameContent(StackPanel body, GameToolsState state,
        Func<GameToolsState, OverlayProgressState?>? progress)
    {
        SetLiveContent(body, state, CurrentState, SameStructure, progress);
    }

    /// <summary>
    ///     Whether two states of the game draw the same page apart from the running work's byte progress. A new
    ///     phase is structural, because the step indicator, the Cancel availability and the explanation follow it.
    /// </summary>
    private static bool SameStructure(GameToolsState shown, GameToolsState next)
    {
        // The cached scan's lists keep their identity between publications; Peek re-projects only the tools.
        return shown with { Busy = null, Tools = Array.Empty<GameToolsTool>() }
               == next with { Busy = null, Tools = Array.Empty<GameToolsTool>() }
               && (shown.Busy is null
                   ? next.Busy is null
                   : next.Busy is not null
                     && shown.Busy with { Bytes = 0, Total = 0 }
                     == next.Busy with { Bytes = 0, Total = 0 })
               && shown.Tools.SequenceEqual(next.Tools);
    }

    /// <summary>The level's progress element: the operation's phase line, amount and Cancel, or the folder scan.</summary>
    private OverlayProgressState? ProgressOf(GameToolsState state)
    {
        if (state.Busy is { } busy)
        {
            var amount = busy.Bytes <= 0
                ? ""
                : busy.Total > 0
                    ? $" · {LibraryCardView.FormatBytes(busy.Bytes)} of {LibraryCardView.FormatBytes(busy.Total)}"
                    : $" · {LibraryCardView.FormatBytes(busy.Bytes)}";
            var appId = state.AppId;
            return new OverlayProgressState(busy.Detail + "…" + amount, true,
                busy.Total > 0 ? (double)busy.Bytes / busy.Total : null,
                busy.Cancellable
                    ? new OverlayProgressCancel("Cancel", "",
                        () => RunTool((service, token) => service.CancelAsync(appId, token), "cancel"))
                    : null);
        }

        return state.Loading ? new OverlayProgressState("Scanning the game folder…", true) : null;
    }

    // ---- The game card ----

    private Border GameCard(GameToolsState state, string name)
    {
        // Once a tool is installed or working the card is one line, so the tool card leads the page.
        var compact = state.Busy is not null || state.Tools.Any(tool => tool.InstalledVersion is not null);
        var hasFolder = state.Availability is "ready" or "running";
        var change = hasFolder && state.Busy is null && !state.Loading
            ? Tagged(Command("game.change-program",
                state.Runtime is null ? "Choose the program…" : "Change program…",
                () => Navigate(RenderCandidates), state.Runtime is null), "game.change-program")
            : null;
        var title = new TextBlock
        {
            Text = name, FontSize = compact ? 20 : 22, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap
        };
        var heading = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(title);
        var folder = Caption(state.InstallFolder.Length > 0 ? state.InstallFolder : "Install folder not reported.");
        folder.TextWrapping = TextWrapping.NoWrap;
        folder.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(folder, state.InstallFolder);
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 16, RowSpacing = 12
        };
        var wideFacts = !compact && _wide;
        grid.Children.Add(Capsule(state.AppId, compact || !_wide ? 120 : 184, compact || !_wide ? 56 : 86));
        if (compact)
        {
            heading.Children.Add(Caption(Summary(state)));
        }
        else
        {
            heading.Children.Add(folder);
        }

        if (wideFacts && hasFolder)
        {
            var main = new StackPanel { Spacing = 12 };
            main.Children.Add(heading);
            main.Children.Add(Facts(state));
            Grid.SetColumn(main, 1);
            grid.Children.Add(main);
        }
        else
        {
            Grid.SetColumn(heading, 1);
            grid.Children.Add(heading);
            if (!compact && hasFolder)
            {
                grid.RowDefinitions = new RowDefinitions("Auto,Auto");
                var facts = Facts(state);
                Grid.SetRow(facts, 1);
                Grid.SetColumnSpan(facts, 3);
                grid.Children.Add(facts);
            }
        }

        if (change is not null)
        {
            change.VerticalAlignment = wideFacts ? VerticalAlignment.Top : VerticalAlignment.Center;
            change.Margin = default;
            Grid.SetColumn(change, 2);
            grid.Children.Add(change);
        }

        return Card(grid, "game.card:" + compact + ":" + _wide);
    }

    private Grid Facts(GameToolsState state)
    {
        var facts = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 14 };
        var runtime = state.Runtime;
        facts.Children.Add(Fact("Program", runtime?.FileName ?? (state.Loading ? "Looking…" : "Not chosen"),
            runtime is null
                ? state.RememberedMissing
                    ? "The program chosen earlier is gone"
                    : state.Loading
                        ? ""
                        : "Several programs could be the game"
                : string.Join(" · ", new[] { runtime.ArchitectureLabel, runtime.ConfidenceLabel }
                    .Where(part => part.Length > 0)), null));
        var upscalers = UpscalerLabels(state);
        Control? chips = null;
        if (upscalers.Count > 0)
        {
            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var label in upscalers)
            {
                var chip = Chip(label, "");
                chip.Margin = new Thickness(0, 0, 6, 6);
                wrap.Children.Add(chip);
            }

            chips = wrap;
        }

        var upscalerFact = Fact("Upscalers in the folder", upscalers.Count > 0 ? null : "None found",
            upscalers.Count > 0 ? "" : "The game may still have one built in", chips);
        Grid.SetColumn(upscalerFact, 1);
        facts.Children.Add(upscalerFact);
        var antiCheat = Fact("Anti-cheat",
            state.AntiCheat.Count > 0 ? string.Join(", ", state.AntiCheat) : "None found",
            state.GpuVendors.Count > 0 ? "GPU: " + string.Join(", ", state.GpuVendors.Select(VendorLabel)) : "", null);
        Grid.SetColumn(antiCheat, 2);
        facts.Children.Add(antiCheat);
        return facts;
    }

    private static StackPanel Fact(string label, string? value, string detail, Control? content)
    {
        var fact = new StackPanel { Spacing = 3 };
        fact.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(), Classes = { "caption" }, FontSize = 11, FontWeight = FontWeight.SemiBold,
            LetterSpacing = 1, TextWrapping = TextWrapping.Wrap
        });
        if (value is not null)
        {
            fact.Children.Add(new TextBlock
                { Text = value, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        }

        if (content is not null)
        {
            fact.Children.Add(content);
        }

        if (detail.Length > 0)
        {
            var caption = Caption(detail);
            caption.Margin = default;
            fact.Children.Add(caption);
        }

        return fact;
    }

    /// <summary>The compact card's one line: program, how it was chosen, upscalers and anti-cheat.</summary>
    private static string Summary(GameToolsState state)
    {
        List<string> parts = [];
        if (state.Runtime is { } runtime)
        {
            parts.Add(runtime.RelativePath.Replace('/', '\\'));
            if (runtime.ConfidenceLabel.Length > 0)
            {
                parts.Add(char.ToLowerInvariant(runtime.ConfidenceLabel[0]) + runtime.ConfidenceLabel[1..]);
            }
        }

        var upscalers = UpscalerLabels(state);
        parts.Add(upscalers.Count > 0
            ? string.Join(", ", upscalers) + " in the folder"
            : "no standalone upscaler found");
        parts.Add(state.AntiCheat.Count > 0 ? string.Join(", ", state.AntiCheat) + " found" : "no anti-cheat found");
        return string.Join(" · ", parts);
    }

    /// <summary>The distinct upscaler technologies, each with its major and minor version when the name has none.</summary>
    private static List<string> UpscalerLabels(GameToolsState state)
    {
        return
        [
            .. state.Upscalers.Select(upscaler =>
            {
                var numbered = upscaler.Technology.Any(char.IsDigit);
                var parts = (upscaler.Version ?? "").Split('.');
                return numbered || parts.Length < 2 || parts[0].Length == 0
                    ? upscaler.Technology
                    : upscaler.Technology + " " + parts[0] + "." + parts[1];
            }).Distinct(StringComparer.Ordinal)
        ];
    }

    private static string AntiCheatTitle(GameToolsState state)
    {
        return string.Join(", ", state.AntiCheat) + " found in this game's folder";
    }

    // The service's warning names what was found and then the tools' own warnings; the title already names it.
    private static string AntiCheatMessage(GameToolsState state)
    {
        var warning = state.AntiCheatWarning ?? "";
        var prefix = "Anti-cheat found: " + string.Join(", ", state.AntiCheat) + ".";
        return warning.StartsWith(prefix, StringComparison.Ordinal) ? warning[prefix.Length..].Trim() : warning;
    }

    private static string? AvailabilityTitle(string availability)
    {
        return availability switch
        {
            "running" => "The game is running",
            "folderUnavailable" => "Folder unavailable",
            "notInstalled" => "Not installed",
            "steamUnavailable" => "Steam unavailable",
            _ => null
        };
    }

    /// <summary>A bordered card around a panel, keyed so its editors survive publications.</summary>
    private static Border Card(Panel content, string tag)
    {
        return new Border { Child = content, Classes = { "device-group" }, Tag = tag };
    }

    private static string VendorLabel(string vendor)
    {
        return vendor switch
        {
            "amd" => "AMD",
            "intel" => "Intel",
            "nvidia" => "NVIDIA",
            _ => vendor
        };
    }
}
