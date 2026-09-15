using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Readings of a capability command outcome shared by the host's callers.</summary>
internal static class CommandOutcomeExtensions
{
    /// <summary>Whether an outcome means the value reached the device.</summary>
    /// <param name="outcome">The capability command outcome.</param>
    /// <returns><see langword="true"/> for a written value, verified or not.</returns>
    /// <remarks>
    /// <see cref="CommandOutcome.AppliedUnverified"/> counts: a device with no readback for a value
    /// is normal, and refusing to trust it would disable features on that hardware. Everything
    /// else (queued, refused, timed out, interrupted) did not demonstrably arrive.
    /// </remarks>
    internal static bool IsApplied(this CommandOutcome outcome) =>
        outcome is CommandOutcome.AppliedVerified or CommandOutcome.AppliedUnverified;
}
