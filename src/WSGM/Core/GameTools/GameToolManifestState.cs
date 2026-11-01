namespace WSGM.Core;

/// <summary>Whether a manifest describes a completed installation.</summary>
internal enum GameToolManifestState
{
    /// <summary>A transaction was persisted before touching game files and has not completed.</summary>
    Pending,

    /// <summary>The installation completed and its files are recorded.</summary>
    Installed,

    /// <summary>A rollback could not undo every change; the remaining journal waits for a user action.</summary>
    Failed
}
