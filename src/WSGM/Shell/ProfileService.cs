using System;
using System.Collections.Generic;
using System.Linq;
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
///     Shared by Steam and overlay consumers. Serializes edits and publishes only after the supplied
///     mutation delegate saves successfully. The delegate owns disk scheduling; subscribers own device writes.
/// </remarks>
internal sealed class ProfileService
{
    private readonly Lock _gate = new();

    /// <summary>Game and executable pairs already learned or being learned, so each is saved once.</summary>
    private readonly HashSet<string> _learned = new(StringComparer.OrdinalIgnoreCase);

    private readonly CancellationTokenSource _lifetime = new();

    private readonly Func<Func<ProfileConfig, bool>, CancellationToken, Task<ProfileConfig>> _mutate;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private ProfileSnapshot _current;
    private string _currentJson;
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
        _current = new ProfileSnapshot(ConfigJson.Clone(initial, ConfigJsonContext.Tolerant.ProfileConfig),
            ActiveProfile.None, 1);
        _currentJson = JsonSerializer.Serialize(_current.Config, ConfigJsonContext.Tolerant.ProfileConfig);
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

    /// <summary>Completes when queued executable learning has finished.</summary>
    internal Task Completion { get; private set; } = Task.CompletedTask;

    /// <summary>Stops new edits and executable learning without waiting for a store call.</summary>
    internal void Close()
    {
        Log.Observe(_lifetime.CancelAsync(), "Profile service cancellation");
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
            if (_lifetime.IsCancellationRequested)
            {
                return _current;
            }

            _running = target;
            var active = Activate(_current.Config, target);
            if (active == _current.Active)
            {
                return _current;
            }

            next = _current = new ProfileSnapshot(_current.Config, active, _current.Generation + 1);
        }

        Log.Info(
            $"Profile: running {Describe(next.Active)}.");
        Raise(next, ProfileChangeKind.Application);
        LearnRunningExecutable(next);
        return next;
    }

    /// <summary>Reads fresh profiles after earlier edits finish, without rewriting the file.</summary>
    /// <param name="cancellationToken">Cancels the reload.</param>
    /// <returns>Completion after the stored profile snapshot is reloaded and any changed state is published.</returns>
    internal async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await MutateAsync((_, _) => false, cancellationToken).ConfigureAwait(false);
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
            var before = JsonSerializer.Serialize(target, ConfigJsonContext.Tolerant.ProfileValues);
            write(target);
            return before != JsonSerializer.Serialize(target, ConfigJsonContext.Tolerant.ProfileValues);
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
    /// <param name="field">Integer field supported by this service.</param>
    /// <param name="value">Value stored in the currently active profile layer.</param>
    /// <param name="cancellationToken">Cancels waiting and cooperative persistence stages.</param>
    /// <returns>The snapshot published after persistence.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The field is not an integer profile field.</exception>
    internal Task<ProfileSnapshot> SetAsync(ProfileField field, int value,
        CancellationToken cancellationToken = default)
    {
        return SetAsync(values => Write(values, field, value), $"{field}={value}", ProfileLayer.Active,
            cancellationToken);
    }

    /// <summary>Stores one device or graphics capability value.</summary>
    /// <param name="deviceIdentityKey">The device identity, or a graphics package's <c>gpu:</c> key.</param>
    /// <param name="capabilityId">The capability.</param>
    /// <param name="instanceId">Its instance, or null.</param>
    /// <param name="value">The value.</param>
    /// <param name="layer">The layer; a global-only capability passes <see cref="ProfileLayer.Global" />.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <param name="selectSplitMode">Selects independent power limits in the same edit as the PL2 value.</param>
    /// <returns>The snapshot after the save.</returns>
    internal async Task<ProfileSnapshot> SetDeviceAsync(string deviceIdentityKey, string capabilityId,
        string? instanceId, CapabilityValue value, ProfileLayer layer = ProfileLayer.Active,
        CancellationToken cancellationToken = default, bool selectSplitMode = false)
    {
        var result = await MutateAsync((config, active) =>
        {
            var target = ProfileEdits.Target(config, active, layer)
                         ?? throw new InvalidOperationException(
                             "The game profile this change was meant for no longer exists.");
            var before = JsonSerializer.Serialize(config, ConfigJsonContext.Tolerant.ProfileConfig);
            target.SetDevice(deviceIdentityKey, capabilityId, instanceId, value);
            if (selectSplitMode && ProfileResolver.Layers(config, active).ManualTdp()?.Unified == true)
            {
                ProfileEdits.Target(config, active, ProfileLayer.Active)!.TdpUnified = false;
            }

            return before != JsonSerializer.Serialize(config, ConfigJsonContext.Tolerant.ProfileConfig);
        }, cancellationToken).ConfigureAwait(false);
        if (result.Changed)
        {
            Log.Info($"Profile: {deviceIdentityKey}/{capabilityId} saved.");
        }

        return result.Snapshot;
    }

    internal async Task MigrateLegacyBoostAsync(string identityKey, string capabilityId, string? instanceId,
        CancellationToken cancellationToken)
    {
        await MutateAsync((config, _) =>
        {
            var changed = false;
            foreach (var values in new[] { config.Global }.Concat(config.Games.Select(game => game.Values)))
            {
                if (values.BoostWatts is not { } watts)
                {
                    continue;
                }

                if (values.FindDevice(identityKey, capabilityId, instanceId)?.Value is null)
                {
                    values.SetDevice(identityKey, capabilityId, instanceId,
                        new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = watts });
                }

                values.BoostWatts = null;
                changed = true;
            }

            return changed;
        }, cancellationToken).ConfigureAwait(false);
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
    /// <param name="id">Existing profile identifier; null or an unknown identifier creates a new named profile.</param>
    /// <param name="name">Profile display name.</param>
    /// <param name="processNames">Executable names used to match the profile.</param>
    /// <param name="enabled">Whether matching applications use this profile.</param>
    /// <param name="cancellationToken">Cancels waiting and cooperative persistence stages.</param>
    /// <returns>The saved profile identifier.</returns>
    internal async Task<string> SaveGameAsync(string? id, string name, IReadOnlyList<string> processNames,
        bool enabled, CancellationToken cancellationToken = default)
    {
        var saved = id ?? string.Empty;
        await MutateAsync((config, _) =>
        {
            var before = JsonSerializer.Serialize(config, ConfigJsonContext.Tolerant.ProfileConfig);
            saved = ProfileEdits.SaveGame(config, id, name, processNames, enabled);
            return before != JsonSerializer.Serialize(config, ConfigJsonContext.Tolerant.ProfileConfig);
        }, cancellationToken).ConfigureAwait(false);
        return saved;
    }

    /// <summary>Deletes a game profile. Matching applications then use Global.</summary>
    /// <param name="id">Profile identifier to remove.</param>
    /// <param name="cancellationToken">Cancels waiting and cooperative persistence stages.</param>
    /// <returns>True when a profile was removed and the change persisted.</returns>
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
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field, "Not an integer profile field.");
        }
    }

    private async Task<(bool Changed, ProfileSnapshot Snapshot)> MutateAsync(
        Func<ProfileConfig, ActiveProfile, bool> edit,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;
        await _writeGate.WaitAsync(token).ConfigureAwait(false);
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
                token.ThrowIfCancellationRequested();
                changed = edit(config, active);
                return changed;
            }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var storedJson = JsonSerializer.Serialize(stored, ConfigJsonContext.Tolerant.ProfileConfig);
            ProfileSnapshot next;
            bool applicationChanged;
            lock (_gate)
            {
                if (!changed && storedJson == _currentJson)
                {
                    return (false, _current);
                }

                var nextActive = Activate(stored, _running);
                applicationChanged = nextActive != _current.Active &&
                                     nextActive.ApplicationId != _current.Active.ApplicationId;
                next = _current = new ProfileSnapshot(stored, nextActive, _current.Generation + 1);
                _currentJson = storedJson;
            }

            Raise(next, applicationChanged ? ProfileChangeKind.Application : ProfileChangeKind.Values);
            LearnRunningExecutable(next);
            return (changed, next);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Saves the running executable into the matched game profile when it is new to it.</summary>
    /// <param name="snapshot">The snapshot just published.</param>
    /// <remarks>
    ///     A store title's executable is known only while it runs, and a graphics driver that keeps its own
    ///     per-application values matches on executables alone. Learning it here, once, is what lets such a
    ///     driver apply a Steam game's values the next time it starts. The save publishes a value change,
    ///     so the graphics packages receive the new name straight away.
    /// </remarks>
    private void LearnRunningExecutable(ProfileSnapshot snapshot)
    {
        string? executable;
        lock (_gate)
        {
            if (_lifetime.IsCancellationRequested)
            {
                return;
            }

            executable = _running?.RtssProfileName;
        }

        if (snapshot.Game is not { } game || executable is not { Length: > 0 }
                                          || ProfileResolver.KnownExecutables(game)
                                              .Contains(executable, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var gameId = game.Id;
        lock (_gate)
        {
            if (_lifetime.IsCancellationRequested || !_learned.Add(gameId + "|" + executable))
            {
                return;
            }

            var previous = Completion;
            Completion = Task.Run(() => LearnAsync(previous, gameId, executable), CancellationToken.None);
        }
    }

    private async Task LearnAsync(Task previous, string gameId, string executable)
    {
        await previous.ConfigureAwait(false);
        try
        {
            var result = await MutateAsync(
                    (config, _) => ProfileEdits.LearnExecutable(config, gameId, executable),
                    _lifetime.Token)
                .ConfigureAwait(false);
            if (result.Changed)
            {
                Log.Info($"Profile: game {gameId} runs as {executable}.");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Profile: {executable} could not be recorded for game {gameId}: {ex.Message}");
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

    private static string Describe(ActiveProfile active)
    {
        return !active.HasApplication ? "nothing (Global)"
            : active.GameProfileId is null ? $"{active.ApplicationId} with no game profile (Global)"
            : $"{active.ApplicationId} with game profile {active.GameProfileId} ({(active.Enabled ? "on" : "off")})";
    }
}
