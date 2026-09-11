using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace WSGM.Plugin.Ir;

/// <summary>Identity reply. Network fields are absent on firmware before 0.2.0 and then read as unconfigured.</summary>
internal sealed record IrEndpointIdentity(string Identity, string Model, string Firmware, int Protocol, int MaxTimings,
    string? Hostname = null, int Port = 0, bool WifiConfigured = false, bool WifiConnected = false, string? Ip = null);

/// <summary>Where the plugin reaches an endpoint: a USB serial port, or a paired host on the local network.</summary>
internal sealed record IrEndpointTarget(bool Network, string Address, string? Token);

internal interface IIrEndpoint : IAsyncDisposable
{
    /// <summary>The verified identity, or null until <see cref="IdentifyAsync"/> succeeds or after any failed exchange.</summary>
    IrEndpointIdentity? Identity { get; }
    Task<IrEndpointIdentity> IdentifyAsync(CancellationToken token);
    Task<IrPayload> LearnAsync(TimeSpan timeout, CancellationToken token);
    Task TransmitAsync(IrPayload payload, int repeats, int gapMs, CancellationToken token);
    /// <summary>Stores network credentials and the pairing token on the endpoint, or clears them when the SSID is empty.</summary>
    Task<IrEndpointIdentity> ConfigureNetworkAsync(string ssid, string password, string pairingToken, CancellationToken token);
}

/// <summary>One open line-oriented connection to an endpoint.</summary>
internal interface IIrLink : IDisposable
{
    void WriteLine(string frame);
    /// <summary>Returns the next received character, or -1 while idle.</summary>
    int ReadChar();
}

/// <summary>USB CDC serial link at the protocol's fixed rate. Opening does not assert DTR or RTS, so the endpoint is not reset.</summary>
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
            NewLine = "\n",
        };
        _port.Open();
    }

    public void WriteLine(string frame) => _port.WriteLine(frame);

    public int ReadChar()
    {
        try { return _port.ReadChar(); }
        catch (TimeoutException) { return -1; }
    }

    public void Dispose() => _port.Dispose();
}

/// <summary>Plain TCP link to the endpoint's local-network listener. Authentication is the per-request pairing token.</summary>
internal sealed class TcpIrLink : IIrLink
{
    internal const int DefaultPort = 7521;
    private readonly TcpClient _client = new() { NoDelay = true, ReceiveTimeout = 100, SendTimeout = 1000 };
    private readonly NetworkStream _stream;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly byte[] _byte = new byte[1];
    private readonly char[] _chars = new char[2];
    private int _pending, _index;

    public TcpIrLink(string address, CancellationToken token)
    {
        string host = address;
        int port = DefaultPort;
        int separator = address.LastIndexOf(':');
        if (separator > 0 && !address.Contains('[') && address.IndexOf(':') == separator
            && int.TryParse(address.AsSpan(separator + 1), out int explicitPort))
        {
            host = address[..separator];
            port = explicitPort;
        }
        try { _client.ConnectAsync(host, port, token).AsTask().GetAwaiter().GetResult(); }
        catch { _client.Dispose(); throw; }
        _stream = _client.GetStream();
    }

    public void WriteLine(string frame)
    {
        _stream.Write(Encoding.UTF8.GetBytes(frame + "\n"));
        _stream.Flush();
    }

    public int ReadChar()
    {
        if (_index < _pending) { return _chars[_index++]; }
        int value;
        try { value = _stream.ReadByte(); }
        catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut }) { return -1; }
        if (value < 0) { throw new IOException("The IR endpoint closed the network connection."); }
        _byte[0] = (byte)value;
        _pending = _decoder.GetChars(_byte, 0, 1, _chars, 0);
        _index = 0;
        return _index < _pending ? _chars[_index++] : -1;
    }

    public void Dispose()
    {
        _stream.Dispose();
        _client.Dispose();
    }
}

/// <summary>One serialized endpoint connection over any link. Uncertain operations are never retried.</summary>
internal sealed class IrEndpointConnection(Func<CancellationToken, IIrLink> open, string? pairingToken = null) : IIrEndpoint
{
    private const int MaxFrame = 32768;
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };
    private readonly SemaphoreSlim _lane = new(1, 1);
    private IIrLink? _link;
    private bool _disposed;
    private IrEndpointIdentity? _identity;

    /// <summary>Opens the endpoint the target names; a USB target needs no token, a network target sends its pairing token.</summary>
    public static IrEndpointConnection Create(IrEndpointTarget target) => target.Network
        ? new(token => new TcpIrLink(target.Address, token), target.Token)
        : new(_ => new SerialIrLink(target.Address));

    public IrEndpointIdentity? Identity => _identity;

    public async Task<IrEndpointIdentity> IdentifyAsync(CancellationToken token)
    {
        using JsonDocument response = await ExchangeAsync("identify", new { }, TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        try
        {
            _identity = ParseIdentity(response);
            return _identity;
        }
        catch
        {
            await DropAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Closes the link without disposing the endpoint, so the next exchange opens a fresh one after identification.</summary>
    private async Task DropAsync()
    {
        await _lane.WaitAsync().ConfigureAwait(false);
        try { _link?.Dispose(); _link = null; _identity = null; }
        finally { _lane.Release(); }
    }

    public async Task<IrPayload> LearnAsync(TimeSpan timeout, CancellationToken token)
    {
        if (timeout.TotalMilliseconds is < 1000 or > 30000) { throw new ArgumentOutOfRangeException(nameof(timeout)); }
        using JsonDocument response = await ExchangeAsync("learn", new { timeoutMs = (int)timeout.TotalMilliseconds },
            timeout + TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        IrPayload payload = response.RootElement.GetProperty("data").Deserialize<IrPayload>(IrLibrary.Json)
            ?? throw new InvalidDataException("Missing learned IR payload.");
        payload.Validate();
        return payload;
    }

    public async Task TransmitAsync(IrPayload payload, int repeats, int gapMs, CancellationToken token)
    {
        payload.Validate(repeats, gapMs);
        using JsonDocument response = await ExchangeAsync("send", new { payload, repeats, gapMs }, TimeSpan.FromSeconds(7), token)
            .ConfigureAwait(false);
    }

    public async Task<IrEndpointIdentity> ConfigureNetworkAsync(string ssid, string password, string pairingToken, CancellationToken token)
    {
        if (ssid.Length > 32 || password.Length > 63 || (ssid.Length != 0 && pairingToken.Length is < 16 or > 64))
        {
            throw new ArgumentException("SSID is at most 32 characters and the password at most 63.");
        }
        using JsonDocument response = await ExchangeAsync("wifi", new { ssid, password, token = pairingToken },
            TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        return ParseIdentity(response);
    }

    private static IrEndpointIdentity ParseIdentity(JsonDocument response)
    {
        IrEndpointIdentity identity = response.RootElement.GetProperty("data").Deserialize<IrEndpointIdentity>(WireJson)
            ?? throw new InvalidDataException("Missing IR endpoint identity.");
        if (identity.Protocol != 1 || identity.MaxTimings != 1024 || string.IsNullOrWhiteSpace(identity.Identity))
        {
            throw new InvalidDataException($"IR endpoint protocol {identity.Protocol} is incompatible; this plugin requires protocol 1.");
        }
        return identity;
    }

    private async Task<JsonDocument> ExchangeAsync(string operation, object arguments, TimeSpan timeout, CancellationToken token)
    {
        await _lane.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (operation != "identify" && _identity is null) { throw new InvalidOperationException("Identify the IR endpoint first."); }
            using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
            bounded.CancelAfter(timeout);
            // Opening and the synchronous driver reads run on a worker. The link's read timeout bounds
            // each poll; cancellation closes this connection and retires all outstanding responses.
            return await Task.Run(() => Exchange(operation, arguments, bounded.Token), CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            _link?.Dispose();
            _link = null;
            _identity = null;
            throw;
        }
        finally { _lane.Release(); }
    }

    private JsonDocument Exchange(string operation, object arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _link ??= open(token);
        string id = Guid.NewGuid().ToString("N");
        Dictionary<string, object?> request = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            JsonSerializer.Serialize(arguments, IrLibrary.Json))!;
        request["v"] = 1;
        request["id"] = id;
        request["op"] = operation;
        if (pairingToken is not null) { request["token"] = pairingToken; }
        string frame = JsonSerializer.Serialize(request); // One compact line, regardless of library formatting.
        if (Encoding.UTF8.GetByteCount(frame) > MaxFrame) { throw new InvalidDataException("IR request exceeds frame limit."); }
        _link.WriteLine(frame);
        StringBuilder line = new();
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int character = _link.ReadChar();
                if (character < 0 || character == '\r') { continue; }
                if (character != '\n')
                {
                    if (line.Length >= MaxFrame) { throw new InvalidDataException("IR response exceeds frame limit."); }
                    line.Append((char)character);
                    continue;
                }
                string text = line.ToString();
                line.Clear();
                if (!text.StartsWith('{')) { continue; } // Boot ROM diagnostics are not protocol frames.
                JsonDocument response = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
                if (!response.RootElement.TryGetProperty("id", out var responseId) || responseId.GetString() != id)
                {
                    response.Dispose();
                    continue;
                }
                string expected = operation == "send" ? "transmitted" : operation == "learn" ? "learned" : "ok";
                if (response.RootElement.GetProperty("v").GetInt32() != 1
                    || response.RootElement.GetProperty("status").GetString() != expected)
                {
                    string status = response.RootElement.GetProperty("status").GetString() ?? "unknown";
                    response.Dispose();
                    throw new InvalidDataException(Describe(operation, status));
                }
                return response;
            }
        }
        catch (OperationCanceledException) when (operation == "learn")
        {
            // Cancel is a distinct operation, never a retry of learn or send. The connection is
            // then discarded; firmware also has its own bounded learning deadline.
            try
            {
                Dictionary<string, object?> cancel = new() { ["v"] = 1, ["id"] = Guid.NewGuid().ToString("N"), ["op"] = "cancel" };
                if (pairingToken is not null) { cancel["token"] = pairingToken; }
                _link.WriteLine(JsonSerializer.Serialize(cancel));
            }
            catch (IOException) { }
            catch (TimeoutException) { }
            throw;
        }
    }

    /// <summary>Turns a protocol refusal into the message shown in Tools. Unknown statuses stay literal.</summary>
    internal static string Describe(string operation, string status) => status switch
    {
        "timeout" => "No IR signal arrived before the learn timeout. Point the remote at the receiver and press one button briefly.",
        "busy" => "The endpoint is still learning. Wait for the timeout or cancel first.",
        "capture-overflow" => "The signal was too long to capture in one payload. Press the remote button briefly instead of holding it.",
        "timing-limit" => "The captured signal contains a gap longer than one payload can represent.",
        "protocol-mismatch" => "The endpoint firmware speaks a different protocol version than this plugin.",
        "unauthorized" => "The endpoint rejected the pairing token. Pair it again over USB.",
        "usb-only" => "Wi-Fi setup is only accepted over the USB connection.",
        "unsupported-operation" when operation == "wifi" => "This endpoint firmware has no Wi-Fi support; flash firmware 0.2.0 or later.",
        _ => $"IR endpoint refused {operation}: {status}.",
    };

    public async ValueTask DisposeAsync()
    {
        await _lane.WaitAsync().ConfigureAwait(false);
        try { _disposed = true; _link?.Dispose(); _link = null; _identity = null; }
        finally { _lane.Release(); }
    }
}
