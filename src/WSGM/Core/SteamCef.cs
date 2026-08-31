using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>
/// Owns only Steam's remote-debugging opt-in and pure JavaScript/endpoint validation helpers.
/// </summary>
public static class SteamCef
{
    private const int DebugPort = 8080;
    private const string FlagFileName = ".cef-enable-remote-debugging";

    /// <summary>Writes the CEF remote-debugging flag into the Steam directory when
    /// it is missing so Steam opens its localhost devtools port on next start.
    /// Idempotent and best-effort; returns whether the flag is present afterwards.</summary>
    public static bool EnsureRemoteDebuggingEnabled()
    {
        // Master switch off: never write the debug flag. An existing flag (from the
        // user or a prior run) is deliberately left in place — AGENTS invariant 8
        // forbids deleting the shared file; we simply stop opting Steam into it.
        if (!SteamUiTransportSession.Enabled)
        {
            return false;
        }
        try
        {
            var steamDirectory = Steam.InstallDirectory;
            if (steamDirectory is null)
            {
                return false;
            }
            var flag = Path.Combine(steamDirectory, FlagFileName);
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

    /// <summary>Builds a JS string literal (with surrounding quotes) for embedding
    /// a value into an expression. JSON-encoding is mandatory — a raw path would
    /// lose its backslashes.</summary>
    /// <param name="value">The string to embed.</param>
    public static string JsString(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";

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

    /// <summary>Decides whether the debug port is owned by Steam, given a listener table
    /// and a pid→process-name lookup. A loopback listener is preferred over a wildcard one
    /// so a 127.0.0.1 squatter cannot hide behind Steam's own <c>[::]</c>/<c>0.0.0.0</c> row.
    /// <para>A pure seam over the live check so the hardening is regression-testable.</para></summary>
    /// <param name="listeners">The TCP listener table, or null when it could not be read.</param>
    /// <param name="processName">Resolves a pid to its process name, or null if it is gone.</param>
    /// <param name="reason">
    /// What was observed, whatever the verdict, ready to be logged verbatim by the caller.
    /// </param>
    /// <returns><see langword="true"/> only when a Steam process owns the port.</returns>
    /// <remarks>
    /// The reason is an out-parameter rather than a log call because this is a pure seam and has to
    /// stay one, and because the caller was otherwise left inventing a cause: it logged "listener
    /// is not a Steam process" for all four outcomes, including the ordinary one where Steam simply
    /// had not started yet. A wrong reason is worse than none — that line sent a maintainer hunting
    /// a squatter on a port that nothing was listening on.
    /// </remarks>
    internal static bool IsSteamPortOwner(IReadOnlyList<NativeTcp.Listener>? listeners,
        Func<int, string?> processName,
        out string reason)
    {
        if (listeners is null)
        {
            // A failed table read is NOT "nothing is listening" — reporting it as such
            // once sent the maintainer hunting a closed Steam port that was open.
            reason = $"the TCP listener table was unavailable, so the owner of port {DebugPort} "
                + "could not be verified";
            return false;
        }
        var candidates = listeners
            .Where(l => l.Port == DebugPort)
            .OrderBy(l => l.LocalAddress == NativeTcp.Loopback ? 0 : 1)
            .ToList();
        if (candidates.Count == 0)
        {
            reason = $"nothing is listening on port {DebugPort}";
            return false;
        }
        int unidentified = 0;
        foreach (var listener in candidates)
        {
            var name = processName(listener.ProcessId);
            if (name is null)
            {
                // The owner exited between the table read and the lookup; that row is
                // stale, so keep scanning rather than treating it as a verdict.
                unidentified++;
                continue;
            }
            if (name is "steamwebhelper" or "steam")
            {
                reason = $"port {DebugPort} is owned by {name} (pid {listener.ProcessId})";
                return true;
            }
            // Decisive, not just logged: we connect to 127.0.0.1, and Windows routes
            // that to the MOST SPECIFIC binding — the loopback listener sorted first
            // above. If that one is not Steam, then Steam's own wildcard row further
            // down the list belongs to a socket our connect never reaches, and
            // continuing the scan would clear a squatter sitting in front of Steam.
            reason = $"port {DebugPort} is owned by {name} (pid {listener.ProcessId}), not Steam";
            return false;
        }

        // Every candidate's owning process exited during lookup. That is distinct from a confirmed
        // non-Steam owner and must remain diagnosable.
        reason = $"{unidentified} listener(s) on port {DebugPort} could not be attributed to a "
            + "running process";
        return false;
    }

}
