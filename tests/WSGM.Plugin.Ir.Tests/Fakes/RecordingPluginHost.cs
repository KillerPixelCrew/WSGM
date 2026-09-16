using WSGM.Plugin.Sdk;

namespace WSGM.Plugin.Ir.Tests.Fakes;

/// <summary>A host that keeps what the plugin published, so a test can read the ids it offers.</summary>
internal sealed class RecordingPluginHost : IPluginHost
{
    internal List<PluginStatePublication> States { get; } = [];

    public void PublishHealth(PluginHealthPublication publication)
    {
    }

    public void PublishState(PluginStatePublication publication)
    {
        States.Add(publication);
    }
}
