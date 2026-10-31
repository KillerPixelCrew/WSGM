namespace WSGM.Core;

/// <summary>Result of capturing a canonical Explorer and creating its replacement anchor.</summary>
/// <param name="Prepared">Whether a verified anchor is retained before shell exit.</param>
/// <param name="Detail">Diagnostic readiness or refusal reason.</param>
internal readonly record struct ExplorerPreparationResult(
    bool Prepared,
    string Detail);
