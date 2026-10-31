using System.Threading.Tasks;

namespace WSGM.Shell;

/// <summary>The ordered effects shared by normal desktop return and failed entry recovery.</summary>
internal interface IDesktopReturnBackend
{
    /// <summary>Requests leaving Big Picture before desktop restoration.</summary>
    /// <returns>Completion of the bounded exit attempt.</returns>
    Task ExitBigPictureAsync();

    /// <summary>Restores the recorded or configured desktop display layout.</summary>
    /// <returns>Whether the requested layout was restored; false preserves pending recovery.</returns>
    Task<bool> RestoreLayoutAsync();

    /// <summary>Restores desktop audio after the display attempt settles.</summary>
    /// <returns>Whether the requested audio settings were restored; false preserves pending recovery.</returns>
    Task<bool> RestoreAudioAsync();

    /// <summary>Releases game-mode UI and shell ownership before Explorer starts.</summary>
    /// <returns>Completion of the retirement attempt.</returns>
    Task RetireGameModeAsync();

    /// <summary>Restores the desktop shell using the saved Explorer anchor.</summary>
    /// <returns>Whether Explorer restoration succeeded.</returns>
    Task<bool> RestoreExplorerAsync();

    /// <summary>Runs the configured external leave actions after Explorer is restored.</summary>
    /// <returns>Completion of the leave-action sequence.</returns>
    Task RunLeaveActionsAsync();

    /// <summary>Clears the durable recovery record after display, audio and Explorer restoration.</summary>
    /// <returns>Completion after the cleared record is persisted.</returns>
    Task ClearPendingReturnAsync();
}
