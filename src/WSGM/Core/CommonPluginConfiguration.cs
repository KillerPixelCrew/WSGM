using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using WSGM.Plugin.Sdk;

namespace WSGM.Core;

/// <summary>Saved user preferences for one common plugin instance.</summary>
public sealed class CommonPluginConfiguration
{
    /// <summary>Plugin package identity.</summary>
    public string PluginId { get; set; } = "";
    /// <summary>Host-selected instance identity.</summary>
    public string InstanceId { get; set; } = "";
    /// <summary>Increasing user-intent revision.</summary>
    public long Revision { get; set; }
    /// <summary>Explicitly saved values only; readback and declaration defaults never populate this map.</summary>
    public Dictionary<string, PluginValue> Values { get; set; } = new(StringComparer.Ordinal);
}

internal sealed record SavedPluginConfiguration(long Revision, IReadOnlyDictionary<string, PluginValue> Values);

internal interface IPluginConfigurationStore
{
    SavedPluginConfiguration Read(PluginInstanceIdentity identity);
    SavedPluginConfiguration Save(PluginInstanceIdentity identity, long expectedRevision, IReadOnlyDictionary<string, PluginValue> changes);
}

/// <summary>Uses the same strict, serialized and atomic configuration owner as the rest of WSGM.</summary>
internal sealed class ApplicationPluginConfigurationStore : IPluginConfigurationStore
{
    public SavedPluginConfiguration Read(PluginInstanceIdentity identity)
    {
        using var held = ConfigStore.AcquireLock();
        return ReadFrom(ConfigStore.LoadForMutation(), identity);
    }

    public SavedPluginConfiguration Save(PluginInstanceIdentity identity, long expectedRevision, IReadOnlyDictionary<string, PluginValue> changes)
    {
        var config = ConfigStore.Mutate(current => SaveInto(current, identity, expectedRevision, changes));
        return ReadFrom(config, identity);
    }

    internal static SavedPluginConfiguration ReadFrom(AppConfig config, PluginInstanceIdentity identity)
    {
        var matches = config.PluginConfigurations?.Where(entry => entry is not null && entry.PluginId == identity.PluginId && entry.InstanceId == identity.InstanceId).ToArray()
            ?? throw new InvalidOperationException("Stored plugin configuration is invalid.");
        if (matches.Length > 1) { throw new InvalidOperationException("Duplicate plugin preference identities."); }
        var saved = matches.SingleOrDefault();
        if (saved is not null && (saved.Revision < 0 || saved.Values is null || saved.Values.Count > 128
            || saved.Values.Any(pair => !PluginConfigurationRules.ValidKey(pair.Key) || !pair.Value.IsValid)))
        { throw new InvalidOperationException("Stored plugin preferences are invalid; preserving them without overwrite."); }
        return new(saved?.Revision ?? 0, new ReadOnlyDictionary<string, PluginValue>(
            saved is null ? new Dictionary<string, PluginValue>(StringComparer.Ordinal) : new Dictionary<string, PluginValue>(saved.Values, StringComparer.Ordinal)));
    }

    internal static void SaveInto(AppConfig config, PluginInstanceIdentity identity, long expectedRevision, IReadOnlyDictionary<string, PluginValue> changes)
    {
        var previous = ReadFrom(config, identity);
        if (previous.Revision != expectedRevision) { throw new InvalidOperationException("Plugin preferences changed; refresh before editing."); }
        if (changes.Count == 0 || changes.Count > 128 || changes.Any(pair => !PluginConfigurationRules.ValidKey(pair.Key) || !pair.Value.IsValid))
        { throw new ArgumentException("Plugin preference changes are invalid."); }
        Dictionary<string, PluginValue> values = new(previous.Values, StringComparer.Ordinal);
        foreach (var pair in changes) { values[pair.Key] = pair.Value; }
        if (values.Count > 128) { throw new InvalidOperationException("Too many stored plugin preferences."); }
        var saved = new CommonPluginConfiguration
        {
            PluginId = identity.PluginId,
            InstanceId = identity.InstanceId,
            Revision = checked(previous.Revision + 1),
            Values = values,
        };
        config.PluginConfigurations.RemoveAll(entry => entry is not null && entry.PluginId == identity.PluginId && entry.InstanceId == identity.InstanceId);
        config.PluginConfigurations.Add(saved);
    }
}
