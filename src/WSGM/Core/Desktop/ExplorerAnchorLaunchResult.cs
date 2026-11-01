namespace WSGM.Core;

/// <summary>Bounded result of an authenticated anchor launch request.</summary>
/// <param name="Disposition">Whether the request crossed, may have crossed, or did not cross dispatch.</param>
/// <param name="ProcessId">Created process PID when returned, or zero; this is not proof of shell ownership.</param>
/// <param name="Detail">Protocol or failure diagnostic.</param>
internal readonly record struct ExplorerAnchorLaunchResult(
    ExplorerAnchorLaunchDisposition Disposition,
    uint ProcessId,
    string Detail);
