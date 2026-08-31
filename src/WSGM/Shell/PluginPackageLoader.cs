using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using WSGM.Core;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Shell;

/// <summary>Loads the sole validated device plugin into a package-local collectible context.</summary>
internal sealed class PluginPackageLoader : IDisposable
{
    private readonly PluginLoadContext _loadContext;
    private bool _disposed;

    private PluginPackageLoader(
        string packageRoot,
        PluginLoadContext loadContext,
        IDevicePlugin plugin)
    {
        PackageRoot = packageRoot;
        _loadContext = loadContext;
        Plugin = plugin;
    }

    internal string PackageRoot { get; }

    internal IDevicePlugin Plugin { get; }

    internal static PluginPackageLoader Load(InstalledDevicePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!package.Valid || package.Manifest is null)
        {
            throw new InvalidDataException("The installed device package is not valid.");
        }

        string root = Path.GetFullPath(package.PackagePath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("The plugin package directory is missing.");
        }

        string entryPath = ConstrainPackagePath(root, package.Manifest.EntryAssembly);
        if (!File.Exists(entryPath))
        {
            throw new FileNotFoundException("The plugin entry point is missing.", entryPath);
        }

        PluginLoadContext context = new(root, entryPath);
        IDevicePlugin? plugin = null;
        try
        {
            Assembly assembly;
            using (FileStream entry = File.OpenRead(entryPath))
            {
                // Loading the entry image from a stream avoids pinning the installed DLL for the
                // lifetime of the collectible context. The plugin can therefore be replaced as
                // soon as its lifecycle is quiescent; dependencies still resolve package-locally.
                assembly = context.LoadFromStream(entry);
            }
            Type entryType = assembly.GetType(
                package.Manifest.EntryType,
                throwOnError: false,
                ignoreCase: false)
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

            return new PluginPackageLoader(root, context, plugin);
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loadContext.Unload();
    }

    private static string ConstrainPackagePath(string packageRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Package paths must be non-empty and relative.");
        }

        string rootPrefix = packageRoot.TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(packageRoot, relativePath));
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A package path escaped the package directory.");
        }

        return candidate;
    }

    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private static readonly string SdkName = typeof(IDevicePlugin).Assembly.GetName().Name!;
        private readonly string _packageRoot;
        private readonly AssemblyDependencyResolver _resolver;

        internal PluginLoadContext(string packageRoot, string entryPath)
            : base($"WSGM.Plugin:{Path.GetFileName(packageRoot)}", isCollectible: true)
        {
            _packageRoot = packageRoot;
            _resolver = new AssemblyDependencyResolver(entryPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, SdkName, StringComparison.Ordinal))
            {
                return null;
            }

            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            if (path is null)
            {
                return null;
            }

            EnsurePackagePath(path);
            return LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (path is null)
            {
                return nint.Zero;
            }

            EnsurePackagePath(path);
            return LoadUnmanagedDllFromPath(path);
        }

        private void EnsurePackagePath(string path)
        {
            string rootPrefix = _packageRoot.TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "A resolved dependency escaped the package directory.");
            }
        }
    }
}
