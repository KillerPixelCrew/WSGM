using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

internal sealed record CommonPluginInstanceView(PluginInstanceIdentity Identity, PluginManifest Manifest,
    PluginRegistration? Registration, string? Error);

/// <summary>Owns explicitly enabled non-device instances independently of the Device master switch.</summary>
internal sealed class CommonPluginManager
{
    private readonly PluginHost _host;
    private readonly string _installedRoot;
    private readonly string _stateRoot;
    private readonly Action<Action> _postToUi;
    private readonly Func<CommonInstalledPlugin, CancellationToken, Task<IPlugin>> _load;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly Dictionary<PluginInstanceIdentity, Entry> _entries = [];
    private volatile bool _stopping;
    private long _requestedRevision;
    private volatile CommonPluginCatalog _catalog = new([], []);

    internal CommonPluginManager(PluginHost host, string installedRoot, string stateRoot, Action<Action> postToUi,
        Func<CommonInstalledPlugin, CancellationToken, Task<IPlugin>>? load = null)
    {
        _host = host;
        _installedRoot = Path.GetFullPath(installedRoot);
        _stateRoot = Path.GetFullPath(stateRoot);
        _postToUi = postToUi;
        _load = load ?? (async (package, token) => await CommonPluginPackage.LoadAsync(package.PackageRoot, package.Manifest, token).ConfigureAwait(false));
    }

    internal event Action? Changed;
    internal CommonPluginCatalog Catalog => _catalog;
    internal CommonPluginInstanceView[] Snapshot()
    {
        lock (_stateGate)
        { return _entries.Values.Select(entry => new CommonPluginInstanceView(entry.Identity, entry.Package.Manifest, entry.Registration, entry.Error)).ToArray(); }
    }

    internal Task ReconcileAsync(IReadOnlyList<CommonPluginInstanceConfig> configured, CancellationToken cancellationToken)
    {
        long revision = Interlocked.Increment(ref _requestedRevision);
        if (configured.Count > 128) { throw new InvalidDataException("Too many configured plugin instances."); }
        var desired = configured.Where(instance => instance is not null && instance.Enabled)
            .Select(instance => new PluginInstanceIdentity(instance.PluginId, instance.InstanceId)).ToArray();
        if (desired.Any(identity => !PluginConfigurationRules.ValidKey(identity.PluginId) || !PluginConfigurationRules.ValidKey(identity.InstanceId))
            || desired.Distinct().Count() != desired.Length)
        { throw new InvalidDataException("Configured plugin instance identities are invalid or duplicated."); }
        Entry[] retiring;
        lock (_stateGate) { retiring = _entries.Values.Where(entry => !desired.Contains(entry.Identity)).ToArray(); }
        foreach (var entry in retiring)
        {
            entry.Retiring = true;
            Cancel(entry.Cancellation);
        }
        return Task.Run(() => ReconcileCoreAsync(desired, revision, cancellationToken), CancellationToken.None);
    }

    private async Task ReconcileCoreAsync(PluginInstanceIdentity[] desired, long revision, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopping || revision != Volatile.Read(ref _requestedRevision)) { return; }
            _catalog = await Task.Run(() => CommonPluginCatalog.Discover(_installedRoot), cancellationToken).ConfigureAwait(false);
            if (_stopping || revision != Volatile.Read(ref _requestedRevision)) { return; }
            Entry[] previous;
            lock (_stateGate) { previous = _entries.Values.Reverse().ToArray(); }
            foreach (var entry in previous.Where(entry => entry.Retiring || !desired.Contains(entry.Identity)))
            {
                try { await StopEntryAsync(entry, DateTimeOffset.UtcNow.AddSeconds(5)).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { entry.Error = ex.Message; }
            }

            foreach (var entry in previous.Where(entry => !entry.Retiring && desired.Contains(entry.Identity) && entry.StartWork.IsCompleted))
            {
                if (_stopping || revision != Volatile.Read(ref _requestedRevision)) { return; }
                if (entry.Registration is not { IsStopping: false, Quarantined: false, Settings: not null } registration) { continue; }
                try
                {
                    var result = await registration.RefreshConfigurationAsync(DateTimeOffset.UtcNow.AddSeconds(5), cancellationToken).ConfigureAwait(false);
                    entry.Error = result.Outcome == PluginConfigurationOutcome.Applied ? null : result.Detail ?? "Configuration is unconfirmed.";
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { entry.Error = ex.Message; }
            }

            var enabledPackages = _catalog.Packages.Where(package => desired.Any(identity => identity.PluginId == package.Manifest.Id)).ToArray();
            var plan = CommonPluginDependencyPlan.Create(enabledPackages.Select(package => package.Manifest).ToArray());
            List<string> errors = [.. _catalog.Errors, .. plan.Rejected.Select(pair => pair.Key + ": " + pair.Value)];
            foreach (var identity in desired.Where(identity => !_catalog.Packages.Any(package => package.Manifest.Id == identity.PluginId)))
            { errors.Add(identity.PluginId + ": installed package is unavailable."); }
            foreach (var manifest in plan.Ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var package = enabledPackages.Single(candidate => candidate.Manifest.Id == manifest.Id);
                foreach (var identity in desired.Where(identity => identity.PluginId == manifest.Id))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_stopping || revision != Volatile.Read(ref _requestedRevision)) { return; }
                    Entry entry;
                    lock (_stateGate)
                    {
                        if (_entries.ContainsKey(identity)) { continue; }
                        if (manifest.Dependencies.Any(dependency => !_entries.Values.Any(provider => provider.Identity.PluginId == dependency.Id
                            && provider.Error is null && provider.Registration?.Health.Health == PluginHealth.Ready)))
                        { errors.Add(identity.PluginId + ": dependency is not ready."); continue; }
                        entry = new(identity, package, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
                        _entries.Add(identity, entry);
                    }
                    entry.StartWork = StartEntryAsync(entry);
                    try { await entry.StartWork.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                    {
                        entry.Error = "Plugin startup did not finish: " + ex.Message;
                        Cancel(entry.Cancellation);
                    }
                }
            }
            _catalog = new(_catalog.Packages, errors.AsReadOnly());
        }
        finally { _gate.Release(); Notify(); }
    }

    private async Task StartEntryAsync(Entry entry)
    {
        try
        {
            entry.Loaded = await _load(entry.Package, entry.Cancellation.Token).ConfigureAwait(false);
            entry.Cancellation.Token.ThrowIfCancellationRequested();
            // Hash host instance identities so configuration cannot introduce filesystem aliases or traversal.
            string instanceDirectory = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Identity.InstanceId)));
            string state = PluginPackageLoader.ConstrainPackagePath(_stateRoot, Path.Combine(entry.Identity.PluginId, instanceDirectory));
            Directory.CreateDirectory(state);
            entry.Registration = _host.Admit(entry.Loaded, entry.Identity, entry.Package.Manifest.Category,
                PluginCategoryPolicy.Multiple, false, 1, state);
            await entry.Registration.StartAsync(DateTimeOffset.UtcNow.AddSeconds(15), entry.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            entry.Error = ex.Message;
            entry.LoadCleanupUnconfirmed = entry.Loaded is null && ex is AggregateException;
        }
        finally { Notify(); }
    }

    internal async Task StopAsync(DateTimeOffset deadline)
    {
        _stopping = true;
        Entry[] entries;
        lock (_stateGate) { entries = _entries.Values.Reverse().ToArray(); }
        foreach (var entry in entries) { Cancel(entry.Cancellation); }
        using var budget = new CancellationTokenSource(Remaining(deadline));
        await _gate.WaitAsync(budget.Token).ConfigureAwait(false);
        try
        {
            lock (_stateGate) { entries = _entries.Values.Reverse().ToArray(); }
            List<Exception> failures = [];
            foreach (var entry in entries)
            {
                try { await StopEntryAsync(entry, deadline).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { failures.Add(ex); }
            }
            if (failures.Count != 0) { throw new AggregateException("Common plugin cleanup was not confirmed.", failures); }
        }
        finally { _gate.Release(); Notify(); }
    }

    internal async Task PowerTransitionAsync(bool suspend, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        await _gate.WaitAsync(budget.Token).ConfigureAwait(false);
        try
        {
            if (_stopping) { return; }
            Entry[] entries;
            lock (_stateGate) { entries = _entries.Values.ToArray(); }
            await Task.WhenAll(entries.Select(async entry =>
            {
                if (!entry.StartWork.IsCompleted || entry.Suspended == suspend
                    || entry.Registration is not { IsStopping: false, Quarantined: false } registration) { return; }
                try
                {
                    if (suspend) { await registration.SuspendAsync(deadline, budget.Token).ConfigureAwait(false); }
                    else { await registration.ResumeAsync(checked(registration.Context.Generation + 1), deadline, budget.Token).ConfigureAwait(false); }
                    entry.Suspended = suspend;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { entry.Error = "Power transition failed: " + ex.Message; }
            })).ConfigureAwait(false);
        }
        finally { _gate.Release(); Notify(); }
    }

    private async Task StopEntryAsync(Entry entry, DateTimeOffset deadline)
    {
        Cancel(entry.Cancellation);
        try
        {
            await entry.StartWork.WaitAsync(Remaining(deadline)).ConfigureAwait(false);
            if (entry.LoadCleanupUnconfirmed) { throw new InvalidOperationException("Package construction cleanup was not confirmed."); }
            if (entry.Registration is { } registration)
            {
                bool released = await registration.StopAsync(deadline, CancellationToken.None).ConfigureAwait(false);
                await registration.DisposeAsync().ConfigureAwait(false);
                if (!released) { throw new InvalidOperationException("Plugin release was not confirmed."); }
            }
            else if (entry.Loaded is { } loaded)
            {
                // The package never entered Start, so only construction cleanup is required.
                entry.Disposal ??= Task.Run(async () => await loaded.DisposeAsync().ConfigureAwait(false));
                await entry.Disposal.WaitAsync(Remaining(deadline)).ConfigureAwait(false);
            }
            lock (_stateGate) { _entries.Remove(entry.Identity); }
            entry.Cancellation.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { entry.Error = "Cleanup unconfirmed: " + ex.Message; throw; }
    }

    private void Notify() => _postToUi(() => Changed?.Invoke());
    private static TimeSpan Remaining(DateTimeOffset deadline)
    {
        var remaining = deadline - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : throw new TimeoutException("Plugin cleanup deadline expired.");
    }
    private static void Cancel(CancellationTokenSource cancellation)
    {
        try
        {
            _ = cancellation.CancelAsync().ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (ObjectDisposedException) { }
    }

    private sealed class Entry(PluginInstanceIdentity identity, CommonInstalledPlugin package, CancellationTokenSource cancellation)
    {
        internal PluginInstanceIdentity Identity { get; } = identity;
        internal CommonInstalledPlugin Package { get; } = package;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal Task StartWork { get; set; } = Task.CompletedTask;
        internal Task? Disposal { get; set; }
        internal volatile IPlugin? Loaded;
        internal volatile PluginRegistration? Registration;
        internal volatile string? Error;
        internal bool LoadCleanupUnconfirmed;
        internal bool Suspended;
        internal volatile bool Retiring;
    }
}
