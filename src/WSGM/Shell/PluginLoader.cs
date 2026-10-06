using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WinRT;
using WSGM.Core;
using WSGM.Device.Sdk.Plugin;
using WSGM.Plugin.Sdk;
using WinRtPoint = Windows.Foundation.Point;

namespace WSGM.Shell;

/// <summary>A package that could not be loaded, and whether the code it had already constructed was cleaned up.</summary>
/// <param name="message">Why loading failed.</param>
/// <param name="cleanupConfirmed">
///     False when a constructed plugin's disposal or the unload failed. Its code then stays loaded, and its
///     instance stays reserved.
/// </param>
/// <param name="inner">The original failure, when there was one.</param>
internal sealed class PluginLoadException(string message, bool cleanupConfirmed, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>Whether everything the load created was released.</summary>
    internal bool CleanupConfirmed { get; } = cleanupConfirmed;
}

/// <summary>A loaded plugin instance with the package file and load context its code runs from.</summary>
/// <typeparam name="TEntry">The contract the entry type implements.</typeparam>
/// <remarks>
///     The plugin is disposed exactly once, by whoever owns it at that moment, and its code is unloaded only
///     after that disposal completed. A disposal that failed or never finished leaves the code loaded.
/// </remarks>
internal sealed class LoadedPluginPackage<TEntry> where TEntry : class, IAsyncDisposable
{
    private readonly PluginLoader.PluginLoadContext? _context;
    private int _unloaded;

    /// <param name="plugin">The plugin instance.</param>
    /// <param name="package">The open package its code came from, or null for a plugin built into the process.</param>
    /// <param name="context">The load context holding its code, or null for a plugin built into the process.</param>
    internal LoadedPluginPackage(TEntry plugin, PluginPackageFile? package = null,
        PluginLoader.PluginLoadContext? context = null)
    {
        Plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        Package = package;
        _context = context;
    }

    /// <summary>The plugin instance.</summary>
    internal TEntry Plugin { get; }

    /// <summary>The open package, which also serves a device plugin's glyph files.</summary>
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

/// <summary>Loads a validated package's entry type into a package-local collectible context.</summary>
/// <remarks>
///     The device package and the common packages share every rule here; only the entry contract and the
///     graphics category's <see cref="ICapabilityPlugin" /> requirement differ.
/// </remarks>
internal static class PluginLoader
{
    /// <summary>Loads the sole valid device package.</summary>
    /// <param name="package">The package discovery admitted.</param>
    /// <returns>The loaded plugin and its package.</returns>
    /// <exception cref="PluginLoadException">The package could not be loaded.</exception>
    internal static LoadedPluginPackage<IDevicePlugin> LoadDevice(InstalledDevicePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!package.Valid || package.Manifest is not { } manifest)
        {
            throw new InvalidDataException("The installed device package is not valid.");
        }

        return Load<IDevicePlugin>(
            package.PackagePath,
            new Entry(manifest.Id, manifest.Version, manifest.EntryAssembly, manifest.EntryType,
                manifest.WsgmVersion),
            static file => file.DeviceManifest is { } reopened
                ? new Entry(reopened.Id, reopened.Version, reopened.EntryAssembly, reopened.EntryType,
                    reopened.WsgmVersion)
                : null,
            static _ => null,
            static plugin => plugin.PackageId,
            CancellationToken.None);
    }

    /// <summary>Loads a common package off the calling thread.</summary>
    /// <param name="packagePath">The package file.</param>
    /// <param name="admitted">The manifest discovery admitted.</param>
    /// <param name="cancellationToken">Cancels the load; a constructed plugin is disposed again.</param>
    /// <returns>The loaded plugin and its package.</returns>
    /// <exception cref="PluginLoadException">The package could not be loaded.</exception>
    internal static Task<LoadedPluginPackage<IPlugin>> LoadCommonAsync(string packagePath, PluginManifest admitted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admitted);
        if (PluginManifestReader.Validate(admitted).Count != 0 || admitted.Category == PluginCategories.Device)
        {
            throw new InvalidDataException("Common package metadata is not admissible.");
        }

        var graphics = admitted.Category == PluginCategories.Gpu;
        return Task.Run(() => Load<IPlugin>(
            packagePath,
            new Entry(admitted.Id, admitted.Version, admitted.EntryAssembly, admitted.EntryType, admitted.WsgmVersion),
            static file => file.CommonManifest is { } reopened
                ? new Entry(reopened.Id, reopened.Version, reopened.EntryAssembly, reopened.EntryType,
                    reopened.WsgmVersion)
                : null,
            type => graphics && !typeof(ICapabilityPlugin).IsAssignableFrom(type)
                ? "A graphics package's entry type must implement ICapabilityPlugin."
                : null,
            static plugin => plugin.Id,
            cancellationToken), CancellationToken.None);
    }

    private static LoadedPluginPackage<TEntry> Load<TEntry>(
        string packagePath,
        Entry admitted,
        Func<PluginPackageFile, Entry?> reopen,
        Func<Type, string?> entryRule,
        Func<TEntry, string> identity,
        CancellationToken cancellationToken)
        where TEntry : class, IAsyncDisposable
    {
        PluginPackageFile file;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Discovery closed its handle, so the file is opened again and must still be the package
            // that was admitted: a replacement dropped in between is refused, not loaded.
            file = PluginPackageFile.Open(packagePath);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new PluginLoadException(ex.Message, true, ex);
        }

        if (reopen(file) != admitted)
        {
            file.Dispose();
            throw new PluginLoadException("The plugin package changed between discovery and loading.", true);
        }

        PluginLoadContext context = new(file);
        TEntry? plugin = null;
        try
        {
            var assembly = context.LoadEntry(admitted.EntryAssembly);
            var type = assembly.GetType(admitted.EntryType, false, false)
                       ?? throw new InvalidDataException("The declared plugin entry type was not found.");
            if (!type.IsPublic || type.IsAbstract || type.IsInterface || type.ContainsGenericParameters
                || !typeof(TEntry).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) is null)
            {
                throw new InvalidDataException(
                    $"The entry type must be a public concrete {typeof(TEntry).Name} with a parameterless constructor.");
            }

            if (entryRule(type) is { } refusal)
            {
                throw new InvalidDataException(refusal);
            }

            plugin = (TEntry)Activator.CreateInstance(type)!;
            if (!string.Equals(identity(plugin), admitted.Id, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The plugin code and manifest package identifiers differ.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new LoadedPluginPackage<TEntry>(plugin, file, context);
        }
        catch (Exception loadFailure) when (loadFailure is not OutOfMemoryException)
        {
            // Constructors must not acquire external resources. If disposal fails, the context is kept
            // rather than unloading code whose cleanup was not confirmed.
            if (plugin is not null)
            {
                try
                {
                    plugin.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception cleanupFailure) when (cleanupFailure is not OutOfMemoryException)
                {
                    throw new PluginLoadException(
                        $"{loadFailure.Message} Its cleanup was not confirmed: {cleanupFailure.Message}", false,
                        new AggregateException(loadFailure, cleanupFailure));
                }
            }

            try
            {
                context.Unload();
                file.Dispose();
            }
            catch (Exception unloadFailure) when (unloadFailure is not OutOfMemoryException)
            {
                throw new PluginLoadException(
                    $"{loadFailure.Message} Unloading it failed: {unloadFailure.Message}", false,
                    new AggregateException(loadFailure, unloadFailure));
            }

            throw new PluginLoadException(loadFailure.Message, true, loadFailure);
        }
    }

    /// <summary>The manifest fields a reopened package must still carry.</summary>
    private sealed record Entry(
        string Id,
        string Version,
        string EntryAssembly,
        string EntryType,
        string? WsgmVersion);

    internal sealed class PluginLoadContext : AssemblyLoadContext
    {
        // Assemblies that may exist only once per process, whatever version the package carries.
        // CsWinRT's runtime registers a process-global ComWrappers instance when it first runs; a
        // second copy loaded into this context makes whichever side initializes second throw
        // "Attempt to update previously set global instance" for the rest of the process. The
        // Claw package ships both (any `-windows10.0.x` plugin build copies them), and the plugin
        // touched WinRT first, so WSGM's own Wi-Fi and Bluetooth queries were the side that died
        // (device-reproduced 2026-09-01).
        private static readonly Dictionary<string, Assembly> HostOwned = new(StringComparer.Ordinal)
        {
            [typeof(IDevicePlugin).Assembly.GetName().Name!] = typeof(IDevicePlugin).Assembly,
            [typeof(IPlugin).Assembly.GetName().Name!] = typeof(IPlugin).Assembly,
            [typeof(ISteamUiModule).Assembly.GetName().Name!] = typeof(ISteamUiModule).Assembly,
            [typeof(IWinRTObject).Assembly.GetName().Name!] = typeof(IWinRTObject).Assembly,
            [typeof(WinRtPoint).Assembly.GetName().Name!] =
                typeof(WinRtPoint).Assembly
        };

        private readonly Lock _gate = new();
        private readonly PluginPackageFile _package;

        internal PluginLoadContext(PluginPackageFile package)
            : base($"WSGM.Plugin:{package.Id}", true)
        {
            _package = package;
        }

        /// <summary>Loads the package's entry assembly from memory.</summary>
        internal Assembly LoadEntry(string entryAssembly)
        {
            return TryLoadFromPackage(entryAssembly)
                   ?? throw new FileNotFoundException("The plugin entry point is missing.", entryAssembly);
        }

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
}
