using WSGM.Device.Sdk.Input;

namespace WSGM.Tests.Builders;

/// <summary>Canonical controller samples for tests that only care about which controls are held.</summary>
internal static class ControllerSamples
{
    internal static CanonicalControllerSample Sample(
        CanonicalButtons buttons,
        float leftTrigger = 0,
        float rightTrigger = 0)
    {
        return new CanonicalControllerSample
        {
            Sequence = 1,
            CycleGeneration = 1,
            Timestamp = DateTimeOffset.UnixEpoch,
            Buttons = buttons,
            LeftTrigger = leftTrigger,
            RightTrigger = rightTrigger
        };
    }
}
