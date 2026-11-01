using System;
using System.Collections.Generic;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Complete retained outcome of controller handoff, handheld stop, detach, and disposal.</summary>
/// <param name="Failures">All retained cleanup failures; an empty list means no stage reported an unverified outcome.</param>
internal sealed record DeviceClientTeardownResult(IReadOnlyList<Exception> Failures)
{
    /// <summary>Shared result for cleanup with no reported failures.</summary>
    internal static DeviceClientTeardownResult Clean { get; } = new([]);

    /// <summary>Whether no cleanup stage reported failure; this does not add independent hardware evidence.</summary>
    internal bool Verified => Failures.Count == 0;

    /// <summary>Combines retained cleanup failures for reporting without rerunning their operations.</summary>
    /// <returns>The sole failure, or a combined exception describing multiple failures.</returns>
    internal Exception ToException()
    {
        return Failures.Combine("Multiple device teardown steps were unverified.");
    }
}
