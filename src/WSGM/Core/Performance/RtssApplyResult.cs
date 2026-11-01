namespace WSGM.Core;

/// <summary>Result of an adapter mutation attempt.</summary>
/// <param name="Applied">Whether the adapter accepted the mutation; does not require a following readback.</param>
/// <param name="Diagnostic">Reason for refusal, when available.</param>
internal sealed record RtssApplyResult(bool Applied, string? Diagnostic);
