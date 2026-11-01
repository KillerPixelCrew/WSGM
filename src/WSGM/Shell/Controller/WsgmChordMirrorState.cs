namespace WSGM.Shell;

/// <summary>What the chord reset hook publishes: whether the mirror is watching.</summary>
/// <param name="Active">Whether the guide chord mirror is active, so a reset needs reporting.</param>
internal sealed record WsgmChordMirrorState(bool Active);
