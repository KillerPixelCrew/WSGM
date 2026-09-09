using System.IO.Ports;
using System.Text;
using System.Text.Json;

namespace WSGM.Plugin.Ir;

internal sealed record IrEndpointIdentity(string Identity, string Model, string Firmware, int Protocol, int MaxTimings);

internal interface IIrEndpoint : IAsyncDisposable
{
    Task<IrEndpointIdentity> IdentifyAsync(CancellationToken token);
    Task<IrPayload> LearnAsync(TimeSpan timeout, CancellationToken token);
    Task TransmitAsync(IrPayload payload, int repeats, int gapMs, CancellationToken token);
}

/// <summary>One serialized USB endpoint connection. Uncertain operations are never retried.</summary>
internal sealed class SerialIrEndpoint(string portName) : IIrEndpoint
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };
    private readonly SemaphoreSlim _lane = new(1, 1);
    private SerialPort? _port;
    private bool _disposed;
    private IrEndpointIdentity? _identity;

    public async Task<IrEndpointIdentity> IdentifyAsync(CancellationToken token)
    {
        using JsonDocument response = await ExchangeAsync("identify", new { }, TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        IrEndpointIdentity identity = response.RootElement.GetProperty("data").Deserialize<IrEndpointIdentity>(WireJson)
            ?? throw new InvalidDataException("Missing IR endpoint identity.");
        if (identity.Protocol != 1 || identity.MaxTimings != 1024 || string.IsNullOrWhiteSpace(identity.Identity))
        {
            throw new InvalidDataException("IR endpoint protocol is incompatible.");
        }
        _identity = identity;
        return identity;
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

    private async Task<JsonDocument> ExchangeAsync(string operation, object arguments, TimeSpan timeout, CancellationToken token)
    {
        await _lane.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (operation != "identify" && _identity is null) { throw new InvalidOperationException("Identify the IR endpoint first."); }
            using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
            bounded.CancelAfter(timeout);
            // SerialPort.Open and its synchronous driver reads run on a worker. ReadTimeout bounds
            // each poll; cancellation closes this connection and retires all outstanding responses.
            return await Task.Run(() => Exchange(operation, arguments, bounded.Token), CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            _port?.Dispose();
            _port = null;
            _identity = null;
            throw;
        }
        finally { _lane.Release(); }
    }

    private JsonDocument Exchange(string operation, object arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_port is null)
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
        string id = Guid.NewGuid().ToString("N");
        Dictionary<string, object?> request = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            JsonSerializer.Serialize(arguments, IrLibrary.Json))!;
        request["v"] = 1;
        request["id"] = id;
        request["op"] = operation;
        string frame = JsonSerializer.Serialize(request); // One compact line, regardless of library formatting.
        if (Encoding.UTF8.GetByteCount(frame) > 32768) { throw new InvalidDataException("IR request exceeds frame limit."); }
        _port.WriteLine(frame);
        StringBuilder line = new();
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int character;
                try { character = _port.ReadChar(); }
                catch (TimeoutException) { continue; }
                if (character == '\r') { continue; }
                if (character != '\n')
                {
                    if (line.Length >= 32768) { throw new InvalidDataException("IR response exceeds frame limit."); }
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
                    throw new InvalidDataException($"IR endpoint refused {operation}: {status}.");
                }
                return response;
            }
        }
        catch (OperationCanceledException) when (operation == "learn")
        {
            // Cancel is a distinct operation, never a retry of learn or send. The connection is
            // then discarded; firmware also has its own bounded learning deadline.
            try { _port.WriteLine(JsonSerializer.Serialize(new { v = 1, id = Guid.NewGuid().ToString("N"), op = "cancel" })); }
            catch (IOException) { }
            catch (TimeoutException) { }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lane.WaitAsync().ConfigureAwait(false);
        try { _disposed = true; _port?.Dispose(); _port = null; _identity = null; }
        finally { _lane.Release(); }
    }
}
