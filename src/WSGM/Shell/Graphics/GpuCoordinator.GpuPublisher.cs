using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibGPUDriverInteract;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

internal sealed partial class GpuCoordinator
{
    /// <summary>One running graphics publisher: its channel, router and sync record.</summary>
    private sealed class GpuPublisher : IAsyncDisposable
    {
        private readonly Action _descriptorsAccepted;
        private readonly Action _opened;
        private readonly GpuCoordinator _owner;
        private readonly SemaphoreSlim _refreshGate = new(1, 1);
        private readonly List<Task> _refreshWork = [];
        private readonly Action<IReadOnlyList<DeviceCapabilityView>> _routerChanged;
        private volatile bool _closed;
        private string? _lastApplicationId;
        private string? _lastSyncFingerprint;

        /// <summary>The descriptor set whose restore still waits for its states, or null.</summary>
        private PendingRestore? _pendingRestore;

        private volatile bool _syncRequired = true;

        internal GpuPublisher(GpuCoordinator owner, PluginInstanceIdentity identity, string name, string profileKey,
            ICapabilityPublisher source, DeviceCapabilityRouter router, PluginCapabilityChannel? channel = null,
            GpuDriverPublisher? driver = null)
        {
            _owner = owner;
            Identity = identity;
            Name = name;
            ProfileKey = profileKey;
            Source = source;
            Channel = channel;
            Driver = driver;
            Router = router;
            _opened = OnOpened;
            _descriptorsAccepted = OnDescriptorsAccepted;
            _routerChanged = _ =>
            {
                owner.RaiseChangedOnUi();
                TryRestoreWithStates();
            };
            if (Channel is not null)
            {
                Channel.Opened += _opened;
                Channel.AdmissionClosed += Router.CloseCommandAdmission;
            }

            Router.DescriptorsAccepted += _descriptorsAccepted;
            Router.Changed += _routerChanged;
            if (Driver is not null)
            {
                Router.Attach(Source);
                UpdateContext(owner._profiles.Current);
            }
        }

        internal PluginInstanceIdentity Identity { get; }
        internal string Name { get; }
        internal string ProfileKey { get; }
        internal ICapabilityPublisher Source { get; }
        internal PluginCapabilityChannel? Channel { get; }
        internal GpuDriverPublisher? Driver { get; }
        internal DeviceCapabilityRouter Router { get; }

        public async ValueTask DisposeAsync()
        {
            Task[] refreshes;
            lock (_owner._gate)
            {
                _closed = true;
                refreshes = [.. _refreshWork];
                _refreshWork.Clear();
            }

            if (Channel is not null)
            {
                Channel.Opened -= _opened;
                Channel.AdmissionClosed -= Router.CloseCommandAdmission;
            }

            Router.DescriptorsAccepted -= _descriptorsAccepted;
            Router.Changed -= _routerChanged;
            Channel?.Dispose();
            Router.Detach();
            try
            {
                await Task.WhenAll(refreshes).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Queued refreshes use the session lifetime and are expected to cancel at shutdown.
            }
            finally
            {
                await _refreshGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await Router.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    _refreshGate.Release();
                }
            }
        }

        internal Task WaitForRefreshesAsync(CancellationToken cancellationToken)
        {
            Task[] work;
            lock (_owner._gate)
            {
                work = [.. _refreshWork];
            }

            return Task.WhenAll(work).WaitAsync(cancellationToken);
        }

        internal GpuPublisherView Describe(IReadOnlyList<PluginHealthPublication> health)
        {
            if (Driver is { } driver)
            {
                var status = driver.Status;
                return new GpuPublisherView(Identity, Name, ProfileKey, !driver.IsActive
                    ? PluginHealth.Unavailable
                    : status.Health switch
                    {
                        GpuHealth.Ready => PluginHealth.Ready,
                        GpuHealth.Unavailable => PluginHealth.Unavailable,
                        GpuHealth.Failed => PluginHealth.Failed,
                        _ => throw new ArgumentOutOfRangeException(nameof(status))
                    }, status.Detail);
            }

            var current = health.FirstOrDefault(publication => publication.Instance == Identity);
            return new GpuPublisherView(Identity, Name, ProfileKey, current?.Health ?? PluginHealth.Unavailable,
                current?.Detail);
        }

        internal void UpdateContext(ProfileSnapshot snapshot)
        {
            // Unknown power reads as AC here, as it does for the device: only a confirmed battery state
            // selects battery values.
            Router.UpdateDesiredContext(ProfileKey, snapshot.Layers, _owner._onAcPower() ?? true);
        }

        /// <summary>Reconciles desired values, then syncs the per-application set when it changed.</summary>
        internal async Task RefreshAsync(ProfileSnapshot snapshot, string reason, CancellationToken cancellationToken)
        {
            await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_closed || _owner._lifetime.IsCancellationRequested || !Source.IsActive)
                {
                    return;
                }

                UpdateContext(snapshot);
                await CapabilityDesiredReconciler.RunAsync(
                    new CapabilityReconcilePass(ProfileKey, Router.Snapshot, Router.TryGetView, RestoreAsync)
                    {
                        Priority = GraphicsPrerequisites.Priority,
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
                cancellationToken: _owner._lifetime.Token).ConfigureAwait(false);
        }

        /// <summary>Hands the driver every game's native values when the set, the game or the cycle changed.</summary>
        private async Task SyncAsync(ProfileSnapshot snapshot, string reason, CancellationToken cancellationToken)
        {
            if (_owner._lifetime.IsCancellationRequested)
            {
                return;
            }

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
            ApplicationProfileSync sync = new(Interlocked.Increment(ref _owner._syncRevision),
                profiles);
            try
            {
                ApplicationProfileSyncResult? result;
                if (Driver is { } driver)
                {
                    using var budget = Deadline.After(SyncBudget)
                        .CreateCancellationSource(cancellationToken);
                    result = await driver.SyncApplicationProfilesAsync(sync, budget.Token).ConfigureAwait(false);
                }
                else
                {
                    result = await Channel!.SyncApplicationProfilesAsync(sync, SyncBudget, cancellationToken)
                        .ConfigureAwait(false);
                }

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
                foreach (var failure in result.Failures)
                {
                    if (string.IsNullOrEmpty(failure.ProfileId) && string.IsNullOrEmpty(failure.Executable))
                    {
                        Log.Warn($"Graphics {ProfileKey}: application profile sync was refused: {failure.Detail}");
                    }
                    else
                    {
                        Log.Warn($"Graphics {ProfileKey}: {failure.CapabilityId} for {failure.Executable} "
                                 + $"(game {failure.ProfileId}) was refused: {failure.Detail}");
                    }
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

        private void OnOpened()
        {
            // Runs on the lifecycle lane before the plugin starts or resumes, so the router is connected
            // to the new cycle before the first descriptor set of it arrives.
            lock (_owner._gate)
            {
                if (_closed || _owner._disposed || _owner._lifetime.IsCancellationRequested)
                {
                    return;
                }

                Router.Attach(Source);
            }

            if (_owner._lifetime.IsCancellationRequested)
            {
                return;
            }

            UpdateContext(_owner._profiles.Current);
            _syncRequired = true;
            Log.Info($"Graphics {ProfileKey}: capability publisher opened.");
        }

        private void OnDescriptorsAccepted()
        {
            // A new set arrives with no state at all, so restoring now would find every capability
            // unknown and restore nothing. The restore runs when the last of the set's first states
            // arrives; the plugin reports every capability in the observation that follows the set.
            _syncRequired = true;
            Volatile.Write(ref _pendingRestore, new PendingRestore());
            TryRestoreWithStates();
        }

        /// <summary>Runs the pending restore once every descriptor of its set has reported a state.</summary>
        private void TryRestoreWithStates()
        {
            if (Volatile.Read(ref _pendingRestore) is not { } restore
                || !Router.HasStateForEveryDescriptor()
                || !ReferenceEquals(Interlocked.CompareExchange(ref _pendingRestore, null, restore), restore))
            {
                return;
            }

            Run(restore.Reason);
        }

        private void Run(string reason)
        {
            lock (_owner._gate)
            {
                if (_owner._disposed || !TryGetLifetime(out var token))
                {
                    return;
                }

                _refreshWork.RemoveAll(task => task.IsCompleted);
                var refresh = Task.Run(() => RefreshAsync(_owner._profiles.Current, reason, token), token);
                _refreshWork.Add(refresh);
                refresh.ObserveFaults();
            }
        }

        private bool TryGetLifetime(out CancellationToken token)
        {
            try
            {
                token = _owner._lifetime.Token;
                return !_closed && !token.IsCancellationRequested;
            }
            catch (ObjectDisposedException)
            {
                token = CancellationToken.None;
                return false;
            }
        }

        /// <summary>A descriptor set's restore, waiting for the set's first states.</summary>
        private sealed class PendingRestore
        {
            internal string Reason => "capability replacement";
        }
    }
}
