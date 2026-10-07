using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Install;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>Installed instance and its current admission outcome, including failures retained for presentation.</summary>
/// <param name="Identity">Configured or implicitly enabled package/instance key.</param>
/// <param name="Manifest">Selected validated package metadata.</param>
/// <param name="Registration">Live registration, or null when no runtime has been admitted.</param>
/// <param name="Error">Load or runtime failure detail, or null when none is retained.</param>
/// <param name="SteamCefEnabled">Whether current user policy admits this instance's unrestricted frontend.</param>
internal sealed record CommonPluginInstanceView(
    PluginInstanceIdentity Identity,
    PluginManifest Manifest,
    PluginRegistration? Registration,
    string? Error,
    bool SteamCefEnabled = false);

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
    private readonly Func<CommonInstalledPlugin, CancellationToken, Task<LoadedPluginPackage<IPlugin>>> _load;
    private readonly Lock _stateGate = new();
    private readonly string _stateRoot;
    private readonly ConfigStore? _store;
    private volatile PluginPackageCatalog _catalog = PluginPackageCatalog.Empty;
    private bool _cefWarningAccepted;

    /// <summary>The configuration the last reconcile was asked for, so a resume can restart an instance.</summary>
    private CommonPluginInstanceConfig[] _configured = [];

    /// <summary>The adapters the last reconcile read, for deciding which instances are retiring.</summary>
    private IReadOnlyList<DisplayAdapterIdentity> _lastAdapters = [];

    private long _requestedRevision;
    private volatile bool _stopping;

    /// <summary>Creates the resident package reconciler without loading or starting a package.</summary>
    /// <param name="host">The plugin host instances are admitted to.</param>
    /// <param name="installedRoot">The Plugins folder.</param>
    /// <param name="stateRoot">The root of every instance's state directory.</param>
    /// <param name="load">Loads a package's plugin; tests replace it.</param>
    /// <param name="capabilityChannels">
    ///     Opens a graphics instance's capability channel. Without one, graphics packages cannot start.
    /// </param>
    /// <param name="adapters">Reads the present display adapters; defaults to a fresh read at each reconcile.</param>
    /// <param name="store">Persists frontend failures so packages never reload automatically after an error.</param>
    internal CommonPluginManager(PluginHost host, string installedRoot, string stateRoot,
        Func<CommonInstalledPlugin, CancellationToken, Task<LoadedPluginPackage<IPlugin>>>? load = null,
        ICapabilityChannelRegistry? capabilityChannels = null,
        Func<IReadOnlyList<DisplayAdapterIdentity>>? adapters = null, ConfigStore? store = null)
    {
        _host = host;
        _store = store;
        _capabilityChannels = capabilityChannels;
        _adapters = adapters ?? (static () => CommonPluginEnablement.ReadAdapters());
        _installedRoot = Path.GetFullPath(installedRoot);
        _stateRoot = Path.GetFullPath(stateRoot);
        _load = load ?? (static (package, token) =>
            PluginLoader.LoadCommonAsync(package.PackagePath, package.Manifest, token));
    }

    /// <summary>The installed packages as of the last reconcile, read without touching the disk.</summary>
    internal PluginPackageCatalog Catalog => _catalog;

    /// <summary>Raised after the admitted-plugin projection may have changed.</summary>
    /// <remarks>
    ///     Raised on the thread that finished the operation, usually a pool thread. Subscribers marshal
    ///     themselves.
    /// </remarks>
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
                        entry.Error, _cefWarningAccepted && _configured.Any(config =>
                            config.PluginId == entry.Identity.PluginId
                            && config.InstanceId == entry.Identity.InstanceId && config.SteamCefEnabled
                            && string.IsNullOrEmpty(config.SteamCefFailure))))
            ];
        }
    }

    internal Task ReconcileAsync(IReadOnlyList<CommonPluginInstanceConfig> configured,
        CancellationToken cancellationToken, bool? cefWarningAccepted = null)
    {
        if (_stopping)
        {
            return Task.CompletedTask;
        }

        var revision = Interlocked.Increment(ref _requestedRevision);
        if (cefWarningAccepted.HasValue)
        {
            _cefWarningAccepted = cefWarningAccepted.Value;
        }

        // Detached, so a caller editing its configuration objects later cannot change this reconcile.
        CommonPluginInstanceConfig[] snapshot =
        [
            .. configured.Select(instance => new CommonPluginInstanceConfig
            {
                PluginId = instance.PluginId, InstanceId = instance.InstanceId,
                Enabled = instance.Enabled && string.IsNullOrEmpty(instance.SteamCefFailure),
                SteamCefEnabled = instance.SteamCefEnabled, SteamCefFailure = instance.SteamCefFailure
            })
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
            var adapters =
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

            foreach (var entry in previous.Where(entry => entry.Retiring || !desired.Contains(entry.Identity)
                                                                         || _catalog.Common.All(package =>
                                                                             package.Manifest.Id !=
                                                                             entry.Identity.PluginId
                                                                             || package.Sha256 !=
                                                                             entry.Package.Sha256)))
            {
                entry.Retiring = true;
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
                        if (_stopping)
                        {
                            return;
                        }

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

                    // Active time, so a sleep during startup does not spend its budget.
                    var startDeadline = Deadline.After(TimeSpan.FromSeconds(15));
                    entry.StartWork = StartEntryAsync(entry, startDeadline);
                    using var bounded = startDeadline.CreateCancellationSource(cancellationToken);
                    try
                    {
                        await entry.StartWork.WaitAsync(bounded.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException ex)
                    {
                        entry.Error = "Plugin startup did not finish: " + (cancellationToken.IsCancellationRequested
                            ? ex.Message
                            : new TimeoutException().Message);
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

    private async Task StartEntryAsync(Entry entry, Deadline deadline)
    {
        try
        {
            var loaded = await _load(entry.Package, entry.Cancellation.Token).ConfigureAwait(false);
            entry.Loaded = loaded;
            entry.Cancellation.Token.ThrowIfCancellationRequested();
            var plugin = loaded.Plugin;
            // Hash host instance identities so configuration cannot introduce filesystem aliases or traversal.
            var instanceDirectory =
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Identity.InstanceId)));
            var state = ConstrainStatePath(Path.Combine(entry.Identity.PluginId, instanceDirectory));
            Directory.CreateDirectory(state);
            PluginCapabilityChannel? channel = null;
            if (entry.Package.Manifest.Category == PluginCategories.Gpu)
            {
                if (plugin is not ICapabilityPlugin capabilityPlugin)
                {
                    throw new InvalidDataException("A graphics package must implement ICapabilityPlugin.");
                }

                channel = entry.Channel = (_capabilityChannels
                                           ?? throw new InvalidOperationException(
                                               "Graphics packages cannot run in this session."))
                    .Open(entry.Identity, entry.Package.Manifest, capabilityPlugin);
            }

            entry.Registration = _host.Admit(plugin, entry.Identity, entry.Package.Manifest.Category,
                PluginCategoryPolicy.Multiple, false, 1, state, channel);
            await entry.Registration.StartAsync(deadline, entry.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            entry.Error = ex.Message;
            entry.LoadCleanupUnconfirmed = ex is PluginLoadException { CleanupConfirmed: false };
            CloseChannel(entry);
        }
        finally
        {
            NotifyChanged();
        }
    }

    internal (string Script, string? Style, IPluginSteamFrontend? Backend) ReadFrontend(
        PluginRegistration owner, PluginFrontendModule module)
    {
        lock (_stateGate)
        {
            var entry = _entries.Values.Single(candidate => ReferenceEquals(candidate.Registration, owner));
            var package = entry.Loaded?.Package ?? throw new InvalidDataException("Frontend package is unavailable.");
            var utf8 = new UTF8Encoding(false, true);
            if (!package.TryRead(module.Script, 16 * 1024 * 1024, out var script))
            {
                throw new InvalidDataException("Frontend script is missing or exceeds the package limit.");
            }

            string? style = null;
            if (module.Style is { } path)
            {
                if (!package.TryRead(path, 16 * 1024 * 1024, out var bytes))
                {
                    throw new InvalidDataException("Frontend stylesheet is missing or exceeds the package limit.");
                }

                style = utf8.GetString(bytes);
            }

            return (utf8.GetString(script), style, entry.Loaded!.Plugin as IPluginSteamFrontend);
        }
    }

    internal void FailFrontend(PluginRegistration owner, string module, string reason)
    {
        if (owner.Quarantined || owner.IsStopping)
        {
            return;
        }

        var detail = $"Steam CEF module {module}: {reason}";
        owner.QuarantineFrontend(detail);
        _ = Task.Run(async () =>
        {
            try
            {
                _store?.Update(config =>
                {
                    var instance = config.PluginInstances.FirstOrDefault(candidate =>
                        candidate.PluginId == owner.Identity.PluginId &&
                        candidate.InstanceId == owner.Identity.InstanceId);
                    if (instance is null)
                    {
                        instance = new CommonPluginInstanceConfig
                        {
                            PluginId = owner.Identity.PluginId,
                            InstanceId = owner.Identity.InstanceId
                        };
                        config.PluginInstances.Add(instance);
                    }

                    instance.Enabled = false;
                    instance.SteamCefFailure = detail;
                    return true;
                });
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    Entry? entry;
                    lock (_stateGate)
                    {
                        entry = _entries.Values.FirstOrDefault(candidate =>
                            ReferenceEquals(candidate.Registration, owner));
                        foreach (var instance in _configured.Where(candidate =>
                                     candidate.PluginId == owner.Identity.PluginId
                                     && candidate.InstanceId == owner.Identity.InstanceId))
                        {
                            instance.Enabled = false;
                            instance.SteamCefFailure = detail;
                        }
                    }

                    if (entry is not null)
                    {
                        entry.Retiring = true;
                        await StopEntryAsync(entry, Deadline.After(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _gate.Release();
                    NotifyChanged();
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Log.Warn($"Plugins: disabling {owner.Identity.PluginId} after {detail} failed: {error.Message}");
            }
        });
    }

    internal async Task<JsonElement?> InvokeFrontendAsync(PluginRegistration owner, IPluginSteamFrontend backend,
        string module, string method, JsonElement payload, CancellationToken cancellationToken)
    {
        CancellationTokenSource admitted;
        Entry entry;
        Task<JsonElement?> work;
        lock (_stateGate)
        {
            entry = _entries.Values.FirstOrDefault(candidate => ReferenceEquals(candidate.Registration, owner))!;
            if (entry is null || entry.Retiring || owner.IsStopping || owner.Quarantined)
            {
                throw new InvalidOperationException("The plugin frontend is no longer active.");
            }

            admitted = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, entry.Cancellation.Token);
            work = Task.Run(async () => await backend.InvokeFrontendAsync(module, method, payload, admitted.Token)
                .ConfigureAwait(false), CancellationToken.None);
            entry.FrontendWork.Add(work);
        }

        using (admitted)
        {
            try
            {
                return await work.ConfigureAwait(false);
            }
            finally
            {
                lock (_stateGate)
                {
                    entry.FrontendWork.Remove(work);
                }
            }
        }
    }

    /// <summary>Refuses new starts and power work, and cancels admitted startup without waiting.</summary>
    internal void CloseAdmission()
    {
        Entry[] entries;
        lock (_stateGate)
        {
            _stopping = true;
            entries = [.. _entries.Values.Reverse()];
        }

        List<Exception> failures = [];
        foreach (var entry in entries)
        {
            try
            {
                entry.Registration?.CloseAdmission();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add(ex);
            }

            Cancel(entry.Cancellation);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Common plugin admission closure failed.", failures);
        }
    }

    internal async Task StopAsync(Deadline deadline)
    {
        List<Exception> failures = [];
        try
        {
            CloseAdmission();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failures.Add(ex);
        }

        Entry[] entries;

        using var budget = new CancellationTokenSource(Remaining(deadline));
        await _gate.WaitAsync(budget.Token).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                entries = [.. _entries.Values.Reverse()];
            }

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
        if (_stopping)
        {
            return;
        }

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
                if (_stopping || !entry.StartWork.IsCompleted || entry.Suspended == suspend
                    || entry.Registration is not
                        { IsStopping: false, Quarantined: false } registration)
                {
                    return;
                }

                try
                {
                    using var admitted =
                        CancellationTokenSource.CreateLinkedTokenSource(token, entry.Cancellation.Token);
                    if (suspend)
                    {
                        await registration.SuspendAsync(deadline, admitted.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await registration.ResumeAsync(checked(registration.Context.Generation + 1), deadline,
                                admitted.Token)
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
            await WaitWithinAsync(entry.StartWork, deadline).ConfigureAwait(false);
            Task[] frontendWork;
            lock (_stateGate)
            {
                frontendWork = [.. entry.FrontendWork];
            }

            await WaitWithinAsync(Task.WhenAll(frontendWork.Select(work => work.ContinueWith(_ => { },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default))),
                deadline).ConfigureAwait(false);
            if (entry.LoadCleanupUnconfirmed)
            {
                throw new InvalidOperationException("Package construction cleanup was not confirmed.");
            }

            if (entry.Registration is { } registration)
            {
                var released = await registration.StopAsync(deadline, CancellationToken.None).ConfigureAwait(false);
                await registration.DisposeAsync().ConfigureAwait(false);
                // The plugin's disposal completed, so its code can go. An unconfirmed release still keeps
                // the instance reserved below.
                entry.Loaded?.Unload();
                if (!released)
                {
                    throw new InvalidOperationException("Plugin release was not confirmed.");
                }
            }
            else if (entry.Loaded is { } loaded)
            {
                // The package never entered Start, so only construction cleanup is required.
                entry.Disposal ??= Task.Run(async () => await loaded.DisposeAsync().ConfigureAwait(false));
                await WaitWithinAsync(entry.Disposal, deadline).ConfigureAwait(false);
            }

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
            // The plugin was told to stop whatever it answered, so its capabilities end here. An unconfirmed
            // stop is still reported, but must not leave its publisher registered: that would refuse the same
            // instance when it is enabled again, until WSGM restarts.
            CloseChannel(entry);
            NotifyChanged();
        }
    }

    /// <summary>Waits for work within an active-time deadline.</summary>
    /// <exception cref="TimeoutException">The deadline passed while the work was still running.</exception>
    private static async Task WaitWithinAsync(Task work, Deadline deadline)
    {
        using var bounded = deadline.CreateCancellationSource();
        try
        {
            await work.WaitAsync(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!work.IsCompleted)
        {
            throw new TimeoutException("Plugin cleanup deadline expired.");
        }
    }

    /// <summary>Resolves a path below the state root, refusing an escape.</summary>
    private string ConstrainStatePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Package paths must be non-empty and relative.");
        }

        var rootPrefix = _stateRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(_stateRoot, relativePath));
        return candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            ? candidate
            : throw new InvalidDataException("A package path escaped the package directory.");
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

    /// <summary>One instance.</summary>
    /// <remarks>
    ///     Its fields are read and written while <see cref="_gate" /> is held. A start that outlives its wait
    ///     writes only the volatile fields and <see cref="Channel" />, and every reader of the others awaits
    ///     <see cref="StartWork" /> first.
    /// </remarks>
    private sealed class Entry(
        PluginInstanceIdentity identity,
        CommonInstalledPlugin package,
        CancellationTokenSource cancellation)
    {
        internal PluginCapabilityChannel? Channel;
        internal volatile string? Error;
        internal bool LoadCleanupUnconfirmed;
        internal volatile LoadedPluginPackage<IPlugin>? Loaded;
        internal volatile PluginRegistration? Registration;
        internal volatile bool Retiring;
        internal bool Suspended;
        internal PluginInstanceIdentity Identity { get; } = identity;
        internal CommonInstalledPlugin Package { get; } = package;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal Task StartWork { get; set; } = Task.CompletedTask;
        internal Task? Disposal { get; set; }
        internal HashSet<Task> FrontendWork { get; } = [];
    }
}
