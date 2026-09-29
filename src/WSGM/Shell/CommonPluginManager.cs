using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Install;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

internal sealed record CommonPluginInstanceView(
    PluginInstanceIdentity Identity,
    PluginManifest Manifest,
    PluginRegistration? Registration,
    string? Error);

/// <summary>Opens and closes the capability channel of each <c>wsgm.gpu</c> instance.</summary>
internal interface ICapabilityChannelRegistry
{
    /// <summary>Creates the channel before the plugin is admitted, so its router sees every publication.</summary>
    /// <param name="identity">The plugin instance.</param>
    /// <param name="manifest">Its manifest, whose capability roles the channel enforces.</param>
    /// <param name="plugin">The plugin's capability surface.</param>
    /// <returns>The channel to admit the plugin with.</returns>
    PluginCapabilityChannel Open(PluginInstanceIdentity identity, PluginManifest manifest, ICapabilityPlugin plugin);

    /// <summary>Ends a channel once its plugin stopped or failed to start.</summary>
    /// <param name="channel">The channel.</param>
    void Close(PluginCapabilityChannel channel);
}

/// <summary>
///     Owns enabled non-device instances independently of the Device master switch, including the graphics
///     packages <see cref="CommonPluginEnablement" /> enables by default.
/// </summary>
internal sealed class CommonPluginManager
{
    private readonly Func<IReadOnlyList<DisplayAdapterIdentity>> _adapters;
    private readonly ICapabilityChannelRegistry? _capabilityChannels;
    private readonly Dictionary<PluginInstanceIdentity, Entry> _entries = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly PluginHost _host;
    private readonly string _installedRoot;
    private readonly Func<CommonInstalledPlugin, CancellationToken, Task<IPlugin>> _load;
    private readonly Lock _stateGate = new();
    private readonly string _stateRoot;
    private volatile PluginPackageCatalog _catalog = PluginPackageCatalog.Empty;

    /// <summary>The configuration the last reconcile was asked for, so a resume can restart an instance.</summary>
    private CommonPluginInstanceConfig[] _configured = [];

    /// <summary>The adapters the last reconcile read, for deciding which instances are retiring.</summary>
    private IReadOnlyList<DisplayAdapterIdentity> _lastAdapters = [];

    private long _requestedRevision;
    private volatile bool _stopping;

    /// <param name="host">The plugin host instances are admitted to.</param>
    /// <param name="installedRoot">The Plugins folder.</param>
    /// <param name="stateRoot">The root of every instance's state directory.</param>
    /// <param name="load">Loads a package's plugin; tests replace it.</param>
    /// <param name="capabilityChannels">
    ///     Opens a graphics instance's capability channel. Without one, graphics packages cannot start.
    /// </param>
    /// <param name="adapters">Reads the present display adapters; defaults to the cached inventory.</param>
    internal CommonPluginManager(PluginHost host, string installedRoot, string stateRoot,
        Func<CommonInstalledPlugin, CancellationToken, Task<IPlugin>>? load = null,
        ICapabilityChannelRegistry? capabilityChannels = null,
        Func<IReadOnlyList<DisplayAdapterIdentity>>? adapters = null)
    {
        _host = host;
        _capabilityChannels = capabilityChannels;
        _adapters = adapters ?? (static () => CommonPluginEnablement.PresentAdapters);
        _installedRoot = Path.GetFullPath(installedRoot);
        _stateRoot = Path.GetFullPath(stateRoot);
        _load = load ?? (async (package, token) =>
            package.Factory is { } factory
                ? factory()
                : await CommonPluginPackage.LoadAsync(package.PackagePath, package.Manifest, token)
                    .ConfigureAwait(false));
    }

    /// <summary>The installed packages as of the last reconcile, read without touching the disk.</summary>
    internal PluginPackageCatalog Catalog => _catalog;

    /// <summary>Raised after the admitted-plugin projection may have changed.</summary>
    internal event Action? Changed;

    /// <summary>Whether an installed package runs by default when the configuration does not name it.</summary>
    /// <param name="manifest">The package manifest.</param>
    /// <returns>True for a graphics package serving an adapter the last reconcile found.</returns>
    internal bool EnabledByDefault(PluginManifest manifest)
    {
        return CommonPluginEnablement.EnabledByDefault(manifest, Volatile.Read(ref _lastAdapters));
    }

    internal CommonPluginInstanceView[] Snapshot()
    {
        lock (_stateGate)
        {
            return
            [
                .. _entries.Values.Select(entry =>
                    new CommonPluginInstanceView(entry.Identity, entry.Package.Manifest, entry.Registration,
                        entry.Error))
            ];
        }
    }

    internal Task ReconcileAsync(IReadOnlyList<CommonPluginInstanceConfig> configured,
        CancellationToken cancellationToken)
    {
        var revision = Interlocked.Increment(ref _requestedRevision);
        if (configured.Count > 128)
        {
            throw new InvalidDataException("Too many configured plugin instances.");
        }

        // Detached, so a caller editing its configuration objects later cannot change this reconcile.
        CommonPluginInstanceConfig[] snapshot =
        [
            .. configured.Select(instance => new CommonPluginInstanceConfig
                { PluginId = instance.PluginId, InstanceId = instance.InstanceId, Enabled = instance.Enabled })
        ];
        PluginInstanceIdentity[] enabled =
        [
            .. snapshot.Where(instance => instance.Enabled)
                .Select(instance => new PluginInstanceIdentity(instance.PluginId, instance.InstanceId))
        ];
        if (enabled.Any(identity => !PluginConfigurationRules.ValidKey(identity.PluginId) ||
                                    !PluginConfigurationRules.ValidKey(identity.InstanceId))
            || enabled.Distinct().Count() != enabled.Length)
        {
            throw new InvalidDataException("Configured plugin instance identities are invalid or duplicated.");
        }

        // Against the packages and adapters the last reconcile read; the core pass decides again from a
        // fresh read, and only an instance neither wants is cut short here.
        var desired = CommonPluginEnablement.Desired(snapshot, _catalog.Common, Volatile.Read(ref _lastAdapters));
        Entry[] retiring;
        lock (_stateGate)
        {
            retiring = [.. _entries.Values.Where(entry => !desired.Contains(entry.Identity))];
        }

        foreach (var entry in retiring)
        {
            entry.Retiring = true;
            Cancel(entry.Cancellation);
        }

        return Task.Run(() => ReconcileCoreAsync(snapshot, revision, cancellationToken), CancellationToken.None);
    }

    private async Task ReconcileCoreAsync(CommonPluginInstanceConfig[] configured, long revision,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopping || revision != Volatile.Read(ref _requestedRevision))
            {
                return;
            }

            _configured = configured;

            _catalog = await Task.Run(() => PluginPackageCatalog.Discover(_installedRoot), cancellationToken)
                .ConfigureAwait(false);
            if (_stopping || revision != Volatile.Read(ref _requestedRevision))
            {
                return;
            }

            // Adapters matter only to a graphics package, so a machine without one never enumerates them.
            IReadOnlyList<DisplayAdapterIdentity> adapters =
                _catalog.Common.Any(package => package.Manifest.Category == PluginCategories.Gpu) ? _adapters() : [];
            Volatile.Write(ref _lastAdapters, adapters);
            var desired = CommonPluginEnablement.Desired(configured, _catalog.Common, adapters);
            foreach (var automatic in desired.Where(identity =>
                         configured.All(instance => instance.PluginId != identity.PluginId)))
            {
                Log.Change($"plugin-auto-enable/{automatic.PluginId}",
                    $"Plugins: {automatic.PluginId} runs by default; this machine has a display adapter it serves.");
            }

            Entry[] previous;
            lock (_stateGate)
            {
                previous = [.. _entries.Values.Reverse()];
            }

            foreach (var entry in previous.Where(entry => entry.Retiring || !desired.Contains(entry.Identity)))
            {
                try
                {
                    await StopEntryAsync(entry, Deadline.After(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    entry.Error = ex.Message;
                }
            }

            foreach (var entry in previous.Where(entry =>
                         !entry.Retiring && desired.Contains(entry.Identity) && entry.StartWork.IsCompleted))
            {
                if (_stopping || revision != Volatile.Read(ref _requestedRevision))
                {
                    return;
                }

                if (entry.Registration is not
                    { IsStopping: false, Quarantined: false, Settings: not null } registration)
                {
                    continue;
                }

                try
                {
                    var result = await registration
                        .RefreshConfigurationAsync(Deadline.After(TimeSpan.FromSeconds(5)), cancellationToken)
                        .ConfigureAwait(false);
                    entry.Error = result.Outcome == PluginConfigurationOutcome.Applied
                        ? null
                        : result.Detail ?? "Configuration is unconfirmed.";
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    entry.Error = ex.Message;
                }
            }

            var enabledPackages = _catalog.Common
                .Where(package => desired.Any(identity => identity.PluginId == package.Manifest.Id)).ToArray();
            var plan = CommonPluginDependencyPlan.Create([.. enabledPackages.Select(package => package.Manifest)]);
            List<string> errors =
            [
                .. _catalog.Errors,
                .. plan.Rejected.Select(pair => pair.Key + ": " + pair.Value),
                .. desired
                    .Where(identity => _catalog.Common.All(package => package.Manifest.Id != identity.PluginId))
                    .Select(identity => identity.PluginId + ": installed package is unavailable.")
            ];
            foreach (var manifest in plan.Ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var package = enabledPackages.Single(candidate => candidate.Manifest.Id == manifest.Id);
                foreach (var identity in desired.Where(identity => identity.PluginId == manifest.Id))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_stopping || revision != Volatile.Read(ref _requestedRevision))
                    {
                        return;
                    }

                    Entry entry;
                    lock (_stateGate)
                    {
                        if (_entries.ContainsKey(identity))
                        {
                            continue;
                        }

                        if (manifest.Dependencies.Any(dependency => !_entries.Values.Any(provider =>
                                provider.Identity.PluginId == dependency.Id
                                && provider.Error is null &&
                                provider.Registration?.Health.Health == PluginHealth.Ready)))
                        {
                            errors.Add(identity.PluginId + ": dependency is not ready.");
                            continue;
                        }

                        entry = new Entry(identity, package,
                            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
                        _entries.Add(identity, entry);
                    }

                    entry.StartWork = StartEntryAsync(entry);
                    try
                    {
                        await entry.StartWork.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                    {
                        entry.Error = "Plugin startup did not finish: " + ex.Message;
                        Cancel(entry.Cancellation);
                    }
                }
            }

            _catalog = _catalog with { Errors = errors.AsReadOnly() };
        }
        finally
        {
            _gate.Release();
            NotifyChanged();
        }
    }

    private async Task StartEntryAsync(Entry entry)
    {
        try
        {
            entry.Loaded = await _load(entry.Package, entry.Cancellation.Token).ConfigureAwait(false);
            entry.Cancellation.Token.ThrowIfCancellationRequested();
            // Hash host instance identities so configuration cannot introduce filesystem aliases or traversal.
            var instanceDirectory =
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Identity.InstanceId)));
            var state = PluginPackageLoader.ConstrainPackagePath(_stateRoot,
                Path.Combine(entry.Identity.PluginId, instanceDirectory));
            Directory.CreateDirectory(state);
            PluginCapabilityChannel? channel = null;
            if (entry.Package.Manifest.Category == PluginCategories.Gpu)
            {
                if (entry.Loaded is not ICapabilityPlugin capabilityPlugin
                    || entry.Loaded is CommonPluginPackage { PublishesCapabilities: false })
                {
                    throw new InvalidDataException("A graphics package must implement ICapabilityPlugin.");
                }

                channel = entry.Channel = (_capabilityChannels
                                           ?? throw new InvalidOperationException(
                                               "Graphics packages cannot run in this session."))
                    .Open(entry.Identity, entry.Package.Manifest, capabilityPlugin);
            }

            entry.Registration = _host.Admit(entry.Loaded, entry.Identity, entry.Package.Manifest.Category,
                PluginCategoryPolicy.Multiple, false, 1, state, channel);
            await entry.Registration.StartAsync(Deadline.After(TimeSpan.FromSeconds(15)), entry.Cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            entry.Error = ex.Message;
            entry.LoadCleanupUnconfirmed = entry.Loaded is null && ex is AggregateException;
            CloseChannel(entry);
        }
        finally
        {
            NotifyChanged();
        }
    }

    internal async Task StopAsync(Deadline deadline)
    {
        _stopping = true;
        Entry[] entries;
        lock (_stateGate)
        {
            entries = [.. _entries.Values.Reverse()];
        }

        foreach (var entry in entries)
        {
            Cancel(entry.Cancellation);
        }

        using var budget = new CancellationTokenSource(Remaining(deadline));
        await _gate.WaitAsync(budget.Token).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                entries = [.. _entries.Values.Reverse()];
            }

            List<Exception> failures = [];
            foreach (var entry in entries)
            {
                try
                {
                    await StopEntryAsync(entry, deadline).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    failures.Add(ex);
                }
            }

            if (failures.Count != 0)
            {
                throw new AggregateException("Common plugin cleanup was not confirmed.", failures);
            }
        }
        finally
        {
            _gate.Release();
            NotifyChanged();
        }
    }

    internal async Task PowerTransitionAsync(bool suspend, CancellationToken cancellationToken)
    {
        var deadline = Deadline.After(TimeSpan.FromSeconds(5));
        using var budget = deadline.CreateCancellationSource(cancellationToken);
        var restarted = false;
        await _gate.WaitAsync(budget.Token).ConfigureAwait(false);
        try
        {
            if (_stopping)
            {
                return;
            }

            Entry[] entries;
            lock (_stateGate)
            {
                entries = [.. _entries.Values];
            }

            var token = budget.Token;
            if (!suspend)
            {
                // A suspend cut off by the freeze quarantines its plugin, and a quarantined plugin is
                // never resumed. The sleep reset whatever it drove, so it is stopped here and started
                // again by the reconcile below, as a WSGM restart would.
                foreach (var entry in entries.Where(entry => entry.StartWork.IsCompleted
                                                             && entry.Registration is
                                                                 { IsStopping: false, Quarantined: true }))
                {
                    try
                    {
                        await StopEntryAsync(entry, Deadline.After(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                        restarted = true;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        entry.Error = ex.Message;
                    }
                }
            }

            await Task.WhenAll(entries.Select(async entry =>
            {
                if (!entry.StartWork.IsCompleted || entry.Suspended == suspend
                                                 || entry.Registration is not
                                                     { IsStopping: false, Quarantined: false } registration)
                {
                    return;
                }

                try
                {
                    if (suspend)
                    {
                        await registration.SuspendAsync(deadline, token).ConfigureAwait(false);
                    }
                    else
                    {
                        await registration.ResumeAsync(checked(registration.Context.Generation + 1), deadline, token)
                            .ConfigureAwait(false);
                    }

                    entry.Suspended = suspend;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    entry.Error = "Power transition failed: " + ex.Message;
                }
            })).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            NotifyChanged();
        }

        if (restarted)
        {
            await ReconcileCoreAsync(_configured, Interlocked.Increment(ref _requestedRevision), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Ends an entry's capability channel, once.</summary>
    private void CloseChannel(Entry entry)
    {
        if (Interlocked.Exchange(ref entry.Channel, null) is { } channel)
        {
            _capabilityChannels?.Close(channel);
        }
    }

    private async Task StopEntryAsync(Entry entry, Deadline deadline)
    {
        Cancel(entry.Cancellation);
        try
        {
            await entry.StartWork.WaitAsync(Remaining(deadline)).ConfigureAwait(false);
            if (entry.LoadCleanupUnconfirmed)
            {
                throw new InvalidOperationException("Package construction cleanup was not confirmed.");
            }

            if (entry.Registration is { } registration)
            {
                var released = await registration.StopAsync(deadline, CancellationToken.None).ConfigureAwait(false);
                await registration.DisposeAsync().ConfigureAwait(false);
                if (!released)
                {
                    throw new InvalidOperationException("Plugin release was not confirmed.");
                }
            }
            else if (entry.Loaded is { } loaded)
            {
                // The package never entered Start, so only construction cleanup is required.
                entry.Disposal ??= Task.Run(async () => await loaded.DisposeAsync().ConfigureAwait(false));
                await entry.Disposal.WaitAsync(Remaining(deadline)).ConfigureAwait(false);
            }

            CloseChannel(entry);
            lock (_stateGate)
            {
                _entries.Remove(entry.Identity);
            }

            entry.Cancellation.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            entry.Error = "Cleanup unconfirmed: " + ex.Message;
            throw;
        }
        finally
        {
            NotifyChanged();
        }
    }

    private static TimeSpan Remaining(Deadline deadline)
    {
        var remaining = deadline.Remaining;
        return remaining > TimeSpan.Zero ? remaining : throw new TimeoutException("Plugin cleanup deadline expired.");
    }

    private static void Cancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.CancelAsync().ObserveFaults();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }

    private sealed class Entry(
        PluginInstanceIdentity identity,
        CommonInstalledPlugin package,
        CancellationTokenSource cancellation)
    {
        internal PluginCapabilityChannel? Channel;
        internal volatile string? Error;
        internal bool LoadCleanupUnconfirmed;
        internal volatile IPlugin? Loaded;
        internal volatile PluginRegistration? Registration;
        internal volatile bool Retiring;
        internal bool Suspended;
        internal PluginInstanceIdentity Identity { get; } = identity;
        internal CommonInstalledPlugin Package { get; } = package;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal Task StartWork { get; set; } = Task.CompletedTask;
        internal Task? Disposal { get; set; }
    }
}
