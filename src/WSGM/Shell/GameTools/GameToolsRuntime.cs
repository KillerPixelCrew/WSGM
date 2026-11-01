namespace WSGM.Shell;

/// <summary>The chosen runtime executable.</summary>
/// <param name="RelativePath">Forward-slash path relative to the install folder, or absolute outside it.</param>
/// <param name="FileName">The file name.</param>
/// <param name="Architecture"><c>x64</c>, <c>x86</c>, <c>arm64</c> or empty.</param>
/// <param name="ArchitectureLabel">The architecture shown to the user, empty when unknown.</param>
/// <param name="Confidence"><c>confirmed</c>, <c>structural</c>, <c>single</c> or <c>needsChoice</c>.</param>
/// <param name="ConfidenceLabel">How the program was chosen, empty when the user must choose.</param>
internal sealed record GameToolsRuntime(
    string RelativePath,
    string FileName,
    string Architecture,
    string ArchitectureLabel,
    string Confidence,
    string ConfidenceLabel);
