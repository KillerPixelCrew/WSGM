using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>A common package sharing the established Device loader's dependency and WinRT identity rules.</summary>
internal sealed class CommonPluginPackage : IPlugin, IConfigurablePlugin, IPluginActions, IPluginUi, IPluginSteamUi
{
    private readonly PluginPackageLoader.PluginLoadContext _context;
    private readonly PluginPackageFile _package;
    private readonly IPlugin _plugin;
    private bool _disposed;

    private CommonPluginPackage(PluginPackageLoader.PluginLoadContext context, PluginPackageFile package,
        IPlugin plugin)
    {
        _context = context;
        _package = package;
        _plugin = plugin;
    }

    public IReadOnlyList<PluginSetting> Settings =>
        _plugin is IConfigurablePlugin configurable ? configurable.Settings : [];

    public ValueTask<PluginConfigurationResult> ConfigureAsync(PluginConfiguration configuration, PluginContext context,
        CancellationToken cancellationToken)
    {
        return _plugin is IConfigurablePlugin configurable
            ? configurable.ConfigureAsync(configuration, context, cancellationToken)
            : ValueTask.FromResult(new PluginConfigurationResult(configuration.Revision,
                PluginConfigurationOutcome.Applied));
    }

    public string Id => _plugin.Id;

    public ValueTask<PluginHealth> StartAsync(IPluginHost host, PluginContext context,
        CancellationToken cancellationToken)
    {
        return _plugin.StartAsync(host, context, cancellationToken);
    }

    public ValueTask SessionChangedAsync(PluginContext context, CancellationToken cancellationToken)
    {
        return _plugin.SessionChangedAsync(context, cancellationToken);
    }

    public ValueTask SuspendAsync(PluginContext context, CancellationToken cancellationToken)
    {
        return _plugin.SuspendAsync(context, cancellationToken);
    }

    public ValueTask ResumeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        return _plugin.ResumeAsync(context, cancellationToken);
    }

    public ValueTask<bool> StopAsync(PluginContext context, CancellationToken cancellationToken)
    {
        return _plugin.StopAsync(context, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _plugin.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
        _context.Unload();
        _package.Dispose();
    }

    public IReadOnlyList<PluginAction> Actions => _plugin is IPluginActions actions ? actions.Actions : [];

    public ValueTask<PluginActionResult> ExecuteActionAsync(PluginActionRequest request, PluginContext context,
        CancellationToken cancellationToken)
    {
        return _plugin is IPluginActions actions
            ? actions.ExecuteActionAsync(request, context, cancellationToken)
            : ValueTask.FromResult(new PluginActionResult(request.OperationId, PluginActionOutcome.Rejected,
                "Plugin does not expose actions."));
    }

    public IReadOnlyList<PluginSteamUiContribution> SteamUiContributions =>
        _plugin is IPluginSteamUi steamUi ? steamUi.SteamUiContributions : [];

    public IReadOnlyList<ISteamUiModule> SteamUiModules =>
        _plugin is IPluginSteamUi steamUi ? steamUi.SteamUiModules : [];

    public event Action? SteamUiChanged
    {
        add
        {
            if (_plugin is IPluginSteamUi steamUi)
            {
                steamUi.SteamUiChanged += value;
            }
        }
        remove
        {
            if (_plugin is IPluginSteamUi steamUi)
            {
                steamUi.SteamUiChanged -= value;
            }
        }
    }

    public IReadOnlyList<PluginUiContribution> Contributions => _plugin is IPluginUi ui ? ui.Contributions : [];
    public IReadOnlyList<PluginWidget> Widgets => _plugin is IPluginUi ui ? ui.Widgets : [];

    /// <summary>Loads trusted code off the UI thread; the caller retains ownership of a timed-out task.</summary>
    internal static Task<CommonPluginPackage> LoadAsync(string packagePath, PluginManifest admitted,
        CancellationToken cancellationToken)
    {
        if (PluginManifestReader.Validate(admitted).Count != 0 || admitted.Category == PluginCategories.Device)
        {
            throw new InvalidDataException("Common package metadata is not admissible.");
        }

        var snapshot = Snapshot(admitted);
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Discovery closed its handle; the reopened file must still be the admitted package.
            var file = PluginPackageFile.Open(packagePath);
            if (file.CommonManifest is not { } reopened || reopened.Id != snapshot.Id
                                                        || reopened.Version != snapshot.Version
                                                        || reopened.EntryAssembly != snapshot.EntryAssembly
                                                        || reopened.EntryType != snapshot.EntryType)
            {
                file.Dispose();
                throw new InvalidDataException("The plugin package changed between discovery and loading.");
            }

            var context = new PluginPackageLoader.PluginLoadContext(file);
            IPlugin? plugin = null;
            try
            {
                var assembly = context.LoadEntry(snapshot.EntryAssembly);
                var type = assembly.GetType(snapshot.EntryType, false, false);
                if (type is null || !type.IsPublic || type.IsAbstract || type.ContainsGenericParameters
                    || !typeof(IPlugin).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) is null)
                {
                    throw new InvalidDataException(
                        "The entry type must be a public concrete IPlugin with a parameterless constructor.");
                }

                plugin = (IPlugin)Activator.CreateInstance(type)!;
                if (plugin.Id != snapshot.Id)
                {
                    throw new InvalidDataException("Plugin code and manifest identities differ.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                return new CommonPluginPackage(context, file, plugin);
            }
            catch (Exception loadFailure)
            {
                // Constructors must not acquire external resources. If disposal fails, retain the
                // context rather than explicitly unloading code whose cleanup was not confirmed.
                if (plugin is not null)
                {
                    try
                    {
                        await plugin.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception cleanupFailure)
                    {
                        throw new AggregateException("Plugin loading and cleanup both failed.", loadFailure,
                            cleanupFailure);
                    }
                }

                context.Unload();
                file.Dispose();
                throw;
            }
        }, CancellationToken.None);
    }

    private static PluginManifest Snapshot(PluginManifest manifest)
    {
        return manifest with
        {
            Dependencies = Array.AsReadOnly([.. manifest.Dependencies]),
            Permissions = Array.AsReadOnly([.. manifest.Permissions])
        };
    }
}
