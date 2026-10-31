using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>
///     Owns every running <c>wsgm.gpu</c> package's capabilities: one router per publisher, its desired
///     values, the user's writes and the per-application sync.
/// </summary>
/// <remarks>
///     Beside <see cref="DeviceCoordinator" />, never merged with it: the device router answers "the power
///     limit" or "the fan" with exactly one match, and a graphics package must not add a second. It runs
///     whatever the device integration switch says and takes no part in the machine-wide device owner. The
///     built-in drivers' lifecycles belong to <see cref="BuiltinGpuService" />. Independent third-party
///     publishers retain <see cref="CommonPluginManager" /> ownership. Both use the same profile routers.
/// </remarks>
internal sealed partial class GpuCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SyncBudget = TimeSpan.FromSeconds(15);

    private readonly Lock _gate = new();
    private readonly PluginHost _host;
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Saves a user-originated variable-refresh write to the performance profile.</summary>
    private readonly Action<bool>? _manualVariableRefresh;

    private readonly Func<bool?> _onAcPower;
    private readonly Action<Action> _postToUi;
    private readonly ProfileService _profiles;
    private readonly List<Task> _publisherDisposals = [];
    private readonly List<GpuPublisher> _publishers = [];
    private bool _changePosted;
    private volatile bool _disposed;

    /// <summary>
    ///     The last per-application sync revision of this coordinator. One coordinator serves the graphics
    ///     plugins of a session, so a plugin stopped and started again within it never sees a revision repeat.
    /// </summary>
    private long _syncRevision;

    /// <param name="postToUi">Posts work to the UI dispatcher.</param>
    /// <param name="profiles">The profile owner values are stored in.</param>
    /// <param name="host">The plugin host, for each publisher's health.</param>
    /// <param name="onAcPower">Reads whether the machine runs on AC power; null when unknown.</param>
    /// <param name="manualVariableRefresh">
    ///     Saves a variable-refresh state the user set on a graphics package's control, as the device
    ///     coordinator does for the device's; null when no profile owner exists.
    /// </param>
    internal GpuCoordinator(Action<Action> postToUi, ProfileService profiles, PluginHost host,
        Func<bool?> onAcPower, Action<bool>? manualVariableRefresh = null)
    {
        _postToUi = postToUi ?? throw new ArgumentNullException(nameof(postToUi));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _onAcPower = onAcPower ?? throw new ArgumentNullException(nameof(onAcPower));
        _manualVariableRefresh = manualVariableRefresh;
        _host.HealthChanged += OnHealthChanged;
    }

    /// <summary>The profile keys of every running graphics publisher.</summary>
    internal IReadOnlyList<string> ActiveProfileKeys
    {
        get
        {
            lock (_gate)
            {
                return [.. _publishers.Select(publisher => publisher.ProfileKey).Distinct(StringComparer.Ordinal)];
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            CloseAdmission();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Graphics admission closure failed; disposal continues: {ex.Message}");
        }

        GpuPublisher[] publishers;
        Task[] disposals;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            publishers = [.. _publishers];
            _publishers.Clear();
            disposals = [.. _publisherDisposals];
            _publisherDisposals.Clear();
        }

        _host.HealthChanged -= OnHealthChanged;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(disposals.Concat(publishers.Select(publisher => publisher.DisposeAsync().AsTask())))
                .ConfigureAwait(false);
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    /// <summary>
    ///     Opens a <c>wsgm.gpu</c> instance's capability channel before the plugin is admitted, so its router sees
    ///     every publication.
    /// </summary>
    /// <param name="identity">The plugin instance.</param>
    /// <param name="manifest">Its manifest, whose capability roles the channel enforces.</param>
    /// <param name="plugin">The plugin's capability surface.</param>
    /// <returns>The channel to admit the plugin with.</returns>
    public PluginCapabilityChannel Open(PluginInstanceIdentity identity, PluginManifest manifest,
        ICapabilityPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return Open(identity, manifest.Name, manifest.Capabilities, plugin);
    }

    /// <summary>Ends a channel once its plugin stopped or failed to start.</summary>
    /// <param name="channel">The channel.</param>
    public void Close(PluginCapabilityChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        GpuPublisher? publisher;
        lock (_gate)
        {
            publisher = _publishers.FirstOrDefault(candidate => ReferenceEquals(candidate.Channel, channel));
            if (publisher is not null)
            {
                _publishers.Remove(publisher);
                RetirePublisher(publisher);
            }
        }

        if (publisher is null)
        {
            channel.Dispose();
            return;
        }

        Log.Info($"Graphics: {publisher.Identity.PluginId} stopped publishing.");
        PostChanged();
    }

    internal void OpenBuiltin(GpuDriverPublisher driver)
    {
        var identity = new PluginInstanceIdentity(driver.Definition.Id, CommonPluginEnablement.DefaultInstanceId);
        var key = ProfileSettingKey.GpuPublisher(driver.Definition.Id);
        var router = new DeviceCapabilityRouter(_postToUi, key);
        var publisher = new GpuPublisher(this, identity, driver.Definition.Name, key, driver, router,
            driver: driver);
        lock (_gate)
        {
            if (_disposed || _lifetime.IsCancellationRequested || _publishers.Any(item => item.Identity == identity))
            {
                RetirePublisher(publisher);
                throw new InvalidOperationException("The graphics driver already has a native owner.");
            }

            _publishers.Add(publisher);
        }

        PostChanged();
    }

    internal void CloseBuiltin(GpuDriverPublisher driver)
    {
        lock (_gate)
        {
            var publisher = _publishers.FirstOrDefault(item => ReferenceEquals(item.Driver, driver));
            if (publisher is null)
            {
                return;
            }

            _publishers.Remove(publisher);
            RetirePublisher(publisher);
        }

        PostChanged();
    }

    private PluginCapabilityChannel Open(PluginInstanceIdentity identity, string name,
        IReadOnlyList<CapabilityRole> declared, ICapabilityPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var key = ProfileSettingKey.GpuPublisher(identity.PluginId);
        PluginCapabilityChannel channel = new(identity, declared, plugin);
        DeviceCapabilityRouter router = new(_postToUi, key);
        GpuPublisher publisher = new(this, identity, name, key, channel, router, channel);
        var replaced = false;
        lock (_gate)
        {
            var existing = _publishers.FirstOrDefault(candidate => candidate.Identity == identity);
            if (_disposed || _lifetime.IsCancellationRequested
                          || (existing is not null && existing.Channel is not { IsClosed: true }))
            {
                RetirePublisher(publisher);
                throw new InvalidOperationException("The graphics publisher is already open or the session ended.");
            }

            // A registration whose channel already ended is no longer attached to any plugin: its owner
            // lost it without closing it here. It must not keep the instance from starting again.
            if (existing is not null)
            {
                _publishers.Remove(existing);
                RetirePublisher(existing);
                replaced = true;
            }

            _publishers.Add(publisher);
        }

        if (replaced)
        {
            Log.Warn($"Graphics: {identity.PluginId} replaced a registration whose channel had already ended.");
        }

        Log.Info($"Graphics: {identity.PluginId} ({name}) publishes as {key}, roles "
                 + $"{string.Join(", ", declared)}.");
        PostChanged();
        return channel;
    }

    /// <summary>Stops command and refresh admission while retaining publishers for ordered disposal.</summary>
    internal void CloseAdmission()
    {
        GpuPublisher[] publishers;
        lock (_gate)
        {
            if (_disposed || _lifetime.IsCancellationRequested)
            {
                return;
            }

            _lifetime.CancelAsync().ObserveFaults();
            publishers = [.. _publishers];
        }

        List<Exception> failures = [];
        foreach (var publisher in publishers)
        {
            try
            {
                publisher.Router.CloseCommandAdmission();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Graphics command admission closure was incomplete.", failures);
        }
    }

    /// <summary>Called under the coordinator gate so shutdown also joins a removed publisher's cleanup.</summary>
    private void RetirePublisher(GpuPublisher publisher)
    {
        _publisherDisposals.RemoveAll(task => task.IsCompletedSuccessfully);
        var disposal = publisher.DisposeAsync().AsTask();
        _publisherDisposals.Add(disposal);
        disposal.ObserveFaults();
    }

    /// <summary>
    ///     Raised on the UI dispatcher when a publisher opened or closed, its health changed or any of its
    ///     capabilities changed.
    /// </summary>
    internal event Action? Changed;

    /// <summary>Every running graphics publisher's sections and capabilities.</summary>
    /// <returns>
    ///     One page per publisher, in the order they started, read against one health snapshot; empty once
    ///     admission closed.
    /// </returns>
    internal IReadOnlyList<GpuPublisherSnapshot> Snapshots()
    {
        GpuPublisher[] publishers;
        lock (_gate)
        {
            if (_disposed || _lifetime.IsCancellationRequested)
            {
                return [];
            }

            publishers = [.. _publishers];
        }

        var health = HealthSnapshot();
        var layers = _profiles.Current.Layers;
        return [.. publishers.Select(publisher => Snapshot(publisher, health, layers))];
    }

    /// <summary>One publisher's sections and capabilities.</summary>
    /// <param name="pluginId">The graphics plugin's id.</param>
    /// <returns>The page, or null when that plugin is not running.</returns>
    internal GpuPublisherSnapshot? Snapshot(string pluginId)
    {
        return Find(pluginId) is { } publisher
            ? Snapshot(publisher, HealthSnapshot(), _profiles.Current.Layers)
            : null;
    }

    /// <summary>Every published capability that matches, across all graphics publishers.</summary>
    /// <param name="predicate">The filter.</param>
    /// <returns>The matches with their plugin ids.</returns>
    internal IReadOnlyList<PublishedCapability> FindCapabilities(Func<DeviceCapabilityView, bool> predicate)
    {
        GpuPublisher[] publishers;
        lock (_gate)
        {
            publishers = [.. _publishers];
        }

        return
        [
            .. publishers.SelectMany(publisher => publisher.Router.Snapshot().Where(predicate)
                .Select(view => new PublishedCapability(view, publisher.Identity.PluginId)))
        ];
    }

    /// <summary>Refreshes the enabled session-owned graphics drivers after a display layout change.</summary>
    internal async Task RefreshDisplayTopologyAsync(CancellationToken cancellationToken)
    {
        GpuPublisher[] publishers;
        lock (_gate)
        {
            publishers = [.. _publishers.Where(publisher => publisher.Driver is not null)];
        }

        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        foreach (var publisher in publishers)
        {
            admission.Token.ThrowIfCancellationRequested();
            await publisher.Driver!.RefreshTopologyAsync(admission.Token).ConfigureAwait(false);
            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _postToUi(published.SetResult);
            await published.Task.WaitAsync(admission.Token).ConfigureAwait(false);
            // Descriptor replacement schedules the existing profile restoration. Let that finish
            // before layout-specific values are written, so an older profile cannot overwrite them.
            await publisher.WaitForRefreshesAsync(admission.Token).ConfigureAwait(false);
        }
    }

    /// <summary>Reads detached display-control metadata from enabled owners without creating a native driver.</summary>
    internal IReadOnlyList<DisplayGpuCapability> DisplayCapabilities()
    {
        GpuPublisher[] publishers;
        lock (_gate)
        {
            publishers = [.. _publishers];
        }

        List<DisplayGpuCapability> capabilities = [];
        foreach (var publisher in publishers)
        {
            var outputs = publisher.Driver?.DisplayTargets;
            foreach (var view in publisher.Router.Snapshot())
            {
                var descriptor = view.Descriptor;
                // Native output IDs and friendly names do not identify a saved physical monitor.
                if (descriptor.SectionId is not { } section || outputs is null
                                                            || !outputs.TryGetValue(section, out var output)
                                                            || (string.IsNullOrEmpty(output.DevicePath)
                                                                && string.IsNullOrEmpty(output.EdidIdentity)))
                {
                    continue;
                }

                capabilities.Add(new DisplayGpuCapability
                {
                    PluginId = publisher.Identity.PluginId,
                    Target = DisplayGpuProfileService.Target(output,
                        publisher.Router.Sections.FirstOrDefault(item => item.SectionId == section)?.CustomTitle),
                    Descriptor = descriptor,
                    ObservedValue = CapabilityValues.ToPluginValue(view.Projection.State.ObservedValue)
                });
            }
        }

        return capabilities;
    }

    /// <summary>Applies a layout's value to the freshly matched output without creating a per-game override.</summary>
    internal Task<CapabilityCommandResult> ExecuteDisplayPreferenceAsync(DisplayGpuPreference preference,
        CancellationToken cancellationToken)
    {
        var current = DisplayGpuProfileService.Resolve(preference, DisplayCapabilities());
        if (current?.Descriptor is not { SupportsWrite: true } descriptor
            || CapabilityValues.FromPluginValue(preference.Value, descriptor) is not { } value)
        {
            return Task.FromResult(new CapabilityCommandResult
            {
                CommandId = Guid.NewGuid(), Outcome = CommandOutcome.Rejected,
                Reason = new CapabilityReason(CapabilityReasonCode.HostUnavailable,
                    "The configured display control is unavailable, ambiguous or no longer accepts this value.", true),
                CompletedAt = DateTimeOffset.UtcNow
            });
        }

        return ExecuteAsync(current.PluginId!, descriptor.CapabilityId, descriptor.InstanceId, value,
            CapabilityCommandOrigin.ProfileRestore, cancellationToken);
    }

    /// <summary>Writes one graphics capability and, for a user's write, remembers it by its profile scope.</summary>
    /// <param name="pluginId">The graphics plugin's id.</param>
    /// <param name="capabilityId">The capability.</param>
    /// <param name="instanceId">Its instance, or null.</param>
    /// <param name="value">The value, or null to invoke an action.</param>
    /// <param name="origin">Who asked; only <see cref="CapabilityCommandOrigin.User" /> is saved.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>
    ///     The plugin's result; <see cref="CommandOutcome.Accepted" /> when a native per-application value was
    ///     saved for the running game instead of written.
    /// </returns>
    /// <remarks>
    ///     A switched value is saved to the layer in force, a global-only one to Global, and a native
    ///     per-application one to the running game's profile while it is on (see
    ///     <see cref="CapabilityUserWrites" />). Variable refresh is saved through the performance profile's
    ///     own field, like the device's.
    /// </remarks>
    internal async Task<CapabilityCommandResult> ExecuteAsync(
        string pluginId,
        string capabilityId,
        string? instanceId,
        CapabilityValue? value,
        CapabilityCommandOrigin origin = CapabilityCommandOrigin.User,
        CancellationToken cancellationToken = default)
    {
        if (Find(pluginId) is not { } publisher)
        {
            return new CapabilityCommandResult
            {
                CommandId = Guid.NewGuid(),
                Outcome = CommandOutcome.Rejected,
                Reason = new CapabilityReason(CapabilityReasonCode.HostUnavailable,
                    "The graphics plugin is not running.", true),
                CompletedAt = DateTimeOffset.UtcNow
            };
        }

        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = admission.Token;
        var view = publisher.Router.TryGetView(new DeviceCapabilityKey(capabilityId, instanceId));
        if (origin is CapabilityCommandOrigin.User && value is not null && view is not null)
        {
            if (GraphicsPrerequisites.Required(capabilityId, value) is { } prerequisite)
            {
                await EnablePrerequisiteAsync(publisher, prerequisite, capabilityId, instanceId, cancellationToken)
                    .ConfigureAwait(false);
            }

            var result = await CapabilityUserWrites.HandleUserWriteAsync(_profiles, publisher.ProfileKey, view,
                    value, () => publisher.Router.ExecuteAsync(capabilityId, instanceId, value, CommandTimeout,
                        cancellationToken: cancellationToken), _manualVariableRefresh, cancellationToken)
                .ConfigureAwait(false);
            publisher.UpdateContext(_profiles.Current);
            return result;
        }

        return await publisher.Router.ExecuteAsync(capabilityId, instanceId, value, CommandTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Turns a setting's prerequisite on and saves it beside the setting, before the setting itself.</summary>
    /// <param name="publisher">The publisher both belong to.</param>
    /// <param name="prerequisite">The capability that must be on.</param>
    /// <param name="dependent">The capability the user is changing, for the log.</param>
    /// <param name="instanceId">The instance both share.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <remarks>
    ///     Saved to the same layer the user's edit lands in, so a later profile pass keeps it on instead of
    ///     restoring an older value under the setting that needs it. One already on is only saved. A refusal
    ///     is logged and the user's own write still goes ahead.
    /// </remarks>
    private async Task EnablePrerequisiteAsync(GpuPublisher publisher, string prerequisite, string dependent,
        string? instanceId, CancellationToken cancellationToken)
    {
        var view = publisher.Router.TryGetView(new DeviceCapabilityKey(prerequisite, instanceId));
        if (view is null || !view.Descriptor.SupportsWrite)
        {
            return;
        }

        var on = GraphicsPrerequisites.On;
        if (view.Projection.State.ObservedValue is { } observed && CapabilityValues.Same(observed, on))
        {
            await CapabilityUserWrites.PersistAsync(_profiles, publisher.ProfileKey, view, on, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var result = await CapabilityUserWrites.HandleUserWriteAsync(_profiles, publisher.ProfileKey, view, on,
                () => publisher.Router.ExecuteAsync(prerequisite, instanceId, on, CommandTimeout,
                    cancellationToken: cancellationToken), _manualVariableRefresh, cancellationToken)
            .ConfigureAwait(false);
        var message = $"Graphics {publisher.ProfileKey}: {prerequisite} turned on for {dependent}: {result.Outcome}"
                      + (result.Reason?.Detail is { Length: > 0 } detail ? $", {detail}." : ".");
        if (result.Outcome.IsApplied())
        {
            Log.Info(message);
        }
        else
        {
            Log.Warn(message);
        }
    }

    /// <summary>Returns a graphics setting to Global for the running game.</summary>
    /// <param name="overrideId">The id a <see cref="GpuCapabilityView" /> carried.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>Whether an override was removed.</returns>
    /// <remarks>
    ///     Clears exactly that publisher's value. The typed variable-refresh field is cleared as the
    ///     performance rows clear it; a device package's own id is refused, because it is not this owner's.
    /// </remarks>
    internal async Task<bool> UseGlobalAsync(string overrideId, CancellationToken cancellationToken = default)
    {
        if (_disposed || _lifetime.IsCancellationRequested || !ProfileSettingKey.TryParse(overrideId, out var key)
            || (key.Field is ProfileField.Device && !ProfileSettingKey.IsGpuPublisher(key.Publisher)))
        {
            return false;
        }

        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        return await _profiles.ClearGameOverrideAsync(key, key.Publisher, admission.Token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Carries a profile change to every graphics publisher: desired values, their reconciliation and the
    ///     per-application sync.
    /// </summary>
    /// <param name="snapshot">The profiles and the application they resolve for.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>A task completing once every publisher was attempted.</returns>
    internal async Task ApplyProfilesAsync(ProfileSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        GpuPublisher[] publishers;
        lock (_gate)
        {
            if (_disposed || _lifetime.IsCancellationRequested)
            {
                return;
            }

            publishers = [.. _publishers];
        }

        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = admission.Token;
        foreach (var publisher in publishers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await publisher.RefreshAsync(snapshot, $"profile generation {snapshot.Generation}",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"Graphics {publisher.ProfileKey}: profile apply failed: {ex.Message}");
            }
        }
    }

    private static GpuPublisherSnapshot Snapshot(
        GpuPublisher publisher,
        IReadOnlyList<PluginHealthPublication> health,
        ProfileLayers layers)
    {
        return new GpuPublisherSnapshot(
            publisher.Describe(health),
            publisher.Router.Sections,
            [
                .. publisher.Router.Snapshot()
                    .Select(view => new GpuCapabilityView(view, CapabilityRowProjection.OverrideIdFor(view, layers)))
            ]);
    }

    private GpuPublisher? Find(string pluginId)
    {
        lock (_gate)
        {
            if (_disposed || _lifetime.IsCancellationRequested)
            {
                return null;
            }

            return _publishers.FirstOrDefault(publisher =>
                string.Equals(publisher.Identity.PluginId, pluginId, StringComparison.Ordinal));
        }
    }

    private void OnHealthChanged(PluginHealthPublication publication)
    {
        lock (_gate)
        {
            if (_publishers.All(publisher => publisher.Identity != publication.Instance))
            {
                return;
            }
        }

        PostChanged();
    }

    internal void BuiltinChanged()
    {
        PostChanged();
    }

    private IReadOnlyList<PluginHealthPublication> HealthSnapshot()
    {
        return _host.Snapshot();
    }

    /// <summary>Coalesces change notifications into one on the UI dispatcher.</summary>
    private void PostChanged()
    {
        lock (_gate)
        {
            if (_changePosted || _disposed || _lifetime.IsCancellationRequested)
            {
                return;
            }

            _changePosted = true;
        }

        _postToUi(() =>
        {
            lock (_gate)
            {
                _changePosted = false;
                if (_disposed || _lifetime.IsCancellationRequested)
                {
                    return;
                }
            }

            Changed?.Invoke();
        });
    }

    private void RaiseChangedOnUi()
    {
        if (_disposed || _lifetime.IsCancellationRequested)
        {
            return;
        }

        // The router already raised this on the UI dispatcher.
        Changed?.Invoke();
    }
}
