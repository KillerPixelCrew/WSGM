using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Input;

namespace WSGM.Input;

internal enum HidBackendHealthState
{
    Unavailable,
    Incompatible,
    Ready
}

internal enum ManagedTargetState
{
    Absent,
    Neutral,
    Active,
    Faulted
}

internal sealed record HidBackendCapabilities(
    IReadOnlyList<ManagedControllerTarget> SupportedTargets);

internal sealed record HidBackendHealth(
    HidBackendHealthState State,
    string Detail,
    HidBackendCapabilities? Capabilities = null);

internal sealed record HidTargetHandle(
    ManagedControllerTarget Kind,
    long Generation);

internal sealed record HidTargetOutput(
    HapticOutputFrame Frame,
    ManagedControllerTarget SourceKind,
    TimeSpan? StopAfter = null);

internal interface IHidBackend : IAsyncDisposable
{
    event EventHandler<HidTargetOutput>? OutputReceived;

    event EventHandler<long>? TargetLost;

    Task<HidBackendHealth> DiscoverAsync(CancellationToken cancellationToken);

    Task<HidTargetHandle> CreateTargetAsync(
        ManagedControllerTarget kind,
        CanonicalControllerSample initialNeutralState,
        CancellationToken cancellationToken);

    Task<bool> WaitForEnumerationAsync(
        HidTargetHandle target,
        CancellationToken cancellationToken);

    ValueTask<bool> PublishAsync(
        HidTargetHandle target,
        CanonicalControllerSample sample,
        CancellationToken cancellationToken);

    Task NeutralizeAsync(
        HidTargetHandle target,
        CanonicalControllerSample neutralState,
        CancellationToken cancellationToken);

    Task RemoveTargetAsync(HidTargetHandle target, CancellationToken cancellationToken);

    Task<bool> WaitForRemovalAsync(
        HidTargetHandle target,
        CancellationToken cancellationToken);
}

internal static class ManagedControllerSampleValidator
{
    /// <summary>Whether every value in the sample is a real number in range.</summary>
    /// <remarks>
    ///     Nothing about where or when the sample came from: each sample is the full state, so a late one
    ///     is corrected by the next, as in HC. Generation, sequence, age and discontinuity checks here
    ///     neutralized the pad after every restart and wake.
    /// </remarks>
    internal static bool TryValidate(CanonicalControllerSample sample, out string reason)
    {
        if (!Axis(sample.LeftStickX)
            || !Axis(sample.LeftStickY)
            || !Axis(sample.RightStickX)
            || !Axis(sample.RightStickY)
            || !FiniteUnit(sample.LeftTrigger)
            || !FiniteUnit(sample.RightTrigger)
            || !Motion(sample.Motion))
        {
            reason = "out-of-range-sample";
            return false;
        }

        reason = string.Empty;
        return true;
    }

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
