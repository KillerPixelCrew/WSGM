using WSGM.Core;

namespace WSGM.Tests.Builders;

/// <summary>One decision from a recorded AutoTDP trace and the decision replay produced for it.</summary>
/// <param name="Row">The trace row number.</param>
/// <param name="Recorded">What the live controller decided, or null in a hand-authored fixture.</param>
/// <param name="Replayed">What a fresh controller decides from the same recorded inputs.</param>
internal sealed record AutoTdpReplayedDecision(long Row, AutoTdpDecision? Recorded, AutoTdpDecision Replayed);
