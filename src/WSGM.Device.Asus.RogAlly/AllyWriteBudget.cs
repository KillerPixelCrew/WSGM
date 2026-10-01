// SPDX-License-Identifier: MIT

using System;
using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Device.Asus.RogAlly;

/// <summary>The one minimum budget required before any hardware write.</summary>
internal static class AllyWriteBudget
{
    private static readonly TimeSpan Minimum = TimeSpan.FromSeconds(2);

    public static bool IsAvailable(Deadline deadline)
    {
        return deadline.Remaining >= Minimum;
    }

    /// <summary>Throws <see cref="AllyBudgetException" /> when the deadline leaves too little time.</summary>
    public static void Require(Deadline deadline, string operation)
    {
        if (!IsAvailable(deadline))
        {
            throw new AllyBudgetException($"Insufficient budget for {operation}.");
        }
    }
}

/// <summary>A write was refused for lack of time before anything reached the hardware.</summary>
internal sealed class AllyBudgetException(string message) : Exception(message);
