using System.Collections.Generic;
using System.Linq;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>What the overlay's Animations view says about the choice and each movie.</summary>
/// <remarks>Pure, so the wording is tested without building a window.</remarks>
internal static class AnimationsRows
{
    /// <summary>The home level's summary.</summary>
    /// <param name="state">The published state.</param>
    /// <returns>Which movie Big Picture starts with, and whether Steam has to restart to show it.</returns>
    internal static string Summary(SteamAnimationsState state)
    {
        var selected = state.Library.FirstOrDefault(item => item.Id == state.Selected);
        var line = selected is null
            ? "Big Picture starts with Steam's own movie."
            : $"Big Picture starts with {selected.Name}.";
        if (state.Settings.RestartNeeded)
        {
            line += " Restart Steam to see the change.";
        }

        return line;
    }

    /// <summary>One library entry's line.</summary>
    /// <param name="item">The entry.</param>
    /// <param name="state">The published state, for whether it plays.</param>
    /// <returns>Whether it is the user's file, its author, and whether it plays at boot.</returns>
    internal static string Describe(SteamAnimationsItem item, SteamAnimationsState state)
    {
        List<string> parts = [item.Custom ? "Your file" : "SteamDeckRepo"];
        if (item.Author.Length > 0)
        {
            parts.Add(item.Author);
        }

        if (state.Selected == item.Id)
        {
            parts.Add("Plays at boot");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>One repository listing's line.</summary>
    /// <param name="item">The listing.</param>
    /// <returns>Its author, date, likes, downloads and whether the library holds it.</returns>
    internal static string DescribeListing(SteamAnimationsItem item)
    {
        List<string> parts = [];
        if (item.Author.Length > 0)
        {
            parts.Add(item.Author);
        }

        if (item.Updated.Length > 0)
        {
            parts.Add(item.Updated);
        }

        parts.Add($"{item.Likes} likes");
        parts.Add($"{item.Downloads} downloads");
        if (item.Downloaded)
        {
            parts.Add("In the library");
        }

        return string.Join(" · ", parts);
    }
}
