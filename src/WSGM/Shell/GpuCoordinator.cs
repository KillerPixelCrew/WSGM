using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>One running graphics publisher, as a page lists it.</summary>
/// <param name="Identity">The plugin instance.</param>
/// <param name="Name">The package's display name.</param>
/// <param name="ProfileKey">The key its values are stored under, <c>gpu:</c> and the plugin id.</param>
/// <param name="Health">The plugin's last reported health.</param>
/// <param name="HealthDetail">The plugin's detail for that health, or null.</param>
internal sealed record GpuPublisherView(
    PluginInstanceIdentity Identity,
    string Name,
    string ProfileKey,
    PluginHealth Health,
    string? HealthDetail);

/// <summary>One graphics capability, ready for a control.</summary>
/// <param name="View">
///     The router's view: the descriptor, the projection with its desired and Global values, profile scope
///     and apply timing, and the last command result.
/// </param>
/// <param name="OverrideId">
///     The id to hand <see cref="GpuCoordinator.UseGlobalAsync" /> while the running game's profile supplies
///     the value, or null when the value is Global's.
/// </param>
internal sealed record GpuCapabilityView(DeviceCapabilityView View, string? OverrideId);

/// <summary>One graphics publisher's page.</summary>
/// <param name="Publisher">The publisher.</param>
/// <param name="Sections">The overlay sections its descriptor set declares.</param>
/// <param name="Capabilities">Its capabilities in router order.</param>
internal sealed record GpuPublisherSnapshot(
    GpuPublisherView Publisher,
    IReadOnlyList<CapabilitySection> Sections,
    IReadOnlyList<GpuCapabilityView> Capabilities);

/// <summary>A published capability and the graphics plugin it came from, or null for the device package.</summary>
/// <param name="View">The capability.</param>
/// <param name="GpuPluginId">The graphics plugin's id, or null when the device package publishes it.</param>
internal sealed record PublishedCapability(DeviceCapabilityView View, string? GpuPluginId);

/// <summary>
///     Owns every running <c>wsgm.gpu</c> package's capabilities: one router per publisher, its desired
///     values, the user's writes and the per-application sync.
/// </summary>
/// <remarks>
///     Beside <see cref="DeviceCoordinator" />, never merged with it: the device router answers "the power
///     limit" or "the fan" with exactly one match, and a graphics package must not add a second. It runs
///     whatever the device integration switch says and takes no part in the machine-wide device owner. The
///     plugins' lifecycles belong to <see cref="CommonPluginManager" />; this opens a channel for each one
///     it starts and drops it when the plugin stops.
/// </remarks>
internal sealed class GpuCoordinator : ICapabilityChannelRegistry, IAsyncDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SyncBudget = TimeSpan.FromSeconds(15);

    /// <summary>How long a new descriptor set may wait for its states before its restore runs anyway.</summary>
    private static readonly TimeSpan StateWait = TimeSpan.FromSeconds(3);

    /// <summary>
    ///     The last per-application sync revision, shared by every publisher and every open of one, so a
    ///     plugin that is stopped and started again within the process never sees a revision repeat.
    /// </summary>
    private static long _syncRevision;

    private readonly Lock _gate = new();
    private readonly PluginHost _host;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Action<Action> _postToUi;
    private readonly ProfileService _profiles;
    private readonly List<GpuPublisher> _publishers = [];
    private bool _changePosted;
    private bool _disposed;
    private Action<bool>? _manualVariableRefresh;

    /// <param name="postToUi">Posts work to the UI dispatcher.</param>
    /// <param name="profiles">The profile owner values are stored in.</param>
    /// <param name="host">The plugin host, for each publisher's health.</param>
    internal GpuCoordinator(Action<Action> postToUi, ProfileService profiles, PluginHost host)
    {
        _postToUi = postToUi ?? throw new ArgumentNullException(nameof(postToUi));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _host = host ?? throw new ArgumentNullException(nameof(host));
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
        GpuPublisher[] publishers;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            publishers = [.. _publishers];
            _publishers.Clear();
        }

        _host.HealthChanged -= OnHealthChanged;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        foreach (var publisher in publishers)
        {
            await publisher.DisposeAsync().ConfigureAwait(false);
        }

        _lifetime.Dispose();
    }

    public PluginCapabilityChannel Open(PluginInstanceIdentity identity, PluginManifest manifest,
        ICapabilityPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(plugin);
        var key = ProfileSettingKey.GpuPublisher(identity.PluginId);
        PluginCapabilityChannel channel = new(identity, manifest.Capabilities, plugin);
        DeviceCapabilityRouter router = new(_postToUi, key);
        GpuPublisher publisher = new(this, identity, manifest.Name, key, channel, router);
        GpuPublisher? stale = null;
        lock (_gate)
        {
            var existing = _publishers.FirstOrDefault(candidate => candidate.Identity == identity);
            if (_disposed || existing is { Channel.IsClosed: false })
            {
                publisher.DisposeAsync().AsTask().ObserveFaults();
                throw new InvalidOperationException("The graphics publisher is already open or the session ended.");
            }

            // A registration whose channel already ended is no longer attached to any plugin: its owner
            // lost it without closing it here. It must not keep the instance from starting again.
            if (existing is not null)
            {
                _publishers.Remove(existing);
                stale = existing;
            }

            _publishers.Add(publisher);
        }

        if (stale is not null)
        {
            stale.DisposeAsync().AsTask().ObserveFaults();
            Log.Warn($"Graphics: {identity.PluginId} replaced a registration whose channel had already ended.");
        }

        Log.Info($"Graphics: {identity.PluginId} ({manifest.Name}) publishes as {key}, roles "
                 + $"{string.Join(", ", manifest.Capabilities)}.");
        PostChanged();
        return channel;
    }

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
            }
        }

        if (publisher is null)
        {
            channel.Dispose();
            return;
        }

        publisher.DisposeAsync().AsTask().ObserveFaults();
        Log.Info($"Graphics: {publisher.Identity.PluginId} stopped publishing.");
        PostChanged();
    }

    /// <summary>
    ///     Raised on the UI dispatcher when a publisher opened or closed, its health changed or any of its
    ///     capabilities changed.
    /// </summary>
    internal event Action? Changed;

    /// <summary>The running graphics publishers.</summary>
    /// <returns>Each publisher with its health, in the order they started.</returns>
    internal IReadOnlyList<GpuPublisherView> Publishers()
    {
        GpuPublisher[] publishers;
        lock (_gate)
        {
            publishers = [.. _publishers];
        }

        var health = _host.Snapshot();
        return [.. publishers.Select(publisher => publisher.Describe(health))];
    }

    /// <summary>One publisher's sections and capabilities.</summary>
    /// <param name="pluginId">The graphics plugin's id.</param>
    /// <returns>The page, or null when that plugin is not running.</returns>
    internal GpuPublisherSnapshot? Snapshot(string pluginId)
    {
        if (Find(pluginId) is not { } publisher)
        {
            return null;
        }

        var layers = _profiles.Current.Layers;
        return new GpuPublisherSnapshot(
            publisher.Describe(_host.Snapshot()),
            publisher.Router.Sections,
            [
                .. publisher.Router.Snapshot()
                    .Select(view => new GpuCapabilityView(view, DeviceOverlayBridge.OverrideIdFor(view, layers)))
            ]);
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

    /// <summary>
    ///     Attaches the hook that saves a user-originated variable-refresh write to the performance
    ///     profile, as <see cref="DeviceCoordinator.AttachManualVariableRefreshOverride" /> does for the device.
    /// </summary>
    /// <param name="note">Receives the accepted state, or null when no profile owner exists.</param>
    internal void AttachManualVariableRefreshOverride(Action<bool>? note)
    {
        _manualVariableRefresh = note;
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

        var view = publisher.Router.Snapshot().FirstOrDefault(candidate =>
            string.Equals(candidate.Descriptor.CapabilityId, capabilityId, StringComparison.Ordinal)
            && string.Equals(candidate.Descriptor.InstanceId, instanceId, StringComparison.Ordinal));
        var user = origin is CapabilityCommandOrigin.User && value is not null && view is not null;
        if (user && !DeviceCoordinator.PerformanceProfileOwnsRole(view!.Descriptor.Role)
                 && !CapabilityUserWrites.Decide(view.Descriptor.ProfileScope, _profiles.Current.EditsGame).Command)
        {
            var stored = await CapabilityUserWrites.StoreForApplicationAsync(_profiles, publisher.ProfileKey,
                view, value!, cancellationToken).ConfigureAwait(false);
            publisher.UpdateContext(_profiles.Current);
            Log.Info($"Graphics {publisher.ProfileKey}: {CapabilityDesiredReconciler.Name(view)} saved for game "
                     + $"{_profiles.Current.Active.GameProfileId}; its driver applies it ({stored.Outcome}).");
            return stored;
        }

        var result = await publisher.Router.ExecuteAsync(capabilityId, instanceId, value, CommandTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!user || !result.Outcome.IsApplied() || !view!.Descriptor.SupportsWrite)
        {
            return result;
        }

        if (view.Descriptor.Role is CapabilityRole.VariableRefreshRate)
        {
            if (value!.BooleanValue is { } enabled)
            {
                _manualVariableRefresh?.Invoke(enabled);
            }
        }
        else if (!DeviceCoordinator.PerformanceProfileOwnsRole(view.Descriptor.Role)
                 && await CapabilityUserWrites.PersistAsync(_profiles, publisher.ProfileKey, view, value!,
                     cancellationToken).ConfigureAwait(false))
        {
            publisher.UpdateContext(_profiles.Current);
        }

        return result;
    }

    /// <summary>Returns a graphics setting to Global for the running game.</summary>
    /// <param name="overrideId">The id a <see cref="GpuCapabilityView" /> carried.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>Whether an override was removed.</returns>
    /// <remarks>
    ///     Clears exactly that publisher's value. The typed variable-refresh field is cleared as the
    ///     performance rows clear it; a device package's own id is refused, because it is not this owner's.
    /// </remarks>
    internal Task<bool> UseGlobalAsync(string overrideId, CancellationToken cancellationToken = default)
    {
        if (!ProfileSettingKey.TryParse(overrideId, out var key)
            || (key.Field is ProfileField.Device && !ProfileSettingKey.IsGpuPublisher(key.Publisher)))
        {
            return Task.FromResult(false);
        }

        return _profiles.ClearGameOverrideAsync(key, key.Publisher, cancellationToken);
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
            publishers = [.. _publishers];
        }

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

    private GpuPublisher? Find(string pluginId)
    {
        lock (_gate)
        {
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

    /// <summary>Coalesces change notifications into one on the UI dispatcher.</summary>
    private void PostChanged()
    {
        lock (_gate)
        {
            if (_changePosted || _disposed)
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
                if (_disposed)
                {
                    return;
                }
            }

            Changed?.Invoke();
        });
    }

    private void RaiseChangedOnUi()
    {
        // The router already raised this on the UI dispatcher.
        Changed?.Invoke();
    }

    /// <summary>One running graphics publisher: its channel, router and sync record.</summary>
    private sealed class GpuPublisher : IAsyncDisposable
    {
        private readonly Action<long> _cycleStarted;
        private readonly Action<long, long> _descriptorsAccepted;
        private readonly GpuCoordinator _owner;
        private readonly SemaphoreSlim _refreshGate = new(1, 1);
        private readonly Action<IReadOnlyList<DeviceCapabilityView>> _routerChanged;
        private volatile bool _closed;
        private string? _lastApplicationId;
        private string? _lastSyncFingerprint;

        /// <summary>The descriptor set whose restore still waits for its states, or null.</summary>
        private PendingRestore? _pendingRestore;

        private volatile bool _syncRequired = true;

        internal GpuPublisher(GpuCoordinator owner, PluginInstanceIdentity identity, string name, string profileKey,
            PluginCapabilityChannel channel, DeviceCapabilityRouter router)
        {
            _owner = owner;
            Identity = identity;
            Name = name;
            ProfileKey = profileKey;
            Channel = channel;
            Router = router;
            _cycleStarted = OnCycleStarted;
            _descriptorsAccepted = OnDescriptorsAccepted;
            _routerChanged = _ =>
            {
                owner.RaiseChangedOnUi();
                TryRestoreWithStates();
            };
            Channel.CycleStarted += _cycleStarted;
            Channel.AdmissionClosed += Router.CloseCommandAdmission;
            Router.DescriptorsAccepted += _descriptorsAccepted;
            Router.Changed += _routerChanged;
        }

        internal PluginInstanceIdentity Identity { get; }
        internal string Name { get; }
        internal string ProfileKey { get; }
        internal PluginCapabilityChannel Channel { get; }
        internal DeviceCapabilityRouter Router { get; }

        public async ValueTask DisposeAsync()
        {
            _closed = true;
            Channel.CycleStarted -= _cycleStarted;
            Channel.AdmissionClosed -= Router.CloseCommandAdmission;
            Router.DescriptorsAccepted -= _descriptorsAccepted;
            Router.Changed -= _routerChanged;
            Channel.Dispose();
            Router.Detach();
            await Router.DisposeAsync().ConfigureAwait(false);
        }

        internal GpuPublisherView Describe(IReadOnlyList<PluginHealthPublication> health)
        {
            var current = health.FirstOrDefault(publication => publication.Instance == Identity);
            return new GpuPublisherView(Identity, Name, ProfileKey, current?.Health ?? PluginHealth.Unavailable,
                current?.Detail);
        }

        internal void UpdateContext(ProfileSnapshot snapshot)
        {
            // Unknown power reads as AC here, as it does for the device: only a confirmed battery state
            // selects battery values.
            Router.UpdateDesiredContext(ProfileKey, snapshot.Layers, DeviceCoordinator.ReadOnAcPower() ?? true);
        }

        /// <summary>Reconciles desired values, then syncs the per-application set when it changed.</summary>
        internal async Task RefreshAsync(ProfileSnapshot snapshot, string reason, CancellationToken cancellationToken)
        {
            UpdateContext(snapshot);
            await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_closed || !Channel.IsOpen)
                {
                    return;
                }

                await CapabilityDesiredReconciler.RunAsync(
                    new CapabilityReconcilePass(ProfileKey, Router.Snapshot, RestoreAsync)
                    {
                        // A capability the plugin has not reported yet is waiting, not unavailable; the
                        // pass that follows its first state restores it.
                        HasState = view => Router.HasState(view.Descriptor)
                    },
                    reason,
                    cancellationToken).ConfigureAwait(false);
                await SyncAsync(snapshot, reason, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        private async Task<CapabilityCommandResult?> RestoreAsync(
            DeviceCapabilityView view,
            CapabilityValue desired,
            CancellationToken cancellationToken)
        {
            // Cancelled between writes, never inside one, as the device restore is: a write cancelled
            // in flight ends Indeterminate. The command's own timeout still bounds the wait.
            return await Router.ExecuteAsync(
                view.Descriptor.CapabilityId,
                view.Descriptor.InstanceId,
                desired,
                CommandTimeout,
                view.Projection.State.CycleGeneration,
                view.Projection.State.DescriptorGeneration,
                cancellationToken: _owner._lifetime.Token).ConfigureAwait(false);
        }

        /// <summary>Hands the driver every game's native values when the set, the game or the cycle changed.</summary>
        private async Task SyncAsync(ProfileSnapshot snapshot, string reason, CancellationToken cancellationToken)
        {
            var descriptors = Router.Snapshot().Select(view => view.Descriptor).ToArray();
            if (!descriptors.Any(descriptor => descriptor.ProfileScope is CapabilityProfileScope.NativePerApplication))
            {
                return;
            }

            var profiles = ApplicationProfileSyncBuilder.Build(snapshot.Config, ProfileKey, descriptors);
            var fingerprint = ApplicationProfileSyncBuilder.Fingerprint(profiles);
            // A game starting is its own reason: the driver reads its profile at launch, and a sync that
            // failed earlier gets another chance exactly when it matters.
            var applicationStarted = snapshot.Active.ApplicationId is { } application
                                     && !string.Equals(application, _lastApplicationId, StringComparison.Ordinal);
            _lastApplicationId = snapshot.Active.ApplicationId;
            if (!_syncRequired && !applicationStarted
                               && string.Equals(fingerprint, _lastSyncFingerprint, StringComparison.Ordinal))
            {
                return;
            }

            _syncRequired = false;
            // Forgotten until the plugin confirms this set, so a sync that failed, timed out or was refused
            // in part never lets a later pass skip the same set as already applied.
            _lastSyncFingerprint = null;
            ApplicationProfileSync sync = new(Interlocked.Increment(ref _syncRevision), Channel.CycleGeneration,
                profiles);
            try
            {
                var result = await Channel.SyncApplicationProfilesAsync(sync, SyncBudget, cancellationToken)
                    .ConfigureAwait(false);
                if (result is null)
                {
                    // Admission closed under the pass; the next cycle's descriptors ask again.
                    _syncRequired = true;
                    return;
                }

                if (result.Failures.Count == 0)
                {
                    _lastSyncFingerprint = fingerprint;
                }

                Log.Info($"Graphics {ProfileKey}: per-application sync {sync.Revision} ({reason}): "
                         + $"games={profiles.Count}, written={result.Written}, removed={result.Removed}, "
                         + $"refused={result.Failures.Count}.");
                foreach (var failure in result.Failures.Take(8))
                {
                    Log.Warn($"Graphics {ProfileKey}: {failure.CapabilityId} for {failure.Executable} "
                             + $"(game {failure.ProfileId}) was refused: {failure.Detail}");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Log.Warn($"Graphics {ProfileKey}: per-application sync {sync.Revision} did not finish in "
                         + $"{SyncBudget.TotalSeconds:0} s; the next change sends the full set again.");
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
            {
                Log.Warn($"Graphics {ProfileKey}: per-application sync {sync.Revision} failed: {ex.Message}");
            }
        }

        private void OnCycleStarted(long generation)
        {
            // Runs on the lifecycle lane before the plugin starts or resumes, so the router is connected
            // to the new cycle before the first descriptor set of it arrives.
            Router.Attach(Channel, generation);
            UpdateContext(_owner._profiles.Current);
            _syncRequired = true;
            Log.Info($"Graphics {ProfileKey}: capability cycle {generation} began.");
        }

        private void OnDescriptorsAccepted(long cycleGeneration, long descriptorGeneration)
        {
            // A new set arrives with no state at all, so restoring now would find every capability
            // unknown and restore nothing. The restore waits for the set's states, as the device's waits
            // for its cycle to become active, and runs anyway once the wait runs out.
            _syncRequired = true;
            PendingRestore restore = new(cycleGeneration, descriptorGeneration);
            Volatile.Write(ref _pendingRestore, restore);
            if (!TryGetLifetime(out var token))
            {
                return;
            }

            Task.Delay(StateWait, token).ContinueWith(_ =>
                {
                    // Still pending: run without the missing states, and keep waiting for them.
                    if (ReferenceEquals(Volatile.Read(ref _pendingRestore), restore))
                    {
                        Run(restore.Reason + ", states still missing");
                    }
                }, token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default)
                .ObserveFaults();
            TryRestoreWithStates();
        }

        /// <summary>Runs the pending restore once every descriptor of its set has reported a state.</summary>
        private void TryRestoreWithStates()
        {
            if (Volatile.Read(ref _pendingRestore) is not { } restore
                || !Router.HasStateForEveryDescriptor(restore.DescriptorGeneration)
                || !ReferenceEquals(Interlocked.CompareExchange(ref _pendingRestore, null, restore), restore))
            {
                return;
            }

            Run(restore.Reason);
        }

        private void Run(string reason)
        {
            if (!TryGetLifetime(out var token))
            {
                return;
            }

            Task.Run(() => RefreshAsync(_owner._profiles.Current, reason, token), token).ObserveFaults();
        }

        private bool TryGetLifetime(out CancellationToken token)
        {
            try
            {
                token = _owner._lifetime.Token;
                return !_closed;
            }
            catch (ObjectDisposedException)
            {
                token = CancellationToken.None;
                return false;
            }
        }

        /// <summary>A descriptor set's restore, waiting for the set's first states.</summary>
        private sealed class PendingRestore(long cycleGeneration, long descriptorGeneration)
        {
            internal long DescriptorGeneration { get; } = descriptorGeneration;

            internal string Reason { get; } = $"cycle {cycleGeneration}, descriptors {descriptorGeneration}";
        }
    }
}
