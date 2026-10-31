using System;

namespace WSGM.Overlay;

/// <summary>A running operation's Cancel command.</summary>
/// <param name="Title">The row's title.</param>
/// <param name="Description">What stopping keeps or loses.</param>
/// <param name="Activate">Asks the owning service to stop.</param>
internal sealed record OverlayProgressCancel(string Title, string Description, Action Activate);
