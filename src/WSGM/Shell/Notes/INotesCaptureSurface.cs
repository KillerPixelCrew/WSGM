using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Shell;

/// <summary>Hides a Notes surface for a screenshot and brings it back afterwards.</summary>
internal interface INotesCaptureSurface
{
    /// <summary>Closes the surface and gives the game the foreground and the input.</summary>
    /// <param name="target">The game.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>Whether the game was handed back.</returns>
    Task<bool> ReturnToGameAsync(NotesCaptureTarget target, CancellationToken cancellationToken);

    /// <summary>Opens the surface again on a page.</summary>
    /// <param name="target">The game the surface belongs to.</param>
    /// <param name="pageId">The page to show.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>Whether the surface reported the page open.</returns>
    Task<bool> ReopenAsync(NotesCaptureTarget target, string pageId, CancellationToken cancellationToken);
}
