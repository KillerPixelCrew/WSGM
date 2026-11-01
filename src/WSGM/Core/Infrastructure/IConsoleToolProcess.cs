using System;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>
///     Narrow owned-process surface used to verify bounded console-tool cleanup without
///     starting a live operating-system command from tests.
/// </summary>
internal interface IConsoleToolProcess : IDisposable
{
    /// <summary>Gets the process exit code after exit.</summary>
    int ExitCode { get; }

    /// <summary>Captures stdout and stderr concurrently.</summary>
    /// <returns>A task containing captured stdout followed by stderr; concurrent stream order is not preserved.</returns>
    Task<string> ReadOutputAsync();

    /// <summary>Waits for the exact process to exit.</summary>
    /// <param name="cancellationToken">Cancels this wait; the owner separately decides whether to kill the process.</param>
    /// <returns>A task completing when the owned process exits.</returns>
    /// <exception cref="OperationCanceledException">The wait was canceled.</exception>
    Task WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Requests termination of the exact process and its descendants.</summary>
    void KillTree();
}
