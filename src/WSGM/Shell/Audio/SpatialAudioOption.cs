using System;

namespace WSGM.Shell;

/// <summary>One named spatial-audio-format choice.</summary>
/// <param name="Format">Spatial format identifier, including the Off sentinel.</param>
/// <param name="Name">Display label for the format.</param>
public sealed record SpatialAudioOption(Guid Format, string Name)
{
    /// <inheritdoc />
    public override string ToString()
    {
        return Name;
    }
}
