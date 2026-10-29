using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>One explicitly selected artwork revision handled by an account's shortcut.</summary>
/// <param name="Url">The selected image URL, or empty for a clear.</param>
/// <param name="Revision">The explicit selection identity, retained across scans.</param>
public sealed record AppliedArtworkChoice(string Url, string Revision);

/// <summary>One Steam account's exact shortcut command, ownership and unsettled work for a title.</summary>
public sealed class ImportAccountMapping
{
    /// <summary>The observed userdata account, or null for an unresolved legacy receipt.</summary>
    public uint? AccountId { get; set; }

    /// <summary>The shortcut id, or zero while creation is unconfirmed.</summary>
    public uint AppId { get; set; }

    /// <summary>A complete read of this account confirmed that its previously created shortcut is absent.</summary>
    public bool RemovedFromSteam { get; set; }

    /// <summary>The exact target WSGM wrote.</summary>
    public string Target { get; set; } = "";

    /// <summary>The exact arguments WSGM wrote.</summary>
    public string LaunchOptions { get; set; } = "";

    /// <summary>The exact working directory WSGM wrote.</summary>
    public string StartDirectory { get; set; } = "";

    /// <summary>A command update that must be settled before another write.</summary>
    public ShortcutFields? PendingLaunch { get; set; }

    /// <summary>Whether owned-shortcut cleanup remains to be settled in this account.</summary>
    public bool PendingDeletion { get; set; }

    /// <summary>A native removal was admitted and must be observed before any further mutation.</summary>
    public bool DeletionDispatched { get; set; }

    /// <summary>The selected input mode.</summary>
    public string Mode { get; set; } = "";

    /// <summary>The selected command route.</summary>
    public string Route { get; set; } = "";

    /// <summary>The multiplayer-risk acknowledgement recorded for this shortcut.</summary>
    public bool Acknowledged { get; set; }

    /// <summary>UTC creation intent timestamp.</summary>
    public string ImportedUtc { get; set; } = "";

    /// <summary>UTC confirmation timestamp, empty until observed.</summary>
    public string ConfirmedUtc { get; set; } = "";

    /// <summary>The artwork application receipt.</summary>
    public int ArtworkApplied { get; set; }

    /// <summary>Explicit selections already handled by this account, including clears and uncertain dispatched writes.</summary>
    public Dictionary<ArtworkAsset, AppliedArtworkChoice> SettledArtwork { get; set; } = [];

    /// <summary>Whether this import owns the per-game profile.</summary>
    public bool OwnsProfile { get; set; }

    internal ImportAccountMapping Copy()
    {
        var copy = (ImportAccountMapping)MemberwiseClone();
        copy.SettledArtwork = new Dictionary<ArtworkAsset, AppliedArtworkChoice>(SettledArtwork);
        return copy;
    }

    internal bool SameAs(ImportAccountMapping other)
    {
        return AccountId == other.AccountId && AppId == other.AppId && RemovedFromSteam == other.RemovedFromSteam
               && Target == other.Target
               && LaunchOptions == other.LaunchOptions && StartDirectory == other.StartDirectory
               && PendingLaunch == other.PendingLaunch && PendingDeletion == other.PendingDeletion
               && DeletionDispatched == other.DeletionDispatched
               && Mode == other.Mode && Route == other.Route && Acknowledged == other.Acknowledged
               && ImportedUtc == other.ImportedUtc && ConfirmedUtc == other.ConfirmedUtc
               && ArtworkApplied == other.ArtworkApplied && OwnsProfile == other.OwnsProfile
               && SettledArtwork.Count == other.SettledArtwork.Count
               && SettledArtwork.All(pair => other.SettledArtwork.TryGetValue(pair.Key, out var url) && url == pair.Value);
    }

    internal static ImportAccountMapping From(ImportedEntry entry, uint? accountId)
    {
        return new ImportAccountMapping
        {
            AccountId = accountId, AppId = entry.AppId, RemovedFromSteam = entry.RemovedFromSteam, Target = entry.Target,
            LaunchOptions = entry.LaunchOptions, StartDirectory = entry.StartDirectory,
            PendingLaunch = entry.PendingLaunch, PendingDeletion = entry.PendingDeletion,
            DeletionDispatched = entry.DeletionDispatched,
            Mode = entry.Mode, Route = entry.Route, Acknowledged = entry.Acknowledged,
            ImportedUtc = entry.ImportedUtc, ConfirmedUtc = entry.ConfirmedUtc,
            ArtworkApplied = entry.ArtworkApplied, OwnsProfile = entry.OwnsProfile,
            SettledArtwork = new Dictionary<ArtworkAsset, AppliedArtworkChoice>(entry.SettledArtwork)
        };
    }

    internal void Project(ImportedEntry entry)
    {
        entry.AccountId = AccountId;
        entry.AppId = AppId;
        entry.RemovedFromSteam = RemovedFromSteam;
        entry.Target = Target;
        entry.LaunchOptions = LaunchOptions;
        entry.StartDirectory = StartDirectory;
        entry.PendingLaunch = PendingLaunch;
        entry.PendingDeletion = PendingDeletion;
        entry.DeletionDispatched = DeletionDispatched;
        entry.Mode = Mode;
        entry.Route = Route;
        entry.Acknowledged = Acknowledged;
        entry.ImportedUtc = ImportedUtc;
        entry.ConfirmedUtc = ConfirmedUtc;
        entry.ArtworkApplied = ArtworkApplied;
        entry.SettledArtwork = new Dictionary<ArtworkAsset, AppliedArtworkChoice>(SettledArtwork);
        entry.OwnsProfile = OwnsProfile;
        entry.HasSteamMapping = true;
    }
}
