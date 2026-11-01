using System;
using System.Collections.Generic;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>The stored profile values one device's capabilities resolve their desired state from.</summary>
/// <remarks>
///     Immutable, one index per layer, built when the profiles or the running application change rather
///     than on every snapshot a state delta builds. The layers stay apart so each descriptor's profile
///     scope decides which of them it resolves from.
/// </remarks>
internal sealed class CapabilityDesiredLayers
{
    private readonly Dictionary<DeviceCapabilityKey, CapabilityValue> _game;
    private readonly Dictionary<DeviceCapabilityKey, CapabilityValue> _global;

    private CapabilityDesiredLayers(Dictionary<DeviceCapabilityKey, CapabilityValue> global,
        Dictionary<DeviceCapabilityKey, CapabilityValue> game)
    {
        _global = global;
        _game = game;
    }

    /// <summary>No stored values, as before a device identity or profile is known.</summary>
    internal static CapabilityDesiredLayers Empty { get; } = new([], []);

    /// <summary>Indexes the values stored for one device in Global and the running game's layer.</summary>
    /// <param name="deviceIdentityKey">The device the values were stored for, or null before it is known.</param>
    /// <param name="layers">Global and the running game's layer.</param>
    /// <returns>The indexed layers; empty without an identity.</returns>
    internal static CapabilityDesiredLayers From(string? deviceIdentityKey, ProfileLayers layers)
    {
        return new CapabilityDesiredLayers(Index(deviceIdentityKey, layers.Global),
            Index(deviceIdentityKey, layers.Game));
    }

    /// <summary>The value Global stores for a capability, or null.</summary>
    /// <param name="key">The capability instance.</param>
    /// <returns>The stored Global value.</returns>
    internal CapabilityValue? Global(DeviceCapabilityKey key)
    {
        return _global.GetValueOrDefault(key);
    }

    /// <summary>The value a capability shows as wanted, by its profile scope.</summary>
    /// <param name="key">The capability instance.</param>
    /// <param name="scope">Its descriptor's profile scope.</param>
    /// <returns>The resolved value and the layer it came from.</returns>
    /// <remarks>
    ///     A switched or native per-application value resolves the game over Global. The native one is
    ///     shown that way because its driver applies the game's value itself; WSGM still commands only the
    ///     Global value, read from <see cref="CapabilityProjection.GlobalDesiredValue" />. A global-only
    ///     value never reads the game layer, so it is never marked as the game's.
    /// </remarks>
    internal Resolved<CapabilityValue?> Resolve(DeviceCapabilityKey key, CapabilityProfileScope scope)
    {
        if (scope is not CapabilityProfileScope.GlobalOnly && _game.TryGetValue(key, out var game))
        {
            return new Resolved<CapabilityValue?>(game, ProfileSource.Game);
        }

        return _global.TryGetValue(key, out var global)
            ? new Resolved<CapabilityValue?>(global, ProfileSource.Global)
            : new Resolved<CapabilityValue?>(null, ProfileSource.None);
    }

    private static Dictionary<DeviceCapabilityKey, CapabilityValue> Index(
        string? deviceIdentityKey,
        ProfileValues? values)
    {
        Dictionary<DeviceCapabilityKey, CapabilityValue> index = [];
        if (deviceIdentityKey is null)
        {
            return index;
        }

        foreach (var entry in values?.Device ?? [])
        {
            if (entry.Value is not null
                && string.Equals(entry.DeviceIdentityKey, deviceIdentityKey, StringComparison.Ordinal))
            {
                index[new DeviceCapabilityKey(entry.CapabilityId, entry.InstanceId)] = entry.Value;
            }
        }

        return index;
    }
}
