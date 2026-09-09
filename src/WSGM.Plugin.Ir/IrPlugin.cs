using System.IO.Ports;
using WSGM.Plugin.Sdk;

namespace WSGM.Plugin.Ir;

/// <summary>Independent IR command library and versioned endpoint integration.</summary>
public sealed class IrPlugin : IPlugin, IConfigurablePlugin, IPluginActions, IPluginUi
{
    private readonly Func<string, IIrEndpoint> _createEndpoint;

    /// <summary>Creates an inactive plugin. Resources are acquired only by explicit connection.</summary>
    public IrPlugin() : this(port => new SerialIrEndpoint(port)) { }

    internal IrPlugin(Func<string, IIrEndpoint> createEndpoint) => _createEndpoint = createEndpoint;
    private readonly SemaphoreSlim _lane = new(1, 1);
    private IPluginHost? _host;
    private PluginContext? _context;
    private IIrEndpoint? _endpoint;
    private IrLibrary _library = IrLibrary.Empty;
    private string _port = "";
    private string _lastCommand = "";
    private long _sequence;
    private bool _stopped = true;

    /// <inheritdoc />
    public string Id => "wsgm.ir";

    /// <inheritdoc />
    public IReadOnlyList<PluginSetting> Settings { get; } =
    [new("port", "USB serial port", PluginSettingKind.Text, new(Text: ""))];

    private static PluginSetting Text(string key, string label, string value = "") =>
        new(key, label, PluginSettingKind.Text, new(Text: value));

    /// <inheritdoc />
    public IReadOnlyList<PluginAction> Actions { get; } =
    [
        new("discover", "Find USB endpoints", []),
        new("connect", "Connect IR endpoint", []),
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
        new("learn", "Learn command", "infrared", PluginUiKind.Action, ActionId: "learn"),
        new("send", "Test selected command", "infrared", PluginUiKind.Action, ActionId: "send"),
        new("carrier-source", "Carrier provenance", "infrared", PluginUiKind.Status, StateKey: "carrier-source"),
        new("carrier", "Carrier frequency override (Hz)", "infrared", PluginUiKind.Slider,
            StateKey: "carrier-hz", ActionId: "set-carrier", ArgumentKey: "carrier-hz"),
        new("reset-carrier", "Use captured carrier metadata", "infrared", PluginUiKind.Action, ActionId: "reset-carrier"),
        new("export", "Back up command library", "infrared", PluginUiKind.Action, ActionId: "export"),
    ];

    /// <inheritdoc />
    public async ValueTask<PluginHealth> StartAsync(IPluginHost host, PluginContext context, CancellationToken cancellationToken)
    {
        _host = host;
        _context = context;
        _library = await IrLibrary.LoadAsync(LibraryPath(context), cancellationToken).ConfigureAwait(false);
        _lastCommand = _library.SelectedCommandId ?? _library.Commands.LastOrDefault()?.Id ?? "";
        _stopped = false;
        Publish("status", "Choose a USB port in plugin preferences, then connect.");
        PublishLibrary();
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
        Publish("status", "Resumed; reconnect the IR endpoint before sending.");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask<PluginConfigurationResult> ConfigureAsync(PluginConfiguration configuration,
        PluginContext context, CancellationToken cancellationToken)
    {
        string port = configuration.Values["port"].Text ?? "";
        if (port.Length != 0 && (!port.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(port.AsSpan(3), out int number) || number <= 0))
        {
            return new(configuration.Revision, PluginConfigurationOutcome.Rejected, "Select a COM port.");
        }
        await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_port != port)
            {
                if (_endpoint is not null) { await _endpoint.DisposeAsync().ConfigureAwait(false); _endpoint = null; }
                _port = port;
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
                    Publish("ports", string.Join(", ", SerialPort.GetPortNames().Order(StringComparer.Ordinal)), request.OperationId);
                    break;
                case "connect":
                    if (string.IsNullOrEmpty(_port)) { throw new InvalidOperationException("Select a USB serial port first."); }
                    if (_endpoint is not null) { await _endpoint.DisposeAsync().ConfigureAwait(false); }
                    _endpoint = _createEndpoint(_port);
                    IrEndpointIdentity identity = await _endpoint.IdentifyAsync(cancellationToken).ConfigureAwait(false);
                    Publish("status", $"{identity.Model} {identity.Identity}, firmware {identity.Firmware}, protocol {identity.Protocol}", request.OperationId);
                    return new(request.OperationId, PluginActionOutcome.AppliedVerified, "Endpoint identity verified.");
                case "learn":
                    if (_library.Commands.Any(item => item.Device == Arg("device") && item.Name == Arg("name")))
                    {
                        return new(request.OperationId, PluginActionOutcome.Rejected, "This command already exists. Select it and use Relearn.");
                    }
                    IrPayload payload = await Endpoint().LearnAsync(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
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
                    IrPayload capture = await Endpoint().LearnAsync(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
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
                    await SendAsync(Arg("command"), cancellationToken).ConfigureAwait(false);
                    Publish("status", "IR emitted; appliance state is not verified.", request.OperationId);
                    return new(request.OperationId, PluginActionOutcome.Dispatched, "IR emitted; appliance state is not verified.");
                case "scene":
                    IrScene scene = ResolveScene(Arg("scene"));
                    foreach (IrSceneStep step in scene.Steps)
                    {
                        await SendAsync(step.CommandId, cancellationToken).ConfigureAwait(false);
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

    private IIrEndpoint Endpoint() => _endpoint ?? throw new InvalidOperationException("Connect the IR endpoint first.");
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
    private async Task SendAsync(string id, CancellationToken token)
    {
        IrCommand command = ResolveCommand(id);
        await Endpoint().TransmitAsync(command.TransmitPayload, command.Repeats, command.GapMs, token).ConfigureAwait(false);
    }
    private static string LibraryPath(PluginContext context) => Path.Combine(context.StateDirectory, "library.json");
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
