using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>Which boot movie Big Picture starts with, and whether it is reshuffled at start.</summary>
/// <remarks>
///     The choice is a library id, or empty for Steam's own movie. The content is the library's;
///     this is only the choice, which is what lets a shuffle pick again and a return to Steam's own
///     leave the downloads in place.
/// </remarks>
public sealed class AnimationsConfig
{
    /// <summary>The library id playing at Big Picture's start, or empty for Steam's own.</summary>
    public string Boot { get; set; } = string.Empty;

    /// <summary>Whether the boot movie is picked anew from the library each time WSGM starts.</summary>
    public bool ShuffleOnStart { get; set; }

    /// <summary>Library ids a shuffle never picks.</summary>
    public List<string> ShuffleExclusions { get; set; } = [];

    /// <summary>A copy, for a service that shows a change before the saved configuration reaches it again.</summary>
    /// <returns>The copy.</returns>
    public AnimationsConfig Clone()
    {
        return new AnimationsConfig
        {
            Boot = Boot,
            ShuffleOnStart = ShuffleOnStart,
            ShuffleExclusions = [.. ShuffleExclusions]
        };
    }
}
