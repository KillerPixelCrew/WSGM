// SPDX-License-Identifier: MIT

using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Plugin.Gpu;

/// <summary>
///     Whether one operation may still reach a native setter. Every setter checks it immediately before its
///     native call, after any preparatory reads.
/// </summary>
internal readonly struct WriteAdmission(CancellationToken token, Deadline deadline, Func<bool> admitted)
{
    internal bool Admitted => !token.IsCancellationRequested && !deadline.HasExpired && admitted();

    internal void Check()
    {
        if (!Admitted)
        {
            throw new DriverFailure("The GPU write is no longer admitted (cancelled, expired or stopped).");
        }
    }
}
