using Avalonia.Controls;

namespace WSGM.Overlay;

/// <summary>Updates a retained UI control from a newly rendered description without replacing its focus identity.</summary>
internal interface IOverlayRefreshable
{
    /// <summary>Refreshes callbacks and non-editing state on the UI thread while retaining active drafts.</summary>
    /// <param name="replacement">An unmounted description of the same concrete control type and semantic key.</param>
    /// <remarks>The caller retains this control and discards the replacement; implementations must not adopt its lifetime.</remarks>
    void RefreshFrom(Control replacement);
}
