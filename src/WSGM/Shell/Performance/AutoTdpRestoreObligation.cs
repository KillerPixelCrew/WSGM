using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>The limit AutoTDP took over from and must hand back when control ends.</summary>
/// <remarks>
///     Captured when the first write of a generation is admitted, moved by a manual change, and
///     dropped only once the restore landed or a refusal proves nothing reached the hardware. It
///     survives device cycles: the restore goes through whatever primary limit is published then.
///     <para>
///         Not synchronized. <see cref="AutoTdpService" /> calls every mutating member under its gate,
///         the same lock that orders a write's admission against an application change.
///     </para>
/// </remarks>
internal sealed class AutoTdpRestoreObligation
{
    private bool _powerMayDiffer;

    /// <summary>The primary limit to restore, or null when nothing is owed.</summary>
    internal int? Watts { get; private set; }

    /// <summary>The paired limit as it was when the obligation was captured, or null without a pair.</summary>
    internal DeviceCapabilityView? Pair { get; private set; }

    /// <summary>Whether an unavailable primary limit was already reported for the current obligation.</summary>
    internal bool UnavailableReported { get; private set; }

    /// <summary>Captures the limit being taken over, once per obligation.</summary>
    /// <param name="watts">The user's limit in effect before AutoTDP's first write.</param>
    /// <param name="power">The primary power-limit capability being written.</param>
    /// <param name="capability">The lookup the pair is read through.</param>
    /// <returns>False when the descriptor requires a pair that cannot be commanded, so nothing may be written.</returns>
    internal bool TryCapture(int watts, DeviceCapabilityView power, AutoTdpPowerCapability capability)
    {
        if (Watts is null)
        {
            Pair = capability.FindPaired(power);
            if (power.Descriptor.PairedPowerLimitId is not null && !AutoTdpPowerCapability.CanWrite(Pair))
            {
                return false;
            }
        }

        Watts ??= watts;
        return true;
    }

    /// <summary>Records a write's outcome against the obligation.</summary>
    /// <param name="outcome">The command outcome.</param>
    /// <remarks>
    ///     Rejected is the one outcome that proves nothing reached hardware. If no earlier write of this
    ///     generation may have landed, there is consequently nothing to restore.
    /// </remarks>
    internal void NoteOutcome(CommandOutcome outcome)
    {
        if (outcome == CommandOutcome.Rejected && !_powerMayDiffer)
        {
            Watts = null;
            Pair = null;
        }
        else if (outcome != CommandOutcome.Rejected)
        {
            _powerMayDiffer = true;
        }
    }

    /// <summary>Records a write that failed in a way that may have followed dispatch.</summary>
    internal void NoteUncertain()
    {
        _powerMayDiffer = true;
    }

    /// <summary>Moves the obligation to a limit the user just chose.</summary>
    /// <param name="watts">The manual limit, or null when a firmware performance profile now owns power.</param>
    /// <param name="capability">The lookup the current pair is read through.</param>
    /// <remarks>
    ///     A chosen profile owns power, so nothing numeric is restored: on devices like the Atlas a
    ///     restored limit would leave that profile. A numeric change replaces an existing obligation and
    ///     never creates one.
    /// </remarks>
    internal void FollowManualChange(int? watts, AutoTdpPowerCapability capability)
    {
        if (watts is null)
        {
            Watts = null;
            Pair = null;
        }
        else if (Watts is not null)
        {
            Watts = watts;
            if (capability.FindPrimary() is { } primary)
            {
                Pair = capability.FindPaired(primary);
            }
        }
    }

    /// <summary>Starts a restore attempt and says whether an unavailable limit needs reporting.</summary>
    /// <param name="powerAvailable">Whether a primary power limit is published now.</param>
    /// <param name="reportUnavailable">Receives true once per obligation when the limit is gone.</param>
    /// <returns>The limit to restore, or null when nothing is owed.</returns>
    internal int? BeginRestore(bool powerAvailable, out bool reportUnavailable)
    {
        reportUnavailable = Watts is not null && !powerAvailable && !UnavailableReported;
        if (powerAvailable)
        {
            UnavailableReported = false;
        }
        else if (reportUnavailable)
        {
            UnavailableReported = true;
        }

        return Watts;
    }

    /// <summary>Clears the obligation after its restore landed, unless a newer limit replaced it meanwhile.</summary>
    /// <param name="watts">The limit that was restored.</param>
    /// <returns>True when the obligation was the one restored and is now cleared.</returns>
    internal bool Fulfil(int watts)
    {
        if (Watts != watts)
        {
            return false;
        }

        Watts = null;
        Pair = null;
        _powerMayDiffer = false;
        return true;
    }
}
