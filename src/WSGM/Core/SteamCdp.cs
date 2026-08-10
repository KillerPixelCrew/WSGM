using System;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Result of a live library add through Steam's own client.</summary>
public enum SteamLibraryAddStatus
{
    /// <summary>Steam adopted the folder as a new library.</summary>
    Added,
    /// <summary>The drive already carries a Steam library (nothing to do).</summary>
    AlreadyPresent,
    /// <summary>Steam actively refused the folder; <c>Detail</c> is its reason.</summary>
    Rejected,
    /// <summary>The debug channel could not be reached, so no live add happened.</summary>
    Unavailable,
}

/// <summary>Outcome of a live library add.</summary>
/// <param name="Status">What Steam did.</param>
/// <param name="Detail">Steam's reason code, when it gave one.</param>
public readonly record struct SteamLibraryAddResult(SteamLibraryAddStatus Status, string? Detail);

/// <summary>Adds a Steam library to the RUNNING client by driving Steam's own
/// front-end API over its CEF remote-debugging port: a WebSocket
/// <c>Runtime.evaluate</c> calls <c>SteamClient.InstallFolder.AddInstallFolder</c>,
/// so Steam adopts, persists, mounts and scans the folder on its own thread with no
/// restart. This is version-proof (no binary offsets) and safe (Steam performs the
/// operation), unlike poking the client's internals in-process.
///
/// Steam only opens the port when it starts with the
/// <c>.cef-enable-remote-debugging</c> flag file present, which
/// <see cref="EnsureRemoteDebuggingEnabled"/> writes. In game mode WSGM sets it
/// before launching Steam, so the port is always up; on an already-running desktop
/// Steam that started without it, the flag takes effect on Steam's next start and
/// the caller falls back to a config-file registration.</summary>
public static class SteamCdp
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

    /// <summary>Blocking wrapper for worker-thread callers (never call on the UI thread).</summary>
    /// <param name="libraryPath">The library folder, e.g. <c>E:\SteamLibrary</c>.</param>
    /// <param name="label">A label to apply after adding, or null/empty for none.</param>
    public static SteamLibraryAddResult AddLibrary(string libraryPath, string? label = null)
        => AddLibraryAsync(libraryPath, label).GetAwaiter().GetResult();

    /// <summary>Adds <paramref name="libraryPath"/> to the live Steam client and,
    /// on success, labels it.</summary>
    /// <param name="libraryPath">The library folder, e.g. <c>E:\SteamLibrary</c>.</param>
    /// <param name="label">A label to apply after adding, or null/empty for none.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public static async Task<SteamLibraryAddResult> AddLibraryAsync(
        string libraryPath, string? label = null, CancellationToken cancellationToken = default)
    {
        // Always leave the flag set so a later Steam start has the port, even when
        // this attempt cannot reach it now.
        EnsureRemoteDebuggingEnabled();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var token = timeout.Token;

            var socketUrl = await GetSharedJsContextSocketAsync(token).ConfigureAwait(false);
            if (socketUrl is null)
            {
                return new SteamLibraryAddResult(
                    SteamLibraryAddStatus.Unavailable, "Steam debug port not reachable.");
            }

            var value = await EvaluateAsync(socketUrl, BuildAddExpression(libraryPath, label), token)
                .ConfigureAwait(false);
            return Interpret(value);
        }
        catch (OperationCanceledException)
        {
            return new SteamLibraryAddResult(
                SteamLibraryAddStatus.Unavailable, "Timed out talking to Steam's debug port.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam live library add failed: {ex.Message}");
            return new SteamLibraryAddResult(SteamLibraryAddStatus.Unavailable, ex.Message);
        }
    }

    /// <summary>Finds the WebSocket URL of Steam's SharedJSContext — the page that
    /// exposes the global <c>SteamClient</c> object.</summary>
    private static async Task<string?> GetSharedJsContextSocketAsync(CancellationToken token)
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
            if (target.TryGetProperty("title", out var title)
                && title.ValueKind == JsonValueKind.String
                && title.GetString() == "SharedJSContext"
                && target.TryGetProperty("webSocketDebuggerUrl", out var url)
                && url.ValueKind == JsonValueKind.String)
            {
                return url.GetString();
            }
        }
        Log.Warn("Steam CEF: SharedJSContext target not found.");
        return null;
    }

    /// <summary>Builds the JS that adds the folder, labels it when a label is
    /// given, and reports the outcome as a JSON string. Both the path and label are
    /// JSON-encoded into JS string literals — a raw path would lose its backslashes
    /// and Steam would reject the malformed path.</summary>
    private static string BuildAddExpression(string libraryPath, string? label)
    {
        var pathLiteral = "\"" + JsonEncodedText.Encode(libraryPath) + "\"";
        var labelLiteral = string.IsNullOrEmpty(label)
            ? "null"
            : "\"" + JsonEncodedText.Encode(label) + "\"";
        return
            "(async()=>{try{const i=await SteamClient.InstallFolder.AddInstallFolder(" +
            pathLiteral + ");const l=" + labelLiteral + ";" +
            "if(l!==null&&typeof i==='number'&&i>=0){" +
            "try{await SteamClient.InstallFolder.SetFolderLabel(i,l);}catch(e){}}" +
            "return JSON.stringify({ok:true,index:i});}" +
            "catch(e){return JSON.stringify({ok:false,result:(e&&e.result),message:(e&&e.message)});}})()";
    }

    /// <summary>Opens a CDP WebSocket, evaluates <paramref name="expression"/> with
    /// promise awaiting, and returns the by-value string result.</summary>
    private static async Task<string?> EvaluateAsync(
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

    /// <summary>Maps Steam's JSON reply to a result. Success carries no reason;
    /// <c>DriveAlreadyHasLibrary</c> is treated as already-present; anything else is
    /// a genuine rejection with Steam's own reason code.</summary>
    private static SteamLibraryAddResult Interpret(string? jsonValue)
    {
        if (jsonValue is null)
        {
            return new SteamLibraryAddResult(
                SteamLibraryAddStatus.Unavailable, "No response from Steam.");
        }
        try
        {
            using var document = JsonDocument.Parse(jsonValue);
            var root = document.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                Log.Info("Steam library added to the live client.");
                return new SteamLibraryAddResult(SteamLibraryAddStatus.Added, null);
            }

            var message = root.TryGetProperty("message", out var reason)
                && reason.ValueKind == JsonValueKind.String
                ? reason.GetString()
                : null;

            if (string.Equals(message, "DriveAlreadyHasLibrary", StringComparison.Ordinal))
            {
                return new SteamLibraryAddResult(SteamLibraryAddStatus.AlreadyPresent, message);
            }
            Log.Warn($"Steam rejected the library add: {message ?? "unknown reason"}.");
            return new SteamLibraryAddResult(SteamLibraryAddStatus.Rejected, message);
        }
        catch (Exception ex)
        {
            return new SteamLibraryAddResult(SteamLibraryAddStatus.Unavailable, ex.Message);
        }
    }
}
