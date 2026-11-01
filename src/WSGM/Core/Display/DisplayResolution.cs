namespace WSGM.Core;

/// <summary>One display resolution the driver accepted.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public readonly record struct DisplayResolution(int Width, int Height)
{
    /// <summary>Renders the resolution the way a user reads it.</summary>
    /// <returns>Width and height separated by an <c>x</c>.</returns>
    public override string ToString()
    {
        return $"{Width}x{Height}";
    }
}
