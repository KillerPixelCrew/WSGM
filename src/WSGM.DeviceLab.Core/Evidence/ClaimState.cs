using System.Text.Json.Serialization;

namespace WSGM.DeviceLab.Core.Evidence;

/// <summary>
/// How well established one protocol claim is.
/// </summary>
/// <remarks>
/// A ladder, and each rung means something specific about what may be generated from the claim. The
/// jump that matters is <see cref="Corroborated"/> to <see cref="HardwareVerified"/>: agreement
/// between three independent open-source projects is still not evidence that a value behaves this way
/// on <em>this</em> board, and only a bounded trial on the target with readback and verified
/// restoration crosses that line.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ClaimState>))]
public enum ClaimState
{
    /// <summary>Suggested by a reference, a similarity match, a schema, or one observation.</summary>
    Candidate,

    /// <summary>Repeatedly associated with the expected action, but not causally proven.</summary>
    Correlated,

    /// <summary>Supported by repeated A/B/revert evidence or independent sources.</summary>
    Corroborated,

    /// <summary>
    /// Reproduced on the exact target by a reviewed bounded trial, with readback and verified
    /// restoration.
    /// </summary>
    HardwareVerified,

    /// <summary>Reviewed against supported firmware, lifecycle, failure, and safety gates.</summary>
    RetailApproved,

    /// <summary>Disproven, unsafe, incompatible, or superseded.</summary>
    Rejected,
}

/// <summary>
/// What may be generated from a claim in a given state.
/// </summary>
public static class ClaimStatePolicy
{
    /// <summary>
    /// Whether generated code may write to hardware based on this claim.
    /// </summary>
    /// <param name="state">The claim's state.</param>
    /// <returns><see langword="true"/> only for states proven on the target.</returns>
    /// <remarks>
    /// The scaffold generator asks this before emitting any setter. A claim below
    /// <see cref="ClaimState.HardwareVerified"/> produces a capability that is omitted or explicitly
    /// unavailable — never a placeholder setter, because a placeholder is indistinguishable from a
    /// working one until it runs on someone's device.
    /// </remarks>
    public static bool MayGenerateWrite(ClaimState state) => state
        is ClaimState.HardwareVerified or ClaimState.RetailApproved;

    /// <summary>
    /// Whether a claim may be exercised by an explicitly authorized bounded trial.
    /// </summary>
    /// <param name="state">The claim's state.</param>
    /// <returns><see langword="true"/> when a trial may attempt it.</returns>
    /// <remarks>
    /// <see cref="ClaimState.Corroborated"/> is the entry point: enough evidence to be worth testing
    /// on hardware, not enough to ship. Anything weaker would mean pointing a write at an address
    /// suggested by a single observation.
    /// </remarks>
    public static bool MayAttemptTrial(ClaimState state) => state
        is ClaimState.Corroborated or ClaimState.HardwareVerified or ClaimState.RetailApproved;

    /// <summary>
    /// Whether generated code may read based on this claim.
    /// </summary>
    /// <param name="state">The claim's state.</param>
    /// <returns><see langword="true"/> for anything not rejected.</returns>
    /// <remarks>
    /// Reads are permitted earlier than writes because a wrong read reports a wrong number, while a
    /// wrong write changes hardware. They are still rate-limited and deadline-bounded: a getter is
    /// not assumed safe merely because it reads.
    /// </remarks>
    public static bool MayGenerateRead(ClaimState state) => state is not ClaimState.Rejected;
}
