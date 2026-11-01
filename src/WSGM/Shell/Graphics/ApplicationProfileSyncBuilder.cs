using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>Builds the per-application set one graphics publisher's driver should hold.</summary>
internal static class ApplicationProfileSyncBuilder
{
    /// <summary>Every enabled game's native per-application values for one publisher.</summary>
    /// <param name="config">The profile store.</param>
    /// <param name="publisherKey">The publisher's profile key, <c>gpu:</c> and its plugin id.</param>
    /// <param name="descriptors">The publisher's current descriptors.</param>
    /// <returns>
    ///     One entry per game whose profile is on, that has at least one known executable and at least one
    ///     value for a published <see cref="CapabilityProfileScope.NativePerApplication" /> capability that
    ///     its descriptor still accepts, in a stable order.
    /// </returns>
    /// <remarks>
    ///     A game that is off, or whose values no longer fit, is simply absent, and the plugin removes
    ///     whatever it stored for it earlier. A game known only by its Steam identity waits until WSGM has
    ///     seen its executable run.
    /// </remarks>
    internal static IReadOnlyList<ApplicationCapabilityProfile> Build(
        ProfileConfig config,
        string publisherKey,
        IReadOnlyList<CapabilityDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrEmpty(publisherKey);
        ArgumentNullException.ThrowIfNull(descriptors);
        Dictionary<(string CapabilityId, string InstanceId), CapabilityDescriptor> native = [];
        foreach (var descriptor in descriptors.Where(descriptor =>
                     descriptor is { ProfileScope: CapabilityProfileScope.NativePerApplication, SupportsWrite: true }))
        {
            native[(descriptor.CapabilityId, descriptor.InstanceId ?? string.Empty)] = descriptor;
        }

        List<ApplicationCapabilityProfile> profiles = [];
        if (native.Count == 0)
        {
            return profiles;
        }

        foreach (var game in config.Games.Where(game => game.Enabled).OrderBy(game => game.Id, StringComparer.Ordinal))
        {
            var executables = ProfileResolver.KnownExecutables(game)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (executables.Length == 0)
            {
                continue;
            }

            List<ApplicationCapabilityValue> values = [];
            foreach (var entry in game.Values.Device
                         .Where(entry => entry.Value is not null
                                         && string.Equals(entry.DeviceIdentityKey, publisherKey,
                                             StringComparison.Ordinal))
                         .OrderBy(entry => entry.CapabilityId, StringComparer.Ordinal)
                         .ThenBy(entry => entry.InstanceId, StringComparer.Ordinal))
            {
                if (native.TryGetValue((entry.CapabilityId, entry.InstanceId ?? string.Empty), out var descriptor)
                    && CapabilityValueValidation.ValueMatches(entry.Value!, descriptor, out _))
                {
                    values.Add(new ApplicationCapabilityValue(entry.CapabilityId, entry.InstanceId, entry.Value!));
                }
            }

            if (values.Count == 0)
            {
                continue;
            }

            profiles.Add(new ApplicationCapabilityProfile(
                game.Id,
                game.Name.Length > 0 ? game.Name : game.Id,
                executables,
                values));
        }

        return profiles;
    }

    /// <summary>A content fingerprint, so an unchanged set is not handed to the plugin again.</summary>
    /// <param name="profiles">The set.</param>
    /// <returns>A string equal for equal sets.</returns>
    internal static string Fingerprint(IReadOnlyList<ApplicationCapabilityProfile> profiles)
    {
        StringBuilder text = new();
        foreach (var profile in profiles)
        {
            text.Append(profile.ProfileId).Append('\u001f');
            text.AppendJoin('\u001e', profile.Executables).Append('\u001f');
            foreach (var value in profile.Values)
            {
                text.Append(value.CapabilityId).Append('#').Append(value.InstanceId).Append('=')
                    .Append(Describe(value.Value)).Append('\u001e');
            }

            text.Append('\u001d');
        }

        return text.ToString();
    }

    private static string Describe(CapabilityValue value)
    {
        return value.Kind switch
        {
            CapabilityValueKind.Boolean => $"b:{value.BooleanValue}",
            CapabilityValueKind.Integer => $"i:{value.IntegerValue}",
            CapabilityValueKind.Choice => $"c:{value.ChoiceValue}",
            CapabilityValueKind.Color => $"k:{value.ColorValue}",
            CapabilityValueKind.Text => $"t:{value.TextValue}",
            CapabilityValueKind.Curve => "v:" + string.Join(',',
                value.CurveValue.Select(point => $"{point.Input}/{point.Output}")),
            _ => value.Kind.ToString()
        };
    }
}
