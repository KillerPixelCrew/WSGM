using System;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Core;

/// <summary>Which profile layer supplied a value.</summary>
public enum ProfileSource
{
    /// <summary>Neither layer sets it. WSGM writes nothing and the device keeps what it has.</summary>
    None,

    /// <summary>The Global profile.</summary>
    Global,

    /// <summary>The running game's enabled profile.</summary>
    Game
}

/// <summary>A resolved value and the layer it came from.</summary>
/// <typeparam name="T">The nullable value type.</typeparam>
/// <param name="Value">The value, or null when nothing sets it.</param>
/// <param name="Source">The layer that supplied it.</param>
public readonly record struct Resolved<T>(T Value, ProfileSource Source)
{
    /// <summary>Whether the running game overrides this value.</summary>
    public bool IsGameOverride => Source is ProfileSource.Game;
}

/// <summary>The application WSGM treats as running, and the game profile that matched it.</summary>
/// <param name="ApplicationId">Canonical application identity, or null when nothing is running.</param>
/// <param name="Executable">The running executable, once known.</param>
/// <param name="SteamAppId">The Steam AppID, when the application has one.</param>
/// <param name="GameProfileId">The matched game profile, enabled or not.</param>
/// <param name="Enabled">Whether the matched profile's values are in force.</param>
public sealed record ActiveProfile(
    string? ApplicationId,
    string? Executable,
    uint? SteamAppId,
    string? GameProfileId,
    bool Enabled)
{
    /// <summary>Nothing is running.</summary>
    public static ActiveProfile None { get; } = new(null, null, null, null, false);

    /// <summary>Whether an application is running at all.</summary>
    public bool HasApplication => ApplicationId is { Length: > 0 };
}

/// <summary>The two layers a value can come from, for one running application.</summary>
/// <param name="Global">The Global layer.</param>
/// <param name="Game">The game layer, only when its profile is enabled.</param>
public readonly record struct ProfileLayers(ProfileValues Global, ProfileValues? Game)
{
    /// <summary>The setting that holds the manual power target for the current mode.</summary>
    public ProfileSettingKey PowerTargetKey => Value(values => values.TdpUnified).Value == true
        ? new ProfileSettingKey(ProfileField.UnifiedWatts)
        : new ProfileSettingKey(ProfileField.SustainedWatts);

    /// <summary>The number of settings the game layer overrides.</summary>
    public int GameOverrideCount => Game?.Count() ?? 0;

    /// <summary>Resolves a value-typed member: Game, then Global, then nothing.</summary>
    /// <typeparam name="T">The member's value type.</typeparam>
    /// <param name="read">Reads the member from one layer.</param>
    /// <returns>The value and its source.</returns>
    public Resolved<T?> Value<T>(Func<ProfileValues, T?> read) where T : struct
    {
        return Game is not null && read(Game) is { } game ? new Resolved<T?>(game, ProfileSource.Game)
            : read(Global) is { } global ? new Resolved<T?>(global, ProfileSource.Global)
            : new Resolved<T?>(null, ProfileSource.None);
    }

    /// <summary>Resolves a reference-typed member: Game, then Global, then nothing.</summary>
    /// <typeparam name="T">The member's type.</typeparam>
    /// <param name="read">Reads the member from one layer.</param>
    /// <returns>The value and its source.</returns>
    public Resolved<T?> Reference<T>(Func<ProfileValues, T?> read) where T : class
    {
        return Game is not null && read(Game) is { } game ? new Resolved<T?>(game, ProfileSource.Game)
            : read(Global) is { } global ? new Resolved<T?>(global, ProfileSource.Global)
            : new Resolved<T?>(null, ProfileSource.None);
    }

    /// <summary>Resolves one device capability value.</summary>
    /// <param name="deviceIdentityKey">Stable local device identity.</param>
    /// <param name="capabilityId">The capability.</param>
    /// <param name="instanceId">Its instance, or null.</param>
    /// <returns>The value and its source.</returns>
    public Resolved<CapabilityValue?> Device(string deviceIdentityKey, string capabilityId, string? instanceId)
    {
        return Reference(values => values.FindDevice(deviceIdentityKey, capabilityId, instanceId)?.Value);
    }

    /// <summary>Resolves the layer supplying one setting.</summary>
    /// <param name="key">The setting.</param>
    /// <param name="deviceIdentityKey">The device, for a device setting; null matches any device.</param>
    /// <returns>The layer that sets it.</returns>
    public ProfileSource Source(ProfileSettingKey key, string? deviceIdentityKey = null)
    {
        return Game is not null && Game.Has(key, deviceIdentityKey) ? ProfileSource.Game
            : Global.Has(key, deviceIdentityKey) ? ProfileSource.Global
            : ProfileSource.None;
    }

    /// <summary>The manual power preferences in force, each field resolved on its own.</summary>
    /// <returns>The merged preferences, or null when no layer sets any of them.</returns>
    /// <remarks>
    ///     Field by field on purpose. Resolving the record as a whole let a game that set only its mode
    ///     hide every Global wattage behind nulls.
    /// </remarks>
    public ManualTdpProfile? ManualTdp()
    {
        var unified = Value(values => values.TdpUnified);
        var unifiedWatts = Value(values => values.UnifiedWatts);
        var sustained = Value(values => values.SustainedWatts);
        var boost = Value(values => values.BoostWatts);
        return unified.Source is ProfileSource.None
               && unifiedWatts.Source is ProfileSource.None
               && sustained.Source is ProfileSource.None
               && boost.Source is ProfileSource.None
            ? null
            : new ManualTdpProfile(unified.Value ?? false, unifiedWatts.Value, sustained.Value, boost.Value);
    }
}

/// <summary>An immutable view of the profile store and the application it is resolved for.</summary>
/// <param name="Config">A detached copy of the store. Never mutate it.</param>
/// <param name="Active">The running application and its matched profile.</param>
/// <param name="Generation">Increases with every published change.</param>
public sealed record ProfileSnapshot(ProfileConfig Config, ActiveProfile Active, long Generation)
{
    /// <summary>An empty store with nothing running.</summary>
    public static ProfileSnapshot Empty { get; } = new(new ProfileConfig(), ActiveProfile.None, 0);

    /// <summary>The matched game profile, enabled or not.</summary>
    public GameProfile? Game => ProfileResolver.FindGame(Config, Active.GameProfileId);

    /// <summary>The layers values resolve from.</summary>
    public ProfileLayers Layers => ProfileResolver.Layers(Config, Active);

    /// <summary>Whether an edit made now lands in the game profile rather than Global.</summary>
    public bool EditsGame => Active.Enabled && Game is not null;
}

/// <summary>Matches the running application to a game profile and resolves the layers.</summary>
public static class ProfileResolver
{
    /// <summary>Matches a running application against the game profiles.</summary>
    /// <param name="config">The profile store.</param>
    /// <param name="applicationId">Canonical application identity.</param>
    /// <param name="executable">The running executable, once known.</param>
    /// <returns>The single matching profile, or null.</returns>
    public static GameProfile? Match(ProfileConfig config, string? applicationId, string? executable)
    {
        ArgumentNullException.ThrowIfNull(config);
        return ApplicationProfileRules.Match(config.Games, applicationId, executable,
            game => game.Id, game => game.ProcessNames);
    }

    /// <summary>Builds the active profile for a running application.</summary>
    /// <param name="config">The profile store.</param>
    /// <param name="applicationId">Canonical application identity, or null when nothing runs.</param>
    /// <param name="executable">The running executable, once known.</param>
    /// <param name="steamAppId">The Steam AppID, when known.</param>
    /// <returns>The active profile.</returns>
    public static ActiveProfile Activate(ProfileConfig config, string? applicationId, string? executable,
        uint? steamAppId)
    {
        if (applicationId is not { Length: > 0 })
        {
            return ActiveProfile.None;
        }

        var game = Match(config, applicationId, executable);
        return new ActiveProfile(applicationId, executable, steamAppId, game?.Id, game?.Enabled == true);
    }

    /// <summary>Finds a game profile by id.</summary>
    /// <param name="config">The profile store.</param>
    /// <param name="id">The profile id.</param>
    /// <returns>The profile, or null.</returns>
    public static GameProfile? FindGame(ProfileConfig config, string? id)
    {
        return id is { Length: > 0 }
            ? config.Games.FirstOrDefault(game => string.Equals(game.Id, id, StringComparison.Ordinal))
            : null;
    }

    /// <summary>The layers values resolve from for an active profile.</summary>
    /// <param name="config">The profile store.</param>
    /// <param name="active">The running application.</param>
    /// <returns>Global, plus the game layer when its profile is enabled.</returns>
    public static ProfileLayers Layers(ProfileConfig config, ActiveProfile active)
    {
        ArgumentNullException.ThrowIfNull(config);
        var game = active.Enabled ? FindGame(config, active.GameProfileId) : null;
        return new ProfileLayers(config.Global, game?.Enabled == true ? game.Values : null);
    }
}
