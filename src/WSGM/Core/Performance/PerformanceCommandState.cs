namespace WSGM.Core;

/// <summary>Immutable command status shared by every performance UI client.</summary>
/// <param name="Sequence">Monotonically increasing service command identifier; zero is the idle sentinel.</param>
/// <param name="Origin">Sanitized name of the submitting surface or workflow.</param>
/// <param name="CorrelationId">Sanitized request token for diagnostics.</param>
/// <param name="Control">Control addressed by this command.</param>
/// <param name="RequestedValue">Requested value, or null when no command exists.</param>
/// <param name="Phase">Current processing outcome or stage.</param>
/// <param name="Diagnostic">Explanation of the stage or failure, when available.</param>
internal sealed record PerformanceCommandState(
    long Sequence,
    string Origin,
    string CorrelationId,
    PerformanceControl Control,
    int? RequestedValue,
    PerformanceCommandPhase Phase,
    string? Diagnostic)
{
    /// <summary>Initial state before any command is submitted.</summary>
    internal static readonly PerformanceCommandState Idle = new(
        0,
        string.Empty,
        string.Empty,
        PerformanceControl.FrameLimit,
        null,
        PerformanceCommandPhase.Idle,
        null);
}
