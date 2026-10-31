namespace WSGM.Core;

/// <summary>Action taken by an anchor after its owning WSGM process disappears.</summary>
internal enum ExplorerAnchorOwnerLossAction
{
    /// <summary>Exit without starting anything.</summary>
    Exit,

    /// <summary>Restore the fixed canonical Explorer path.</summary>
    RestoreExplorer
}
