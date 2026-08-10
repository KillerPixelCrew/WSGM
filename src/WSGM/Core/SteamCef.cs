using System;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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

    /// <summary>Writes the CEF remote-debugging flag into the Steam directory when
    /// it is missing so Steam opens its localhost devtools port on next start.
    /// Idempotent and best-effort; returns whether the flag is present afterwards.</summary>
    public static bool EnsureRemoteDebuggingEnabled()
    {
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
        EnsureRemoteDebuggingEnabled();
        try
        {
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
        catch (OperationCanceledException)
        {
            return CefEvalResult.Unreachable("Timed out talking to Steam's debug port.");
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
                return url.GetString();
            }
        }
        Log.Warn($"Steam CEF: {which} target not found.");
        return null;
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
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }
                builder.Append(Encoding.UTF8.GetString(buffer, 0, received.Count));
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
            return null;
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
