using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>
///     A graphics driver that holds a display at any refresh rate inside a range without a Windows mode
///     change. Intel's Arc Sync Custom profile is the one implementation.
/// </summary>
/// <remarks>
///     Every member runs synchronously on the caller's thread and returns once the driver answered. A
///     write is never retried; a failure is reported and left for the next user action.
/// </remarks>
internal interface IFixedRefreshRateDriver
{
    /// <summary>The range the driver can hold one display at.</summary>
    /// <param name="target">The display.</param>
    /// <returns>The range, or null when no driver offers one for that display.</returns>
    DriverRefreshRange? ReadRange(DisplayTargetIdentity target);

    /// <summary>Holds one display at a rate.</summary>
    /// <param name="target">The display.</param>
    /// <param name="refreshHz">The rate, inside the range the driver reported.</param>
    /// <returns>Whether the driver accepted it.</returns>
    bool Apply(DisplayTargetIdentity target, int refreshHz);

    /// <summary>Hands the display back to the profile the driver used before the first fixed rate.</summary>
    /// <param name="target">The display.</param>
    /// <returns>Whether nothing is left fixed: released, nothing to release, or no driver holding it any more.</returns>
    bool Release(DisplayTargetIdentity target);
}
