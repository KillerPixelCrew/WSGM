using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace WSGM.Core;

/// <summary>What WSGM remembers about one entry it created.</summary>
public sealed class ImportedEntry
{
    /// <summary>Which source it came from.</summary>
    public string Source { get; set; } = "";

    /// <summary>Its stable identity in that source. The AUMID, for Xbox.</summary>
    public string Key { get; set; } = "";

    /// <summary>The shortcut id Steam generated, or zero while unconfirmed.</summary>
    [JsonIgnore]
    public uint AppId { get; set; }

    /// <summary>What the entry was called when it was created.</summary>
    public string Name { get; set; } = "";

    /// <summary>Exactly the Target that was written.</summary>
    [JsonIgnore]
    public string Target { get; set; } = "";

    /// <summary>Exactly the Launch Arguments that were written.</summary>
    [JsonIgnore]
    public string LaunchOptions { get; set; } = "";

    /// <summary>The exact working directory written to Steam, retained for edit detection.</summary>
    [JsonIgnore]
    public string StartDirectory { get; set; } = "";

    /// <summary>Emulator shortcut command persisted before an update, settled only by a Steam answer or observation.</summary>
    [JsonIgnore]
    public ShortcutFields? PendingLaunch { get; set; }

    /// <summary>The content and launch metadata resolved by the installed helper.</summary>
    public ManagedContentRecord? Content { get; set; }

    /// <summary>Which input mode it was generated for: the packaged route's choice.</summary>
    [JsonIgnore]
    public string Mode { get; set; } = "";

    /// <summary>Which command route it was generated for, or empty for the packaged route.</summary>
    [JsonIgnore]
    public string Route { get; set; } = "";

    /// <summary>Whether the user accepted the ban risk for this title.</summary>
    [JsonIgnore]
    public bool Acknowledged { get; set; }

    /// <summary>When it was created, round-trip UTC.</summary>
    [JsonIgnore]
    public string ImportedUtc { get; set; } = "";

    /// <summary>When its id was confirmed by a library read, or empty while unconfirmed.</summary>
    [JsonIgnore]
    public string ConfirmedUtc { get; set; } = "";

    /// <summary>How many images, from any source or provider, have been applied to its shortcut.</summary>
    [JsonIgnore]
    public int ArtworkApplied { get; set; }

    /// <summary>Whether an import created the per-game profile holding its controller override.</summary>
    /// <remarks>
    ///     Only such a profile is removed when the override is cleared and nothing else is left in it.
    ///     A profile the user made, or had before an entry was adopted, keeps its name, executables
    ///     and switch.
    /// </remarks>
    [JsonIgnore]
    public bool OwnsProfile { get; set; }

    /// <summary>Account-local shortcut receipts, the only persisted shortcut fields.</summary>
    public List<ImportAccountMapping> SteamMappings { get; set; } = [];

    /// <summary>The account represented by this detached flattened projection.</summary>
    [JsonIgnore]
    public uint? AccountId { get; set; }

    /// <summary>Whether the current Steam account could not be established.</summary>
    [JsonIgnore]
    public bool AccountUnknown { get; set; }

    /// <summary>Whether the projection has an active-account shortcut receipt.</summary>
    [JsonIgnore]
    public bool HasSteamMapping { get; set; }

    /// <summary>Whether an exact account observation is still needed for a legacy receipt.</summary>
    [JsonIgnore]
    public bool HasUnresolvedLegacyMapping { get; set; }

    /// <summary>Whether the confirmed account no longer contains this previously created shortcut.</summary>
    [JsonIgnore]
    public bool RemovedFromSteam { get; set; }

    /// <summary>The active account's explicit artwork choices already handled for this shortcut.</summary>
    [JsonIgnore]
    public Dictionary<ArtworkAsset, AppliedArtworkChoice> SettledArtwork { get; set; } = [];

    /// <summary>Copies this import record for a detached caller projection.</summary>
    /// <returns>A new record object carrying the same scalar and string values.</returns>
    internal ImportedEntry Copy()
    {
        var copy = (ImportedEntry)MemberwiseClone();
        copy.Content = Content?.Copy();
        copy.SettledArtwork = new Dictionary<ArtworkAsset, AppliedArtworkChoice>(SettledArtwork);
        copy.SteamMappings = [.. SteamMappings.Select(mapping => mapping.Copy())];
        return copy;
    }
}
