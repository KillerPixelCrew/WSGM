using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Why the profile snapshot changed.</summary>
internal enum ProfileChangeKind
{
    /// <summary>A different application, or a different match, is now running.</summary>
    Application,

    /// <summary>A value, a profile switch or a profile's binding changed.</summary>
    Values
}

/// <summary>
///     The one owner of the Global and per-game profiles and of which application they resolve for.
/// </summary>
/// <remarks>
///     Every surface writes through here: the overlay rows, Steam's Quick Access rows, Steam's per-game
///     toggle and reset, and the manual power and refresh funnels. Every consumer reads the same
///     snapshot. That is what makes the overlay header, Steam's toggle and every applied value agree
///     about which game is running and which layer is in force. The rules are in
///     <c>docs\profiles.md</c>.
///     <para>
///         Writes are serialized and saved before they are published, so a value that could not be
///         saved never reaches a device. Nothing here touches the disk on the caller's thread: the
///         configuration mutation runs on a worker.
///     </para>
/// </remarks>
internal sealed class ProfileService
{
    private readonly Lock _gate = new();
    private readonly Func<Func<ProfileConfig, bool>, CancellationToken, Task<ProfileConfig>> _mutate;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private ProfileSnapshot _current;
    private PerformanceApplicationTarget? _running;

    /// <summary>Creates the owner.</summary>
    /// <param name="initial">The stored profiles at startup.</param>
    /// <param name="mutate">
    ///     Applies an edit to freshly loaded configuration under the cross-process lock, saves it when the
    ///     edit reports a change, and returns a detached copy of the stored profiles.
    /// </param>
    internal ProfileService(
        ProfileConfig initial,
        Func<Func<ProfileConfig, bool>, CancellationToken, Task<ProfileConfig>> mutate)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _mutate = mutate ?? throw new ArgumentNullException(nameof(mutate));
        _current = new ProfileSnapshot(initial.Copy(), ActiveProfile.None, 1);
    }

    /// <summary>The snapshot in force.</summary>
    internal ProfileSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Raised after a change is published, on the thread that made it.</summary>
    /// <remarks>Subscribers must not block; the session queues the device work.</remarks>
    internal event Action<ProfileSnapshot, ProfileChangeKind>? Changed;

    /// <summary>Tells the store which application is running.</summary>
    /// <param name="target">The running application, or null when none is.</param>
    /// <returns>The snapshot now in force.</returns>
    internal ProfileSnapshot SetRunningApplication(PerformanceApplicationTarget? target)
    {
        ProfileSnapshot next;
        lock (_gate)
        {
            _running = target;
            var active = Activate(_current.Config, target);
            if (active == _current.Active)
            {
                return _current;
            }

            next = _current = _current with { Active = active, Generation = _current.Generation + 1 };
        }

        Log.Info(
            $"Profile: running {Describe(next.Active)}.");
        Raise(next, ProfileChangeKind.Application);
        return next;
    }

    /// <summary>Takes profiles another process saved, such as Settings.</summary>
    /// <param name="stored">The reloaded profiles.</param>
    internal void ApplyConfig(ProfileConfig stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var copy = stored.Copy();
        ProfileSnapshot next;
        bool applicationChanged;
        lock (_gate)
        {
            if (SameStore(copy, _current.Config))
            {
                return;
            }

            var active = Activate(copy, _running);
            applicationChanged = active != _current.Active;
            next = _current = new ProfileSnapshot(copy, active, _current.Generation + 1);
        }

        Raise(next, applicationChanged ? ProfileChangeKind.Application : ProfileChangeKind.Values);
    }

    /// <summary>Stores a value in the layer an edit made now means.</summary>
    /// <param name="write">Writes the value into the layer.</param>
    /// <param name="description">What was set, for the log.</param>
    /// <param name="layer">The layer; <see cref="ProfileLayer.Active" /> follows the per-game switch.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>The snapshot after the save.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The game profile the edit was meant for no longer exists. The edit is refused rather than
    ///     widened to Global.
    /// </exception>
    internal async Task<ProfileSnapshot> SetAsync(
        Action<ProfileValues> write,
        string description,
        ProfileLayer layer = ProfileLayer.Active,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        var refused = false;
        var where = "Global";
        var result = await MutateAsync((config, active) =>
        {
            var target = ProfileEdits.Target(config, active, layer);
            if (target is null)
            {
                refused = true;
                return false;
            }

            where = ReferenceEquals(target, config.Global) ? "Global" : $"game {active.GameProfileId}";
            var before = JsonSerializer.Serialize(target, ConfigJsonContext.Default.ProfileValues);
            write(target);
            return before != JsonSerializer.Serialize(target, ConfigJsonContext.Default.ProfileValues);
        }, cancellationToken).ConfigureAwait(false);
        if (refused)
        {
            throw new InvalidOperationException(
                "The game profile this change was meant for no longer exists.");
        }

        if (result.Changed)
        {
            Log.Info($"Profile: {description} saved to {where}.");
        }

        return result.Snapshot;
    }

    /// <summary>Stores one field value.</summary>
    internal Task<ProfileSnapshot> SetAsync(ProfileField field, int value,
        CancellationToken cancellationToken = default)
    {
        return SetAsync(values => Write(values, field, value), $"{field}={value}", ProfileLayer.Active,
            cancellationToken);
    }

    /// <summary>Stores one device capability value.</summary>
    internal Task<ProfileSnapshot> SetDeviceAsync(string deviceIdentityKey, string capabilityId,
        string? instanceId, CapabilityValue value, CancellationToken cancellationToken = default)
    {
        return SetAsync(values => values.SetDevice(deviceIdentityKey, capabilityId, instanceId, value),
            $"{capabilityId}{(instanceId is { Length: > 0 } ? "#" + instanceId : string.Empty)}",
            ProfileLayer.Active, cancellationToken);
    }

    /// <summary>Removes the running game's override, so the setting falls back to Global.</summary>
    /// <param name="key">The setting.</param>
    /// <param name="deviceIdentityKey">The device, for a device setting.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>Whether an override was removed.</returns>
    internal async Task<bool> ClearGameOverrideAsync(ProfileSettingKey key, string? deviceIdentityKey,
        CancellationToken cancellationToken = default)
    {
        var result = await MutateAsync((config, active) =>
            active.Enabled && ProfileResolver.FindGame(config, active.GameProfileId) is { Enabled: true } game
                           && game.Values.Clear(key, deviceIdentityKey), cancellationToken).ConfigureAwait(false);
        if (result.Changed)
        {
            Log.Info($"Profile: {key.Id} now uses Global for game {result.Snapshot.Active.GameProfileId}.");
        }

        return result.Changed;
    }

    /// <summary>Turns the running application's profile on or off.</summary>
    /// <param name="enabled">The requested state.</param>
    /// <param name="expectedApplicationId">Refuses when a different application is running.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>Whether the profile is now in the requested state.</returns>
    internal async Task<bool> SetGameEnabledAsync(bool enabled, string? expectedApplicationId = null,
        CancellationToken cancellationToken = default)
    {
        var stale = false;
        var result = await MutateAsync((config, active) =>
        {
            if (expectedApplicationId is not null && active.ApplicationId != expectedApplicationId)
            {
                stale = true;
                return false;
            }

            return ProfileEdits.SetGameEnabled(config, active, enabled);
        }, cancellationToken).ConfigureAwait(false);
        if (stale)
        {
            // The request was for another application. Whatever the current one's switch says, this
            // request did not set it.
            return false;
        }

        if (result.Changed)
        {
            Log.Info($"Profile: per-game profile {(enabled ? "on" : "off")} for {Describe(result.Snapshot.Active)}.");
        }

        return result.Snapshot.EditsGame == enabled;
    }

    /// <summary>Steam's "Reset to default": clears the game profile, or the Performance-tab Global values.</summary>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>Whether anything changed.</returns>
    internal async Task<bool> ResetAsync(CancellationToken cancellationToken = default)
    {
        var game = false;
        var result = await MutateAsync((config, active) =>
        {
            if (active.Enabled && ProfileResolver.FindGame(config, active.GameProfileId) is { Enabled: true } entry)
            {
                game = true;
                return ProfileEdits.ResetGame(config, entry.Id);
            }

            return ProfileEdits.ResetGlobalPerformance(config);
        }, cancellationToken).ConfigureAwait(false);
        if (result.Changed)
        {
            Log.Info(game
                ? $"Profile: game {result.Snapshot.Active.GameProfileId} reset; it now inherits every value from Global."
                : "Profile: Global performance values reset.");
        }

        return result.Changed;
    }

    /// <summary>Pins or releases the controller target of a profile addressed by application identity.</summary>
    /// <param name="id">The canonical application identity, such as <c>steam:1234567890</c>.</param>
    /// <param name="name">What to call the profile when this creates it.</param>
    /// <param name="target">The target to pin, or null to release it.</param>
    /// <param name="removeEmptyProfile">When releasing, whether an emptied profile is removed.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether this created the profile.</returns>
    /// <remarks>
    ///     The library importer's controller-only route is this and nothing else. The profile
    ///     system already switches the virtual pad for whatever Steam reports running, so an
    ///     imported shortcut needs no launcher-to-WSGM channel of its own, and the user can see and
    ///     change the override in the Quick Access rows like any other.
    /// </remarks>
    internal async Task<bool> SetApplicationControllerTargetAsync(
        string id, string name, ManagedControllerTarget? target, bool removeEmptyProfile,
        CancellationToken cancellationToken = default)
    {
        var created = false;
        var result = await MutateAsync(
                (config, _) => ProfileEdits.SetApplicationControllerTarget(
                    config, id, name, target, removeEmptyProfile, out created),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Changed)
        {
            Log.Info(target is null
                ? $"Profile: {id} no longer pins a controller target."
                : $"Profile: {id} pins the {target} controller target.");
        }

        return result.Changed && created;
    }

    /// <summary>Creates or updates a game profile's name, processes and switch.</summary>
    internal async Task<string> SaveGameAsync(string? id, string name, IReadOnlyList<string> processNames,
        bool enabled, CancellationToken cancellationToken = default)
    {
        var saved = id ?? string.Empty;
        await MutateAsync((config, _) =>
        {
            var before = JsonSerializer.Serialize(config, ConfigJsonContext.Default.ProfileConfig);
            saved = ProfileEdits.SaveGame(config, id, name, processNames, enabled);
            return before != JsonSerializer.Serialize(config, ConfigJsonContext.Default.ProfileConfig);
        }, cancellationToken).ConfigureAwait(false);
        return saved;
    }

    /// <summary>Deletes a game profile. Matching applications then use Global.</summary>
    internal async Task<bool> DeleteGameAsync(string id, CancellationToken cancellationToken = default)
    {
        var result = await MutateAsync((config, _) => ProfileEdits.DeleteGame(config, id), cancellationToken)
            .ConfigureAwait(false);
        return result.Changed;
    }

    private static void Write(ProfileValues values, ProfileField field, int value)
    {
        switch (field)
        {
            case ProfileField.FrameLimit:
                values.FrameLimit = value;
                break;
            case ProfileField.OverlayLevel:
                values.OverlayLevel = value;
                break;
            case ProfileField.UnifiedWatts:
                values.UnifiedWatts = value;
                break;
            case ProfileField.SustainedWatts:
                values.SustainedWatts = value;
                break;
            case ProfileField.BoostWatts:
                values.BoostWatts = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field, "Not an integer profile field.");
        }
    }

    private async Task<(bool Changed, ProfileSnapshot Snapshot)> MutateAsync(
        Func<ProfileConfig, ActiveProfile, bool> edit,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ActiveProfile active;
            lock (_gate)
            {
                active = _current.Active;
            }

            var changed = false;
            var stored = await _mutate(config =>
            {
                changed = edit(config, active);
                return changed;
            }, cancellationToken).ConfigureAwait(false);
            ProfileSnapshot next;
            bool applicationChanged;
            lock (_gate)
            {
                if (!changed && SameStore(stored, _current.Config))
                {
                    return (false, _current);
                }

                var nextActive = Activate(stored, _running);
                applicationChanged = nextActive != _current.Active &&
                                     nextActive.ApplicationId != _current.Active.ApplicationId;
                next = _current = new ProfileSnapshot(stored, nextActive, _current.Generation + 1);
            }

            Raise(next, applicationChanged ? ProfileChangeKind.Application : ProfileChangeKind.Values);
            return (changed, next);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void Raise(ProfileSnapshot snapshot, ProfileChangeKind kind)
    {
        try
        {
            Changed?.Invoke(snapshot, kind);
        }
        catch (Exception ex)
        {
            Log.Error("Profile change observer failed", ex);
        }
    }

    private static ActiveProfile Activate(ProfileConfig config, PerformanceApplicationTarget? target)
    {
        return target is null
            ? ActiveProfile.None
            : ProfileResolver.Activate(config, target.ApplicationId, target.RtssProfileName, target.SteamAppId);
    }

    private static bool SameStore(ProfileConfig left, ProfileConfig right)
    {
        return JsonSerializer.Serialize(left, ConfigJsonContext.Default.ProfileConfig)
               == JsonSerializer.Serialize(right, ConfigJsonContext.Default.ProfileConfig);
    }

    private static string Describe(ActiveProfile active)
    {
        return !active.HasApplication ? "nothing (Global)"
            : active.GameProfileId is null ? $"{active.ApplicationId} with no game profile (Global)"
            : $"{active.ApplicationId} with game profile {active.GameProfileId} ({(active.Enabled ? "on" : "off")})";
    }
}
