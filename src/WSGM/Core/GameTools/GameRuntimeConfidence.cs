namespace WSGM.Core;

/// <summary>How sure the resolver is about the runtime executable.</summary>
internal enum GameRuntimeConfidence
{
    /// <summary>The user confirmed it earlier and it is still present.</summary>
    Confirmed,

    /// <summary>An engine layout or Steam's shortcut target identifies it.</summary>
    Structural,

    /// <summary>It is the only plausible candidate, or decisively ahead of the others.</summary>
    Single,

    /// <summary>Several candidates are plausible; the user must choose.</summary>
    NeedsChoice
}
