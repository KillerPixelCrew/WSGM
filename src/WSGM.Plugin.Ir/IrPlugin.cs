using System.IO.Ports;
using System.Security.Cryptography;
using WSGM.Plugin.Sdk;

namespace WSGM.Plugin.Ir;

/// <summary>Independent IR command library and versioned endpoint integration.</summary>
public sealed class IrPlugin : IPlugin, IConfigurablePlugin, IPluginActions, IPluginUi
{
    private const string UsbTransport = "usb", WifiTransport = "wifi";
    // Half-second polls, bounded to the ten minutes a sequence's delays may add up to.
    private const int SequencePollLimit = 1200;
    private readonly Func<IrEndpointTarget, IIrEndpoint> _createEndpoint;

    /// <summary>Creates an inactive plugin. Resources are acquired only by explicit connection.</summary>
    public IrPlugin() : this(IrEndpointConnection.Create) { }

    internal IrPlugin(Func<IrEndpointTarget, IIrEndpoint> createEndpoint) => _createEndpoint = createEndpoint;
    private readonly SemaphoreSlim _lane = new(1, 1);
    private IPluginHost? _host;
    private PluginContext? _context;
    private IIrEndpoint? _endpoint;
    private IrLibrary _library = IrLibrary.Empty;
    private IrRemoteCatalog? _remotes;
    private IrPairing? _pairing;
    private string _port = "", _transport = UsbTransport, _hostName = "";
    private string _lastCommand = "";
    private long _sequence;
    private bool _stopped = true;

    /// <inheritdoc />
    public string Id => "wsgm.ir";

    /// <inheritdoc />
    public IReadOnlyList<PluginSetting> Settings { get; } =
    [
        new("port", "USB serial port", PluginSettingKind.Text, new(Text: "")),
        new("transport", "Endpoint connection", PluginSettingKind.Text, new(Text: UsbTransport), Choices: [UsbTransport, WifiTransport]),
        new("host", "Wi-Fi host name or IP (empty uses the paired endpoint)", PluginSettingKind.Text, new(Text: "")),
    ];

    private static PluginSetting Text(string key, string label, string value = "") =>
        new(key, label, PluginSettingKind.Text, new(Text: value));

    /// <inheritdoc />
    public IReadOnlyList<PluginAction> Actions { get; } =
    [
        new("discover", "Find USB endpoints", []),
        new("connect", "Connect IR endpoint", []),
        new("wifi-setup", "Pair Wi-Fi over USB", [Text("ssid", "Network name (SSID)"), Text("password", "Network password")]),
        new("wifi-clear", "Forget Wi-Fi over USB", []),
        new("learn", "Learn command", [Text("device", "Device", "Remote"), Text("name", "Command name", "Learned command")]),
        new("select", "Select command", [Text("command", "Device / command name")]),
        new("rename", "Rename selected command", [Text("device", "Device"), Text("name", "Command name")]),
        new("relearn", "Relearn selected command", []),
        new("timing", "Edit repeat timing", [
            new("repeats", "Additional repeats", PluginSettingKind.Number, new(Number: 0), 0, 4),
            new("gap-ms", "Gap between repeats (ms)", PluginSettingKind.Number, new(Number: 40), 0, 200)]),
        new("save-scene", "Create or replace scene", [Text("name", "Scene name"),
            Text("commands", "Device / command names, separated by semicolons"),
            new("delay-ms", "Delay after each command (ms)", PluginSettingKind.Number, new(Number: 100), 0, 5000)]),
        new("delete-scene", "Delete scene", [Text("scene", "Scene name")]),
        new("send", "Send command", [Text("command", "Command (selected or Device / Name)", "selected")]),
        new("set-carrier", "Override carrier frequency", [Text("command", "Command (selected or Device / Name)", "selected"),
            new("carrier-hz", "Carrier frequency (Hz)", PluginSettingKind.Number, new(Number: 38000), 20000, 60000)]),
        new("reset-carrier", "Use captured carrier metadata", [Text("command", "Command (selected or Device / Name)", "selected")]),
        new("scene", "Run scene", [Text("scene", "Scene name")]),
        new("delete", "Delete command", [Text("command", "Command (selected or Device / Name)", "selected")]),
        new("export", "Back up command library", []),
        new("import", "Restore command library backup", []),
        new("remote-refresh", "Read built-in remotes", []),
        new("remote-press", "Press a built-in remote button",
            [Text("remote", "Remote id"), Text("button", "Button id")]),
        new("remote-run", "Run a built-in remote sequence",
            [Text("remote", "Remote id"), Text("sequence", "Sequence id"),
             new("wait", "Wait for the sequence to finish", PluginSettingKind.Boolean, new(Boolean: true))]),
        new("remote-climate", "Set a built-in air conditioner",
            [Text("remote", "Remote id"),
             new("power", "On", PluginSettingKind.Boolean, new(Boolean: true)),
             Text("mode", "Mode", "cool"),
             new("degrees", "Temperature", PluginSettingKind.Number, new(Number: 24), 10, 90),
             Text("fan", "Fan", "auto"),
             new("toggle-swing", "Toggle swing", PluginSettingKind.Boolean, new(Boolean: false))]),
    ];

    /// <inheritdoc />
    public IReadOnlyList<PluginWidget> Widgets { get; } =
    [
        new("selected-command", "IR remote", ["selected", "send"], Icon: "action", NavigationCategory: "commands"),
    ];

    /// <inheritdoc />
    public IReadOnlyList<PluginUiContribution> Contributions { get; } =
    [
        new("status", "IR endpoint", "infrared", PluginUiKind.Status, StateKey: "status"),
        new("library", "Command library", "infrared", PluginUiKind.Status, StateKey: "library"),
        new("selected", "Selected command", "commands", PluginUiKind.Status, StateKey: "selected"),
        new("select", "Select command", "commands", PluginUiKind.Action, ActionId: "select"),
        new("rename", "Rename selected command", "commands", PluginUiKind.Action, ActionId: "rename"),
        new("relearn", "Relearn selected command", "commands", PluginUiKind.Action, ActionId: "relearn"),
        new("timing", "Edit repeat timing", "commands", PluginUiKind.Action, ActionId: "timing"),
        new("delete", "Delete command", "commands", PluginUiKind.Action, ActionId: "delete"),
        new("save-scene", "Create or replace scene", "scenes", PluginUiKind.Action, ActionId: "save-scene"),
        new("scene", "Run scene", "scenes", PluginUiKind.Action, ActionId: "scene"),
        new("delete-scene", "Delete scene", "scenes", PluginUiKind.Action, ActionId: "delete-scene"),
        new("import", "Restore command library backup", "library", PluginUiKind.Action, ActionId: "import"),
        new("ports", "USB ports", "infrared", PluginUiKind.Status, StateKey: "ports"),
        new("discover", "Find USB endpoints", "infrared", PluginUiKind.Action, ActionId: "discover"),
        new("connect", "Connect IR endpoint", "infrared", PluginUiKind.Action, ActionId: "connect"),
        new("network", "Wi-Fi pairing", "network", PluginUiKind.Status, StateKey: "network"),
        new("wifi-setup", "Pair Wi-Fi over USB", "network", PluginUiKind.Action, ActionId: "wifi-setup"),
        new("wifi-clear", "Forget Wi-Fi over USB", "network", PluginUiKind.Action, ActionId: "wifi-clear"),
        new("learn", "Learn command", "infrared", PluginUiKind.Action, ActionId: "learn"),
        new("send", "Test selected command", "infrared", PluginUiKind.Action, ActionId: "send"),
        new("carrier-source", "Carrier provenance", "infrared", PluginUiKind.Status, StateKey: "carrier-source"),
        new("carrier", "Carrier frequency override (Hz)", "infrared", PluginUiKind.Slider,
            StateKey: "carrier-hz", ActionId: "set-carrier", ArgumentKey: "carrier-hz"),
        new("reset-carrier", "Use captured carrier metadata", "infrared", PluginUiKind.Action, ActionId: "reset-carrier"),
        new("export", "Back up command library", "infrared", PluginUiKind.Action, ActionId: "export"),
        new("remotes", "Built-in remotes", "remotes", PluginUiKind.Status, StateKey: "remotes"),
        new("remote-refresh", "Read built-in remotes", "remotes", PluginUiKind.Action, ActionId: "remote-refresh"),
        new("remote-press", "Press a built-in remote button", "remotes", PluginUiKind.Action, ActionId: "remote-press"),
        new("remote-run", "Run a built-in remote sequence", "remotes", PluginUiKind.Action, ActionId: "remote-run"),
        new("remote-climate", "Set a built-in air conditioner", "remotes", PluginUiKind.Action, ActionId: "remote-climate"),
    ];

    /// <inheritdoc />
    public async ValueTask<PluginHealth> StartAsync(IPluginHost host, PluginContext context, CancellationToken cancellationToken)
    {
        _host = host;
        _context = context;
        _library = await IrLibrary.LoadAsync(LibraryPath(context), cancellationToken).ConfigureAwait(false);
        _pairing = await IrPairing.LoadAsync(PairingPath(context), cancellationToken).ConfigureAwait(false);
        _lastCommand = _library.SelectedCommandId ?? _library.Commands.LastOrDefault()?.Id ?? "";
        _stopped = false;
        Publish("status", "Choose the endpoint connection in plugin preferences. Learn and send identify it on demand.");
        PublishNetwork();
        PublishLibrary();
        PublishRemotes();
        return PluginHealth.Ready;
    }

    /// <inheritdoc />
    public ValueTask SessionChangedAsync(PluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _context = context;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask SuspendAsync(PluginContext context, CancellationToken cancellationToken) =>
        _ = await StopAsync(context, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public ValueTask ResumeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _context = context;
        _stopped = false;
        Publish("status", "Resumed; the next learn or send identifies the endpoint again.");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask<PluginConfigurationResult> ConfigureAsync(PluginConfiguration configuration,
        PluginContext context, CancellationToken cancellationToken)
    {
        string port = Value(configuration, "port") ?? "";
        string transport = Value(configuration, "transport") ?? UsbTransport;
        string hostName = (Value(configuration, "host") ?? "").Trim();
        if (port.Length != 0 && (!port.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(port.AsSpan(3), out int number) || number <= 0))
        {
            return new(configuration.Revision, PluginConfigurationOutcome.Rejected, "Select a COM port.");
        }
        if (transport is not (UsbTransport or WifiTransport))
        {
            return new(configuration.Revision, PluginConfigurationOutcome.Rejected, "Endpoint connection must be usb or wifi.");
        }
        if (hostName.Length > 253 || hostName.Any(char.IsWhiteSpace))
        {
            return new(configuration.Revision, PluginConfigurationOutcome.Rejected, "Enter a host name or IP address without spaces.");
        }
        await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_port != port || _transport != transport || _hostName != hostName)
            {
                if (_endpoint is not null) { await _endpoint.DisposeAsync().ConfigureAwait(false); _endpoint = null; }
                _port = port;
                _transport = transport;
                _hostName = hostName;
            }
            return new(configuration.Revision, PluginConfigurationOutcome.Applied);
        }
        finally { _lane.Release(); }
    }

    /// <inheritdoc />
    public async ValueTask<PluginActionResult> ExecuteActionAsync(PluginActionRequest request, PluginContext context,
        CancellationToken cancellationToken)
    {
        await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopped || _context?.Generation != context.Generation)
            {
                return new(request.OperationId, PluginActionOutcome.Rejected, "Plugin generation changed.");
            }
            string Arg(string key) => request.Arguments[key].Text ?? "";
            Publish("status", $"{request.ActionId} requested", request.OperationId);
            switch (request.ActionId)
            {
                case "discover":
                    string[] ports = SerialPort.GetPortNames().Order(StringComparer.Ordinal).ToArray();
                    Publish("ports", ports.Length == 0 ? "No USB serial ports found." : string.Join(", ", ports), request.OperationId);
                    break;
                case "connect":
                    if (_endpoint is not null) { await _endpoint.DisposeAsync().ConfigureAwait(false); _endpoint = null; }
                    await EndpointAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
                    return new(request.OperationId, PluginActionOutcome.AppliedVerified, "Endpoint identity verified.");
                case "wifi-setup":
                    if (Arg("ssid").Trim().Length == 0)
                    {
                        return new(request.OperationId, PluginActionOutcome.Rejected, "Enter the network name. Use Forget Wi-Fi to clear it.");
                    }
                    return await PairAsync(Arg("ssid"), Arg("password"), request.OperationId, context, cancellationToken).ConfigureAwait(false);
                case "wifi-clear":
                    return await PairAsync("", "", request.OperationId, context, cancellationToken).ConfigureAwait(false);
                case "learn":
                    if (_library.Commands.Any(item => item.Device == Arg("device") && item.Name == Arg("name")))
                    {
                        return new(request.OperationId, PluginActionOutcome.Rejected, "This command already exists. Select it and use Relearn.");
                    }
                    IrPayload payload = await LearnAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
                    string id = Guid.NewGuid().ToString("N");
                    IrLibrary learned = _library with
                    {
                        Commands = [.. _library.Commands, new(id, Arg("device"), Arg("name"), payload)],
                        SelectedCommandId = id
                    };
                    await learned.SaveAsync(LibraryPath(context), cancellationToken).ConfigureAwait(false);
                    _library = learned;
                    _lastCommand = id;
                    break;
                case "select":
                    IrCommand selected = ResolveCommand(Arg("command"));
                    await SaveLibraryAsync(_library with { SelectedCommandId = selected.Id }, context, cancellationToken).ConfigureAwait(false);
                    _lastCommand = selected.Id;
                    break;
                case "rename":
                    IrCommand renamed = ResolveCommand("selected") with { Device = Arg("device"), Name = Arg("name") };
                    if (_library.Commands.Any(item => item.Id != renamed.Id && item.Device == renamed.Device && item.Name == renamed.Name))
                    {
                        return new(request.OperationId, PluginActionOutcome.Rejected, "This device already has a command with that name.");
                    }
                    await ReplaceCommandAsync(renamed, context, cancellationToken).ConfigureAwait(false);
                    break;
                case "relearn":
                    IrCommand previous = ResolveCommand("selected");
                    IrPayload capture = await LearnAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
                    await ReplaceCommandAsync(previous with { Payload = capture }, context, cancellationToken).ConfigureAwait(false);
                    break;
                case "timing":
                    await ReplaceCommandAsync(ResolveCommand("selected") with
                    {
                        Repeats = IntegerArgument(request, "repeats", 0, 4),
                        GapMs = IntegerArgument(request, "gap-ms", 0, 200)
                    }, context, cancellationToken).ConfigureAwait(false);
                    break;
                case "save-scene":
                    string name = Arg("name");
                    string[] names = Arg("commands").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    int delay = IntegerArgument(request, "delay-ms", 0, 5000);
                    IrScene? existing = _library.Scenes.FirstOrDefault(item => item.Name == name);
                    IrScene created = new(existing?.Id ?? Guid.NewGuid().ToString("N"), name,
                        names.Select(item => new IrSceneStep(ResolveCommand(item).Id, delay)).ToArray());
                    await SaveLibraryAsync(_library with
                    {
                        Scenes = [.. _library.Scenes.Where(item => item.Id != created.Id), created]
                    }, context, cancellationToken).ConfigureAwait(false);
                    break;
                case "delete-scene":
                    IrScene removed = ResolveScene(Arg("scene"));
                    await SaveLibraryAsync(_library with { Scenes = _library.Scenes.Where(item => item.Id != removed.Id).ToArray() },
                        context, cancellationToken).ConfigureAwait(false);
                    break;
                case "set-carrier":
                case "reset-carrier":
                    IrCommand original = ResolveCommand(Arg("command"));
                    string commandId = original.Id;
                    int? frequency = null;
                    if (request.ActionId == "set-carrier")
                    {
                        double value = request.Arguments["carrier-hz"].Number ?? double.NaN;
                        if (!double.IsFinite(value) || value != Math.Truncate(value) || value is < 20000 or > 60000)
                        {
                            return new(request.OperationId, PluginActionOutcome.Rejected, "Carrier must be an integer from 20000 to 60000 Hz.");
                        }
                        frequency = (int)value;
                    }
                    IrLibrary edited = _library with
                    {
                        Commands = _library.Commands.Select(item => item.Id == commandId
                            ? original with { CarrierOverrideHz = frequency } : item).ToArray()
                    };
                    await edited.SaveAsync(LibraryPath(context), cancellationToken).ConfigureAwait(false);
                    _library = edited;
                    break;
                case "send":
                    await SendAsync(Arg("command"), request.OperationId, cancellationToken).ConfigureAwait(false);
                    Publish("status", "IR emitted; appliance state is not verified.", request.OperationId);
                    return new(request.OperationId, PluginActionOutcome.Dispatched, "IR emitted; appliance state is not verified.");
                case "scene":
                    IrScene scene = ResolveScene(Arg("scene"));
                    foreach (IrSceneStep step in scene.Steps)
                    {
                        await SendAsync(step.CommandId, request.OperationId, cancellationToken).ConfigureAwait(false);
                        await Task.Delay(step.DelayAfterMs, cancellationToken).ConfigureAwait(false);
                    }
                    return new(request.OperationId, PluginActionOutcome.Dispatched, "Scene emitted; appliance state is not verified.");
                case "delete":
                    string removedId = ResolveCommand(Arg("command")).Id;
                    if (_library.Scenes.Any(item => item.Steps.Any(step => step.CommandId == removedId)))
                    {
                        return new(request.OperationId, PluginActionOutcome.Rejected,
                            "This command is used by a scene. Edit or delete that scene first.");
                    }
                    IrLibrary deleted = _library with
                    {
                        Commands = _library.Commands.Where(item => item.Id != removedId).ToArray(),
                        SelectedCommandId = _library.SelectedCommandId == removedId ? null : _library.SelectedCommandId
                    };
                    await deleted.SaveAsync(LibraryPath(context), cancellationToken).ConfigureAwait(false);
                    _library = deleted;
                    if (_lastCommand == removedId) { _lastCommand = ""; }
                    break;
                case "export":
                    await _library.SaveAsync(Path.Combine(context.StateDirectory, "library.backup.json"), cancellationToken).ConfigureAwait(false);
                    break;
                case "import":
                    string backup = Path.Combine(context.StateDirectory, "library.backup.json");
                    if (!File.Exists(backup)) { throw new FileNotFoundException("No command library backup exists."); }
                    IrLibrary restored = await IrLibrary.LoadAsync(backup, cancellationToken).ConfigureAwait(false);
                    await restored.SaveAsync(LibraryPath(context), cancellationToken).ConfigureAwait(false);
                    _library = restored;
                    _lastCommand = restored.SelectedCommandId ?? restored.Commands.LastOrDefault()?.Id ?? "";
                    break;
                case "remote-refresh":
                    await RefreshRemotesAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
                    break;
                case "remote-press":
                case "remote-run":
                case "remote-climate":
                    return await RemoteActionAsync(request, cancellationToken).ConfigureAwait(false);
                default:
                    return new(request.OperationId, PluginActionOutcome.Rejected, "Unknown IR action.");
            }
            PublishLibrary();
            Publish("status", $"{request.ActionId} completed", request.OperationId);
            return new(request.OperationId, PluginActionOutcome.AppliedVerified);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Publish("status", $"{request.ActionId} failed: {ex.Message}", request.OperationId);
            return new(request.OperationId, PluginActionOutcome.Unconfirmed, ex.Message);
        }
        finally { _lane.Release(); }
    }

    /// <summary>
    /// Returns an endpoint with a verified identity, identifying it first when no verified connection
    /// exists. Identification only reads; this lets route automation send after a restart or a dropped
    /// link without a manual Connect, while emission still needs an explicit send or scene.
    /// </summary>
    private async Task<IIrEndpoint> EndpointAsync(Guid operation, CancellationToken token)
    {
        _endpoint ??= _createEndpoint(Target());
        if (_endpoint.Identity is null)
        {
            IrEndpointIdentity identity = await _endpoint.IdentifyAsync(token).ConfigureAwait(false);
            Publish("status", Describe(identity), operation);
        }
        return _endpoint;
    }

    /// <summary>Reads the endpoint's built-in remotes and publishes them as the ids an action takes.
    /// Reading is a read: nothing is emitted.</summary>
    private async Task RefreshRemotesAsync(Guid operation, CancellationToken token)
    {
        IIrEndpoint endpoint = await EndpointAsync(operation, token).ConfigureAwait(false);
        _remotes = await endpoint.ListRemotesAsync(token).ConfigureAwait(false);
        PublishRemotes();
    }

    /// <summary>
    /// Runs one built-in remote action. The endpoint owns the remotes, so its own catalog decides
    /// which ids exist: an unknown one is looked up once more against a fresh read before it is
    /// refused, because the firmware may have been reflashed since the last read.
    /// </summary>
    private async Task<PluginActionResult> RemoteActionAsync(PluginActionRequest request, CancellationToken token)
    {
        string Arg(string key) => request.Arguments[key].Text ?? "";
        IIrEndpoint endpoint = await EndpointAsync(request.OperationId, token).ConfigureAwait(false);
        if (endpoint.Identity is { Remotes: 0 })
        {
            return new(request.OperationId, PluginActionOutcome.Rejected,
                "This endpoint firmware carries no built-in remotes; flash firmware 0.4.0 or later.");
        }

        string remoteId = Arg("remote");
        IrRemote? remote = Find(remoteId);
        if (remote is null)
        {
            await RefreshRemotesAsync(request.OperationId, token).ConfigureAwait(false);
            remote = Find(remoteId);
        }
        if (remote is null)
        {
            return new(request.OperationId, PluginActionOutcome.Rejected,
                $"The endpoint has no remote \"{remoteId}\". Read its built-in remotes to see the ids.");
        }

        switch (request.ActionId)
        {
            case "remote-press":
                string button = Arg("button");
                if (!remote.Buttons.Any(item => item.Id == button))
                {
                    return new(request.OperationId, PluginActionOutcome.Rejected,
                        $"\"{remote.Name}\" has no button \"{button}\".");
                }
                await endpoint.PressAsync(remote.Id, button, token).ConfigureAwait(false);
                break;
            case "remote-run":
                string sequence = Arg("sequence");
                if (!remote.Sequences.Any(item => item.Id == sequence))
                {
                    return new(request.OperationId, PluginActionOutcome.Rejected,
                        $"\"{remote.Name}\" has no sequence \"{sequence}\".");
                }
                await endpoint.RunSequenceAsync(remote.Id, sequence, token).ConfigureAwait(false);
                if (request.Arguments["wait"].Boolean == true)
                {
                    await WaitForSequenceAsync(endpoint, token).ConfigureAwait(false);
                }
                break;
            default:
                if (remote.Climate is null)
                {
                    return new(request.OperationId, PluginActionOutcome.Rejected,
                        $"\"{remote.Name}\" is not an air conditioner.");
                }
                double degrees = request.Arguments["degrees"].Number ?? double.NaN;
                IrClimateRequest climate = new(
                    request.Arguments["power"].Boolean ?? true, Arg("mode"), degrees, Arg("fan"),
                    request.Arguments["toggle-swing"].Boolean ?? false);
                if (!remote.Climate.Modes.Contains(climate.Mode) || !remote.Climate.Fans.Contains(climate.Fan)
                    || !double.IsFinite(degrees) || degrees < remote.Climate.MinDegrees || degrees > remote.Climate.MaxDegrees
                    || (climate.ToggleSwing && remote.Climate.Swing != "toggle"))
                {
                    return new(request.OperationId, PluginActionOutcome.Rejected,
                        $"\"{remote.Name}\" accepts modes {string.Join("/", remote.Climate.Modes)}, "
                        + $"fans {string.Join("/", remote.Climate.Fans)} and "
                        + $"{remote.Climate.MinDegrees:0}-{remote.Climate.MaxDegrees:0} degrees"
                        + (remote.Climate.Swing == "toggle" ? "." : ", and has no swing."));
                }
                await endpoint.ClimateAsync(remote.Id, climate, token).ConfigureAwait(false);
                break;
        }

        Publish("status", "IR emitted; appliance state is not verified.", request.OperationId);
        return new(request.OperationId, PluginActionOutcome.Dispatched, "IR emitted; appliance state is not verified.");

        IrRemote? Find(string id) => _remotes?.Remotes.FirstOrDefault(item => item.Id == id || item.Name == id);
    }

    /// <summary>Waits for a started sequence to finish by polling the endpoint's own flag. A
    /// cancelled wait sends the endpoint's cancel, which is a distinct operation and never a retry
    /// of the sequence.</summary>
    private static async Task WaitForSequenceAsync(IIrEndpoint endpoint, CancellationToken token)
    {
        try
        {
            for (int poll = 0; poll < SequencePollLimit; poll++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
                IrEndpointIdentity identity = await endpoint.IdentifyAsync(token).ConfigureAwait(false);
                if (!identity.SequenceRunning) { return; }
            }
        }
        catch (OperationCanceledException)
        {
            await endpoint.CancelAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private void PublishRemotes() => Publish("remotes", _remotes is null || _remotes.Remotes.Length == 0
        ? "No built-in remotes were read from the endpoint."
        : string.Join("\n", _remotes.Remotes.Select(remote =>
            $"{remote.Id} ({remote.Name}): "
            + string.Join(", ", remote.Buttons.Take(24).Select(button => button.Id))
            + (remote.Sequences.Length == 0 ? "" : "; sequences " + string.Join(", ", remote.Sequences.Select(item => item.Id)))
            + (remote.Climate is null ? "" : "; climate"))));

    private IrEndpointTarget Target()
    {
        if (_transport == WifiTransport)
        {
            if (_pairing is null) { throw new InvalidOperationException("Pair the endpoint over USB first, or choose the USB connection."); }
            string address = _hostName.Length != 0 ? _hostName : _pairing.Hostname;
            return new(true, address, _pairing.Token);
        }
        if (string.IsNullOrEmpty(_port)) { throw new InvalidOperationException("Select a USB serial port in plugin preferences first."); }
        return new(false, _port, null);
    }

    private async Task<IrPayload> LearnAsync(Guid operation, CancellationToken token)
    {
        IIrEndpoint endpoint = await EndpointAsync(operation, token).ConfigureAwait(false);
        Publish("status", "Learning: point the remote at the receiver and press one button briefly.", operation);
        return await endpoint.LearnAsync(TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
    }

    /// <summary>
    /// Pairs over USB regardless of the configured transport: the cable proves possession, the plugin
    /// mints the pairing token, and the endpoint stores credentials and token itself. An empty SSID
    /// clears both sides. Credentials and the token are never published as state.
    /// </summary>
    private async Task<PluginActionResult> PairAsync(string ssid, string password, Guid operation, PluginContext context,
        CancellationToken token)
    {
        if (string.IsNullOrEmpty(_port)) { throw new InvalidOperationException("Select the USB serial port the endpoint is attached to first."); }
        if (_endpoint is not null) { await _endpoint.DisposeAsync().ConfigureAwait(false); _endpoint = null; }
        await using IIrEndpoint usb = _createEndpoint(new(false, _port, null));
        await usb.IdentifyAsync(token).ConfigureAwait(false);
        if (ssid.Length == 0)
        {
            await usb.ConfigureNetworkAsync("", "", "", token).ConfigureAwait(false);
            _pairing = null;
            File.Delete(PairingPath(context));
            PublishNetwork();
            return new(operation, PluginActionOutcome.AppliedVerified, "The endpoint forgot its Wi-Fi network and pairing token.");
        }
        string pairingToken = _pairing?.Token ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        IrEndpointIdentity identity = await usb.ConfigureNetworkAsync(ssid, password, pairingToken, token).ConfigureAwait(false);
        string hostname = string.IsNullOrWhiteSpace(identity.Hostname) ? "" : identity.Hostname + ".local";
        IrPairing pairing = new(pairingToken, hostname, identity.Ip ?? "");
        await pairing.SaveAsync(PairingPath(context), token).ConfigureAwait(false);
        _pairing = pairing;
        Publish("network", "Paired; waiting for the endpoint to join the network.", operation);
        // The endpoint joins in the background. Poll its identity for a bounded time; none of this emits IR.
        for (int attempt = 0; attempt < 15 && !identity.WifiConnected; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            identity = await usb.IdentifyAsync(token).ConfigureAwait(false);
        }
        if (identity.WifiConnected && !string.IsNullOrEmpty(identity.Ip))
        {
            _pairing = pairing with { Ip = identity.Ip };
            await _pairing.SaveAsync(PairingPath(context), token).ConfigureAwait(false);
            PublishNetwork();
            return new(operation, PluginActionOutcome.AppliedVerified, $"Endpoint joined the network as {_pairing.Hostname} ({identity.Ip}).");
        }
        PublishNetwork();
        return new(operation, PluginActionOutcome.Unconfirmed,
            "The endpoint stored the network and token but has not joined yet. Check the SSID and password, then Connect.");
    }

    private static string? Value(PluginConfiguration configuration, string key) =>
        configuration.Values.TryGetValue(key, out PluginValue value) ? value.Text : null;

    private static string Describe(IrEndpointIdentity identity) =>
        $"{identity.Model} {identity.Identity}, firmware {identity.Firmware}, protocol {identity.Protocol}"
        + (identity.WifiConnected ? $", Wi-Fi {identity.Ip}" : identity.WifiConfigured ? ", Wi-Fi configured but not connected" : "");

    private void PublishNetwork() => Publish("network", _pairing is null
        ? "Not paired. Pair over USB to enable the Wi-Fi connection."
        : $"Paired with {_pairing.Hostname}" + (_pairing.Ip.Length != 0 ? $" (last address {_pairing.Ip})" : ""));

    private static int IntegerArgument(PluginActionRequest request, string key, int minimum, int maximum)
    {
        double value = request.Arguments[key].Number ?? double.NaN;
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value < minimum || value > maximum)
        {
            throw new ArgumentException($"{key} must be an integer from {minimum} to {maximum}.");
        }
        return (int)value;
    }
    private IrCommand ResolveCommand(string name)
    {
        if (name is "selected" or "last") { name = _lastCommand; }
        IrCommand[] matches = _library.Commands.Where(item => item.Id == name || $"{item.Device} / {item.Name}" == name).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidOperationException("Select an existing, unambiguous Device / command name.");
    }
    private IrScene ResolveScene(string name) => _library.Scenes.FirstOrDefault(item => item.Id == name || item.Name == name)
        ?? throw new InvalidOperationException("Choose an existing scene name.");
    private async Task SaveLibraryAsync(IrLibrary library, PluginContext context, CancellationToken token)
    {
        await library.SaveAsync(LibraryPath(context), token).ConfigureAwait(false);
        _library = library;
    }
    private Task ReplaceCommandAsync(IrCommand command, PluginContext context, CancellationToken token) =>
        SaveLibraryAsync(_library with { Commands = _library.Commands.Select(item => item.Id == command.Id ? command : item).ToArray() }, context, token);
    private async Task SendAsync(string id, Guid operation, CancellationToken token)
    {
        IrCommand command = ResolveCommand(id);
        IIrEndpoint endpoint = await EndpointAsync(operation, token).ConfigureAwait(false);
        await endpoint.TransmitAsync(command.TransmitPayload, command.Repeats, command.GapMs, token).ConfigureAwait(false);
    }
    private static string LibraryPath(PluginContext context) => Path.Combine(context.StateDirectory, "library.json");
    private static string PairingPath(PluginContext context) => Path.Combine(context.StateDirectory, "endpoint.json");
    private void PublishLibrary()
    {
        Publish("library", $"{_library.Commands.Length} commands, {_library.Scenes.Length} scenes\n"
            + string.Join("\n", _library.Commands.Take(30).Select(item => $"{item.Device} / {item.Name}"))
            + "\nScenes: " + string.Join(", ", _library.Scenes.Take(30).Select(item => item.Name)));
        IrCommand? selected = _library.Commands.FirstOrDefault(item => item.Id == _lastCommand);
        Publish("selected", selected is null ? "No command selected" : $"{selected.Device} / {selected.Name}; repeats {selected.Repeats}, gap {selected.GapMs} ms");
        if (selected is null)
        {
            Publish("carrier-source", "Learn or select a command first.");
            return;
        }
        Publish("carrier-source", $"{selected.TransmitPayload.CarrierHz} Hz ({selected.TransmitPayload.CarrierSource}); captured value: {selected.Payload.CarrierHz} Hz ({selected.Payload.CarrierSource})");
        if (_host is { } host && _context is { } context)
        {
            host.PublishState(new(context.Instance, context.Generation, Interlocked.Increment(ref _sequence),
                "carrier-hz", new(Number: selected.TransmitPayload.CarrierHz), PluginStateOrigin.Initialization));
        }
    }
    private void Publish(string key, string text, Guid? operation = null)
    {
        if (_host is { } host && _context is { } context)
        {
            host.PublishState(new(context.Instance, context.Generation, Interlocked.Increment(ref _sequence), key,
                new(Text: text.Length <= 4096 ? text : text[..4096]),
                operation.HasValue ? PluginStateOrigin.Action : PluginStateOrigin.Initialization, OperationId: operation));
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> StopAsync(PluginContext context, CancellationToken cancellationToken)
    {
        await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _stopped = true;
            if (_endpoint is not null) { await _endpoint.DisposeAsync().ConfigureAwait(false); _endpoint = null; }
            return true;
        }
        finally { _lane.Release(); }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_context is { } context) { await StopAsync(context, CancellationToken.None).ConfigureAwait(false); }
        _host = null;
    }
}
