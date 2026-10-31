namespace WSGM.Core;

/// <summary>How an installed tool version relates to the latest published one.</summary>
internal enum GameToolVersionRelation
{
    /// <summary>One side is unknown or the two cannot be compared.</summary>
    Unknown,

    /// <summary>The installed copy is current.</summary>
    Current,

    /// <summary>A newer release, or a rebuild of the same number, is published.</summary>
    UpdateAvailable,

    /// <summary>The installed copy is newer than the latest stable release.</summary>
    NewerThanLatest
}
