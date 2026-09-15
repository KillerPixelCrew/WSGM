using WSGM.Core;
using WSGM.Plugin.Sdk;

namespace WSGM.Tests;

/// <summary>Plugin configuration kept in an in-memory application configuration.</summary>
internal sealed class MemoryPluginConfigurationStore : IPluginConfigurationStore
{
    internal AppConfig Config { get; } = new();

    internal bool FailSave { get; set; }

    public SavedPluginConfiguration Read(PluginInstanceIdentity identity) =>
        ApplicationPluginConfigurationStore.ReadFrom(Config, identity);

    public SavedPluginConfiguration Save(PluginInstanceIdentity identity, long revision, IReadOnlyDictionary<string, PluginValue> changes)
    {
        if (FailSave) { throw new IOException("Fixture persistence failure"); }
        ApplicationPluginConfigurationStore.SaveInto(Config, identity, revision, changes);
        return Read(identity);
    }
}
