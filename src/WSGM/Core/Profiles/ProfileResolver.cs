using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>Matches the running application to a game profile and resolves the layers.</summary>
public static class ProfileResolver
{
    /// <summary>The identity prefix of an application known only by its executable.</summary>
    private const string ProcessIdPrefix = "process:";

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

    /// <summary>Every executable name a game profile is known to run as.</summary>
    /// <param name="game">The game profile.</param>
    /// <returns>
    ///     Its activation names, the names learned while it ran and the name in a <c>process:</c> identity,
    ///     without duplicates.
    /// </returns>
    public static IReadOnlyList<string> KnownExecutables(GameProfile game)
    {
        ArgumentNullException.ThrowIfNull(game);
        List<string> names = [.. game.ProcessNames, .. game.Executables];
        if (game.Id.StartsWith(ProcessIdPrefix, StringComparison.Ordinal)
            && game.Id.Length > ProcessIdPrefix.Length)
        {
            names.Add(game.Id[ProcessIdPrefix.Length..]);
        }

        return [.. names.Distinct(StringComparer.OrdinalIgnoreCase)];
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
