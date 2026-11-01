namespace WSGM.Core;

/// <summary>Where the extended VRR range stands on this machine.</summary>
internal enum ExtendedVrrPhase
{
    /// <summary>Not this panel, no Arc Sync, Device Integration off, or not yet decided.</summary>
    Hidden,

    /// <summary>The panel or its override cannot be changed; the status says why.</summary>
    Refused,

    /// <summary>The factory EDID is in force and no override is written.</summary>
    Off,

    /// <summary>The override is written; Windows loads it at the next start.</summary>
    RestartToApply,

    /// <summary>The extended range is in force.</summary>
    Active,

    /// <summary>The override is removed; the extended EDID stays in force until the next start.</summary>
    RestartToRemove,

    /// <summary>The standalone Atlas8Tweaks VRR Fix owns the range.</summary>
    Standalone
}
