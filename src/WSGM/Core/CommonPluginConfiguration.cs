using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Serialization;
using WSGM.Plugin.Sdk;

namespace WSGM.Core;

/// <summary>An explicitly enabled installed common-plugin instance.</summary>
public sealed class CommonPluginInstanceConfig
{
    /// <summary>Installed package identity.</summary>
    public string PluginId { get; set; } = "";

    /// <summary>Stable host instance identity within the package.</summary>
    public string InstanceId { get; set; } = "default";

    /// <summary>Whether WSGM may start this trusted installed package.</summary>
    public bool Enabled { get; set; }

    /// <summary>User opt-in to this package's unrestricted Steam frontend.</summary>
    public bool SteamCefEnabled { get; set; }

    /// <summary>Persisted module failure. Only an explicit manual reload clears it.</summary>
    public string? SteamCefFailure { get; set; }

    /// <summary>Transient explicit reload intent captured by Settings, never persisted.</summary>
    [JsonIgnore]
    public bool SteamCefReloadRequested { get; set; }
}

/// <summary>Saved user preferences for one common plugin instance.</summary>
public sealed class CommonPluginConfiguration
{
    /// <summary>Plugin package identity.</summary>
    public string PluginId { get; set; } = "";

    /// <summary>Host-selected instance identity.</summary>
    public string InstanceId { get; set; } = "";

    /// <summary>Increasing user-intent revision.</summary>
    public long Revision { get; set; }

    /// <summary>
    ///     Explicitly saved values only; readback and declaration defaults never populate this map. Null
    ///     only in a hand-edited file, which is preserved rather than overwritten.
    /// </summary>
    public Dictionary<string, PluginValue>? Values { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Isolated saved preference snapshot for optimistic instance-level edits.</summary>
/// <param name="Revision">Nonnegative intent revision; zero represents no saved preferences.</param>
/// <param name="Values">Read-only copy of explicitly saved values, excluding declaration defaults and readback.</param>
internal sealed record SavedPluginConfiguration(long Revision, IReadOnlyDictionary<string, PluginValue> Values);

/// <summary>Strict persistence boundary for common-plugin preferences with optimistic concurrency.</summary>
internal interface IPluginConfigurationStore
{
    /// <summary>Reads one instance's saved preferences without activating plugin code.</summary>
    /// <param name="identity">Exact package and instance identity.</param>
    /// <returns>An isolated snapshot, or revision zero and empty values when none was saved.</returns>
    /// <exception cref="InvalidOperationException">Stored preferences are malformed or duplicated.</exception>
    SavedPluginConfiguration Read(PluginInstanceIdentity identity);

    /// <summary>Merges explicit changes only if the saved intent revision still matches.</summary>
    /// <param name="identity">Exact package and instance identity.</param>
    /// <param name="expectedRevision">Revision read by the editor before making changes.</param>
    /// <param name="changes">Nonempty valid preference map to merge; absent keys keep their prior values.</param>
    /// <returns>The new isolated snapshot with its incremented revision.</returns>
    /// <exception cref="InvalidOperationException">Stored preferences are invalid or the revision changed.</exception>
    /// <exception cref="ArgumentException">The changes are empty or contain an invalid key/value.</exception>
    SavedPluginConfiguration Save(PluginInstanceIdentity identity, long expectedRevision,
        IReadOnlyDictionary<string, PluginValue> changes);
}

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
