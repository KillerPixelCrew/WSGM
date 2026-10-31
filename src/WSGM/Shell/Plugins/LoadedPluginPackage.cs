using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>A loaded plugin instance with the package file and load context its code runs from.</summary>
/// <remarks>
///     The plugin is disposed exactly once, by whoever owns it at that moment, and its code is unloaded only
///     after that disposal completed. A disposal that failed or never finished leaves the code loaded.
/// </remarks>
internal sealed class LoadedPluginPackage
{
    private readonly PluginLoadContext? _context;
    private int _unloaded;

    /// <summary>Captures the plugin and any package/load-context resources whose release follows plugin disposal.</summary>
    /// <param name="plugin">The plugin instance.</param>
    /// <param name="package">The open package its code came from, or null for a plugin built into the process.</param>
    /// <param name="context">The load context holding its code, or null for a plugin built into the process.</param>
    internal LoadedPluginPackage(IPlugin plugin, PluginPackageFile? package = null,
        PluginLoadContext? context = null)
    {
        Plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        Package = package;
        _context = context;
    }

    /// <summary>The plugin instance.</summary>
    internal IPlugin Plugin { get; }

    /// <summary>The open package the plugin's code and files are served from.</summary>
    internal PluginPackageFile? Package { get; }

    /// <summary>Unloads the code and closes the package. Call only after the plugin's disposal completed.</summary>
    internal void Unload()
    {
        if (Interlocked.Exchange(ref _unloaded, 1) != 0)
        {
            return;
        }

        _context?.Unload();
        Package?.Dispose();
    }

    /// <summary>Disposes a plugin that was never handed to another owner, then unloads its code.</summary>
    /// <returns>Completion after both; a failed disposal throws and leaves the code loaded.</returns>
    internal async ValueTask DisposeAsync()
    {
        await Plugin.DisposeAsync().ConfigureAwait(false);
        Unload();
    }
}
