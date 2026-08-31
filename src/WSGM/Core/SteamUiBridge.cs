using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>A typed request from repository-owned Steam UI code to WSGM.</summary>
/// <param name="Version">Bridge schema version.</param>
/// <param name="Type">Either <c>request</c> or <c>cancel</c>.</param>
/// <param name="PatchId">The patch owning the command.</param>
/// <param name="Command">The allowlisted semantic command.</param>
/// <param name="Sequence">Monotonic request sequence in the current bridge generation.</param>
/// <param name="ActionGeneration">Origin generation used for duplicate suppression.</param>
/// <param name="ContextGeneration">Expected JavaScript context generation.</param>
/// <param name="DocumentGeneration">Expected document generation.</param>
/// <param name="Payload">Bounded semantic payload.</param>
public sealed record SteamUiBridgeRequest(
    int Version,
    string Type,
    string PatchId,
    string Command,
    long Sequence,
    long ActionGeneration,
    long ContextGeneration,
    long DocumentGeneration,
    JsonElement Payload);

/// <summary>Result of authorizing one narrow Steam UI bridge request.</summary>
/// <param name="Accepted">Whether the host may dispatch the request.</param>
/// <param name="Reason">A bounded rejection reason.</param>
public readonly record struct SteamUiBridgeAuthorizationResult(bool Accepted, string? Reason);

/// <summary>Authorizes native-QAM commands without exposing generic evaluation or host APIs.</summary>
public sealed class SteamUiBridgeAuthorizer
{
    private static readonly IReadOnlyDictionary<string, string[]> Commands =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["wsgm.native-qam.tdp"] = ["setPrimaryLimit"],

            // The bootstrap declares this component with command "setAutoTdp" and its control
            // subscribes to the patch id on mount. The id was missing here, and the JS gate is
            // `Object.hasOwn(config.allowed, patchId)` for subscriptions as well as commands, so
            // the AutoTDP row threw "subscription not allowlisted" every time it rendered — the one
            // native-QAM control that could never receive state.
            ["wsgm.native-qam.auto-tdp"] = ["setAutoTdp"],
            // Two commands, one row. With the frame limit switched off the unified slider becomes
            // the refresh rate itself, which is what SteamOS's own row does — so the second
            // command belongs to this control, not to the separate manual-refresh row that stays
            // hidden while a cap owns the rate.
            ["wsgm.native-qam.frame-limit"] = ["setFrameLimit", "setRefreshRate"],
            ["wsgm.native-qam.controller-target"] = ["setControllerTarget"],

            // Hand-built like the resolution row above, and for the same kind of reason: Valve
            // ships a VRR component, but it is gated on a react-query over
            // SteamClient.System.DisplayManager, which this client does not define. The query never
            // succeeds and the component returns null before reading anything WSGM publishes.
            ["wsgm.native-qam.vrr"] = ["setVariableRefreshRate"],
            ["wsgm.native-qam.shell"] = ["toggleQuickAccess"],

            // Audio is supplied as a namespace rather than drawn as a row, so its vocabulary is the
            // set of operations Steam's own audio store calls: the device list it asks for once at
            // construction, and the two changes its device picker and volume slider can make.
            // Registration is not a command — the store subscribes to this patch id for state, and
            // the JS gate checks the id for subscriptions as well as commands.
            ["wsgm.native-qam.audio"] = ["getDevices", "setDefaultDevice", "setVolume"],

            // One command, because every performance setter in Steam's own store funnels into
            // UpdateSettings with a protobuf delta. The delta says which control moved, so a
            // per-control vocabulary here would only duplicate what the payload already carries.
            ["wsgm.native-qam.perf"] = ["updateSettings"],

            // The same trap the AutoTDP comment above records, walked into again on 2026-08-30:
            // this row subscribes to its patch id on mount, the id was missing here, and
            // subscribe() threw "subscription not allowlisted" during render — which Steam's error
            // boundary turned into a blank Performance tab, not a missing row.
            //
            // ANY new native-QAM control needs its id here before it renders, whether or not it
            // sends commands.
            ["wsgm.native-qam.resolution"] = ["setResolution"],

            // Valve's own components, mounted rather than built. No commands: they write through
            // SteamClient.System.Perf.UpdateSettings, which is the perf entry above. The ids are
            // listed so that a subscription from one can never throw the way the row above did.
            ["wsgm.native-qam.valve-profile-header"] = [],
            ["wsgm.native-qam.valve-reset"] = [],
            ["wsgm.native-qam.valve-refresh-rate"] = [],
            ["wsgm.native-qam.valve-overlay-level"] = [],

            // Valve's power-limit pair. No commands: they write the steamos_tdp_limit client
            // settings, which the SteamOS Manager gate watches — that gate owns the setPrimaryLimit
            // vocabulary under wsgm.native-qam.tdp above.
            ["wsgm.native-qam.valve-tdp"] = [],

            // The brightness gate was availability-only until 2026-08-30, when the device disproved
            // its founding assumption: Steam's SetBrightness is a stub on Windows and
            // RegisterForBrightnessChanges never fires, so the revealed slider sat at a default and
            // moved nothing. WSGM is the backend now — the gate forwards the slider's writes here
            // and subscribes to this id for the level to show, so the id needs both halves.
            ["wsgm.steam-display.brightness"] = ["setBrightness"],

            // Not controls: these report when Steam's own network UI starts and stops looking for
            // networks, so WSGM can scan for exactly that long. Scanning on WSGM's own schedule
            // would either waste power or show a list that went stale while the page was open.
            ["wsgm.steam-network.gate"] = ["startScan", "stopScan"],

            // The operations Valve's own pairing UI offers, and nothing beyond them. Reads are not
            // here: GetState and the details calls are answered from the state WSGM already pushed,
            // so the panel never waits on a round trip to draw.
            ["wsgm.steam-bluetooth.service"] =
            [
                "setDiscovering",
                "pair",
                "cancelPair",
                "connect",
                "disconnect",
                "forget",
                "setTrusted",
                "setWakeAllowed",
            ],
        };

    private readonly object _sync = new();
    private readonly Dictionary<string, (long Sequence, long ActionGeneration)> _last =
        new(StringComparer.Ordinal);
    private SteamUiGenerations _generations;
    private long _lastRequestSequence;

    /// <summary>Creates an authorizer bound to one context and document generation.</summary>
    /// <param name="generations">The generations that installed the bridge.</param>
    public SteamUiBridgeAuthorizer(SteamUiGenerations generations) => _generations = generations;

    /// <summary>The exact patch and command vocabulary compiled into both bridge sides.</summary>
    public static IReadOnlyDictionary<string, string[]> AllowedCommands => Commands;

    /// <summary>Replaces the bridge generation and clears replay state.</summary>
    /// <param name="generations">The newly installed bridge generations.</param>
    public void Reset(SteamUiGenerations generations)
    {
        lock (_sync)
        {
            _generations = generations;
            _last.Clear();
            _lastRequestSequence = 0;
        }
    }

    /// <summary>Validates schema, vocabulary, generation, ordering, replay, and payload bounds.</summary>
    /// <param name="request">The decoded bridge request.</param>
    /// <returns>An explicit accept or rejection.</returns>
    public SteamUiBridgeAuthorizationResult Authorize(SteamUiBridgeRequest request)
    {
        if (request.Version != SteamUiBridgeHost.SchemaVersion)
        {
            return Reject("schema version mismatch");
        }
        if (request.Type is not ("request" or "cancel"))
        {
            return Reject("message type is not allowlisted");
        }
        if (string.IsNullOrEmpty(request.PatchId)
            || string.IsNullOrEmpty(request.Command)
            || !Commands.TryGetValue(request.PatchId, out var commands)
            || Array.IndexOf(commands, request.Command) < 0)
        {
            return Reject("patch command is not allowlisted");
        }
        if (request.Sequence <= 0 || request.ActionGeneration <= 0)
        {
            return Reject("sequence or action generation is invalid");
        }
        if (request.Payload.ValueKind == JsonValueKind.Undefined
            || request.Payload.GetRawText().Length > SteamUiBridgeHost.MaximumPayloadCharacters)
        {
            return Reject("payload exceeded its limit");
        }

        lock (_sync)
        {
            if (request.ContextGeneration != _generations.ExecutionContext
                || request.DocumentGeneration != _generations.Document)
            {
                return Reject("stale bridge generation");
            }

            var key = request.PatchId + "\n" + request.Command;
            _last.TryGetValue(key, out var previous);
            if (request.Type == "cancel")
            {
                return request.Sequence <= previous.Sequence
                    ? new SteamUiBridgeAuthorizationResult(true, null)
                    : Reject("cancel references an unknown request");
            }
            if (request.Sequence <= _lastRequestSequence || request.Sequence <= previous.Sequence)
            {
                return Reject("request sequence was replayed");
            }
            if (request.ActionGeneration <= previous.ActionGeneration)
            {
                return Reject("action generation was replayed");
            }
            _lastRequestSequence = request.Sequence;
            _last[key] = (request.Sequence, request.ActionGeneration);
            return new SteamUiBridgeAuthorizationResult(true, null);
        }
    }

    private static SteamUiBridgeAuthorizationResult Reject(string reason) => new(false, reason);
}

/// <summary>Installs and owns the versioned Runtime-binding bridge for native-QAM patches.</summary>
public sealed class SteamUiBridgeHost : IAsyncDisposable
{
    /// <summary>Current bridge schema version.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Maximum decoded payload size accepted from injected code.</summary>
    public const int MaximumPayloadCharacters = 16 * 1024;

    private const string Namespace = "__wsgmSteamUi_v1_28d7c54a";
    private const string BindingName = "__wsgmNativeBridge_v1_7b24d11c";
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);
    private readonly ISteamUiTransport _transport;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SteamUiBridgeAuthorizer _authorizer = new(default);
    private SteamUiGenerations _generations;
    private volatile bool _ready;
    private int _disposed;

    /// <summary>Creates a bridge over the process-owned persistent transport.</summary>
    /// <param name="transport">The single Steam UI transport owner.</param>
    public SteamUiBridgeHost(ISteamUiTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _transport.NotificationReceived += OnNotificationReceived;
        _transport.GenerationChanged += OnGenerationChanged;
    }

    /// <summary>Raised only after a request passes the compiled semantic allowlist.</summary>
    public event EventHandler<SteamUiBridgeRequest>? RequestReceived;

    /// <summary>Whether the bootstrap handshake is healthy for the current generation.</summary>
    public bool IsReady => _ready;

    /// <summary>Installs the Runtime binding and idempotent bootstrap for the current context.</summary>
    /// <param name="cancellationToken">Cancels installation.</param>
    /// <returns>True after a positive compatibility handshake.</returns>
    public async Task<bool> BootstrapAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = FindSharedSnapshot();
            await _transport.SetRuntimeBindingAsync(
                SteamUiTargetRole.SharedJsContext,
                BindingName,
                true,
                OperationTimeout,
                cancellationToken).ConfigureAwait(false);

            snapshot = FindSharedSnapshot();
            var source = SteamUiAssetCatalog.LoadNativeQamBootstrap();
            var configuration = BuildConfiguration(snapshot.Generations);
            var expression = source.Replace(
                "__WSGM_CONFIGURATION_JSON__", configuration, StringComparison.Ordinal);
            var result = await _transport.EvaluateAsync(
                SteamUiTargetRole.SharedJsContext,
                expression,
                OperationTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!result.Reachable || result.Value is null)
            {
                _ready = false;
                return false;
            }
            using var handshake = JsonDocument.Parse(result.Value);
            _ready = handshake.RootElement.TryGetProperty("ok", out var ok)
                && ok.ValueKind == JsonValueKind.True
                && result.Generations.ExecutionContext == snapshot.Generations.ExecutionContext
                && result.Generations.Document == snapshot.Generations.Document;
            if (_ready)
            {
                _generations = result.Generations;
                _authorizer.Reset(_generations);
            }
            return _ready;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ready = false;
            Log.Warn($"Steam UI bridge bootstrap failed: {ex.Message}");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Returns one command result to the exact current bridge generation.</summary>
    /// <param name="request">The accepted request being completed.</param>
    /// <param name="ok">Whether the command succeeded.</param>
    /// <param name="payload">A bounded JSON payload, or null.</param>
    /// <param name="error">A bounded semantic failure.</param>
    /// <param name="cancellationToken">Cancels delivery.</param>
    /// <returns>True when the current document accepted the response.</returns>
    public async Task<bool> RespondAsync(
        SteamUiBridgeRequest request,
        bool ok,
        JsonElement? payload,
        string? error,
        CancellationToken cancellationToken = default)
    {
        if (!_ready)
        {
            return false;
        }
        var json = BuildResponse(request, ok, payload, error);
        var expression = "(()=>{const b=window[" + SteamCef.JsString(Namespace)
            + "];return JSON.stringify({ok:!!(b&&b.deliver(JSON.parse("
            + SteamCef.JsString(json) + ")))});})()";
        var result = await _transport.EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            expression,
            OperationTimeout,
            cancellationToken).ConfigureAwait(false);
        return result.Reachable && result.Value?.Contains("\"ok\":true", StringComparison.Ordinal) == true;
    }

    /// <summary>Publishes immutable semantic state to subscribers of one allowlisted patch.</summary>
    /// <param name="patchId">The exact allowlisted patch identity.</param>
    /// <param name="payload">Bounded semantic state with no raw device or host data.</param>
    /// <param name="cancellationToken">Cancels delivery.</param>
    /// <returns>True when the current document accepted the state envelope.</returns>
    public async Task<bool> PublishStateAsync(
        string patchId,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        if (!_ready
            || !SteamUiBridgeAuthorizer.AllowedCommands.ContainsKey(patchId)
            || payload.GetRawText().Length > MaximumPayloadCharacters)
        {
            return false;
        }

        var json = BuildState(patchId, payload);
        var expression = "(()=>{const b=window[" + SteamCef.JsString(Namespace)
            + "];return JSON.stringify({ok:!!(b&&b.deliver(JSON.parse("
            + SteamCef.JsString(json) + ")))});})()";
        var result = await _transport.EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            expression,
            OperationTimeout,
            cancellationToken).ConfigureAwait(false);
        return result.Reachable && result.Value?.Contains("\"ok\":true", StringComparison.Ordinal) == true;
    }

    /// <summary>Removes only the WSGM-owned bridge namespace and Runtime binding.</summary>
    /// <param name="cancellationToken">Cancels cleanup.</param>
    public async Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        _ready = false;
        try
        {
            await _transport.EvaluateAsync(
                SteamUiTargetRole.SharedJsContext,
                "(()=>{const k=" + SteamCef.JsString(Namespace)
                    + ";const b=window[k];if(b&&b.dispose)b.dispose('WSGM removed');"
                    + "try{delete window[k];}catch(e){}return JSON.stringify({ok:true});})()",
                OperationTimeout,
                cancellationToken).ConfigureAwait(false);
            await _transport.SetRuntimeBindingAsync(
                SteamUiTargetRole.SharedJsContext,
                BindingName,
                false,
                OperationTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Steam UI bridge removal was incomplete: {ex.Message}");
        }
    }

    private void OnNotificationReceived(object? sender, SteamUiNotification notification)
    {
        if (!_ready
            || notification.Role != SteamUiTargetRole.SharedJsContext
            || notification.Method != "Runtime.bindingCalled"
            || notification.Generations.ExecutionContext != _generations.ExecutionContext
            || notification.Generations.Document != _generations.Document)
        {
            return;
        }
        try
        {
            using var parameters = JsonDocument.Parse(notification.ParametersJson);
            var root = parameters.RootElement;
            if (!root.TryGetProperty("name", out var name)
                || name.GetString() != BindingName
                || !root.TryGetProperty("payload", out var payloadElement)
                || payloadElement.ValueKind != JsonValueKind.String)
            {
                return;
            }
            var payload = payloadElement.GetString();
            if (payload is null || payload.Length > MaximumPayloadCharacters)
            {
                return;
            }
            var request = JsonSerializer.Deserialize(
                payload, SteamUiBridgeJsonContext.Default.SteamUiBridgeRequest);
            if (request is null)
            {
                return;
            }
            var authorization = _authorizer.Authorize(request);
            if (!authorization.Accepted)
            {
                // The payload prefix is included because the identifying fields are exactly what a
                // decoding fault empties: "rejected /: schema version mismatch" describes a request
                // that never decoded just as well as one that was genuinely refused, and telling
                // them apart took a live tap on the Runtime binding.
                Log.Change(
                    "steam.ui.bridge.rejected",
                    $"Steam UI bridge rejected {request.PatchId}/{request.Command}: "
                        + $"{authorization.Reason} (payload: "
                        + payload[..Math.Min(payload.Length, 200)] + ")",
                    "warn ");
                return;
            }
            RequestReceived?.Invoke(this, request);
        }
        catch (JsonException ex)
        {
            Log.Warn($"Steam UI bridge rejected malformed payload: {ex.Message}");
        }
    }

    private void OnGenerationChanged(object? sender, SteamUiTransportSnapshot snapshot)
    {
        if (snapshot.Role != SteamUiTargetRole.SharedJsContext)
        {
            return;
        }
        if (snapshot.Generations.ExecutionContext != _generations.ExecutionContext
            || snapshot.Generations.Document != _generations.Document)
        {
            _ready = false;
            _authorizer.Reset(snapshot.Generations);
        }
    }

    private SteamUiTransportSnapshot FindSharedSnapshot()
    {
        foreach (var snapshot in _transport.GetSnapshots())
        {
            if (snapshot.Role == SteamUiTargetRole.SharedJsContext)
            {
                return snapshot;
            }
        }
        throw new InvalidOperationException("SharedJSContext channel is not registered.");
    }

    private static string BuildConfiguration(SteamUiGenerations generations)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", SchemaVersion);
            writer.WriteString("namespace", Namespace);
            writer.WriteString("binding", BindingName);

            // The bootstrap reuses an already-installed bridge when the version and both Steam
            // generations match, and neither of those changes when WSGM is updated. So a new WSGM
            // build kept running the PREVIOUS build's injected script until Steam itself restarted:
            // a fix to the bootstrap appeared to have no effect, and the only clue was a diagnostic
            // field that was missing from output the new code would have produced. Pinning the
            // asset's own hash makes a changed script replace the bridge on the next
            // synchronization, which is what "the bootstrap was updated" has to mean.
            writer.WriteString("assetHash", SteamUiAssetCatalog.NativeQamBootstrapSha256);
            writer.WriteNumber("contextGeneration", generations.ExecutionContext);
            writer.WriteNumber("documentGeneration", generations.Document);
            writer.WriteNumber("maximumPending", 32);
            writer.WriteNumber("timeoutMilliseconds", 5000);
            writer.WriteStartObject("allowed");
            foreach (var pair in SteamUiBridgeAuthorizer.AllowedCommands)
            {
                writer.WriteStartArray(pair.Key);
                foreach (var command in pair.Value)
                {
                    writer.WriteStringValue(command);
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string BuildResponse(
        SteamUiBridgeRequest request, bool ok, JsonElement? payload, string? error)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", SchemaVersion);
            writer.WriteString("type", "response");
            writer.WriteString("patchId", request.PatchId);
            writer.WriteString("command", request.Command);
            writer.WriteNumber("sequence", request.Sequence);
            writer.WriteNumber("contextGeneration", request.ContextGeneration);
            writer.WriteNumber("documentGeneration", request.DocumentGeneration);
            writer.WriteBoolean("ok", ok);
            writer.WritePropertyName("payload");
            if (payload.HasValue)
            {
                payload.Value.WriteTo(writer);
            }
            else
            {
                writer.WriteNullValue();
            }
            if (!string.IsNullOrEmpty(error))
            {
                writer.WriteString("error", error.Length <= 1024 ? error : error[..1024]);
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private string BuildState(string patchId, JsonElement payload)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", SchemaVersion);
            writer.WriteString("type", "state");
            writer.WriteString("patchId", patchId);
            writer.WriteNumber("contextGeneration", _generations.ExecutionContext);
            writer.WriteNumber("documentGeneration", _generations.Document);
            writer.WritePropertyName("payload");
            payload.WriteTo(writer);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _transport.NotificationReceived -= OnNotificationReceived;
        _transport.GenerationChanged -= OnGenerationChanged;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await RemoveAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            // Steam may still be reachable while its document is already tearing down. Bridge
            // cleanup is best effort; it must not abort the enclosing desktop-restore sequence.
            Log.Warn("Steam UI bridge removal exceeded the shutdown budget.");
        }
        finally
        {
            _gate.Dispose();
        }
    }
}

// CamelCase because that is what the bootstrap sends and what this file's own response writers
// emit. Without it the source generator matched PascalCase, and with case-insensitivity explicitly
// off NOTHING bound: every property took its default, so Version arrived as 0 and each request was
// refused as a "schema version mismatch" with an empty patch id. Every native-QAM command had been
// rejected since the bridge was written — invisible only because no row rendered to send one.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(SteamUiBridgeRequest))]
internal sealed partial class SteamUiBridgeJsonContext : JsonSerializerContext;
