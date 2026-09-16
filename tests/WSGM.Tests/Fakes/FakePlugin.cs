using WSGM.Plugin.Sdk;

namespace WSGM.Tests.Fakes;

/// <summary>A common plugin that records every lifecycle call it receives.</summary>
/// <param name="id">The plugin id.</param>
/// <param name="publishReadyOnResume">
///     Whether a resume reports the plugin ready again, as the plugin host
///     tests expect. The manager tests only count resumes.
/// </param>
internal sealed class FakePlugin(string id, bool publishReadyOnResume = false) : IPlugin
{
    internal IPluginHost? Host { get; private set; }

    internal PluginSessionMode Mode { get; private set; }

    internal bool Released { get; init; } = true;

    internal Func<Task>? StartWork { get; init; }

    internal Func<PluginSessionMode, CancellationToken, Task>? ModeWork { get; init; }

    internal int Starts { get; private set; }

    internal int Stops { get; private set; }

    internal int Disposals { get; private set; }

    internal int Suspends { get; private set; }

    internal int Resumes { get; private set; }
    public string Id => id;

    public async ValueTask<PluginHealth> StartAsync(IPluginHost host, PluginContext context,
        CancellationToken cancellationToken)
    {
        Starts++;
        Host = host;
        Mode = context.Mode;
        if (StartWork is not null)
        {
            await StartWork();
        }

        return PluginHealth.Ready;
    }

    public async ValueTask SessionChangedAsync(PluginContext context, CancellationToken cancellationToken)
    {
        if (ModeWork is not null)
        {
            await ModeWork(context.Mode, cancellationToken);
        }

        Mode = context.Mode;
    }

    public ValueTask SuspendAsync(PluginContext context, CancellationToken cancellationToken)
    {
        Suspends++;
        return ValueTask.CompletedTask;
    }

    public ValueTask ResumeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        Resumes++;
        if (publishReadyOnResume)
        {
            Host!.PublishHealth(new PluginHealthPublication(context.Instance, context.Generation, PluginHealth.Ready,
                null));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> StopAsync(PluginContext context, CancellationToken cancellationToken)
    {
        Stops++;
        return ValueTask.FromResult(Released);
    }

    public ValueTask DisposeAsync()
    {
        Disposals++;
        return ValueTask.CompletedTask;
    }
}
