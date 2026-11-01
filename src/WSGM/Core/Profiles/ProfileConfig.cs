using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>The Global profile and every per-game profile.</summary>
/// <remarks>
///     One store for every setting that can differ per game. A game profile holds only what the user
///     changed for that game; everything else is read from <see cref="Global" /> when it is resolved and
///     is never copied in. The rules are in <c>docs\profiles.md</c>.
/// </remarks>
public sealed class ProfileConfig
{
    /// <summary>Values in force when no enabled game profile sets them.</summary>
    public ProfileValues Global { get; set; } = new();

    /// <summary>Per-game profiles, matched against the running application.</summary>
    public List<GameProfile> Games { get; set; } = [];
}
