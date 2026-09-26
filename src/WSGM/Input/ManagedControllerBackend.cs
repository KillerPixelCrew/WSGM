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

    /// <summary>
    ///     Raised when a consumer of the current target asks for motion or stops asking, with the new
    ///     answer. A Steam Deck target's consumers are Steam, which turns the IMU on through the
    ///     set-settings feature report for a layout that uses gyro, and SDL applications, which never
    ///     touch the IMU but feed a watchdog write for as long as they hold the pad; a new or removed
    ///     target starts at false.
    /// </summary>
    event EventHandler<bool>? MotionRequested;

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
    internal static bool TryValidate(
        CanonicalControllerSample sample,
        long sourceGeneration,
        long previousSequence,
        DateTimeOffset now,
        out string reason)
    {
        if (sample.CycleGeneration != sourceGeneration)
        {
            reason = "stale-source-generation";
            return false;
        }

        if (sample.Sequence <= previousSequence)
        {
            reason = "non-monotonic-sequence";
            return false;
        }

        if (sample.Timestamp > now.AddSeconds(1) || now - sample.Timestamp > TimeSpan.FromSeconds(1))
        {
            reason = "stale-or-future-timestamp";
            return false;
        }

        if (sample.Quality is not SampleQuality.Good
            || !Axis(sample.LeftStickX)
            || !Axis(sample.LeftStickY)
            || !Axis(sample.RightStickX)
            || !Axis(sample.RightStickY)
            || !FiniteUnit(sample.LeftTrigger)
            || !FiniteUnit(sample.RightTrigger)
            || !Motion(sample.Motion))
        {
            reason = "invalid-or-discontinuous-sample";
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
