// SPDX-License-Identifier: MIT

using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Plugin.Gpu;

/// <summary>
///     Whether one operation may still reach a native setter. Every setter checks it immediately before its
///     native call, after any preparatory reads.
/// </summary>
/// <param name="token">Caller cancellation checked before dispatch.</param>
/// <param name="deadline">Operation deadline checked before dispatch.</param>
/// <param name="admitted">Current session admission check; no native setter may bypass it.</param>
internal readonly struct WriteAdmission(CancellationToken token, Deadline deadline, Func<bool> admitted)
{
    /// <summary>Whether cancellation, deadline and the current session all permit dispatch.</summary>
    internal bool Admitted => !token.IsCancellationRequested && !deadline.HasExpired && admitted();

    /// <summary>Refuses a setter before dispatch when its operation is no longer admitted.</summary>
    /// <exception cref="DriverFailure">Cancellation, deadline or session shutdown closed admission.</exception>
    internal void Check()
    {
        if (!Admitted)
        {
            throw new DriverFailure("The GPU write is no longer admitted (cancelled, expired or stopped).");
        }
    }
}
