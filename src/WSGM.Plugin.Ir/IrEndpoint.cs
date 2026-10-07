using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace WSGM.Plugin.Ir;

/// <summary>
///     Identity reply. Fields an endpoint leaves out read as unconfigured. The built-in remote count
///     is 0 when the firmware serves no remotes.
/// </summary>
/// <param name="Identity">Stable endpoint identity required by protocol validation.</param>
/// <param name="Model">Firmware-reported hardware/model label.</param>
/// <param name="Firmware">Firmware version string.</param>
/// <param name="Protocol">Wire protocol version; identification rejects incompatible versions.</param>
/// <param name="MaxTimings">Maximum raw timing count the firmware accepts; identification currently requires 1024.</param>
/// <param name="Hostname">Configured network host name, or null when unconfigured.</param>
/// <param name="Port">Endpoint TCP command port, or zero when not reported.</param>
/// <param name="WifiConfigured">Whether Wi-Fi credentials are stored on the endpoint.</param>
/// <param name="WifiConnected">Whether the endpoint currently reports a Wi-Fi connection.</param>
/// <param name="Ip">Current reported network address, or null.</param>
/// <param name="WebPort">Web interface port, or zero when not reported.</param>
/// <param name="WebConfigured">Whether the firmware reports configured web access.</param>
/// <param name="Remotes">Number of built-in remotes reported by firmware; zero when none are served.</param>
/// <param name="SequenceRunning">Whether the firmware is currently running a built-in sequence.</param>
internal sealed record IrEndpointIdentity(
    string Identity,
    string Model,
    string Firmware,
    int Protocol,
    int MaxTimings,
    string? Hostname = null,
    int Port = 0,
    bool WifiConfigured = false,
    bool WifiConnected = false,
    string? Ip = null,
    int WebPort = 0,
    bool WebConfigured = false,
    int Remotes = 0,
    bool SequenceRunning = false);

// ReSharper disable once NotAccessedPositionalProperty.Global
/// <summary>One button of a built-in remote.</summary>
/// <param name="Id">Firmware command identity used to address the button or sequence.</param>
/// <param name="Label">Human-readable name supplied by the firmware.</param>
internal sealed record IrRemoteButton(string Id, string Label);

/// <summary>What a built-in remote's air conditioner accepts, as the firmware declares it.</summary>
/// <param name="Protocol">Firmware IR protocol name for the remote.</param>
/// <param name="Modes">Allowed climate mode identifiers.</param>
/// <param name="Fans">Allowed fan setting identifiers.</param>
/// <param name="MinDegrees">Inclusive minimum temperature in the declared units.</param>
/// <param name="MaxDegrees">Inclusive maximum temperature in the declared units.</param>
/// <param name="Celsius">True for Celsius temperatures; false for Fahrenheit.</param>
/// <param name="Swing">Declared swing behavior; none means no swing command is offered.</param>
internal sealed record IrRemoteClimate(
    // ReSharper disable once NotAccessedPositionalProperty.Global
    string Protocol,
    string[] Modes,
    string[] Fans,
    double MinDegrees,
    double MaxDegrees,
    // ReSharper disable once NotAccessedPositionalProperty.Global
    bool Celsius = true,
    string Swing = "none");

/// <summary>One remote built into the endpoint firmware.</summary>
/// <param name="Id">Firmware remote identity used by endpoint operations.</param>
/// <param name="Name">Displayed remote name.</param>
/// <param name="Buttons">Declared direct button commands.</param>
/// <param name="Sequences">Declared built-in sequences that the firmware executes.</param>
/// <param name="Climate">Climate capabilities, or null for a remote without an air-conditioner interface.</param>
internal sealed record IrRemote(
    string Id,
    string Name,
    IrRemoteButton[] Buttons,
    IrRemoteButton[] Sequences,
    IrRemoteClimate? Climate = null);

/// <summary>Every remote the endpoint carries.</summary>
/// <param name="Remotes">Firmware-provided remote definitions; an empty array means none are available.</param>
internal sealed record IrRemoteCatalog(IrRemote[] Remotes);

/// <summary>One air-conditioner state addressed to a built-in remote's declared capabilities.</summary>
/// <param name="Power">Requested appliance power state.</param>
/// <param name="Mode">Mode from the remote's declared catalog.</param>
/// <param name="Degrees">Temperature in the units declared by the remote, within its inclusive range.</param>
/// <param name="Fan">Fan level from the remote's declared catalog.</param>
/// <param name="ToggleSwing">Requests one swing toggle; an uncertain toggle must not be repeated automatically.</param>
internal sealed record IrClimateRequest(bool Power, string Mode, double Degrees, string Fan, bool ToggleSwing = false);

/// <summary>Where the plugin reaches an endpoint: a USB serial port, or a paired host on the local network.</summary>
/// <param name="Network">True for TCP; false for a USB serial port.</param>
/// <param name="Address">COM port name for USB, or host with optional TCP port for network.</param>
/// <param name="Token">Shared network pairing secret; unused for USB and never suitable for logging.</param>
internal sealed record IrEndpointTarget(bool Network, string Address, string? Token);

/// <summary>Owns one lazy endpoint connection and serializes protocol exchanges without retrying uncertain sends.</summary>
/// <remarks>
///     Mutating operations require a successful identity exchange. Cancellation ends local waiting, not
///     necessarily firmware emission. Dispose closes the owned link after outstanding exchanges leave the lane.
/// </remarks>
internal interface IIrEndpoint : IAsyncDisposable
{
    /// <summary>The verified identity, or null until <see cref="IdentifyAsync" /> succeeds or after any failed exchange.</summary>
    IrEndpointIdentity? Identity { get; }

    /// <summary>Opens the link if needed and validates protocol compatibility and endpoint limits.</summary>
    /// <param name="token">Cancels connection or reply waiting.</param>
    /// <returns>Validated identity cached until a failed exchange or disposal invalidates it.</returns>
    Task<IrEndpointIdentity> IdentifyAsync(CancellationToken token);
    /// <summary>Requests one bounded raw capture without emitting IR.</summary>
    /// <param name="timeout">Firmware capture window from one through thirty seconds.</param>
    /// <param name="token">Cancels reply waiting; absence of a reply is not successful capture.</param>
    /// <returns>A validated envelope; the firmware's default carrier is marked assumed, not measured.</returns>
    Task<IrPayload> LearnAsync(TimeSpan timeout, CancellationToken token);
    /// <summary>Validates and sends one raw envelope with bounded repeats.</summary>
    /// <param name="payload">Envelope and carrier to emit; remains unchanged during the exchange.</param>
    /// <param name="repeats">Additional emissions, from 0 through 4.</param>
    /// <param name="gapMs">Inter-emission gap in milliseconds, from 0 through 200.</param>
    /// <param name="token">Cancels waiting; emission already dispatched may still finish.</param>
    /// <returns>Completion after the endpoint acknowledges transmission, not appliance-state verification.</returns>
    Task TransmitAsync(IrPayload payload, int repeats, int gapMs, CancellationToken token);

    /// <summary>Stores network credentials and the pairing token on the endpoint, or clears them when the SSID is empty.</summary>
    /// <param name="ssid">Network name of at most 32 characters; empty clears pairing.</param>
    /// <param name="password">Network password of at most 63 characters; empty supports an open network.</param>
    /// <param name="networkToken">New pairing token of 16 through 64 characters when configuring a network.</param>
    /// <param name="token">Cancels waiting; a partially written flash update is not retried.</param>
    /// <returns>Endpoint identity reported after acknowledged storage; firmware restricts this operation to USB.</returns>
    Task<IrEndpointIdentity> ConfigureNetworkAsync(string ssid, string password, string networkToken,
        CancellationToken token);

    /// <summary>Reads the remotes built into the firmware, one catalog chunk per exchange.</summary>
    /// <param name="token">Cancels the ordered chunk reads.</param>
    /// <returns>The complete catalog after index/count consistency and JSON validation.</returns>
    Task<IrRemoteCatalog> ListRemotesAsync(CancellationToken token);

    /// <summary>Presses one button of a built-in remote.</summary>
    /// <param name="remote">Firmware catalog remote ID.</param>
    /// <param name="button">Button ID in that remote's catalog.</param>
    /// <param name="token">Cancels waiting without retracting an emitted frame.</param>
    /// <returns>Completion after transmission acknowledgment; no appliance readback is available.</returns>
    Task PressAsync(string remote, string button, CancellationToken token);

    /// <summary>Sends one complete air-conditioner state to a built-in remote.</summary>
    /// <param name="remote">Firmware catalog remote ID with climate support.</param>
    /// <param name="request">Complete requested state within the remote's declared modes, fan levels and temperature range.</param>
    /// <param name="token">Cancels waiting without proving the appliance remained unchanged.</param>
    /// <returns>Completion after transmission acknowledgment.</returns>
    Task ClimateAsync(string remote, IrClimateRequest request, CancellationToken token);

    /// <summary>Starts a built-in remote's sequence. The endpoint runs it in the background.</summary>
    /// <param name="remote">Firmware catalog remote ID.</param>
    /// <param name="sequence">Sequence ID owned by that remote.</param>
    /// <param name="token">Cancels start-acknowledgment waiting.</param>
    /// <returns>Completion after the sequence starts, not after all steps finish; poll identity for running state.</returns>
    Task RunSequenceAsync(string remote, string sequence, CancellationToken token);

    /// <summary>Stops a running learn or sequence. A distinct operation, never a retry.</summary>
    /// <param name="token">Cancels the cancellation-request exchange.</param>
    /// <returns>Completion after firmware acknowledges cancellation; already emitted frames are not undone.</returns>
    Task CancelAsync(CancellationToken token);
}

/// <summary>One open line-oriented connection to an endpoint.</summary>
internal interface IIrLink : IDisposable
{
    /// <summary>Writes one UTF-8 request followed by the protocol line terminator.</summary>
    /// <param name="frame">Complete compact JSON frame without its trailing newline.</param>
    void WriteLine(string frame);

    /// <summary>Returns the next received character, or -1 while idle.</summary>
    /// <returns>The next UTF-16 character, or -1 when the bounded read is idle; disconnection may throw.</returns>
    int ReadChar();
}

/// <summary>
///     USB CDC serial link at the protocol's fixed rate. Opening does not assert DTR or RTS, so the endpoint is not
///     reset.
/// </summary>
internal sealed class SerialIrLink : IIrLink
{
    private readonly SerialPort _port;

    public SerialIrLink(string portName)
    {
        _port = new SerialPort(portName, 115200)
        {
            ReadTimeout = 100,
            WriteTimeout = 1000,
            DtrEnable = false,
            RtsEnable = false,
            Encoding = Encoding.UTF8,
            NewLine = "\n"
        };
        try
        {
            _port.Open();
        }
        catch
        {
            _port.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void WriteLine(string frame)
    {
        _port.WriteLine(frame);
    }

    /// <inheritdoc />
    public int ReadChar()
    {
        try
        {
            return _port.ReadChar();
        }
        catch (TimeoutException)
        {
            return -1;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _port.Dispose();
    }
}

/// <summary>Plain TCP link to the endpoint's local-network listener. Authentication is the per-request pairing token.</summary>
internal sealed class TcpIrLink : IIrLink
{
    /// <summary>How long one read waits for the endpoint before the link polls again, in milliseconds.</summary>
    internal const int ReceiveTimeoutMs = 100;

    private const int DefaultPort = 7521;
    private readonly byte[] _byte = new byte[1];
    private readonly char[] _chars = new char[2];

    private readonly TcpClient _client = new()
        { NoDelay = true, ReceiveTimeout = ReceiveTimeoutMs, SendTimeout = 1000 };

    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly NetworkStream _stream;
    private int _pending, _index;

    public TcpIrLink(string address, CancellationToken token)
    {
        var host = address;
        var port = DefaultPort;
        var separator = address.LastIndexOf(':');
        if (separator > 0 && !address.Contains('[') && address.IndexOf(':') == separator
            && int.TryParse(address.AsSpan(separator + 1), out var explicitPort))
        {
            host = address[..separator];
            port = explicitPort;
        }

        try
        {
            _client.ConnectAsync(host, port, token).AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            _client.Dispose();
            throw;
        }

        _stream = _client.GetStream();
    }

    /// <inheritdoc />
    public void WriteLine(string frame)
    {
        _stream.Write(Encoding.UTF8.GetBytes(frame + "\n"));
        _stream.Flush();
    }

    /// <inheritdoc />
    public int ReadChar()
    {
        if (_index < _pending)
        {
            return _chars[_index++];
        }

        int value;
        try
        {
            value = _stream.ReadByte();
        }
        catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut })
        {
            return -1;
        }

        if (value < 0)
        {
            throw new IOException("The IR endpoint closed the network connection.");
        }

        _byte[0] = (byte)value;
        _pending = _decoder.GetChars(_byte, 0, 1, _chars, 0);
        _index = 0;
        return _index < _pending ? _chars[_index++] : -1;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stream.Dispose();
        _client.Dispose();
    }
}

/// <summary>One serialized endpoint connection over any link. Uncertain operations are never retried.</summary>
/// <param name="open">Lazily opens a link under the serialized exchange lane; the connection owns and disposes the returned link.</param>
/// <param name="pairingToken">Shared token sent on network requests, or null for USB; never log or expose it in diagnostics.</param>
internal sealed class IrEndpointConnection(Func<CancellationToken, IIrLink> open, string? pairingToken = null)
    : IIrEndpoint
{
    /// <summary>The endpoint protocol this plugin speaks; firmware 0.5.0 is the first to speak protocol 2.</summary>
    internal const int ProtocolVersion = 2;

    private const int MaxFrame = 32768;

    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    private readonly SemaphoreSlim _lane = new(1, 1);
    private bool _disposed;
    private IIrLink? _link;

    /// <inheritdoc />
    public IrEndpointIdentity? Identity { get; private set; }

    /// <inheritdoc />
    public async Task<IrEndpointIdentity> IdentifyAsync(CancellationToken token)
    {
        using var response =
            await ExchangeAsync("identify", new { }, TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        try
        {
            Identity = ParseIdentity(response);
            return Identity;
        }
        catch
        {
            await DropAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IrPayload> LearnAsync(TimeSpan timeout, CancellationToken token)
    {
        if (timeout.TotalMilliseconds is < 1000 or > 30000)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var response = await ExchangeAsync("learn", new { timeoutMs = (int)timeout.TotalMilliseconds },
            timeout + TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        var payload = response.RootElement.GetProperty("data").Deserialize<IrPayload>(IrLibrary.Json)
                      ?? throw new InvalidDataException("Missing learned IR payload.");
        payload.Validate();
        return payload;
    }

    /// <inheritdoc />
    public async Task TransmitAsync(IrPayload payload, int repeats, int gapMs, CancellationToken token)
    {
        try
        {
            payload.Validate(repeats, gapMs);
        }
        catch (InvalidDataException ex)
        {
            throw new IrRejectedException(ex.Message);
        }

        using var response =
            await ExchangeAsync("send", new { payload, repeats, gapMs }, TimeSpan.FromSeconds(7), token)
                .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IrRemoteCatalog> ListRemotesAsync(CancellationToken token)
    {
        // A catalog can outgrow one frame, so the endpoint serves its JSON text in chunks cut on UTF-8
        // boundaries. Each chunk is its own read; a reply that disagrees with the request fails the read.
        StringBuilder text = new();
        var count = 1;
        try
        {
            for (var chunk = 0; chunk < count; chunk++)
            {
                using var response = await ExchangeAsync("remotes", new { chunk }, TimeSpan.FromSeconds(5), token)
                    .ConfigureAwait(false);
                var data = response.RootElement.GetProperty("data");
                var total = data.GetProperty("count").GetInt32();
                if (data.GetProperty("index").GetInt32() != chunk || total < 1 || (chunk > 0 && total != count))
                {
                    throw new InvalidDataException("The IR endpoint answered another built-in remote catalog chunk.");
                }

                count = total;
                var part = data.GetProperty("chunk").GetString();
                if (string.IsNullOrEmpty(part))
                {
                    throw new InvalidDataException("The IR endpoint answered an empty built-in remote catalog chunk.");
                }

                text.Append(part);
            }

            return JsonSerializer.Deserialize<IrRemoteCatalog>(text.ToString(), WireJson)
                   ?? throw new InvalidDataException("Missing built-in remote catalog.");
        }
        catch
        {
            await DropAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task PressAsync(string remote, string button, CancellationToken token)
    {
        using var response = await ExchangeAsync("press", new { remote, button },
            TimeSpan.FromSeconds(7), token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ClimateAsync(string remote, IrClimateRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await ExchangeAsync("climate",
            new { remote, request.Power, request.Mode, request.Degrees, request.Fan, request.ToggleSwing },
            TimeSpan.FromSeconds(7), token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RunSequenceAsync(string remote, string sequence, CancellationToken token)
    {
        using var response = await ExchangeAsync("run", new { remote, sequence },
            TimeSpan.FromSeconds(7), token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CancelAsync(CancellationToken token)
    {
        using var response = await ExchangeAsync("cancel", new { }, TimeSpan.FromSeconds(5), token)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IrEndpointIdentity> ConfigureNetworkAsync(string ssid, string password, string networkToken,
        CancellationToken token)
    {
        if (ssid.Length > 32 || password.Length > 63 || (ssid.Length != 0 && networkToken.Length is < 16 or > 64))
        {
            throw new ArgumentException("SSID is at most 32 characters and the password at most 63.");
        }

        using var response = await ExchangeAsync("wifi", new { ssid, password, token = networkToken },
            TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        return ParseIdentity(response);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _lane.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            _link?.Dispose();
            _link = null;
            Identity = null;
        }
        finally
        {
            _lane.Release();
        }
    }

    /// <summary>Creates a lazy connection for a USB or paired network target; no I/O occurs until an exchange.</summary>
    /// <param name="target">USB port or paired network destination and its private pairing token.</param>
    /// <returns>An unopened connection owned by the caller; the first exchange opens its link.</returns>
    public static IrEndpointConnection Create(IrEndpointTarget target)
    {
        return target.Network
            ? new IrEndpointConnection(token => new TcpIrLink(target.Address, token), target.Token)
            : new IrEndpointConnection(_ => new SerialIrLink(target.Address));
    }

    /// <summary>Closes the link without disposing the endpoint, so the next exchange opens a fresh one after identification.</summary>
    private async Task DropAsync()
    {
        await _lane.WaitAsync().ConfigureAwait(false);
        try
        {
            _link?.Dispose();
            _link = null;
            Identity = null;
        }
        finally
        {
            _lane.Release();
        }
    }

    private static IrEndpointIdentity ParseIdentity(JsonDocument response)
    {
        var identity = response.RootElement.GetProperty("data").Deserialize<IrEndpointIdentity>(WireJson)
                       ?? throw new InvalidDataException("Missing IR endpoint identity.");
        if (identity.Protocol != ProtocolVersion || identity.MaxTimings != 1024
                                                 || string.IsNullOrWhiteSpace(identity.Identity))
        {
            throw new InvalidDataException(
                $"IR endpoint protocol {identity.Protocol} is incompatible; this plugin requires protocol {ProtocolVersion}. Flash firmware 0.5.0.");
        }

        return identity;
    }

    private async Task<JsonDocument> ExchangeAsync(string operation, object arguments, TimeSpan timeout,
        CancellationToken token)
    {
        try
        {
            await _lane.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new IrRejectedException("The IR request was cancelled before it could be sent.");
        }

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (operation != "identify" && Identity is null)
            {
                throw new InvalidOperationException("Identify the IR endpoint first.");
            }

            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
            bounded.CancelAfter(timeout);
            var boundedToken = bounded.Token;
            // Opening and the synchronous driver reads run on a worker. The link's read timeout bounds
            // each poll; cancellation closes this connection and retires all outstanding responses.
            return await Task.Run(() => Exchange(operation, arguments, boundedToken), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            _link?.Dispose();
            _link = null;
            Identity = null;
            throw;
        }
        finally
        {
            _lane.Release();
        }
    }

    private JsonDocument Exchange(string operation, object arguments, CancellationToken token)
    {
        var id = Guid.NewGuid().ToString("N");
        string frame;
        try
        {
            token.ThrowIfCancellationRequested();
            _link ??= open(token);
            frame = Request(operation, id, arguments);
            if (Encoding.UTF8.GetByteCount(frame) > MaxFrame)
            {
                throw new InvalidDataException("IR request exceeds frame limit.");
            }

            token.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or IrRejectedException))
        {
            // Nothing has reached WriteLine yet. Once it starts, a partial write is uncertain.
            throw new IrRejectedException("The IR request could not be sent: " + ex.Message);
        }

        _link.WriteLine(frame);
        StringBuilder line = new();
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var character = _link.ReadChar();
                if (character is < 0 or '\r')
                {
                    continue;
                }

                if (character != '\n')
                {
                    if (line.Length >= MaxFrame)
                    {
                        throw new InvalidDataException("IR response exceeds frame limit.");
                    }

                    line.Append((char)character);
                    continue;
                }

                var text = line.ToString();
                line.Clear();
                if (Encoding.UTF8.GetByteCount(text) > MaxFrame)
                {
                    throw new InvalidDataException("IR response exceeds frame limit.");
                }

                if (!text.StartsWith('{'))
                {
                    continue;
                } // Boot ROM diagnostics are not protocol frames.

                if (ReadReply(text, id, operation) is { } response)
                {
                    return response;
                }
            }
        }
        catch (OperationCanceledException) when (operation == "learn")
        {
            // Cancel is a distinct operation, never a retry of learn or send. The connection is
            // then discarded; firmware also has its own bounded learning deadline.
            try
            {
                _link.WriteLine(Request("cancel", Guid.NewGuid().ToString("N"), new { }));
            }
            catch (IOException)
            {
            }
            catch (TimeoutException)
            {
            }

            throw;
        }
    }

    /// <summary>
    ///     One compact request line: the operation's arguments, then the envelope fields. The request token
    ///     replaces an argument of the same name, which only the USB-only wifi operation carries.
    /// </summary>
    private string Request(string operation, string id, object arguments)
    {
        var request = JsonSerializer.SerializeToNode(arguments, IrLibrary.Json)!.AsObject();
        request["v"] = ProtocolVersion;
        request["id"] = id;
        request["op"] = operation;
        if (pairingToken is not null)
        {
            request["token"] = pairingToken;
        }

        return request.ToJsonString();
    }

    /// <summary>
    ///     Reads one reply line. Another request's reply returns null. A matching reply in this protocol is
    ///     either the operation's success status or a refusal: the endpoint answers every other status before it
    ///     emits anything. A reply in another protocol is not understood, unless it says the protocols differ.
    /// </summary>
    private static JsonDocument? ReadReply(string text, string id, string operation)
    {
        var response = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
        var transferred = false;
        try
        {
            if (!response.RootElement.TryGetProperty("id", out var responseId) || responseId.GetString() != id)
            {
                return null;
            }

            var version = response.RootElement.GetProperty("v").GetInt32();
            var status = response.RootElement.GetProperty("status").GetString();
            if (string.IsNullOrEmpty(status))
            {
                throw new InvalidDataException("Missing IR endpoint reply status.");
            }

            if (version == ProtocolVersion && status == ExpectedStatus(operation))
            {
                transferred = true;
                return response;
            }

            if (version == ProtocolVersion || status == "protocol-mismatch")
            {
                throw new IrRejectedException(Describe(operation, status));
            }

            throw new InvalidDataException(Describe(operation, status));
        }
        finally
        {
            if (!transferred)
            {
                response.Dispose();
            }
        }
    }

    /// <summary>
    ///     The status one operation reports on success. A sequence only reports that it
    ///     started: the endpoint runs its steps in the background.
    /// </summary>
    private static string ExpectedStatus(string operation)
    {
        return operation switch
        {
            "send" or "press" or "climate" => "transmitted",
            "learn" => "learned",
            "run" => "started",
            _ => "ok"
        };
    }

    /// <summary>Turns a protocol refusal into the message shown in Tools. Unknown statuses stay literal.</summary>
    /// <param name="operation">Requested protocol operation, used to disambiguate busy and unsupported responses.</param>
    /// <param name="status">Status token returned by the endpoint.</param>
    /// <returns>A user-facing refusal explanation, retaining an unrecognized operation/status literally for diagnosis.</returns>
    internal static string Describe(string operation, string status)
    {
        return status switch
        {
            "busy" when operation is "press" or "climate" or "run" or "send" =>
                "The endpoint is still learning or running a sequence. Wait for it or cancel first.",
            "unknown-remote" => "The endpoint has no remote with that id. Read its built-in remotes first.",
            "unknown-button" => "That remote has no button with that id.",
            "unknown-sequence" => "That remote has no sequence with that id.",
            "unknown-climate" => "That remote is not an air conditioner.",
            "invalid-ac-state" =>
                "The endpoint refused that air-conditioner state; check the mode, fan and temperature it declares.",
            "unsupported-operation" when operation is "remotes" or "press" or "climate" or "run" =>
                "This endpoint firmware has no built-in remotes; flash the firmware that ships with this WSGM.",
            "timeout" =>
                "No IR signal arrived before the learn timeout. Point the remote at the receiver and press one button briefly.",
            "busy" => "The endpoint is still learning. Wait for the timeout or cancel first.",
            "capture-overflow" =>
                "The signal was too long to capture in one payload. Press the remote button briefly instead of holding it.",
            "timing-limit" => "The captured signal contains a gap longer than one payload can represent.",
            "protocol-mismatch" =>
                "The IR endpoint speaks another protocol; flash the firmware that ships with this WSGM.",
            "storage-failed" =>
                "The endpoint could not save the setting to its flash, and part of it may be stored. Set it again.",
            "unauthorized" => "The endpoint rejected the pairing token. Pair it again over USB.",
            "usb-only" => "Wi-Fi setup is only accepted over the USB connection.",
            "unsupported-operation" when operation == "wifi" =>
                "This endpoint firmware has no Wi-Fi support; flash the firmware that ships with this WSGM.",
            _ => $"IR endpoint refused {operation}: {status}."
        };
    }
}
