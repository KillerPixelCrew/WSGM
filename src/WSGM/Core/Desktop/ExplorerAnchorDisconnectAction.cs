namespace WSGM.Core;

/// <summary>Action taken after the authenticated anchor command pipe disconnects.</summary>
internal enum ExplorerAnchorDisconnectAction
{
    /// <summary>Keep the recovery owner alive until its owner exits or it is explicitly stopped.</summary>
    Wait,

    /// <summary>Exit without restoring Explorer.</summary>
    Exit,

    /// <summary>Run abnormal owner-loss recovery.</summary>
    Recover
}
