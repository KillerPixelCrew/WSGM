using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>Which layer an edit writes to.</summary>
public enum ProfileLayer
{
    /// <summary>The running game's profile when it is enabled, Global otherwise.</summary>
    Active,

    /// <summary>Always Global.</summary>
    Global
}

/// <summary>Pure edits of the profile store, applied inside one configuration mutation.</summary>
/// <remarks>
///     Nothing here copies a value from one layer into another. Opting a game in creates an empty
///     profile, and every value in it is one the user set while it was active.
/// </remarks>
public static class ProfileEdits
{
    private const string NamedProfilePrefix = "profile:";

    /// <summary>The layer an edit lands in.</summary>
    /// <param name="config">The store being mutated.</param>
    /// <param name="active">The running application when the edit was made.</param>
    /// <param name="layer">The requested layer.</param>
    /// <returns>
    ///     The layer to write, or null when the game profile the edit was meant for no longer exists.
    ///     Writing Global instead would widen a per-game change to every game.
    /// </returns>
    public static ProfileValues? Target(ProfileConfig config, ActiveProfile active, ProfileLayer layer)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(active);
        if (layer is ProfileLayer.Global || !active.Enabled)
        {
            return config.Global;
        }

        return ProfileResolver.FindGame(config, active.GameProfileId) is { Enabled: true } game
            ? game.Values
            : null;
    }

    /// <summary>Turns the running application's profile on or off.</summary>
    /// <param name="config">The store being mutated.</param>
    /// <param name="active">The running application.</param>
    /// <param name="enabled">The requested state.</param>
    /// <returns>Whether anything changed.</returns>
    /// <remarks>
    ///     Turning a profile on for the first time creates it empty. Turning it off keeps its values, so
    ///     turning it back on restores them.
    /// </remarks>
    public static bool SetGameEnabled(ProfileConfig config, ActiveProfile active, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(active);
        if (!active.HasApplication)
        {
            return false;
        }

        var game = ProfileResolver.Match(config, active.ApplicationId, active.Executable);
        if (game is not null)
        {
            if (game.Enabled == enabled)
            {
                return false;
            }

            game.Enabled = enabled;
            return true;
        }

        if (!enabled)
        {
            return false;
        }

        var executable = ValidExecutable(active.Executable);
        var idTaken = config.Games.Any(entry => entry.Id == active.ApplicationId);
        if (idTaken && executable is null)
        {
            // A generated id with no executable matches nothing: neither a process rule nor the
            // identity fallback could ever select it, so every press would store another dead profile.
            return false;
        }

        config.Games.Add(new GameProfile
        {
            Id = idTaken
                ? NamedProfilePrefix + Guid.NewGuid().ToString("N")
                : active.ApplicationId!,
            Name = executable ?? active.ApplicationId!,
            ProcessNames = executable is null ? [] : [executable],
            Enabled = true
        });
        return true;
    }

    /// <summary>Clears every value in one game profile, keeping the profile and its binding.</summary>
    /// <param name="config">The store being mutated.</param>
    /// <param name="gameId">The game profile.</param>
    /// <returns>Whether anything changed.</returns>
    public static bool ResetGame(ProfileConfig config, string gameId)
    {
        if (ProfileResolver.FindGame(config, gameId) is not { } game || game.Values.Count() == 0)
        {
            return false;
        }

        game.Values = new ProfileValues();
        return true;
    }

    /// <summary>Clears the Performance-tab fields from Global.</summary>
    /// <param name="config">The store being mutated.</param>
    /// <returns>Whether anything changed.</returns>
    /// <remarks>Device values such as lighting are left alone: this is the Performance tab's reset.</remarks>
    public static bool ResetGlobalPerformance(ProfileConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var changed = false;
        foreach (var field in ProfileFields.PerformanceTab)
        {
            changed |= config.Global.Clear(new ProfileSettingKey(field));
        }

        return changed;
    }

    /// <summary>Clears every reference to a deleted authored profile.</summary>
    /// <param name="config">The store being mutated.</param>
    /// <param name="profileId">The deleted profile.</param>
    /// <returns>Whether anything changed.</returns>
    public static bool RemoveFanCurveReferences(ProfileConfig config, string profileId)
    {
        ArgumentNullException.ThrowIfNull(config);
        var changed = false;
        foreach (var values in config.Games.Select(game => game.Values).Prepend(config.Global))
        {
            if (string.Equals(values.FanCurveProfileId, profileId, StringComparison.Ordinal))
            {
                values.FanCurveProfileId = null;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>Creates or updates a game profile's name, activation processes and switch.</summary>
    /// <param name="config">The store being mutated.</param>
    /// <param name="id">The profile id, or null to create a named profile.</param>
    /// <param name="name">The name.</param>
    /// <param name="processNames">Executables that activate it.</param>
    /// <param name="enabled">Whether its values apply.</param>
    /// <returns>The profile id.</returns>
    /// <exception cref="ArgumentException">The name, processes or a conflict are invalid.</exception>
    public static string SaveGame(ProfileConfig config, string? id, string name,
        IEnumerable<string> processNames, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(config);
        name = name.Trim();
        if (name.Length is 0 or > GameProfile.MaxNameLength || name.Any(char.IsControl))
        {
            throw new ArgumentException("Give the profile a name of 1 to 80 characters.");
        }

        var processes = ApplicationProfileRules.ValidateProcesses(processNames);
        var existing = ProfileResolver.FindGame(config, id);
        if (processes.Length == 0
            && (existing is null || existing.Id.StartsWith(NamedProfilePrefix, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Add at least one executable that activates this profile.");
        }

        var conflict = config.Games.FirstOrDefault(game => !ReferenceEquals(game, existing)
                                                           && game.ProcessNames.Intersect(processes,
                                                               StringComparer.OrdinalIgnoreCase).Any());
        if (conflict is not null)
        {
            throw new ArgumentException(
                $"An executable is already assigned to {(string.IsNullOrEmpty(conflict.Name) ? conflict.Id : conflict.Name)}.");
        }

        if (existing is null)
        {
            existing = new GameProfile { Id = NamedProfilePrefix + Guid.NewGuid().ToString("N") };
            config.Games.Add(existing);
        }

        existing.Name = name;
        existing.ProcessNames = [.. processes];
        existing.Enabled = enabled;
        return existing.Id;
    }

    /// <summary>Deletes a game profile. Matching applications then use Global.</summary>
    /// <param name="config">The store being mutated.</param>
    /// <param name="id">The profile id.</param>
    /// <returns>Whether anything changed.</returns>
    public static bool DeleteGame(ProfileConfig config, string id)
    {
        return config.Games.RemoveAll(game => string.Equals(game.Id, id, StringComparison.Ordinal)) > 0;
    }

    private static string? ValidExecutable(string? executable)
    {
        if (executable is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            return ApplicationProfileRules.ValidateProcesses([executable]).FirstOrDefault();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
