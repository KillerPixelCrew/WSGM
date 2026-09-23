using System.Collections.Generic;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>What the overlay's Game Library view says about the library and each of its titles.</summary>
/// <remarks>
///     Pure, so the wording the view shows is tested without building a window. The Steam page words
///     the same facts its own way; both read the one published state.
/// </remarks>
internal static class GameLibraryRows
{
    /// <summary>What an action is called on screen.</summary>
    /// <param name="action">The action name the state publishes.</param>
    /// <returns>Its label; an unknown action is shown by its own name rather than hidden.</returns>
    internal static string Action(string action)
    {
        return action switch
        {
            "Add" => "Add",
            "Update" => "Update",
            "Adopt" => "Adopt",
            "Skip" => "Already imported",
            "Remove" => "Remove",
            "Conflict" => "Edited by hand",
            _ => action
        };
    }

    /// <summary>What a launch mode is called on screen.</summary>
    /// <param name="mode">The mode name the state publishes.</param>
    /// <returns>Its label.</returns>
    internal static string Mode(string mode)
    {
        return mode switch
        {
            "ControllerOnly" => "Controller only",
            "SteamIntegration" => "Steam overlay",
            _ => mode
        };
    }

    /// <summary>One title's line in the review list.</summary>
    /// <param name="entry">The title.</param>
    /// <returns>What would happen to it, how it launches, and how.</returns>
    internal static string Describe(GameLibraryEntry entry)
    {
        List<string> parts =
        [
            entry.Excluded ? "Not importing" : Action(entry.Action),
            entry.LaunchLabel,
            Mode(entry.Mode)
        ];
        if (entry.Selected)
        {
            parts.Insert(0, "Selected");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Whether a title is already in Steam as an entry the library manages.</summary>
    /// <param name="entry">The title.</param>
    /// <returns>True once it has an app id and is not on its way out.</returns>
    internal static bool InSteam(GameLibraryEntry entry)
    {
        return entry.AppId > 0 && entry.Action is not ("Remove" or "Conflict");
    }

    /// <summary>What the Store's artwork did for a title.</summary>
    /// <param name="entry">The title.</param>
    /// <returns>Offered, and applied once it has been imported.</returns>
    internal static string Artwork(GameLibraryEntry entry)
    {
        var offered = entry.ArtworkOffered == 1
            ? "1 Store image offered"
            : $"{entry.ArtworkOffered} Store images offered";
        return entry.ArtworkApplied is { } applied ? $"{offered}, {applied} applied" : offered;
    }

    /// <summary>The home level's summary of the last scan.</summary>
    /// <param name="state">The published state.</param>
    /// <returns>What a sync would do, or a prompt to scan when nothing has been scanned.</returns>
    internal static string Summary(GameLibraryState state)
    {
        if (state.Entries.Count == 0)
        {
            return state.Phase == "idle"
                ? "Scan to see which games can be brought into Steam."
                : "No games were found.";
        }

        return $"{state.AddCount} to add, {state.UpdateCount} to update, {state.SkipCount} already imported"
               + (state.RemoveCount > 0 ? $", {state.RemoveCount} to remove" : string.Empty)
               + (state.ConflictCount > 0 ? $", {state.ConflictCount} edited by hand" : string.Empty);
    }

    /// <summary>What the launch-mode row offers for a title.</summary>
    /// <param name="entry">The title.</param>
    /// <returns>The row's description.</returns>
    internal static string ModeChoice(GameLibraryEntry entry)
    {
        if (!entry.CanUseSteamIntegration)
        {
            return "Controller only. There is no validated way to give this title the Steam overlay.";
        }

        return entry.Mode == "SteamIntegration"
            ? "Steam overlay. Press to switch to controller only."
            : entry.RequiresAcknowledgement
                ? "Controller only. This title is multiplayer; switching asks you to accept the risk."
                : "Controller only. Press to switch to the Steam overlay.";
    }
}
