using WSGM.Core;

namespace WSGM.Settings;

/// <summary>Editable placement of one boot-splash element. Writes through to the
/// placement object inside the view model's splash section, so the section and the
/// editor can never disagree; <see cref="Load"/> re-points it after a preset apply
/// or theme import and re-raises every property.</summary>
public sealed class SplashPlacementEditor : ObservableObject
{
    private SplashElementPlacement _placement = new();

    /// <summary>Points the editor at a splash section's placement object.</summary>
    /// <param name="placement">The placement this editor writes through to.</param>
    internal void Load(SplashElementPlacement placement)
    {
        _placement = placement;
        Raise(nameof(Mode));
        Raise(nameof(Anchor));
        Raise(nameof(PaddingX));
        Raise(nameof(PaddingY));
        Raise(nameof(X));
        Raise(nameof(Y));
        Raise(nameof(IsAnchor));
        Raise(nameof(IsAbsolute));
    }

    /// <summary>Gets or sets how the element is positioned. Also switches which
    /// field group (<see cref="IsAnchor"/>/<see cref="IsAbsolute"/>) the editor shows.</summary>
    public SplashPlacementMode Mode
    {
        get => _placement.Mode;
        set
        {
            _placement.Mode = value;
            Raise(nameof(Mode));
            Raise(nameof(IsAnchor));
            Raise(nameof(IsAbsolute));
        }
    }

    /// <summary>Whether the editor shows the anchor + padding fields.</summary>
    public bool IsAnchor => _placement.Mode == SplashPlacementMode.Anchor;

    /// <summary>Whether the editor shows the absolute X/Y fields.</summary>
    public bool IsAbsolute => _placement.Mode == SplashPlacementMode.Absolute;

    /// <summary>Gets or sets the nine-grid anchor.</summary>
    public SplashPlacementAnchor Anchor
    {
        get => _placement.Anchor;
        set { _placement.Anchor = value; Raise(nameof(Anchor)); }
    }

    /// <summary>Gets or sets the horizontal padding from the anchored edge.</summary>
    public int PaddingX { get => _placement.PaddingX; set { _placement.PaddingX = value; Raise(nameof(PaddingX)); } }

    /// <summary>Gets or sets the vertical padding from the anchored edge.</summary>
    public int PaddingY { get => _placement.PaddingY; set { _placement.PaddingY = value; Raise(nameof(PaddingY)); } }

    /// <summary>Gets or sets the absolute X coordinate in logical pixels.</summary>
    public int X { get => _placement.X; set { _placement.X = value; Raise(nameof(X)); } }

    /// <summary>Gets or sets the absolute Y coordinate in logical pixels.</summary>
    public int Y { get => _placement.Y; set { _placement.Y = value; Raise(nameof(Y)); } }

}
