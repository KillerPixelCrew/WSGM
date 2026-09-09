using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>A common package sharing the established Device loader's dependency and WinRT identity rules.</summary>
internal sealed class CommonPluginPackage : IPlugin, IConfigurablePlugin, IPluginActions, IPluginUi
{
    private readonly PluginPackageLoader.PluginLoadContext _context;
    private readonly IPlugin _plugin;
    private readonly PluginManifest _manifest;
    private bool _disposed;

    private CommonPluginPackage(PluginPackageLoader.PluginLoadContext context, IPlugin plugin, PluginManifest manifest)
    { _context = context; _plugin = plugin; _manifest = manifest; }

    internal PluginManifest Manifest => Snapshot(_manifest);
    public string Id => _plugin.Id;
    public IReadOnlyList<PluginSetting> Settings => _plugin is IConfigurablePlugin configurable ? configurable.Settings : [];
    public IReadOnlyList<PluginAction> Actions => _plugin is IPluginActions actions ? actions.Actions : [];
    public IReadOnlyList<PluginUiContribution> Contributions => _plugin is IPluginUi ui ? ui.Contributions : [];
    public IReadOnlyList<PluginWidget> Widgets => _plugin is IPluginUi ui ? ui.Widgets : [];

    internal static PluginManifest ReadManifest(string packageRoot)
    {
        string root = Path.GetFullPath(packageRoot);
        string path = PackageFile(root, "plugin.wsgm.json");
        using var stream = File.OpenRead(path);
        byte[] bytes = new byte[PluginManifestReader.MaximumBytes + 1];
        int count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (!PluginManifestReader.TryRead(bytes.AsSpan(0, count), out var manifest, out var errors))
        { throw new InvalidDataException(string.Join(" ", errors)); }
        if (manifest!.Category == PluginCategories.Device)
        { throw new InvalidDataException("Device packages use the selected Device adapter and installation slot."); }
        PackageFile(root, manifest.EntryAssembly);
        return Snapshot(manifest);
    }

    /// <summary>Loads trusted code off the UI thread; the caller retains ownership of a timed-out task.</summary>
    internal static Task<CommonPluginPackage> LoadAsync(string packageRoot, PluginManifest admitted, CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(packageRoot);
        if (PluginManifestReader.Validate(admitted).Count != 0 || admitted.Category == PluginCategories.Device)
        { throw new InvalidDataException("Common package metadata is not admissible."); }
        var snapshot = Snapshot(admitted);
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string entryPath = PackageFile(root, snapshot.EntryAssembly);
            var context = new PluginPackageLoader.PluginLoadContext(root, entryPath);
            IPlugin? plugin = null;
            try
            {
                Assembly assembly;
                using (var entry = File.OpenRead(entryPath)) { assembly = context.LoadFromStream(entry); }
                var type = assembly.GetType(snapshot.EntryType, throwOnError: false, ignoreCase: false);
                if (type is null || !type.IsPublic || type.IsAbstract || type.ContainsGenericParameters
                    || !typeof(IPlugin).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) is null)
                { throw new InvalidDataException("The entry type must be a public concrete IPlugin with a parameterless constructor."); }
                plugin = (IPlugin)Activator.CreateInstance(type)!;
                if (plugin.Id != snapshot.Id) { throw new InvalidDataException("Plugin code and manifest identities differ."); }
                cancellationToken.ThrowIfCancellationRequested();
                return new CommonPluginPackage(context, plugin, snapshot);
            }
            catch (Exception loadFailure)
            {
                // Constructors must not acquire external resources. If disposal fails, retain the
                // context rather than explicitly unloading code whose cleanup was not confirmed.
                if (plugin is not null)
                {
                    try { await plugin.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception cleanupFailure)
                    { throw new AggregateException("Plugin loading and cleanup both failed.", loadFailure, cleanupFailure); }
                }
                context.Unload();
                throw;
            }
        }, CancellationToken.None);
    }

    private static PluginManifest Snapshot(PluginManifest manifest) => manifest with
    {
        Dependencies = Array.AsReadOnly(manifest.Dependencies.ToArray()),
        Permissions = Array.AsReadOnly(manifest.Permissions.ToArray()),
    };

    private static string PackageFile(string root, string name)
    {
        var rootAttributes = File.GetAttributes(root);
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0 || (rootAttributes & FileAttributes.Directory) == 0)
        { throw new InvalidDataException("Plugin package roots cannot be reparse points."); }
        string path = PluginPackageLoader.ConstrainPackagePath(root, name);
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        { throw new InvalidDataException("Plugin package files cannot be reparse points."); }
        return path;
    }

    public ValueTask<PluginHealth> StartAsync(IPluginHost host, PluginContext context, CancellationToken cancellationToken) =>
        _plugin.StartAsync(host, context, cancellationToken);
    public ValueTask SessionChangedAsync(PluginContext context, CancellationToken cancellationToken) =>
        _plugin.SessionChangedAsync(context, cancellationToken);
    public ValueTask SuspendAsync(PluginContext context, CancellationToken cancellationToken) => _plugin.SuspendAsync(context, cancellationToken);
    public ValueTask ResumeAsync(PluginContext context, CancellationToken cancellationToken) => _plugin.ResumeAsync(context, cancellationToken);
    public ValueTask<bool> StopAsync(PluginContext context, CancellationToken cancellationToken) => _plugin.StopAsync(context, cancellationToken);
    public ValueTask<PluginConfigurationResult> ConfigureAsync(PluginConfiguration configuration, PluginContext context, CancellationToken cancellationToken) =>
        _plugin is IConfigurablePlugin configurable ? configurable.ConfigureAsync(configuration, context, cancellationToken)
            : ValueTask.FromResult(new PluginConfigurationResult(configuration.Revision, PluginConfigurationOutcome.Applied));
    public ValueTask<PluginActionResult> ExecuteActionAsync(PluginActionRequest request, PluginContext context, CancellationToken cancellationToken) =>
        _plugin is IPluginActions actions ? actions.ExecuteActionAsync(request, context, cancellationToken)
            : ValueTask.FromResult(new PluginActionResult(request.OperationId, PluginActionOutcome.Rejected, "Plugin does not expose actions."));

    public async ValueTask DisposeAsync()
    {
        if (_disposed) { return; }
        await _plugin.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
        _context.Unload();
    }
}
