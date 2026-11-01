namespace WSGM.Shell;

/// <summary>How a HidHide change went, for the log and the controller state.</summary>
/// <param name="Succeeded">Whether all required driver writes were accepted; this does not claim independent readback.</param>
/// <param name="Detail">Operation summary or failure detail suitable for diagnostics.</param>
internal sealed record HidHideResult(bool Succeeded, string Detail);
