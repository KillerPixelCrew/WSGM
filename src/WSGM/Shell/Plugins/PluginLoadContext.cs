using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using SteamUiToolkit;
using WinRT;
using WSGM.Core;
using WSGM.Plugin.Sdk;
using WinRtPoint = Windows.Foundation.Point;

namespace WSGM.Shell;

/// <summary>
///     Collectible managed load context sharing host SDK/WinRT type identity and resolving package assemblies in
///     memory.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    // SDK identity and CsWinRT's process-global ComWrappers registration require a single host copy.
    private static readonly Dictionary<string, Assembly> HostOwned = new(StringComparer.Ordinal)
    {
        [typeof(IPlugin).Assembly.GetName().Name!] = typeof(IPlugin).Assembly,
        [typeof(ISteamUiModule).Assembly.GetName().Name!] = typeof(ISteamUiModule).Assembly,
        [typeof(IWinRTObject).Assembly.GetName().Name!] = typeof(IWinRTObject).Assembly,
        [typeof(WinRtPoint).Assembly.GetName().Name!] =
            typeof(WinRtPoint).Assembly
    };

    private readonly Lock _gate = new();
    private readonly PluginPackageFile _package;

    /// <summary>Creates an unloaded collectible context borrowing the package until its owner unloads it.</summary>
    /// <param name="package">Opened validated package retained for managed assembly resolution.</param>
    internal PluginLoadContext(PluginPackageFile package)
        : base($"WSGM.Plugin:{package.Id}", true)
    {
        _package = package;
    }

    /// <summary>Loads the package's entry assembly from memory.</summary>
    /// <param name="entryAssembly">Validated managed assembly path inside the package.</param>
    /// <returns>The assembly loaded in this collectible context.</returns>
    /// <exception cref="FileNotFoundException">The package does not contain the named entry assembly.</exception>
    internal Assembly LoadEntry(string entryAssembly)
    {
        return TryLoadFromPackage(entryAssembly)
               ?? throw new FileNotFoundException("The plugin entry point is missing.", entryAssembly);
    }

    /// <summary>Shares host contracts and resolves managed dependencies from the host first, then the package.</summary>
    /// <param name="assemblyName">Dependency requested by the runtime.</param>
    /// <returns>The shared/package assembly, or null to allow normal runtime fallback when absent.</returns>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // The host's SDK is the type-identity boundary, whatever assembly version the plugin
        // was compiled against. Deferring to the default context instead would re-check the
        // version and refuse a plugin built against a different SDK build even when the
        // contract still matches - the manifest apiVersion is the real compatibility gate,
        // not the assembly version. The WinRT pair is pinned for the same reason plus the
        // process-global registration above.
        if (HostOwned.TryGetValue(assemblyName.Name ?? string.Empty, out var hostOwned))
        {
            return hostOwned;
        }

        // Host-first for everything else: a dependency the host already ships is shared, not
        // duplicated. Package authors cannot be expected to trim the framework and runtime
        // assemblies their SDK copies beside the plugin, and a second copy of anything that
        // holds process-wide state (native handles, COM registrations, static caches) is a
        // fault the host cannot recover from. The package-local copy is only used for
        // assemblies the host does not have at all, which is the isolation the context is
        // for. A version the host cannot satisfy also falls through to the package copy, so
        // a plugin carrying a newer library than WSGM still loads; that duplicate is logged
        // once because it is the case that can bite later.
        var fileName = PackageFileName(assemblyName);
        try
        {
            return Default.LoadFromAssemblyName(assemblyName);
        }
        catch (FileNotFoundException)
        {
            // Not a host assembly: package-local or absent.
        }
        catch (FileLoadException ex)
        {
            if (fileName is not null)
            {
                Log.Warn($"Plugin dependency {assemblyName.Name} {assemblyName.Version} loads "
                         + $"from the package because the host's copy does not satisfy it ({ex.Message}).");
            }
        }

        return fileName is null ? null : TryLoadFromPackage(fileName);
    }

    // Native resolution is not overridden: packages carry no native images (PluginPackageFile
    // refuses them), so it stays with the system search path, where plugins' system DLLs live.

    private Assembly? TryLoadFromPackage(string fileName)
    {
        lock (_gate)
        {
            if (!_package.TryOpenAssembly(fileName, out var image, out var symbols))
            {
                return null;
            }

            using (image)
            using (symbols)
            {
                return symbols is null ? LoadFromStream(image) : LoadFromStream(image, symbols);
            }
        }
    }

    private static string? PackageFileName(AssemblyName assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName.Name))
        {
            return null;
        }

        var file = assemblyName.Name + ".dll";
        return string.IsNullOrEmpty(assemblyName.CultureName) ? file : assemblyName.CultureName + "/" + file;
    }
}
