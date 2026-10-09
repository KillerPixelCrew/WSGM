using CanonicalButtons = LibHandheld.Contracts.CanonicalButtons;
using CanonicalControllerSample = LibHandheld.Contracts.CanonicalControllerSample;
using MotionSample = LibHandheld.Contracts.MotionSample;

namespace WSGM.Input;

/// <summary>Pure numeric-range checks for canonical input; does not establish device identity or sample freshness.</summary>
internal static class ManagedControllerSampleValidator
{
    /// <summary>Whether every value in the sample is a real number in range.</summary>
    /// <remarks>
    ///     Source ownership and generation belong to the router. A late full-state sample is corrected by the next sample.
    /// </remarks>
    /// <param name="sample">Canonical input to validate before forwarding to a virtual target.</param>
    /// <returns>True when sticks and triggers are finite and in range, and each reported motion component is finite.</returns>
    internal static bool IsValid(CanonicalControllerSample sample)
    {
        return Axis(sample.LeftStickX) && Axis(sample.LeftStickY)
                                       && Axis(sample.RightStickX) && Axis(sample.RightStickY)
                                       && FiniteUnit(sample.LeftTrigger) && FiniteUnit(sample.RightTrigger)
                                       && Motion(sample.Motion);
    }

    /// <summary>Checks whether buttons, sticks, triggers and motion carry no held controller state.</summary>
    /// <param name="sample">Canonical state to compare with the neutral routing baseline.</param>
    /// <returns>True for no buttons, zero axes/triggers and absent motion; touch fields do not participate.</returns>
    internal static bool IsNeutral(CanonicalControllerSample sample)
    {
        return sample is
        {
            Buttons: CanonicalButtons.None,
            LeftStickX: 0,
            LeftStickY: 0,
            RightStickX: 0,
            RightStickY: 0,
            LeftTrigger: 0,
            RightTrigger: 0,
            Motion: null
        };
    }

    private static bool Axis(float value)
    {
        return float.IsFinite(value) && value is >= -1 and <= 1;
    }

    /// <summary>Whether the value is a finite 0..1 unit, as triggers and haptic channels require.</summary>
    /// <param name="value">Unsigned normalized input such as a trigger or touch coordinate.</param>
    /// <returns>True when finite and within zero through one.</returns>
    internal static bool FiniteUnit(float value)
    {
        return float.IsFinite(value) && value is >= 0 and <= 1;
    }

    private static bool Motion(MotionSample? motion)
    {
        if (motion is not { } sample)
        {
            return true;
        }

        return (!sample.HasGyro
                || (float.IsFinite(sample.GyroX)
                    && float.IsFinite(sample.GyroY)
                    && float.IsFinite(sample.GyroZ)))
               && (!sample.HasAccelerometer
                   || (float.IsFinite(sample.AccelX)
                       && float.IsFinite(sample.AccelY)
                       && float.IsFinite(sample.AccelZ)));
    }
}
