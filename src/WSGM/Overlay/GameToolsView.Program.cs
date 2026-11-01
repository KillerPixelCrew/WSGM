using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

public sealed partial class GameToolsView
{
    /// <summary>The nested level that chooses the program the game runs: the resolver's candidates and Browse.</summary>
    private void RenderCandidates()
    {
        if (CurrentState() is not { } state)
        {
            Back();
            return;
        }

        var name = GameName(state);
        var body = NewStack("");
        body.Children.Add(Tagged(Caption("Game Tools  ›  " + name + "  ›  Program"), "candidates.crumb"));
        var heading = new StackPanel { Spacing = 6, Tag = "candidates.heading" };
        heading.Children.Add(new TextBlock
        {
            Text = "Which program does " + name + " run?", FontSize = 22, FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap
        });
        var several = state.Runtime is null || state.Runtime.Confidence == "needsChoice";
        heading.Children.Add(Caption("Tools are installed beside the program the game starts. "
                                     + (several
                                         ? "Several programs in its folder could be it, so pick the game "
                                           + "itself, not its launcher. "
                                         : "Pick the game itself, not its launcher. ")
                                     + "WSGM remembers your choice."));
        body.Children.Add(heading);
        if (state.RememberedMissing)
        {
            body.Children.Add(Banner("candidates.missing", FAInfoBarSeverity.Warning, null,
                "The program you chose earlier is gone, maybe after a game update. Choose it again."));
        }

        if (state.Tools.FirstOrDefault(tool => tool.InstalledVersion is not null) is { } installed)
        {
            body.Children.Add(Banner("candidates.installed", FAInfoBarSeverity.Informational, null,
                installed.Name + " is installed beside the current program, so only a program in the same folder "
                               + "can be chosen until you remove it."));
        }

        var idle = state.Busy is null && !state.Loading;
        if (state.Candidates.Count == 0)
        {
            body.Children.Add(Tagged(Caption(state.Loading
                ? "Looking for programs in the game folder…"
                : "No programs were found in the game folder. Use Browse to choose it."), "candidates.empty"));
        }
        else
        {
            var list = new StackPanel { Spacing = 8, Tag = "candidates" };
            foreach (var candidate in state.Candidates)
            {
                list.Children.Add(Candidate(candidate, idle));
            }

            body.Children.Add(list);
        }

        if (state.ScanTruncated)
        {
            body.Children.Add(Banner("candidates.truncated", FAInfoBarSeverity.Informational, null,
                "This folder is too large to list completely, so the game's program may be missing here. "
                + "Use Browse to choose it."));
        }

        var folder = state.InstallFolder;
        var browse = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, Tag = "candidates.browse-row",
            Margin = new Thickness(0, 4, 0, 0)
        };
        var button = Command("candidates.browse", "Browse for the program…",
            idle ? () => _ = RunSafelyAsync(BrowseAsync(folder), "browse") : null);
        button.Margin = default;
        browse.Children.Add(button);
        var hint = Caption("Use Browse when the game's program is not listed.");
        hint.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(hint, 1);
        browse.Children.Add(hint);
        body.Children.Add(browse);
        body.Children.Add(CommandStrip(Command("candidates.back", "Back to the game", () => Back())));
        SetGameContent(body, state, null);
    }

    private OverlayMediaRow Candidate(GameToolsCandidate candidate, bool idle)
    {
        var path = candidate.RelativePath;
        var line = new WrapPanel { Orientation = Orientation.Horizontal };
        var role = Chip(candidate.RoleLabel, candidate.Role == "game" ? "good" : "");
        role.Margin = new Thickness(0, 2, 10, 2);
        line.Children.Add(role);
        var where = Caption(string.Join(" · ", new[] { candidate.ArchitectureLabel, Location(path) }
            .Where(part => part.Length > 0)));
        where.VerticalAlignment = VerticalAlignment.Center;
        where.Margin = new Thickness(0, 2);
        line.Children.Add(where);
        var row = new OverlayMediaRow(candidate.FileName, string.Join(" · ", candidate.Reasons),
            Radio(candidate.Chosen), line, [],
            idle ? candidate.Chosen ? () => Back() : () => ChooseRuntime(path) : null)
        {
            Tag = "candidate:" + path
        };
        AutomationProperties.SetName(row,
            candidate.FileName + (candidate.Chosen ? ", selected" : ""));
        return row;
    }

    /// <summary>Where a candidate sits: the folder root, a subfolder, or a folder outside the game's.</summary>
    private static string Location(string relativePath)
    {
        var slash = relativePath.LastIndexOfAny(['/', '\\']);
        if (slash < 0)
        {
            return "in the folder root";
        }

        var folder = relativePath[..slash].Replace('/', '\\');
        return Path.IsPathFullyQualified(relativePath) ? folder : folder + "\\";
    }

    /// <summary>The radio mark of a candidate row; the row itself is the focus target.</summary>
    private Control Radio(bool selected)
    {
        var ring = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0),
            Tag = "radio:" + selected
        };
        ring.Bind(Border.BorderBrushProperty,
            this.GetResourceObservable(selected ? "HcAccentBrush" : "DeckSecondaryBrush"));
        if (selected)
        {
            var dot = new Border
            {
                Width = 10, Height = 10, CornerRadius = new CornerRadius(5),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            dot.Bind(Border.BackgroundProperty, this.GetResourceObservable("HcAccentBrush"));
            ring.Child = dot;
        }

        return ring;
    }

    private void ChooseRuntime(string path)
    {
        var appId = _appId;
        RunTool((service, token) => service.ChooseRuntimeAsync(appId, path, token), "choose program", () => Back());
    }

    private async Task BrowseAsync(string folder)
    {
        if (TopLevel.GetTopLevel(this) is not OverlayWindow window)
        {
            return;
        }

        var generation = NavigationGeneration;
        var appId = _appId;
        var picker = new OverlayFilePicker(false, [".exe"], start: folder.Length > 0 ? folder : null);
        var path = await window.Surfaces.PickLocalPathAsync(picker, false);
        if (path is null || generation != NavigationGeneration || appId != _appId)
        {
            return;
        }

        ChooseRuntime(path);
    }
}
