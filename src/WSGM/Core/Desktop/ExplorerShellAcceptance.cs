namespace WSGM.Core;

/// <summary>Result of applying the normal-shell acceptance policy.</summary>
/// <param name="Accepted">Whether the process may serve.</param>
/// <param name="Rejection">Why it may not, or <see cref="ExplorerShellRejection.None" />.</param>
/// <param name="JobBoundLikeSource">
///     Accepted only because the source Explorer was job-bound too; the resulting desktop is degraded.
/// </param>
internal readonly record struct ExplorerShellAcceptance(
    bool Accepted,
    ExplorerShellRejection Rejection,
    bool JobBoundLikeSource = false);
