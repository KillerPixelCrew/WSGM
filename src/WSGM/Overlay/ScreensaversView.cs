using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using SteamUiToolkit;
using WSGM.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>A visual library of local screensavers shared with Steam.</summary>
public sealed class ScreensaversView : ServiceSubView
{
    private ScreensaverService? _service;
    private int _columns = 2;
    private bool _compact;
    private TopLevel? _window;

    /// <summary>Keeps source covers sized to the controls pane.</summary>
    public ScreensaversView()
    {
        SizeChanged += (_, args) =>
        {
            Adapt(args.NewSize.Width, _window?.Bounds.Height ?? 800);
        };
        AttachedToVisualTree += (_, _) =>
        {
            _window = TopLevel.GetTopLevel(this);
            if (_window is not null)
            {
                _window.SizeChanged += WindowResized;
                Adapt(Bounds.Width, _window.Bounds.Height);
            }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_window is not null)
            {
                _window.SizeChanged -= WindowResized;
            }

            _window = null;
        };
    }

    private void WindowResized(object? sender, SizeChangedEventArgs args) => Adapt(Bounds.Width, args.NewSize.Height);

    private void Adapt(double width, double height)
    {
        var columns = width >= 920 ? 3 : width >= 500 ? 2 : 1;
        var compact = height < 740;
        if (columns == _columns && compact == _compact)
        {
            return;
        }

        _columns = columns;
        _compact = compact;
        CurrentLevel?.Invoke();
    }

    /// <inheritdoc />
    protected override string LogScope => "Screensavers";

    internal void Attach(ScreensaverService? service)
    {
        _service = service;
        AttachSource(service);
    }

    private protected override void RenderHome()
    {
        var stack = NewStack("Screensavers");
        if (_service is not { } service)
        {
            stack.Children.Add(Caption("Screensaver sources are unavailable in this session."));
            SetContent(stack);
            return;
        }

        var state = service.Snapshot();
        var choices = new[] { ("steam-gameslideshow", "Steam game slideshow"), ("steam-bouncinglogo", "Steam bouncing logo") }
            .Concat(state.Sources.Select(source => (source.SelectorId, source.Name))).ToArray();
        stack.Children.Add(ChoiceRow("Active screensaver", choices,
            string.IsNullOrEmpty(state.NativeSelection) ? "steam-gameslideshow" : state.NativeSelection,
            choice => Send(new SteamScreensaverContentCommand
            {
                Action = "select", Id = state.Sources.FirstOrDefault(source => source.SelectorId == choice)?.Id ?? choice
            })));
        stack.Children.Add(Caption("Choose a source to see its preview and settings. Steam controls idle timing."));
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Tag = "sources.actions" };
        AddAction(actions, "Add image folder", () => _ = RunSafelyAsync(AddAsync(pack: false), "add folder"), "source.images", primary: true);
        AddAction(actions, "Import pack", () => _ = RunSafelyAsync(AddAsync(pack: true), "import pack"), "source.pack");
        AddAction(actions, "Refresh", () => Send(new SteamScreensaverContentCommand { Action = "refresh" }), "source.refresh");
        stack.Children.Add(actions);
        if (state.Error is { Length: > 0 })
        {
            AddStatus(stack, false, state.Error, null);
        }

        if (state.Sources.Count == 0)
        {
            var empty = new StackPanel { Spacing = 12, Margin = new Thickness(24, 38) };
            empty.Children.Add(new TextBlock { Text = "No custom screensavers yet", FontSize = 22, FontWeight = FontWeight.SemiBold, TextAlignment = TextAlignment.Center });
            empty.Children.Add(Caption("Add a folder of photos to create a slideshow, or import an image or scene pack. Your files stay in their folder."));
            stack.Children.Add(Group(empty));
        }
        else
        {
            var grid = new UniformGrid { Columns = _columns, Tag = "sources.library:" + _columns };
            foreach (var source in state.Sources)
            {
                var id = source.Id;
                var card = new StackPanel { Spacing = 0, Margin = new Thickness(0, 0, 12, 12), Tag = "card:" + id };
                card.Children.Add(Cover(source, height: _compact ? 110 : 170, fill: true));
                var selected = state.SelectedId == id;
                var button = Tagged(Row(source.Name,
                    !source.Available ? source.Detail : (selected ? "Selected · " : "") + Description(source),
                    null, () => Navigate(() => RenderSource(id))), "source:" + id);
                button.Classes.Set("primary", selected);
                ToolTip.SetTip(button, source.Folder);
                card.Children.Add(button);
                grid.Children.Add(card);
            }

            stack.Children.Add(grid);
        }

        SetContent(stack);
    }

    private Control Cover(SteamScreensaverSource source, double height, bool fill)
    {
        if (source.HasPreview && _service is { } service)
        {
            return new OverlayPreviewImage(source.Id + ":" + source.PreviewRevision,
                async token => await service.ReadThumbnailAsync(source.Id, token).ConfigureAwait(false) ?? [],
                height, fill ? Stretch.UniformToFill : Stretch.Uniform)
            {
                Tag = "cover:" + source.Id + ":" + source.PreviewRevision + ":" + fill,
                ClipToBounds = true
            };
        }

        var placeholder = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        placeholder.Children.Add(new TextBlock { Text = source.Type == "scene" ? "HTML scene" : "Image slideshow", FontSize = 22, TextAlignment = TextAlignment.Center });
        var border = Group(placeholder);
        border.Height = height;
        return border;
    }

    private void RenderSource(string id)
    {
        if (_service?.Snapshot() is not { } state || state.Sources.FirstOrDefault(source => source.Id == id) is not { } source)
        {
            Back();
            return;
        }

        var stack = NewStack(source.Name);
        if (state.Error is { Length: > 0 })
        {
            AddStatus(stack, false, state.Error, null);
        }

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 16, Tag = "source.hero" };
        top.Children.Add(Cover(source, height: _compact ? 130 : 190, fill: source.Fill));
        var summary = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        if (!_compact)
        {
            summary.Children.Add(SectionLabel(state.SelectedId == id ? "SELECTED SCREENSAVER" : SourceKind(source).ToUpperInvariant()));
        }
        summary.Children.Add(Caption(source.Available ? Description(source) : source.Detail));
        summary.Children.Add(Tagged(PrimaryRow(state.SelectedId == id ? "Selected" : "Use screensaver", "", Icons.Monitor,
            source.Available ? () => Send(new SteamScreensaverContentCommand { Action = "select", Id = id }) : null), "source.use"));
        summary.Children.Add(Tagged(Row("Preview fullscreen", _compact ? "" : "Move or press a key to return", Icons.Play,
            source.Available ? () => Send(new SteamScreensaverContentCommand { Action = "preview", Id = id }) : null), "source.preview"));
        Grid.SetColumn(summary, 1);
        top.Children.Add(summary);
        stack.Children.Add(top);
        stack.Children.Add(SectionLabel(source.Type == "scene" ? "SCENE" : "SLIDESHOW"));
        var options = new StackPanel { Spacing = 10, Margin = new Thickness(14) };
        if (source.Type == "scene")
        {
            options.Children.Add(ChoiceRow("Animation rate", new[] { 10, 15, 30, 60, source.FramesPerSecond }.Distinct().Order()
                    .Select(value => (value, value + " fps")).ToArray(), source.FramesPerSecond,
                value => Configure(id, "framesPerSecond", value)));
        }
        else
        {
            options.Children.Add(ChoiceRow("Image order", new[] { (true, "Shuffle"), (false, "Filename order") },
                source.Shuffle, value => Configure(id, "shuffle", value ? 1 : 0)));
            options.Children.Add(ChoiceRow("Change image every", new[] { 2, 5, 10, 15, 30, 60, 120, 300, 900, 3600, source.IntervalSeconds }
                    .Distinct().Order().Select(value => (value, Interval(value))).ToArray(), source.IntervalSeconds,
                value => Configure(id, "intervalSeconds", value)));
            options.Children.Add(ChoiceRow("Image sizing", new[] { (false, "Fit"), (true, "Fill") },
                source.Fill, value => Configure(id, "fill", value ? 1 : 0)));
            options.Children.Add(Caption(source.Fill ? "Crop images to fill the display." : "Show the whole image, with borders when needed."));
        }

        stack.Children.Add(Group(options));
        stack.Children.Add(SectionLabel("SOURCE"));
        stack.Children.Add(Tagged(Row("Name", source.Name, Icons.Keyboard,
            () => EditText("Rename screensaver", source.Name, 128,
                name => Send(new SteamScreensaverContentCommand { Action = "rename", Id = id, Name = name }))), "source.rename"));
        stack.Children.Add(Tagged(Row("Folder", source.Folder, Icons.FolderPlus,
            () => _ = RunSafelyAsync(RelocateAsync(id), "change folder")), "source.folder"));
        if (!source.Available)
        {
            stack.Children.Add(Tagged(Row("Check folder again", "Retry after reconnecting the drive", Icons.Restart,
                () => Send(new SteamScreensaverContentCommand { Action = "refresh" })), "source.retry"));
        }

        stack.Children.Add(Tagged(DangerRow("Remove from library", "Keeps all files in the folder", Icons.Close,
            () => ConfirmCommand("Remove " + source.Name, "Remove this source from your screensaver library? Its files will be kept.",
                token => _service!.ExecuteAsync(new SteamScreensaverContentCommand { Action = "remove", Id = id }, token))), "source.remove"));
        SetContent(stack);
    }

    private void Configure(string id, string option, int value)
    {
        Send(new SteamScreensaverContentCommand
        {
            Action = "configure", Id = id, Option = option, Shuffle = value != 0, Fill = value != 0,
            IntervalSeconds = value, FramesPerSecond = value
        });
    }

    private async Task AddAsync(bool pack)
    {
        var generation = NavigationGeneration;
        var folder = await PickPathAsync(folder: true);
        if (folder is null || generation != NavigationGeneration || _service is not { } service)
        {
            return;
        }

        var initial = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        EditText(pack ? "Name this pack" : "Name this slideshow", initial.Length > 0 ? initial : "My screensaver", 128,
            name => Run(token => service.ExecuteAsync(new SteamScreensaverContentCommand
            {
                Action = pack ? "addPack" : "addImages", Name = name, Folder = folder
            }, token), "add source"));
    }

    private async Task RelocateAsync(string id)
    {
        var generation = NavigationGeneration;
        var folder = await PickPathAsync(folder: true);
        if (folder is not null && generation == NavigationGeneration)
        {
            Send(new SteamScreensaverContentCommand { Action = "relocate", Id = id, Folder = folder });
        }
    }

    private void Send(SteamScreensaverContentCommand command)
    {
        if (_service is { } service)
        {
            Run(token => service.ExecuteAsync(command, token), command.Action);
        }
    }

    private static Border Group(Control child)
    {
        return new Border { Child = child, Classes = { "device-group" } };
    }

    private static void AddAction(Panel panel, string title, Action action, string tag, bool primary = false)
    {
        var button = primary ? PrimaryRow(title, "", null, action) : Row(title, "", null, action);
        button.Tag = tag;
        button.Margin = new Thickness(0, 0, 8, 8);
        panel.Children.Add(button);
    }

    private static string SourceKind(SteamScreensaverSource source) => source.Type == "scene" ? "Scene pack" : source.Pack ? "Image pack" : "Image folder";
    private static string Description(SteamScreensaverSource source) => source.Type == "scene" ? source.FramesPerSecond + " fps animation"
        : source.ImageCount + (source.ImageCount == 1 ? " image" : " images") + " · " + (source.Shuffle ? "Shuffle" : "Filename order") + " · " + Interval(source.IntervalSeconds);
    private static string Interval(int seconds) => seconds < 60 ? seconds + " seconds" : seconds < 3600 ? seconds / 60d + " minutes" : "1 hour";
}
