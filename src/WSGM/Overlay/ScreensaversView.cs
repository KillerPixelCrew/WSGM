using System.Linq;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>Local sources and playback options shared with Steam's native Screensaver settings.</summary>
public sealed class ScreensaversView : ServiceSubView
{
    private ScreensaverService? _service;
    private string _name = "";

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
        AddStatus(stack, false, state.Error, state.Status);
        stack.Children.Add(Caption("Steam controls idle timing. Select a stock choice or a local source below."));
        stack.Children.Add(Tagged(Row("Steam game slideshow", state.NativeSelection == "steam-gameslideshow" ? "Selected" : "Stock",
            Icons.Monitor, () => Send(new SteamScreensaverContentCommand { Action = "select", Id = "steam-gameslideshow" })), "stock.slideshow"));
        stack.Children.Add(Tagged(Row("Steam bouncing logo", state.NativeSelection == "steam-bouncinglogo" ? "Selected" : "Stock",
            Icons.Monitor, () => Send(new SteamScreensaverContentCommand { Action = "select", Id = "steam-bouncinglogo" })), "stock.logo"));
        stack.Children.Add(Tagged(Row("Source name", _name.Length == 0 ? "Choose a name before adding a folder" : _name,
            Icons.Keyboard, () => EditText("Source name", _name, 128, name => _name = name.Trim())), "source.name"));
        stack.Children.Add(Tagged(Row("Add image folder", "PNG, JPEG, WebP, GIF and BMP", Icons.FolderPlus,
            () => _ = RunSafelyAsync(AddAsync(pack: false), "add folder")), "source.images"));
        stack.Children.Add(Tagged(Row("Add pack folder", "A screensaver.json manifest with images or an HTML scene", Icons.FolderPlus,
            () => _ = RunSafelyAsync(AddAsync(pack: true), "add pack")), "source.pack"));
        stack.Children.Add(Tagged(Row("Refresh sources", "Rescan files and retry the selected source", Icons.Restart,
            () => Send(new SteamScreensaverContentCommand { Action = "refresh" })), "source.refresh"));
        foreach (var source in state.Sources)
        {
            var id = source.Id;
            stack.Children.Add(Tagged(Row(source.Name, (state.SelectedId == id ? "Selected · " : "") + source.Detail,
                Icons.Monitor, () => Navigate(() => RenderSource(id))), "source:" + id));
        }

        SetContent(stack);
    }

    private void RenderSource(string id)
    {
        if (_service?.Snapshot().Sources.FirstOrDefault(source => source.Id == id) is not { } source)
        {
            Back();
            return;
        }

        var stack = NewStack(source.Name);
        stack.Children.Add(Caption(source.Folder));
        stack.Children.Add(Caption(source.Detail));
        stack.Children.Add(Tagged(Row("Use screensaver", "Select it in Steam's chooser", Icons.Monitor,
            () => Send(new SteamScreensaverContentCommand { Action = "select", Id = id })), "source.use"));
        stack.Children.Add(Tagged(Row("Preview", "Start the selected screensaver; input dismisses it", Icons.Monitor,
            () => Send(new SteamScreensaverContentCommand { Action = "preview", Id = id })), "source.preview"));
        if (source.Type == "scene")
        {
            stack.Children.Add(ChoiceRow("Animation limit", new[] { (10, "10 fps"), (15, "15 fps"), (30, "30 fps"), (60, "60 fps") },
                source.FramesPerSecond, value => Configure(id, "framesPerSecond", value)));
        }
        else
        {
            stack.Children.Add(ChoiceRow("Order", new[] { (true, "Shuffle"), (false, "Sequential") },
                source.Shuffle, value => Configure(id, "shuffle", value ? 1 : 0)));
            stack.Children.Add(ChoiceRow("Sizing", new[] { (false, "Fit"), (true, "Fill") },
                source.Fill, value => Configure(id, "fill", value ? 1 : 0)));
            stack.Children.Add(ChoiceRow("Interval", new[] { (2, "2 seconds"), (5, "5 seconds"), (15, "15 seconds"),
                    (30, "30 seconds"), (60, "1 minute"), (300, "5 minutes"), (3600, "1 hour") },
                source.IntervalSeconds, value => Configure(id, "intervalSeconds", value)));
        }

        stack.Children.Add(Tagged(DangerRow("Remove source", "Keeps all files in the folder", Icons.Close,
            () => ConfirmCommand("Remove source", "Remove this source from WSGM? Its files will be kept.",
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
        if (_name.Length == 0)
        {
            Toast("Choose a source name first.");
            return;
        }

        var name = _name;
        var generation = NavigationGeneration;
        var folder = await PickPathAsync(folder: true);
        if (folder is null || generation != NavigationGeneration || _service is not { } service)
        {
            return;
        }

        await RunCommandAsync(token => service.ExecuteAsync(new SteamScreensaverContentCommand
        {
            Action = pack ? "addPack" : "addImages", Name = name, Folder = folder
        }, token), "add source", () => _name = "");
    }

    private void Send(SteamScreensaverContentCommand command)
    {
        if (_service is { } service)
        {
            Run(token => service.ExecuteAsync(command, token), command.Action);
        }
    }
}
