using System.Text.Json.Serialization;

namespace WSGM.Core;

/// <summary>One source size in a saved driver list.</summary>
/// <param name="Width">Pixel width.</param>
/// <param name="Height">Pixel height.</param>
public readonly record struct ScaledResolutionSize(int Width, int Height)
{
    /// <summary>Whether both dimensions fit the source-mode limits.</summary>
    [JsonIgnore]
    public bool Valid => Width is >= 100 and <= 15999 && Height is >= 100 and <= 15999;
}
