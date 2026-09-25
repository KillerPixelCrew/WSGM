using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Readings of a capability command outcome shared by the host's callers.</summary>
internal static class CommandOutcomeExtensions
{
    /// <summary>Whether an outcome means the value reached the device.</summary>
    /// <param name="outcome">The capability command outcome.</param>
    /// <returns><see langword="true" /> for a written value, verified or not.</returns>
    /// <remarks>
    ///     <see cref="CommandOutcome.AppliedUnverified" /> counts: a device with no readback for a value
    ///     is normal, and refusing to trust it would disable features on that hardware. Everything
    ///     else (queued, refused, timed out, interrupted) did not demonstrably arrive.
    /// </remarks>
    internal static bool IsApplied(this CommandOutcome outcome)
    {
        return outcome is CommandOutcome.AppliedVerified or CommandOutcome.AppliedUnverified;
    }

    /// <summary>Whether a result applied the given integer.</summary>
    /// <param name="result">The capability command result.</param>
    /// <param name="value">The integer that was requested.</param>
    /// <returns>
    ///     <see langword="true" /> for a verified result that read back <paramref name="value" />, or an
    ///     unverified one, which by definition has no readback to disagree with it.
    /// </returns>
    internal static bool Applied(this CapabilityCommandResult result, int value)
    {
        return result.Outcome switch
        {
            CommandOutcome.AppliedVerified => result.ReadbackValue?.IntegerValue == value,
            CommandOutcome.AppliedUnverified => true,
            _ => false
        };
    }
}
