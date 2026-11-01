namespace WSGM.Core;

/// <summary>One bounded adapter discovery result. Process identity is folded into Generation.</summary>
/// <param name="Availability">Discovery or adapter readiness state.</param>
/// <param name="Version">Product version of the installed RTSS executable, when read.</param>
/// <param name="ExecutablePath">The installed RTSS executable path, when the registry named a folder.</param>
/// <param name="Generation">Process/install identity token used to reject stale requests; zero without an identity.</param>
/// <param name="Capabilities">Verified control bounds when the adapter is ready.</param>
/// <param name="Diagnostic">Availability explanation suitable for diagnostics.</param>
/// <param name="ProcessId">Matching running RTSS process, when uniquely identified.</param>
internal sealed record RtssProbe(
    RtssAvailability Availability,
    string? Version,
    string? ExecutablePath,
    long Generation,
    RtssCapabilities? Capabilities,
    string? Diagnostic,
    int? ProcessId = null);
