namespace WSGM.Core;

/// <summary>Narrow property update sent to the adapter.</summary>
/// <param name="RtssProfileName">Executable profile name, or the empty string for the global profile.</param>
/// <param name="Control">Single control to write.</param>
/// <param name="Value">Proposed value, validated by the adapter.</param>
/// <param name="Generation">Expected identity from the probe that admitted this request.</param>
internal sealed record RtssApplyRequest(
    string RtssProfileName,
    PerformanceControl Control,
    int Value,
    long Generation);
