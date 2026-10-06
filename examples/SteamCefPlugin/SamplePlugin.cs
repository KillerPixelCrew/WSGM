using System.Text.Json;
using WSGM.Plugin.Sdk;

namespace SteamCefPlugin;

/// <summary>A small independently packaged frontend with an optional JSON backend.</summary>
public sealed class SamplePlugin : IPlugin, IPluginSteamFrontend
{
    private int _count;
    /// <inheritdoc />
    public string Id => "example.steam-cef";
    /// <inheritdoc />
    public event Action? FrontendChanged;
    /// <inheritdoc />
    public ValueTask<PluginHealth> StartAsync(IPluginHost host, PluginContext context, CancellationToken cancellationToken)
        => ValueTask.FromResult(PluginHealth.Ready);
    /// <inheritdoc />
    public ValueTask SessionChangedAsync(PluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    /// <inheritdoc />
    public ValueTask<bool> StopAsync(PluginContext context, CancellationToken cancellationToken) => ValueTask.FromResult(true);
    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    /// <inheritdoc />
    public JsonElement? ReadFrontendState(string moduleId) => JsonSerializer.SerializeToElement(new { count = Volatile.Read(ref _count) });
    /// <inheritdoc />
    public Task<JsonElement?> InvokeFrontendAsync(string moduleId, string method, JsonElement payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (method == "increment")
        {
            Interlocked.Increment(ref _count);
            FrontendChanged?.Invoke();
        }
        return Task.FromResult(ReadFrontendState(moduleId));
    }
}
