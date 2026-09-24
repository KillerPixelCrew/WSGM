using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Threading;
using Windows.Foundation;
using WinRT;
using WSGM.Core;
using WSGM.Device.Sdk.Plugin;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>Loads the sole validated device plugin into a package-local collectible context.</summary>
internal sealed class PluginPackageLoader : IDisposable
{
    private readonly PluginLoadContext _loadContext;
    private bool _disposed;

    private PluginPackageLoader(
        PluginPackageFile package,
        PluginLoadContext loadContext,
        IDevicePlugin plugin)
    {
        Package = package;
        _loadContext = loadContext;
        Plugin = plugin;
    }

    internal IDevicePlugin Plugin { get; }

    /// <summary>The open package, which also serves the plugin's glyph files.</summary>
    internal PluginPackageFile Package { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loadContext.Unload();
        Package.Dispose();
    }

    internal static PluginPackageLoader Load(InstalledDevicePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!package.Valid || package.Manifest is null)
        {
            throw new InvalidDataException("The installed device package is not valid.");
        }

        // Discovery closed its handle, so the file is opened again and must still be the package
        // that was admitted: a replacement dropped in between is refused, not loaded.
        var file = PluginPackageFile.Open(package.PackagePath);
        if (file.DeviceManifest is not { } reopened
            || reopened.Id != package.Manifest.Id
            || reopened.Version != package.Manifest.Version
            || reopened.EntryAssembly != package.Manifest.EntryAssembly
            || reopened.EntryType != package.Manifest.EntryType
            || reopened.WsgmVersion != package.Manifest.WsgmVersion)
        {
            file.Dispose();
            throw new InvalidDataException("The device package changed between discovery and loading.");
        }

        PluginLoadContext context = new(file);
        IDevicePlugin? plugin = null;
        try
        {
            var assembly = context.LoadEntry(package.Manifest.EntryAssembly);
            var entryType = assembly.GetType(
                                package.Manifest.EntryType,
                                false,
                                false)
                            ?? throw new InvalidDataException("The declared plugin entry type was not found.");
            if (!entryType.IsPublic
                || entryType.IsAbstract
                || entryType.IsInterface
                || entryType.ContainsGenericParameters
                || !typeof(IDevicePlugin).IsAssignableFrom(entryType))
            {
                throw new InvalidDataException(
                    "The declared entry type must be a public, concrete, non-generic IDevicePlugin.");
            }

            if (entryType.GetConstructor(Type.EmptyTypes) is null)
            {
                throw new InvalidDataException(
                    "The plugin entry type needs a public parameterless constructor.");
            }

            plugin = Activator.CreateInstance(entryType) as IDevicePlugin
                     ?? throw new InvalidDataException(
                         "The plugin entry type did not create an IDevicePlugin instance.");
            if (!string.Equals(plugin.PackageId, package.Manifest.Id, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The plugin code and manifest package identifiers differ.");
            }

            return new PluginPackageLoader(file, context, plugin);
        }
        catch (Exception loadFailure)
        {
            List<Exception> failures = [loadFailure];
            if (plugin is not null)
            {
                try
                {
                    plugin.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception disposalFailure)
                {
                    failures.Add(disposalFailure);
                }
            }

            try
            {
                context.Unload();
                file.Dispose();
            }
            catch (Exception unloadFailure)
            {
                failures.Add(unloadFailure);
            }

            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(loadFailure).Throw();
            }

            throw new AggregateException(
                "Plugin loading and resource cleanup were not both verified.",
                failures);
        }
    }

    /// <summary>Resolves a relative path below a host-owned root, refusing escapes.</summary>
    internal static string ConstrainPackagePath(string packageRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Package paths must be non-empty and relative.");
        }

        var rootPrefix = packageRoot.TrimEnd(Path.DirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(packageRoot, relativePath));
        return candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            ? candidate
            : throw new InvalidDataException("A package path escaped the package directory.");
    }

    internal sealed class PluginLoadContext : AssemblyLoadContext
    {
        private static readonly string SdkName = typeof(IDevicePlugin).Assembly.GetName().Name!;

        // Assemblies that may exist only once per process, whatever version the package carries.
        // CsWinRT's runtime registers a process-global ComWrappers instance when it first runs; a
        // second copy loaded into this context makes whichever side initializes second throw
        // "Attempt to update previously set global instance" for the rest of the process. The
        // Claw package ships both (any `-windows10.0.x` plugin build copies them), and the plugin
        // touched WinRT first, so WSGM's own Wi-Fi and Bluetooth queries were the side that died
        // (device-reproduced 2026-09-01).
        private static readonly Dictionary<string, Assembly> HostOwned = new(StringComparer.Ordinal)
        {
            [SdkName] = typeof(IDevicePlugin).Assembly,
            [typeof(IPlugin).Assembly.GetName().Name!] = typeof(IPlugin).Assembly,
            [typeof(ISteamUiModule).Assembly.GetName().Name!] = typeof(ISteamUiModule).Assembly,
            [typeof(IWinRTObject).Assembly.GetName().Name!] = typeof(IWinRTObject).Assembly,
            [typeof(Point).Assembly.GetName().Name!] =
                typeof(Point).Assembly
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
            // assemblies the host does not have at all — which is the isolation the context is
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
