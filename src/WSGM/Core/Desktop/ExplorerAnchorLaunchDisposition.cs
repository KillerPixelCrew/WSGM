namespace WSGM.Core;

/// <summary>
///     Whether an Explorer launch request definitely did, definitely did not, or may have
///     crossed the anchor IPC boundary.
/// </summary>
internal enum ExplorerAnchorLaunchDisposition
{
    /// <summary>No launch was dispatched, so a different recovery route is safe.</summary>
    NotDispatched,

    /// <summary>The anchor confirmed that it dispatched the fixed Explorer launch.</summary>
    Dispatched,

    /// <summary>The request may have been received, so launching another shell would race it.</summary>
    Unknown
}
