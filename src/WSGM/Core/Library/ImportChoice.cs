using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>What the user decided about one title, kept across scans.</summary>
/// <remarks>
///     <para>
///         The Game Library's equivalent of Steam ROM Manager's user exceptions. A scan rebuilds
///         every entry from what the sources and Steam say now; without these, a mode picked and not
///         yet applied, or a title the user said not to import, would come back undone on the next
///         scan.
///     </para>
///     <para>
///         A choice describes intent, and a record describes what Steam has. Once an apply writes a
///         record for a title, everything in its choice that Steam now holds is dropped, so the record
///         is the one truth for anything already imported. Only a match the user fixed stays, because
///         Steam keeps no trace of which game the artwork came from.
///     </para>
/// </remarks>
public sealed class ImportChoice
{
    /// <summary>Which source the title belongs to.</summary>
    public string Source { get; set; } = "";

    /// <summary>Its identity in that source.</summary>
    public string Key { get; set; } = "";

    /// <summary>The launch mode the user picked, or empty when they have not picked one.</summary>
    public string Mode { get; set; } = "";

    /// <summary>Whether the user accepted the ban risk along with that mode.</summary>
    public bool Acknowledged { get; set; }

    /// <summary>Whether the user said not to import this title.</summary>
    public bool Excluded { get; set; }

    /// <summary>The command route the user picked, or empty when they have not picked one.</summary>
    public string Route { get; set; } = "";

    /// <summary>The title identification override kept across scans.</summary>
    public string TitleName { get; set; } = "";

    /// <summary>The per-title emulator installation choice; empty follows its source.</summary>
    public string EmulatorInstallationId { get; set; } = "";

    /// <summary>The per-title RetroArch core choice.</summary>
    public string CoreId { get; set; } = "";

    /// <summary>Whether the user explicitly selected shortcut cleanup.</summary>
    public bool Cleanup { get; set; }

    /// <summary>Per-title typed arguments; empty follows its source and emulator definition.</summary>
    public List<string> Arguments { get; set; } = [];

    /// <summary>The artwork the user picked, one per artwork type at most.</summary>
    public List<ArtworkPick> Artwork { get; set; } = [];

    /// <summary>The provider of the game the user matched the title to, or empty for the automatic match.</summary>
    public string MatchProvider { get; set; } = "";

    /// <summary>That provider's id for the game.</summary>
    public string MatchId { get; set; } = "";

    /// <summary>That game's name, as the provider calls it.</summary>
    public string MatchName { get; set; } = "";

    internal ImportChoice Copy()
    {
        var copy = (ImportChoice)MemberwiseClone();
        copy.Artwork = [.. Artwork.Select(pick => new ArtworkPick
        {
            Asset = pick.Asset, Url = pick.Url, Thumb = pick.Thumb, Provider = pick.Provider, Revision = pick.Revision
        })];
        copy.Arguments = [.. Arguments];
        return copy;
    }

    /// <summary>Whether this choice says anything at all, so an empty one can be dropped.</summary>
    /// <returns>True when nothing is left in it.</returns>
    public bool IsEmpty()
    {
        return Mode.Length == 0 && !Acknowledged && !Excluded && Route.Length == 0 && Artwork.Count == 0
               && MatchId.Length == 0 && TitleName.Length == 0 && EmulatorInstallationId.Length == 0
               && CoreId.Length == 0 && !Cleanup && Arguments.Count == 0;
    }

    /// <summary>The picked mode, when there is one.</summary>
    /// <returns>The mode, or null when the user has not picked one.</returns>
    public ImportMode? PickedMode()
    {
        // Numeric strings parse too, whether or not they name a mode.
        return Enum.TryParse<ImportMode>(Mode, false, out var mode) && Enum.IsDefined(mode) ? mode : null;
    }
}
