using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    /// <summary>Lends the session's Notes owner to the Notes page, or detaches it with null.</summary>
    /// <param name="notes">The owner, or null when the window closes or the session has none.</param>
    internal void AttachNotes(NotesService? notes)
    {
        NotesHost.Attach(notes);
        if (notes is null)
        {
            NotesHost.StartCapture = null;
        }
    }

    /// <summary>Shows Tools > Notes, which opens on the page the overlay last showed.</summary>
    /// <returns>Whether the Notes section exists in this window.</returns>
    internal bool OpenNotes()
    {
        if (_navigation.Page == OverlayPage.Notes)
        {
            // Already showing: start over from the remembered place rather than keep a stale level.
            NotesHost.OpenLastLocation();
        }
        else if (!OpenToolsSection(OverlayPage.Notes))
        {
            return false;
        }

        FocusFirstControl(NotesHost);
        return true;
    }
}
