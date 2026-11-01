using System;
using System.Threading.Tasks;
using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>
///     A graphics driver that holds a display at any refresh rate inside a range without a Windows mode
///     change. Intel's Arc Sync Custom profile is the one implementation.
/// </summary>
/// <remarks>
///     Nothing here blocks its caller, which can be the UI thread: the range is answered from what the
///     driver last reported and refreshed in the background, and writes complete on their own. A write
///     is never retried; a failure is reported and left for the next user action.
/// </remarks>
internal interface IFixedRefreshRateDriver
{
    /// <summary>Raised off the caller's thread when a range the driver reports differs from the one answered.</summary>
    event Action? RangeChanged;

    /// <summary>The range the driver last reported for one display, asking again in the background when unknown.</summary>
    /// <param name="target">The display.</param>
    /// <returns>The range, or null when no driver offered one for that display yet.</returns>
    DriverRefreshRange? ReadRange(DisplayTargetIdentity target);

    /// <summary>Holds one display at a rate.</summary>
    /// <param name="target">The display.</param>
    /// <param name="refreshHz">The rate, inside the range the driver reported.</param>
    /// <returns>Whether the driver accepted it; false also when it did not answer in time.</returns>
    Task<bool> ApplyAsync(DisplayTargetIdentity target, int refreshHz);

    /// <summary>Hands the display back to the profile the driver used before the first fixed rate.</summary>
    /// <param name="target">The display.</param>
    /// <returns>Whether nothing is left fixed: released, nothing to release, or no driver holding it any more.</returns>
    Task<bool> ReleaseAsync(DisplayTargetIdentity target);
}
