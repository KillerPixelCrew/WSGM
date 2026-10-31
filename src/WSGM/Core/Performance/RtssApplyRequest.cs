namespace WSGM.Core;

/// <summary>Narrow property update sent to the adapter.</summary>
/// <param name="RtssProfileName">Executable profile name, or the empty string for the global profile.</param>
/// <param name="Control">Single control to write.</param>
/// <param name="Value">Value already checked against the probe's capabilities.</param>
internal sealed record RtssApplyRequest(
    string RtssProfileName,
    PerformanceControl Control,
    int Value);
