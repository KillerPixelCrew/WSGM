using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;
using Path = Avalonia.Controls.Shapes.Path;

namespace WSGM.Overlay;

/// <summary>
///     The Emulators page over its one session-owned backend: five views behind a segmented track (Installed, Get
///     emulators, RetroArch cores, System defaults, BIOS and firmware) and a detail page per emulator.
/// </summary>
/// <remarks>
///     LB/RB switch the views and Y checks for updates while the page is open. Install, update, Update all and repair
///     run in the service and outlive the page; their steps and Stop show inline on the emulator they work on.
/// </remarks>
public sealed partial class EmulatorManagerView : ServiceSubView
{
    // The controls pane is narrower than this at the 980 x 640 floor; grids then use fewer columns.
    private const double WideWidth = 820;

    private static readonly (string Id, string Label)[] Views =
    [
        ("installed", "Installed"), ("catalog", "Get emulators"), ("cores", "RetroArch cores"),
        ("defaults", "System defaults"), ("bios", "BIOS and firmware")
    ];

    private static readonly string[] InstallSteps = ["Find release", "Download", "Extract", "Check startup"];

    private string? _returnFocus;
    private IEmulatorBackend? _service;
    private string _view = "installed";
    private bool _wide = true;

    /// <summary>Keeps the grids laid out for the controls pane's width.</summary>
    public EmulatorManagerView()
    {
        SizeChanged += (_, args) =>
        {
            var wide = args.NewSize.Width >= WideWidth;
            if (wide != _wide)
            {
                _wide = wide;
                CurrentLevel?.Invoke();
            }
        };
    }

    /// <inheritdoc />
    protected override string LogScope => "Emulator Manager";

    internal void Attach(IEmulatorBackend? service)
    {
        _service = service;
        AttachSource(IsEffectivelyVisible ? service : null);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            AttachSource(IsVisible ? _service : null);
        }
    }

    /// <inheritdoc />
    /// <remarks>Keeps the chosen view, searches and drafts for the next visit; running work continues.</remarks>
    internal override void Leave()
    {
        _returnFocus = null;
        base.Leave();
    }

    private protected override void RenderHome()
    {
        var shown = ReadShown();
        if (shown is null)
        {
            RenderMessage("Emulators", "Emulator information is not available in this session.");
            return;
        }

        var body = NewStack("");
        body.Children.Add(Header(shown));
        body.Children.Add(Tagged(ViewTabs("emu.view", _view,
        [
            .. Views.Select(view => (view.Id,
                view.Id == "installed" ? "Installed " + shown.Page.Installed.Length : view.Label,
                (Action)(() => SelectView(view.Id, true))))
        ]), "emu.view"));
        body.Children.Add(Progress(shown));
        switch (_view)
        {
            case "catalog":
                AddCatalog(body, shown);
                break;
            case "cores":
                AddCores(body, shown);
                break;
            case "defaults":
                AddDefaults(body, shown);
                break;
            case "bios":
                AddBios(body, shown);
                break;
            default:
                AddInstalled(body, shown);
                break;
        }

        SetEmulatorContent(body, shown);
        RestoreFocus();
    }

    /// <summary>The page title, what the page is for and Check for updates.</summary>
    private Grid Header(EmulatorShown shown)
    {
        var labels = new StackPanel { Spacing = 4 };
        labels.Children.Add(new TextBlock
        {
            Text = "Emulators", FontSize = _wide ? 22 : 20, FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap
        });
        var caption = Caption("Install, update and set up the emulators your ROM sources use. WSGM keeps each one "
                              + "portable and adds it to Steam.");
        caption.Margin = default;
        labels.Children.Add(caption);
        var check = Command("emu.refresh", shown.Progress.Operation == "refresh" ? "Checking…" : "Check for updates",
            shown.Progress.Busy ? null : () => Run(_service!.RefreshEmulatorsAsync));
        check.VerticalAlignment = VerticalAlignment.Bottom;
        check.Padding = new Thickness(14, 6);
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16, Tag = "emu.header",
            Margin = new Thickness(0, 0, 0, 4)
        };
        header.Children.Add(labels);
        Grid.SetColumn(check, 1);
        header.Children.Add(check);
        return header;
    }

    /// <summary>Switches the page to one of its views, from the track, LB/RB or a link on another view.</summary>
    /// <param name="view">The view's id.</param>
    /// <param name="focusTab">Whether focus lands on the view's segment rather than its first control.</param>
    private void SelectView(string view, bool focusTab)
    {
        _view = view;
        _returnFocus = focusTab ? "emu.view:" + view : null;
        NavigationStack.Clear();
        NavigationGeneration++;
        Replace(RenderHome);
    }

    /// <summary>LB/RB: the previous or next view, wrapping, while the page is on screen.</summary>
    /// <param name="next">True for RB.</param>
    /// <returns>Whether the page took the press.</returns>
    internal bool SelectAdjacentView(bool next)
    {
        if (!IsEffectivelyVisible || _service is null || CurrentLevel is null)
        {
            return false;
        }

        var index = Array.FindIndex(Views, view => view.Id == _view);
        index = (index + (next ? 1 : Views.Length - 1)) % Views.Length;
        SelectView(Views[index].Id, true);
        return true;
    }

    internal bool CheckForUpdates()
    {
        if (!IsEffectivelyVisible || _service is null)
        {
            return false;
        }

        if (!_service.ReadProgressState().Busy)
        {
            Run(_service.RefreshEmulatorsAsync);
        }

        return true;
    }

    /// <summary>Opens a deeper level and remembers the control that opened it, so Back lands on it again.</summary>
    private void Open(string returnTag, Action render)
    {
        _returnFocus = returnTag;
        Navigate(render);
    }

    /// <summary>Puts focus back on the control a Back or a view switch returns to, after the level's own focus.</summary>
    private void RestoreFocus()
    {
        // A level that does not draw the control keeps the request for the level Back returns to.
        if (_returnFocus is not { } tag || Content is not Control root
                                        || root.GetLogicalDescendants().OfType<Control>()
                                                .FirstOrDefault(control =>
                                                    Equals(control.Tag, tag) && control.Focusable)
                                            is not { } target)
        {
            return;
        }

        _returnFocus = null;
        Dispatcher.UIThread.Post(() =>
        {
            if (target.IsEffectivelyVisible && target.IsEffectivelyEnabled)
            {
                target.BringIntoView();
                target.Focus(NavigationMethod.Directional);
            }
        });
    }

    private bool Matches(string search, params string[] values)
    {
        return search.Length == 0 || EmulatorPresentation.Matches(search, values);
    }

    // ---- Shared pieces ----

    /// <summary>
    ///     An emulator's icon: the icon of its Steam shortcut, the art the shortcut sync applied from SteamGridDB, or a
    ///     tile with the emulator glyph while it has none.
    /// </summary>
    private Control Icon(uint appId, double size)
    {
        var tile = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(8), Classes = { "badge" },
            Padding = default, VerticalAlignment = VerticalAlignment.Center, Tag = "emu.icon:none",
            Child = new Path
            {
                Data = Icons.Grid4, Width = size * 0.42, Height = size * 0.42, Stretch = Stretch.Uniform,
                StrokeThickness = 1.6, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        ((Path)tile.Child).Bind(Shape.StrokeProperty, this.GetResourceObservable("HcAccentBrush"));
        if (appId == 0)
        {
            return tile;
        }

        tile.Tag = null;
        return new OverlayPreviewImage("emu.icon:" + appId, token => Task.Run(() =>
                SteamArtwork.FindCustomArtFile(appId, ArtworkAsset.Icon) is { } path ? File.ReadAllBytes(path) : [],
            token), size)
        {
            Width = size, ClipToBounds = true, CornerRadius = new CornerRadius(8), Placeholder = tile,
            VerticalAlignment = VerticalAlignment.Center, Tag = "emu.icon:" + appId
        };
    }

    /// <summary>A title and caption with an optional control at the right: one line of a card.</summary>
    private static Grid Line(string key, string title, string detail, Control? trailing, Control? leading = null)
    {
        var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock
            { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (detail.Length > 0)
        {
            var caption = Caption(detail);
            caption.Margin = default;
            labels.Children.Add(caption);
        }

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12, MinHeight = 48,
            Margin = new Thickness(0, 4), Tag = key
        };
        if (leading is not null)
        {
            leading.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(leading);
        }

        Grid.SetColumn(labels, 1);
        row.Children.Add(labels);
        if (trailing is not null)
        {
            trailing.VerticalAlignment = VerticalAlignment.Center;
            trailing.Margin = default;
            Grid.SetColumn(trailing, 2);
            row.Children.Add(trailing);
            if (AutomationProperties.GetName(trailing) is not { Length: > 0 } && trailing is not ActionButton)
            {
                AutomationProperties.SetName(trailing, title);
            }
        }

        return row;
    }

    /// <summary>A small round state mark: done, missing or optional.</summary>
    private static Border StateMark(bool ready, bool optional)
    {
        var mark = Chip(ready ? "✓" : optional ? "–" : "✕", ready ? "good" : optional ? "" : "bad");
        mark.Width = 22;
        mark.Height = 22;
        mark.CornerRadius = new CornerRadius(11);
        mark.Padding = default;
        if (mark.Child is TextBlock text)
        {
            text.HorizontalAlignment = HorizontalAlignment.Center;
        }

        return mark;
    }

    /// <summary>A small uppercase label over a group of rows inside a card.</summary>
    private static TextBlock Eyebrow(string text)
    {
        return new TextBlock
        {
            Text = text.ToUpperInvariant(), Classes = { "caption" }, FontSize = 11, FontWeight = FontWeight.SemiBold,
            LetterSpacing = 1, Margin = new Thickness(2, 10, 2, 4), VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static Border Divider()
    {
        return new Border { Height = 1, Classes = { "section-divider" } };
    }

    /// <summary>A card's heading with an optional chip.</summary>
    private static Grid CardTitle(string key, string title, Control? chip)
    {
        var heading = new Grid
            { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 6), Tag = key };
        heading.Children.Add(new TextBlock
        {
            Text = title, FontSize = 16, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center
        });
        if (chip is not null)
        {
            Grid.SetColumn(chip, 1);
            heading.Children.Add(chip);
        }

        return heading;
    }

    /// <summary>Lays cards out in a grid of equal columns whose rows take their own height.</summary>
    private static Grid CardGrid(string key, Control[] cards, int columns)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", columns))),
            RowDefinitions =
                new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", Math.Max(1, (cards.Length + columns - 1)
                    / columns)))),
            // Cards keep their grid cell when reconciled, so a different set or layout is a different grid.
            Tag = key + ":" + columns
        };
        for (var index = 0; index < cards.Length; index++)
        {
            var card = cards[index];
            card.Margin = new Thickness(0, 0, index % columns == columns - 1 ? 0 : 10, 10);
            Grid.SetColumn(card, index % columns);
            Grid.SetRow(card, index / columns);
            grid.Children.Add(card);
        }

        return grid;
    }

    // ---- Progress ----

    /// <summary>The level's one progress element, for work that is not drawn inside an emulator's own card.</summary>
    private OverlayProgress Progress(EmulatorShown shown)
    {
        return new OverlayProgress("emu.progress", ProgressOf(shown.Progress), "emu.stop");
    }

    /// <summary>Whether running work belongs to the given installation or definition, so its card shows it.</summary>
    private static bool WorksOn(EmulatorProgressState progress, string id)
    {
        return progress.Busy && progress.TargetId == id
                             && progress.Operation is "install" or "update" or "repair" or "updateAll";
    }

    /// <summary>An install, update or repair inside its emulator's card: the steps, the meter and Stop.</summary>
    private Border WorkPanel(EmulatorShown shown)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(StepChips(InstallSteps, shown.Progress.Phase switch
        {
            "find" => 0,
            "download" => 1,
            "extract" => 2,
            "check" => 3,
            _ => -1
        }));
        panel.Children.Add(new OverlayProgress("emu.progress", ProgressOf(shown.Progress), "emu.stop", true));
        panel.Children.Add(Caption(shown.Progress.Operation == "install"
            ? "Stopping leaves nothing installed. The emulator is added only after its startup check passes, and "
              + "leaving this page doesn't stop it."
            : "Stopping keeps the active version. The new one replaces it only after its startup check passes, and "
              + "leaving this page doesn't stop it."));
        return new Border
        {
            Child = panel, Classes = { "device-group" }, Margin = new Thickness(0, 12, 0, 0), Tag = "emu.work"
        };
    }

    /// <summary>
    ///     The manager's running work and its last result, from the service's progress channel: the status line with
    ///     the downloaded amount, a bar while it works and Stop while it can be stopped.
    /// </summary>
    private OverlayProgressState ProgressOf(EmulatorProgressState progress)
    {
        var amount = progress.Bytes <= 0
            ? ""
            : progress.Total > 0
                ? $" · {LibraryCardView.FormatBytes(progress.Bytes)} of {LibraryCardView.FormatBytes(progress.Total)}"
                : $" · {LibraryCardView.FormatBytes(progress.Bytes)}";
        return new OverlayProgressState(progress.Status + amount, progress.Busy,
            progress.Busy && progress.Total > 0 ? Math.Clamp((double)progress.Bytes / progress.Total, 0, 1) : null,
            progress.Busy
                ? new OverlayProgressCancel("Stop operation",
                    progress.Operation == "install" ? "Install nothing" : "Keep the active installation",
                    () => Run(_service!.CancelAsync))
                : null);
    }

    /// <summary>
    ///     Sets an emulator level: the service's progress channel updates its progress element in place, and only a
    ///     new catalogue revision or a change of the running work's kind, target or step redraws it.
    /// </summary>
    /// <param name="body">The level.</param>
    /// <param name="shown">The reading the level was drawn from.</param>
    private void SetEmulatorContent(StackPanel body, EmulatorShown shown)
    {
        SetLiveContent(body, shown, ReadShown, SameStructure, next => ProgressOf(next.Progress));
    }

    /// <summary>Reads what an emulator level draws: the catalogue revision, its page projection and the progress.</summary>
    /// <returns>The reading, or null when the session has no emulator manager.</returns>
    private EmulatorShown? ReadShown()
    {
        // The revision first: a projection that changes after it is read carries a newer revision, so the next
        // publication sees it as a change and redraws.
        return _service is { } service
            ? new EmulatorShown(service.CatalogRevision, service.ReadPageState(), service.ReadProgressState())
            : null;
    }

    /// <summary>Whether two readings draw the same level apart from the status line and the bar.</summary>
    private static bool SameStructure(EmulatorShown shown, EmulatorShown next)
    {
        return shown.Catalog == next.Catalog && shown.Progress.Busy == next.Progress.Busy
                                             && shown.Progress.Operation == next.Progress.Operation
                                             && shown.Progress.TargetId == next.Progress.TargetId
                                             && shown.Progress.Phase == next.Progress.Phase;
    }

    private async Task ChooseAsync(bool folder, string[] extensions, Action<string> chosen)
    {
        var generation = NavigationGeneration;
        var path = await PickPathAsync(folder, extensions);
        if (path is not null && generation == NavigationGeneration)
        {
            chosen(path);
        }
    }

    /// <summary>What an emulator level was drawn from, split as the service publishes it.</summary>
    /// <param name="Catalog">The service's catalogue revision when it was read.</param>
    /// <param name="Page">The page projection: catalogue, installations, BIOS, choices and shortcut status.</param>
    /// <param name="Progress">The manager's busy state and status line.</param>
    private sealed record EmulatorShown(long Catalog, EmulatorPageState Page, EmulatorProgressState Progress)
    {
        /// <summary>The projection's catalogue with the manager's busy state and status line.</summary>
        internal EmulatorSnapshot Snapshot { get; } =
            Page.Emulators with { Busy = Progress.Busy, Status = Progress.Status };
    }
}
