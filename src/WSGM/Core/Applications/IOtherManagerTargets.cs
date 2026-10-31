using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>Package activation and dedicated keyboard operations used by manager takeover and recovery.</summary>
public interface IOtherManagerTargets
{
    /// <summary>Whether the exact handheld definition matches this machine.</summary>
    /// <param name="definitionId">Exact built-in LibHandheld definition ID.</param>
    /// <returns>True only for the detected definition.</returns>
    bool MatchesDevice(string definitionId);

    /// <summary>Captures active package/device targets and their original states without changing them.</summary>
    /// <param name="manager">Known manager catalog entry.</param>
    /// <returns>Changes that can be recorded before applying takeover.</returns>
    IReadOnlyList<OtherManagerRecord> Detect(OtherManager manager);

    /// <summary>Applies one recorded change without retrying uncertain output.</summary>
    /// <param name="target">The retained pre-change snapshot.</param>
    /// <returns>Whether Windows accepted the command.</returns>
    bool Disable(OtherManagerRecord target);

    /// <summary>Restores one recorded target; a removed target has nothing to restore.</summary>
    /// <param name="target">The retained pre-change snapshot.</param>
    /// <returns>Whether restoration completed or the target is gone.</returns>
    bool Restore(OtherManagerRecord target);

    /// <summary>Whether an accepted device command requires a restart.</summary>
    bool RestartRequired { get; }
}
