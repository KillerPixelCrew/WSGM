using System.IO.Ports;
using WSGM.Plugin.Sdk;

namespace WSGM.Plugin.Ir;

/// <summary>Independent IR command library and versioned endpoint integration.</summary>
public sealed class IrPlugin : IPlugin, IConfigurablePlugin, IPluginActions, IPluginUi
{
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
        new("send", "Send command", [Text("command", "Command ID", "last")]),
        new("set-carrier", "Override carrier frequency", [Text("command", "Command ID", "last"),
            new("carrier-hz", "Carrier frequency (Hz)", PluginSettingKind.Number, new(Number: 38000), 20000, 60000)]),
        new("reset-carrier", "Use captured carrier metadata", [Text("command", "Command ID", "last")]),
        new("scene", "Run scene", [Text("scene", "Scene ID")]),
        new("delete", "Delete command", [Text("command", "Command ID")]),
        new("export", "Back up command library", []),
        new("import", "Restore command library backup", []),
    ];

    /// <inheritdoc />
    public IReadOnlyList<PluginUiContribution> Contributions { get; } =
    [
        new("status", "IR endpoint", "infrared", PluginUiKind.Status, StateKey: "status"),
        new("library", "Command library", "infrared", PluginUiKind.Status, StateKey: "library"),
        new("ports", "USB ports", "infrared", PluginUiKind.Status, StateKey: "ports"),
        new("discover", "Find USB endpoints", "infrared", PluginUiKind.Action, ActionId: "discover"),
        new("connect", "Connect IR endpoint", "infrared", PluginUiKind.Action, ActionId: "connect"),
        new("learn", "Learn command", "infrared", PluginUiKind.Action, ActionId: "learn"),
        new("send", "Test last learned command", "infrared", PluginUiKind.Action, ActionId: "send"),
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
                    _endpoint = new SerialIrEndpoint(_port);
                    IrEndpointIdentity identity = await _endpoint.IdentifyAsync(cancellationToken).ConfigureAwait(false);
                    Publish("status", $"{identity.Model} {identity.Identity}, firmware {identity.Firmware}, protocol {identity.Protocol}", request.OperationId);
                    return new(request.OperationId, PluginActionOutcome.AppliedVerified, "Endpoint identity verified.");
                case "learn":
                    IrPayload payload = await Endpoint().LearnAsync(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
                    string id = Guid.NewGuid().ToString("N");
                    IrLibrary learned = _library with { Commands = [.. _library.Commands, new(id, Arg("device"), Arg("name"), payload)] };
                    await learned.SaveAsync(LibraryPath(context), cancellationToken).ConfigureAwait(false);
                    _library = learned;
                    _lastCommand = id;
                    break;
                case "set-carrier":
                case "reset-carrier":
                    string commandId = Arg("command") == "last" ? _lastCommand : Arg("command");
                    IrCommand original = _library.Commands.Single(item => item.Id == commandId);
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
                    await SendAsync(Arg("command") == "last" ? _lastCommand : Arg("command"), cancellationToken).ConfigureAwait(false);
                    Publish("status", "IR emitted; appliance state is not verified.", request.OperationId);
                    return new(request.OperationId, PluginActionOutcome.Dispatched, "IR emitted; appliance state is not verified.");
                case "scene":
                    IrScene scene = _library.Scenes.Single(item => item.Id == Arg("scene"));
                    foreach (IrSceneStep step in scene.Steps)
                    {
                        await SendAsync(step.CommandId, cancellationToken).ConfigureAwait(false);
                        await Task.Delay(step.DelayAfterMs, cancellationToken).ConfigureAwait(false);
                    }
                    return new(request.OperationId, PluginActionOutcome.Dispatched, "Scene emitted; appliance state is not verified.");
                case "delete":
                    IrLibrary deleted = _library with { Commands = _library.Commands.Where(item => item.Id != Arg("command")).ToArray() };
                    await deleted.SaveAsync(LibraryPath(context), cancellationToken).ConfigureAwait(false);
                    _library = deleted;
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
    private async Task SendAsync(string id, CancellationToken token)
    {
        IrCommand command = _library.Commands.Single(item => item.Id == id);
        await Endpoint().TransmitAsync(command.TransmitPayload, command.Repeats, command.GapMs, token).ConfigureAwait(false);
    }
    private static string LibraryPath(PluginContext context) => Path.Combine(context.StateDirectory, "library.json");
    private void PublishLibrary()
    {
        Publish("library", $"{_library.Commands.Length} commands, {_library.Scenes.Length} scenes");
        IrCommand? selected = _library.Commands.FirstOrDefault(item => item.Id == _lastCommand);
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
