using System;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>Admits one recovery after Steam's CEF listener remains closed through its startup grace.</summary>
/// <remarks>The Steam gate worker is the sole owner. An unsuccessful recovery cannot start a restart loop.</remarks>
internal sealed class SteamCefActivationPolicy
{
    internal static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(30);

    private TimeSpan? _closedSince;
    private bool _attempted;

    /// <summary>Records a passive observation and claims recovery when the closed-listener grace expires.</summary>
    /// <param name="enabled">Whether the master Steam CEF integration is enabled.</param>
    /// <param name="steamRunning">Whether the Steam client is running.</param>
    /// <param name="transitionPending">Whether a session transition currently owns Steam.</param>
    /// <param name="state">The passively verified state of Steam's debugging listener.</param>
    /// <param name="now">Monotonic elapsed time from the owning worker's clock.</param>
    /// <returns>True once per uninterrupted failure, with the attempt already claimed.</returns>
    internal bool ShouldRecover(bool enabled, bool steamRunning, bool transitionPending,
        SteamCefDebuggingState state, TimeSpan now)
    {
        if (!enabled || state == SteamCefDebuggingState.Open)
        {
            _closedSince = null;
            _attempted = false;
            return false;
        }

        if (!steamRunning || transitionPending || state != SteamCefDebuggingState.Closed)
        {
            // A scheduled launch can return before the replacement appears. Its absence must
            // not rearm an already claimed recovery while that launch is still unresolved.
            _closedSince = null;
            return false;
        }

        _closedSince ??= now;
        if (_attempted || now - _closedSince.Value < StartupGrace)
        {
            return false;
        }

        // Claim before the caller starts asynchronous recovery. A failed restart stays bounded.
        _attempted = true;
        return true;
    }
}
