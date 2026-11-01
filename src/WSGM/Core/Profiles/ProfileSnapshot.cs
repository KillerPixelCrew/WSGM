namespace WSGM.Core;

/// <summary>An immutable view of the profile store and the application it is resolved for.</summary>
/// <param name="config">A detached copy of the store. Never mutate it.</param>
/// <param name="active">The running application and its matched profile.</param>
/// <param name="generation">Increases with every published change.</param>
public sealed class ProfileSnapshot(ProfileConfig config, ActiveProfile active, long generation)
{
    /// <summary>A detached copy of the store. Never mutate it.</summary>
    public ProfileConfig Config { get; } = config;

    /// <summary>The running application and its matched profile.</summary>
    public ActiveProfile Active { get; } = active;

    /// <summary>Increases with every published change.</summary>
    public long Generation { get; } = generation;

    /// <summary>An empty store with nothing running.</summary>
    public static ProfileSnapshot Empty { get; } = new(new ProfileConfig(), ActiveProfile.None, 0);

    /// <summary>The matched game profile, enabled or not.</summary>
    public GameProfile? Game { get; } = ProfileResolver.FindGame(config, active.GameProfileId);

    /// <summary>The layers values resolve from.</summary>
    public ProfileLayers Layers { get; } = ProfileResolver.Layers(config, active);

    /// <summary>Whether an edit made now lands in the game profile rather than Global.</summary>
    public bool EditsGame => Active.Enabled && Game is not null;
}
