using System;
using System.Collections.Generic;
using WSGM.Plugin.Sdk;

namespace WSGM.Core;

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
