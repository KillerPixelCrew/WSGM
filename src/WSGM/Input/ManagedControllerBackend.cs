using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Input;

namespace WSGM.Input;

/// <summary>The virtual controller shapes WSGM can present.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VirtualTargetKind>))]
public enum VirtualTargetKind
{
    /// <summary>Valve's composite Steam Deck controller: the richest handheld model.</summary>
    SteamDeckComposite,

    /// <summary>Xbox 360, for native XInput compatibility with older software.</summary>
    Xbox360,

    /// <summary>DualShock 4, for software requiring a PlayStation controller.</summary>
    DualShock4,
}

internal enum HidBackendHealthState
{
    Unavailable,
    Incompatible,
    Ready,
    Faulted,
}

internal enum ManagedTargetState
{
    Absent,
    Creating,
    Neutral,
    Active,
    Replacing,
    Faulted,
    Removing,
}

internal sealed record HidBackendCapabilities(
    Version ProtocolVersion,
    IReadOnlyList<VirtualTargetKind> SupportedTargets,
    bool SupportsOutput);

internal sealed record HidBackendHealth(
    HidBackendHealthState State,
    string Detail,
    HidBackendCapabilities? Capabilities = null);

internal sealed record HidTargetHandle(
    VirtualTargetKind Kind,
    long Generation,
    string InstanceId);

internal sealed record HidTargetOutput(
    HapticOutputFrame Frame,
    VirtualTargetKind SourceKind);

internal interface IHidBackend : IAsyncDisposable
{
    event EventHandler<HidTargetOutput>? OutputReceived;

    event EventHandler<long>? TargetLost;

    Task<HidBackendHealth> DiscoverAsync(CancellationToken cancellationToken);

    Task<HidTargetHandle> CreateTargetAsync(
        VirtualTargetKind kind,
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

    Task<IReadOnlyDictionary<string, string>> GetDiagnosticsAsync(
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
        ArgumentNullException.ThrowIfNull(sample);
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
            || !Trigger(sample.LeftTrigger)
            || !Trigger(sample.RightTrigger)
            || !Motion(sample.Motion))
        {
            reason = "invalid-or-discontinuous-sample";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    internal static bool IsNeutral(CanonicalControllerSample sample) =>
        sample.Buttons == CanonicalButtons.None
        && sample.LeftStickX == 0
        && sample.LeftStickY == 0
        && sample.RightStickX == 0
        && sample.RightStickY == 0
        && sample.LeftTrigger == 0
        && sample.RightTrigger == 0
        && sample.Motion is null;

    private static bool Axis(float value) => float.IsFinite(value) && value is >= -1 and <= 1;

    private static bool Trigger(float value) => float.IsFinite(value) && value is >= 0 and <= 1;

    private static bool Motion(MotionSample? motion) => motion is null
        || ((!motion.HasGyro
            || (float.IsFinite(motion.GyroX)
                && float.IsFinite(motion.GyroY)
                && float.IsFinite(motion.GyroZ)))
            && (!motion.HasAccelerometer
                || (float.IsFinite(motion.AccelX)
                    && float.IsFinite(motion.AccelY)
                    && float.IsFinite(motion.AccelZ))));
}
