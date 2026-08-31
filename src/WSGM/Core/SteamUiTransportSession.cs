using System;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Outcome of one repository-owned Steam UI evaluation.</summary>
/// <param name="Reachable">Whether a validated Steam target accepted the request.</param>
/// <param name="Value">The by-value JavaScript result.</param>
/// <param name="Error">Why the target or expression was unavailable.</param>
internal readonly record struct CefEvalResult(bool Reachable, string? Value, string? Error)
{
    internal static CefEvalResult Ok(string? value) => new(true, value, null);

    internal static CefEvalResult Unreachable(string error) => new(false, null, error);
}

/// <summary>
/// Exposes the shell session's sole Steam UI transport to repository-owned one-shot operations.
/// </summary>
/// <remarks>
/// This is session ownership, not a transport: it cannot discover a target or open a socket. The
/// attached <see cref="PersistentSteamUiTransport"/> remains the only CDP implementation, so
/// artwork, collections, launch configuration, downloads and resident patches share generations,
/// reference counting and the CEF master switch.
/// </remarks>
internal static class SteamUiTransportSession
{
    private static readonly object Gate = new();
    private static PersistentSteamUiTransport? _transport;
    private static volatile bool _enabled = true;

    internal static bool Enabled => _enabled;

    internal static void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        lock (Gate)
        {
            _transport?.SetEnabled(enabled);
        }
    }

    internal static void Attach(PersistentSteamUiTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        lock (Gate)
        {
            if (_transport is not null && !ReferenceEquals(_transport, transport))
            {
                throw new InvalidOperationException("A Steam UI transport is already attached.");
            }
            _transport = transport;
            transport.SetEnabled(_enabled);
        }
    }

    internal static void Detach(PersistentSteamUiTransport transport)
    {
        lock (Gate)
        {
            if (ReferenceEquals(_transport, transport))
            {
                _transport = null;
            }
        }
    }

    internal static Task<CefEvalResult> EvaluateAsync(
        string expression,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            expression,
            timeout,
            cancellationToken);

    internal static Task<CefEvalResult> EvaluateOnVisibleWindowAsync(
        string expression,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        EvaluateAsync(SteamUiTargetRole.MainWindow, expression, timeout, cancellationToken);

    private static async Task<CefEvalResult> EvaluateAsync(
        SteamUiTargetRole role,
        string expression,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            return CefEvalResult.Unreachable("Steam CEF integration disabled in settings.");
        }
        try
        {
            PersistentSteamUiTransport? transport;
            lock (Gate)
            {
                transport = _transport;
            }
            if (transport is null)
            {
                return CefEvalResult.Unreachable("Steam UI transport is not active.");
            }

            SteamUiEvaluationResult result = await transport.EvaluateAsync(
                    role,
                    expression,
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false);
            return result.Reachable
                ? CefEvalResult.Ok(result.Value)
                : CefEvalResult.Unreachable(
                    result.Error ?? $"Steam UI {role} target is unavailable.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CefEvalResult.Unreachable("Steam CEF evaluation cancelled.");
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
}
