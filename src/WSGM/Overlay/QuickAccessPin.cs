using System;
using Avalonia.Controls;

namespace WSGM.Overlay;

/// <summary>One pin id as the source that owns it resolves it: what it is called, whether it is there and its control.</summary>
/// <param name="Title">The name the pin shows.</param>
/// <param name="Available">
///     Whether its source offers it now. An unavailable pin still draws, so a stored pin can be unpinned, but
///     only a pinned one is offered to toggle.
/// </param>
/// <param name="Tile">Whether it is a tile in the Quick access grid rather than a full-width group or row.</param>
/// <param name="Draw">Draws the pin, reusing the control drawn for it last pass when one is passed.</param>
internal sealed record QuickAccessPin(string Title, bool Available, bool Tile, Func<Control?, Control> Draw);
