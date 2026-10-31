using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>The resolver's answer.</summary>
/// <param name="Engine">The detected engine id.</param>
/// <param name="EngineLabel">The engine shown to the user.</param>
/// <param name="Candidates">All candidates, best first.</param>
/// <param name="Chosen">The chosen candidate, or null when the user must choose.</param>
/// <param name="Confidence">How sure the resolver is.</param>
/// <param name="RememberedMissing">Whether a remembered executable is gone and detection ran again.</param>
internal sealed record GameRuntimeResolution(
    string Engine,
    string EngineLabel,
    IReadOnlyList<GameRuntimeCandidate> Candidates,
    GameRuntimeCandidate? Chosen,
    GameRuntimeConfidence Confidence,
    bool RememberedMissing);
