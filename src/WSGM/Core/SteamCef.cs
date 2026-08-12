using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>Outcome of a CEF evaluation: whether the devtools channel was
/// reachable at all (distinct from a JS result that happens to be null), the
/// by-value string result, and a reason when unreachable.</summary>
/// <param name="Reachable">True when Steam's debug port answered and the
/// expression was evaluated.</param>
/// <param name="Value">The string the expression resolved to (expressions here
/// return <c>JSON.stringify(...)</c>), or null.</param>
/// <param name="Error">Why the channel was unreachable, when it was not.</param>
public readonly record struct CefEvalResult(bool Reachable, string? Value, string? Error)
{
    /// <summary>A reachable evaluation carrying its string value.</summary>
    public static CefEvalResult Ok(string? value) => new(true, value, null);

    /// <summary>An unreachable channel with a reason.</summary>
    public static CefEvalResult Unreachable(string error) => new(false, null, error);
}

/// <summary>Shared plumbing for driving Steam's front-end through its CEF
/// remote-debugging port: enable the flag, find the SharedJSContext page (which
/// exposes the global <c>SteamClient</c>, <c>collectionStore</c>, <c>appStore</c>,
/// …), and evaluate one-shot JS against it. Callers build a JS expression that
/// returns <c>JSON.stringify(result)</c> and parse the value with
/// <see cref="JsonDocument"/>. Both <see cref="SteamCdp"/> (library folders) and
/// <see cref="SteamCollections"/> (library tabs) sit on this.</summary>
public static class SteamCef
{
    private const int DebugPort = 8080;
    private const string FlagFileName = ".cef-enable-remote-debugging";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    // Master CEF switch (AppConfig.Cef.Enabled), mirrored here because this class is
    // static and stateless while every injection path funnels through it. Defaults
    // true so any code path that runs before the shell applies config behaves
    // exactly as before. Set live from the config-apply paths (ShellSession /
    // OverlayController) since a runtime reload replaces the config wholesale.
    private static volatile bool _masterEnabled = true;

    /// <summary>Mirrors <c>AppConfig.Cef.Enabled</c>. When false, WSGM neither writes
    /// the CEF debug flag nor attempts any injection — every <see cref="EvaluateAsync"/>
    /// call returns unreachable. Read live per call, never captured.</summary>
    public static void SetMasterEnabled(bool enabled) => _masterEnabled = enabled;

    /// <summary>Writes the CEF remote-debugging flag into the Steam directory when
    /// it is missing so Steam opens its localhost devtools port on next start.
    /// Idempotent and best-effort; returns whether the flag is present afterwards.</summary>
    public static bool EnsureRemoteDebuggingEnabled()
    {
        // Master switch off: never write the debug flag. An existing flag (from the
        // user or a prior run) is deliberately left in place — AGENTS invariant 8
        // forbids deleting the shared file; we simply stop opting Steam into it.
        if (!_masterEnabled)
        {
            return false;
        }
        try
        {
            var steamExe = Steam.ExePath;
            if (steamExe is null)
            {
                return false;
            }
            var flag = Path.Combine(Path.GetDirectoryName(steamExe)!, FlagFileName);
            if (!File.Exists(flag))
            {
                File.WriteAllBytes(flag, Array.Empty<byte>());
                Log.Info($"Steam CEF remote-debugging enabled ({flag}).");
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not enable Steam CEF remote-debugging: {ex.Message}");
            return false;
        }
    }

    /// <summary>Evaluates <paramref name="expression"/> in Steam's SharedJSContext
    /// with promise awaiting, returning the by-value string result. Always leaves
    /// the debug flag set so a later Steam start has the port even when this
    /// attempt cannot reach it now. Never throws — failures surface as an
    /// unreachable result.</summary>
    /// <param name="expression">A JS expression that resolves to a string
    /// (typically <c>JSON.stringify(...)</c>).</param>
    /// <param name="timeout">The overall budget for the exchange.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public static Task<CefEvalResult> EvaluateAsync(
        string expression, TimeSpan timeout, CancellationToken cancellationToken = default)
        => EvaluateOnAsync(WhichTarget.SharedJsContext, expression, timeout, cancellationToken);

    /// <summary>Evaluates in Steam's VISIBLE main window (the Big Picture library in game
    /// mode), which — unlike <c>SharedJSContext</c> — has the real, rendered library DOM.
    /// This is the target for the current-game route and any injected in-page UI
    /// (<see cref="SteamPageBridge"/>); the stores (<c>collectionStore</c>/<c>appStore</c>)
    /// are NOT here, so data work must still use <see cref="EvaluateAsync"/>.</summary>
    /// <param name="expression">A JS expression resolving to a string.</param>
    /// <param name="timeout">The overall budget.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public static Task<CefEvalResult> EvaluateOnVisibleWindowAsync(
        string expression, TimeSpan timeout, CancellationToken cancellationToken = default)
        => EvaluateOnAsync(WhichTarget.VisibleWindow, expression, timeout, cancellationToken);

    private enum WhichTarget
    {
        SharedJsContext,
        VisibleWindow,
    }

    private static async Task<CefEvalResult> EvaluateOnAsync(
        WhichTarget which, string expression, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Fail closed: the single choke point every injection flows through, so one
        // check here disables all CEF activity when the master switch is off.
        if (!_masterEnabled)
        {
            return CefEvalResult.Unreachable("Steam CEF integration disabled in settings.");
        }
        try
        {
            await Task.Run(EnsureRemoteDebuggingEnabled, cancellationToken).ConfigureAwait(false);
            using var timed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timed.CancelAfter(timeout);
            var token = timed.Token;

            var socketUrl = await GetSocketAsync(which, token).ConfigureAwait(false);
            if (socketUrl is null)
            {
                return CefEvalResult.Unreachable("Steam debug port not reachable.");
            }
            var value = await EvaluateOnSocketAsync(socketUrl, expression, token)
                .ConfigureAwait(false);
            return CefEvalResult.Ok(value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The CALLER cancelled (a debounced order push, the boot sync, shutdown) —
            // not a Steam fault. Still a value, because this method never throws, but
            // with its own reason so a device log does not read as a port timeout.
            return CefEvalResult.Unreachable("Steam CEF evaluation cancelled.");
        }
        catch (OperationCanceledException)
        {
            return CefEvalResult.Unreachable("Timed out talking to Steam's debug port.");
        }
        catch (InvalidDataException ex)
        {
            // Steam answered but the expression did not produce a value: a CDP protocol
            // error, or a JS exception (a renamed SteamClient API after a Steam UI
            // update). That is REACHABLE-but-no-result, which every caller already
            // renders as "No response from Steam." — collapsing it into unreachable
            // would report a broken injection as a closed Steam. The message carries
            // Steam's own error/exceptionDetails text into the only remote log surface.
            Log.Warn($"{ex.Message} (target {which}).");
            return CefEvalResult.Ok(null);
        }
        catch (Exception ex)
        {
            return CefEvalResult.Unreachable(ex.Message);
        }
    }

    /// <summary>Builds a JS string literal (with surrounding quotes) for embedding
    /// a value into an expression. JSON-encoding is mandatory — a raw path would
    /// lose its backslashes.</summary>
    /// <param name="value">The string to embed.</param>
    public static string JsString(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";

    private static async Task<string?> GetSocketAsync(WhichTarget which, CancellationToken token)
    {
        if (!IsSteamPortOwner())
        {
            Log.Warn($"Steam CEF refused port {DebugPort}: listener is not a Steam process.");
            return null;
        }
        string json;
        try
        {
            json = await Http.GetStringAsync(
                $"http://127.0.0.1:{DebugPort}/json/list", token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Info($"Steam CEF port {DebugPort} not reachable: {ex.Message}");
            return null;
        }

        using var document = JsonDocument.Parse(json);
        foreach (var target in document.RootElement.EnumerateArray())
        {
            if (Matches(which, target)
                && target.TryGetProperty("webSocketDebuggerUrl", out var url)
                && url.ValueKind == JsonValueKind.String)
            {
                var candidate = url.GetString();
                if (IsAllowedDebuggerUrl(candidate))
                {
                    return candidate;
                }
                Log.Warn($"Steam CEF rejected non-local debugger URL: {candidate}.");
            }
        }
        Log.Warn($"Steam CEF: {which} target not found.");
        return null;
    }

    /// <summary>Decides whether a <c>webSocketDebuggerUrl</c> from <c>/json/list</c> may
    /// be connected to. A squatter answering the HTTP probe could otherwise redirect the
    /// CDP client anywhere, so the URL must stay loopback WebSocket on the same port.
    /// <para>A pure seam over the live check so the hardening is regression-testable.</para></summary>
    /// <param name="candidate">The URL exactly as Steam's target list reported it.</param>
    /// <returns><see langword="true"/> when the URL is safe to connect to.</returns>
    internal static bool IsAllowedDebuggerUrl(string? candidate) =>
        Uri.TryCreate(candidate, UriKind.Absolute, out var socketUri)
        && socketUri.Scheme is "ws" or "wss"
        && (socketUri.Host == "127.0.0.1" || socketUri.Host == "localhost")
        && socketUri.Port == DebugPort;

    // Reads the TCP listener table directly instead of parsing netstat: netstat's
    // STATE column is localized, so matching the literal "LISTENING" fails closed on
    // every non-English Windows and takes the whole Steam integration down with it.
    private static bool IsSteamPortOwner() =>
        IsSteamPortOwner(NativeTcp.ListListeners(), static pid =>
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return process.ProcessName;
            }
            catch (Exception ex)
            {
                // The owner exited between the table read and the lookup; keep
                // scanning rather than treating one stale row as a verdict.
                Log.Warn($"Steam CEF listener ownership check failed: {ex.Message}");
                return null;
            }
        });

    /// <summary>Decides whether the debug port is owned by Steam, given a listener table
    /// and a pid→process-name lookup. A loopback listener is preferred over a wildcard one
    /// so a 127.0.0.1 squatter cannot hide behind Steam's own <c>[::]</c>/<c>0.0.0.0</c> row.
    /// <para>A pure seam over the live check so the hardening is regression-testable.</para></summary>
    /// <param name="listeners">The listener table, or null when it could not be read.</param>
    /// <param name="processName">Resolves a pid to its process name, or null if it is gone.</param>
    /// <returns><see langword="true"/> only when a Steam process owns the port.</returns>
    internal static bool IsSteamPortOwner(IReadOnlyList<NativeTcp.Listener>? listeners,
        Func<int, string?> processName)
    {
        if (listeners is null)
        {
            // A failed table read is NOT "nothing is listening" — reporting it as such
            // once sent the maintainer hunting a closed Steam port that was open.
            Log.Warn($"Steam CEF: TCP listener table unavailable; "
                + $"cannot verify the owner of port {DebugPort}.");
            return false;
        }
        var candidates = listeners
            .Where(l => l.Port == DebugPort)
            .OrderBy(l => l.LocalAddress == NativeTcp.Loopback ? 0 : 1)
            .ToList();
        if (candidates.Count == 0)
        {
            Log.Info($"Steam CEF: nothing is listening on port {DebugPort}.");
            return false;
        }
        foreach (var listener in candidates)
        {
            var name = processName(listener.ProcessId);
            if (name is null)
            {
                // The owner exited between the table read and the lookup; that row is
                // stale, so keep scanning rather than treating it as a verdict.
                continue;
            }
            if (name is "steamwebhelper" or "steam")
            {
                return true;
            }
            // Decisive, not just logged: we connect to 127.0.0.1, and Windows routes
            // that to the MOST SPECIFIC binding — the loopback listener sorted first
            // above. If that one is not Steam, then Steam's own wildcard row further
            // down the list belongs to a socket our connect never reaches, and
            // continuing the scan would clear a squatter sitting in front of Steam.
            Log.Warn($"Steam CEF: port {DebugPort} is owned by "
                + $"{name} (pid {listener.ProcessId}), not Steam.");
            return false;
        }
        return false;
    }

    private static bool Matches(WhichTarget which, JsonElement target)
    {
        var title = target.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? "" : "";
        if (which == WhichTarget.SharedJsContext)
        {
            return title == "SharedJSContext";
        }

        // The visible main window (device-observed): a real page carrying the window
        // create flags, not the headless SharedJSContext, not a browserview popup
        // (QuickAccess/MainMenu/notification toasts) and not a submenu (openerid). In
        // game mode this resolves to the localized "Big Picture" window; matching by
        // shape rather than the localized title keeps it language-independent.
        var type = target.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String
            ? ty.GetString() ?? "" : "";
        var url = target.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
            ? u.GetString() ?? "" : "";
        return type == "page"
            && title != "SharedJSContext"
            && url.Contains("createflags", StringComparison.Ordinal)
            && !url.Contains("openerid", StringComparison.Ordinal)
            && !url.Contains("browserviewpopup", StringComparison.Ordinal);
    }

    private static async Task<string?> EvaluateOnSocketAsync(
        string socketUrl, string expression, CancellationToken token)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(socketUrl), token).ConfigureAwait(false);

        await socket.SendAsync(
            BuildEvaluateRequest(expression), WebSocketMessageType.Text, true, token)
            .ConfigureAwait(false);

        var buffer = new byte[16384];
        var builder = new StringBuilder();
        while (true)
        {
            builder.Clear();
            var decoder = Encoding.UTF8.GetDecoder();
            var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }
                var count = decoder.GetChars(
                    buffer, 0, received.Count, chars, 0, received.EndOfMessage);
                builder.Append(chars, 0, count);
            }
            while (!received.EndOfMessage);

            using var document = JsonDocument.Parse(builder.ToString());
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.Number || id.GetInt32() != 1)
            {
                // A protocol event, not our reply — keep reading.
                continue;
            }
            if (root.TryGetProperty("result", out var outer)
                && outer.TryGetProperty("result", out var inner)
                && inner.TryGetProperty("value", out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
            var detail = root.TryGetProperty("error", out var protocolError)
                ? protocolError.GetRawText()
                : root.TryGetProperty("result", out var result)
                    && result.TryGetProperty("exceptionDetails", out var exception)
                    ? exception.GetRawText()
                    : "missing by-value string result";
            throw new InvalidDataException($"Steam CEF evaluation failed: {detail}");
        }
    }

    private static byte[] BuildEvaluateRequest(string expression)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", 1);
            writer.WriteString("method", "Runtime.evaluate");
            writer.WriteStartObject("params");
            writer.WriteString("expression", expression);
            writer.WriteBoolean("awaitPromise", true);
            writer.WriteBoolean("returnByValue", true);
            writer.WriteBoolean("userGesture", true);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
}
