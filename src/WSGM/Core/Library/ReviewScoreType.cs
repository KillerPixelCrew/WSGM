namespace WSGM.Core;

/// <summary>Which review score a <see cref="FilterKind.ReviewScore" /> filter reads.</summary>
public enum ReviewScoreType
{
    /// <summary>Metacritic score (0–100).</summary>
    Metacritic,

    /// <summary>Steam positive-review percentage (0–100).</summary>
    SteamPercent
}
