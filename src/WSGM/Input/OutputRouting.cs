using System;
using WSGM.Device.Sdk.Input;

namespace WSGM.Input;

/// <summary>Applies WSGM's virtual-target lifetime policy to physical haptic output.</summary>
public static class OutputRouting
{
    /// <summary>Whether a frame still belongs to the active target and no zero trigger is active.</summary>
    public static bool ShouldDeliver(
        HapticOutputFrame frame,
        long currentTargetGeneration,
        ZeroOutputTrigger zeroTriggers)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return frame.TargetGeneration == currentTargetGeneration
            && zeroTriggers is ZeroOutputTrigger.None;
    }

    /// <summary>Whether an explicit stop must be sent to a device currently producing output.</summary>
    public static bool RequiresStop(ZeroOutputTrigger zeroTriggers, bool outputActive) =>
        outputActive && zeroTriggers is not ZeroOutputTrigger.None;
}
