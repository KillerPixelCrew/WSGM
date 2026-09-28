using System;
using System.Threading;

namespace WSGM.Shell;

/// <summary>Turns a plugin lifecycle deadline into the cancellation token that enforces it.</summary>
internal static class LifecycleDeadline
{
    /// <summary>A token that cancels at <paramref name="deadline" /> or when any of the tokens does.</summary>
    /// <param name="deadline">When the operation must have finished.</param>
    /// <param name="tokens">Further tokens that cancel it.</param>
    /// <returns>The source; the caller disposes it. It is already cancelled when the deadline has passed.</returns>
    internal static CancellationTokenSource Token(DateTimeOffset deadline, params CancellationToken[] tokens)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(tokens);
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            source.Cancel();
        }
        else
        {
            source.CancelAfter(remaining);
        }

        return source;
    }
}
