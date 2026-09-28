using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>Which animation each of Steam's slots plays, and whether they are reshuffled at start.</summary>
/// <remarks>
///     An assignment is an animation's library id, or empty for Steam's own movie. The content is
///     the library's; this is only the choice, which is what lets a shuffle pick again and a return
///     to stock leave the downloads in place.
/// </remarks>
public sealed class AnimationsConfig
{
    /// <summary>The library id playing at Big Picture's start, or empty for Steam's own.</summary>
    public string Boot { get; set; } = string.Empty;

    /// <summary>The library id playing while suspending from the shell, or empty.</summary>
    public string Suspend { get; set; } = string.Empty;

    /// <summary>The library id playing while suspending from a game, or empty.</summary>
    public string Throbber { get; set; } = string.Empty;

    /// <summary>Whether every slot is picked anew from the library each time WSGM starts.</summary>
    public bool ShuffleOnStart { get; set; }

    /// <summary>Library ids a shuffle never picks.</summary>
    public List<string> ShuffleExclusions { get; set; } = [];

    /// <summary>The assignment of one slot.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>The library id, or empty.</returns>
    public string Get(string slot)
    {
        return slot switch
        {
            AnimationSlots.Boot => Boot,
            AnimationSlots.Suspend => Suspend,
            AnimationSlots.Throbber => Throbber,
            _ => string.Empty
        };
    }

    /// <summary>Assigns one slot.</summary>
    /// <param name="slot">The slot.</param>
    /// <param name="id">The library id, or empty for Steam's own movie.</param>
    public void Set(string slot, string id)
    {
        switch (slot)
        {
            case AnimationSlots.Boot:
                Boot = id;
                break;
            case AnimationSlots.Suspend:
                Suspend = id;
                break;
            case AnimationSlots.Throbber:
                Throbber = id;
                break;
        }
    }

    /// <summary>A copy, for a service that shows a change before the saved configuration reaches it again.</summary>
    /// <returns>The copy.</returns>
    public AnimationsConfig Clone()
    {
        return new AnimationsConfig
        {
            Boot = Boot,
            Suspend = Suspend,
            Throbber = Throbber,
            ShuffleOnStart = ShuffleOnStart,
            ShuffleExclusions = [.. ShuffleExclusions]
        };
    }
}
