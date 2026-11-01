using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>One per-game profile.</summary>
public sealed class GameProfile
{
    /// <summary>
    ///     Canonical WSGM application identity, or <c>profile:&lt;guid&gt;</c> for a named profile that
    ///     activates only through <see cref="ProcessNames" />.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>User-visible name; an empty name displays <see cref="Id" />.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Exact executable names that activate this profile, compared without case.</summary>
    public List<string> ProcessNames { get; set; } = [];

    /// <summary>
    ///     Executable names WSGM saw this game run as. They never activate the profile; a graphics driver
    ///     that keeps its own per-application values matches on them. Filled while the game runs, because a
    ///     store title's executable is known only then.
    /// </summary>
    public List<string> Executables { get; set; } = [];

    /// <summary>
    ///     Whether this profile's values apply. This is Steam's "Use per-game profile" switch and the
    ///     overlay's Per-application scope. Off keeps the values, so switching back on restores them.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>The values this game overrides. Everything left unset comes from Global.</summary>
    public ProfileValues Values { get; set; } = new();
}
