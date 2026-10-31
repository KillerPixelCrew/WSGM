using System;
using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>Reports a list of collected failures as one exception.</summary>
internal static class ExceptionList
{
    /// <summary>The single failure itself, or an aggregate of several.</summary>
    /// <param name="failures">The collected failures.</param>
    /// <param name="multipleMessage">The aggregate's message, or null for the default one.</param>
    /// <returns>The exception to report.</returns>
    /// <remarks>
    ///     A single failure is not wrapped in a one-element aggregate, because the message a
    ///     maintainer reads in the log is the inner one.
    /// </remarks>
    internal static Exception Combine(this IReadOnlyList<Exception> failures, string? multipleMessage = null)
    {
        return failures.Count == 1
            ? failures[0]
            : multipleMessage is null
                ? new AggregateException(failures)
                : new AggregateException(multipleMessage, failures);
    }
}
