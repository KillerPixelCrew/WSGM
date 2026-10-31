using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using WSGM.Plugin.Sdk;

namespace WSGM.Core;

/// <summary>Uses the same strict, serialized and atomic configuration owner as the rest of WSGM.</summary>
/// <param name="store">Borrowed strict configuration owner; no default overwrite is allowed on a failed read.</param>
internal sealed class ApplicationPluginConfigurationStore(ConfigStore store) : IPluginConfigurationStore
{
    /// <inheritdoc />
    public SavedPluginConfiguration Read(PluginInstanceIdentity identity)
    {
        return ReadFrom(store.Read().RequireConfig(), identity);
    }

    /// <inheritdoc />
    public SavedPluginConfiguration Save(PluginInstanceIdentity identity, long expectedRevision,
        IReadOnlyDictionary<string, PluginValue> changes)
    {
        var config = store.Update(current =>
        {
            SaveInto(current, identity, expectedRevision, changes);
            return true;
        });
        return ReadFrom(config, identity);
    }

    /// <summary>Validates and copies an instance's saved preferences from a configuration graph.</summary>
    /// <param name="config">Non-null configuration to inspect without mutation.</param>
    /// <param name="identity">Exact package/instance key.</param>
    /// <returns>An isolated read-only value map and revision; empty at revision zero when absent.</returns>
    /// <exception cref="InvalidOperationException">
    ///     Duplicate identities, negative revisions, or invalid preference data were
    ///     found.
    /// </exception>
    internal static SavedPluginConfiguration ReadFrom(AppConfig config, PluginInstanceIdentity identity)
    {
        var matches = config.PluginConfigurations.Where(entry =>
            entry.PluginId == identity.PluginId && entry.InstanceId == identity.InstanceId).ToArray();
        if (matches.Length > 1)
        {
            throw new InvalidOperationException("Duplicate plugin preference identities.");
        }

        var saved = matches.SingleOrDefault();
        if (saved is not null && (saved.Revision < 0 || saved.Values is null
                                                     || saved.Values.Any(pair =>
                                                         !PluginConfigurationRules.ValidKey(pair.Key) ||
                                                         !pair.Value.IsValid)))
        {
            throw new InvalidOperationException(
                "Stored plugin preferences are invalid; preserving them without overwrite.");
        }

        return new SavedPluginConfiguration(saved?.Revision ?? 0, new ReadOnlyDictionary<string, PluginValue>(
            saved?.Values is { } values
                ? new Dictionary<string, PluginValue>(values, StringComparer.Ordinal)
                : new Dictionary<string, PluginValue>(StringComparer.Ordinal)));
    }

    /// <summary>Merges preferences into a caller-owned configuration after validating optimistic concurrency.</summary>
    /// <param name="config">Exclusive mutable configuration; this method does not persist it.</param>
    /// <param name="identity">Exact package/instance key.</param>
    /// <param name="expectedRevision">Previously observed revision that must still match.</param>
    /// <param name="changes">Nonempty valid values to merge over the saved map.</param>
    /// <exception cref="InvalidOperationException">Existing data is invalid or the revision no longer matches.</exception>
    /// <exception cref="ArgumentException">The change map is empty or invalid.</exception>
    /// <exception cref="OverflowException">The saved revision cannot be incremented.</exception>
    internal static void SaveInto(AppConfig config, PluginInstanceIdentity identity, long expectedRevision,
        IReadOnlyDictionary<string, PluginValue> changes)
    {
        var previous = ReadFrom(config, identity);
        if (previous.Revision != expectedRevision)
        {
            throw new InvalidOperationException("Plugin preferences changed; refresh before editing.");
        }

        if (changes.Count == 0 ||
            changes.Any(pair => !PluginConfigurationRules.ValidKey(pair.Key) || !pair.Value.IsValid))
        {
            throw new ArgumentException("Plugin preference changes are invalid.");
        }

        Dictionary<string, PluginValue> values = new(previous.Values, StringComparer.Ordinal);
        foreach (var pair in changes)
        {
            values[pair.Key] = pair.Value;
        }

        var saved = new CommonPluginConfiguration
        {
            PluginId = identity.PluginId,
            InstanceId = identity.InstanceId,
            Revision = checked(previous.Revision + 1),
            Values = values
        };
        config.PluginConfigurations.RemoveAll(entry =>
            entry.PluginId == identity.PluginId && entry.InstanceId == identity.InstanceId);
        config.PluginConfigurations.Add(saved);
    }
}
