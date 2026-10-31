using System;
using System.Collections.Generic;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Core;

/// <summary>The two layers a value can come from, for one running application.</summary>
/// <param name="Global">The Global layer.</param>
/// <param name="Game">The game layer, only when its profile is enabled.</param>
public readonly record struct ProfileLayers(ProfileValues Global, ProfileValues? Game)
{
    /// <summary>The setting that holds the manual power target for the current mode.</summary>
    public ProfileSettingKey PowerTargetKey => Value(values => values.TdpUnified).Value == true
        ? new ProfileSettingKey(ProfileField.UnifiedWatts)
        : new ProfileSettingKey(ProfileField.SustainedWatts);

    /// <summary>The number of settings the game layer overrides, counting every stored device value.</summary>
    public int GameOverrideCount => Game?.Count() ?? 0;

    /// <summary>The number of settings the game layer overrides for what is running now.</summary>
    /// <param name="livePublishers">Identity keys of the device and graphics packages running now.</param>
    /// <returns>The count, leaving out values stored for a package that is not running.</returns>
    public int CountGameOverrides(IReadOnlyCollection<string> livePublishers)
    {
        return Game?.Count(livePublishers) ?? 0;
    }

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
        return unified.Source is ProfileSource.None
               && unifiedWatts.Source is ProfileSource.None
               && sustained.Source is ProfileSource.None
            ? null
            : new ManualTdpProfile(unified.Value ?? false, unifiedWatts.Value, sustained.Value);
    }
}
