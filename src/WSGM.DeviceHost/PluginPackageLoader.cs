using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using WSGM.Device.Sdk.Packaging;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.DeviceHost;

/// <summary>Loads exactly one validated plugin from a deterministic package-local context.</summary>
internal sealed class PluginPackageLoader : IDisposable
{
    private readonly PluginLoadContext _loadContext;
    private bool _disposed;

    private PluginPackageLoader(
        string packageRoot,
        PluginManifest manifest,
        PluginLoadContext loadContext,
        IDevicePlugin plugin)
    {
        PackageRoot = packageRoot;
        Manifest = manifest;
        _loadContext = loadContext;
        Plugin = plugin;
    }

    public string PackageRoot { get; }

    public PluginManifest Manifest { get; }

    public IDevicePlugin Plugin { get; }

    public static PluginPackageMetadata ReadMetadata(string packagePath, string expectedPackageId)
    {
        string root = Path.GetFullPath(packagePath);
        if (!Directory.Exists(root) || IsLink(root))
        {
            throw new InvalidDataException("The plugin package directory is missing or is a link.");
        }

        string manifestPath = Constrain(root, "plugin.wsgm.json");
        FileInfo manifestFile = new(manifestPath);
        if (!manifestFile.Exists)
        {
            throw new InvalidDataException("The package manifest is missing or exceeds its byte limit.");
        }

        byte[] manifestBytes = ReadBoundedManifest(manifestPath);
        PluginManifestReadResult read = PluginManifestReader.Read(manifestBytes);
        PluginManifest manifest = read.Manifest
            ?? throw new InvalidDataException("The package manifest is malformed.");
        if (!read.IsValid)
        {
            throw new InvalidDataException(
                "The package manifest failed validation: "
                    + string.Join("; ", read.Errors.Select(error => error.Message)));
        }

        if (!string.Equals(manifest.Id, expectedPackageId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The package identifier does not match the launch grant.");
        }

        string entryPath = Constrain(root, manifest.EntryAssembly);
        if (!File.Exists(entryPath) || IsLink(entryPath))
        {
            throw new InvalidDataException("The plugin entry point is missing or is a link.");
        }

        return new PluginPackageMetadata(root, entryPath, manifest);
    }

    public static PluginPackageLoader LoadPlugin(PluginPackageMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        PluginLoadContext context = new(metadata.PackageRoot, metadata.EntryPath);
        IDevicePlugin? plugin = null;
        try
        {
            Assembly assembly = context.LoadFromAssemblyPath(metadata.EntryPath);
            Type entryType = assembly.GetType(
                metadata.Manifest.EntryType,
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
                throw new InvalidDataException("The plugin entry type needs a public parameterless constructor.");
            }
            plugin = Activator.CreateInstance(entryType) as IDevicePlugin
                ?? throw new InvalidDataException("The plugin entry type did not create an IDevicePlugin instance.");

            if (!string.Equals(plugin.PackageId, metadata.Manifest.Id, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The plugin code and manifest package identifiers differ.");
            }

            return new PluginPackageLoader(
                metadata.PackageRoot,
                metadata.Manifest,
                context,
                plugin);
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

    private static byte[] ReadBoundedManifest(string manifestPath)
    {
        using var stream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        if (stream.Length > ManifestLimits.MaxDocumentBytes)
        {
            throw new InvalidDataException("The package manifest is missing or exceeds its byte limit.");
        }

        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() >= 0)
        {
            throw new InvalidDataException("The package manifest is missing or exceeds its byte limit.");
        }
        return bytes;
    }

    private static string Constrain(string packageRoot, string relativePath)
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

        string current = packageRoot;
        string relative = Path.GetRelativePath(packageRoot, candidate);
        foreach (string segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && IsLink(current))
            {
                throw new InvalidDataException("Package paths may not traverse links.");
            }
        }

        return candidate;
    }

    private static bool IsLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        return info.LinkTarget is not null
            || (info.Attributes & FileAttributes.ReparsePoint) != 0;
    }

    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private static readonly string SdkName = typeof(IDevicePlugin).Assembly.GetName().Name!;
        private readonly string _packageRoot;
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string packageRoot, string entryPath)
            : base($"WSGM.DeviceHost:{Path.GetFileName(packageRoot)}", isCollectible: true)
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
                // Framework and the shared SDK resolve from the default context. Package
                // dependencies still resolve to concrete paths below and remain confined.
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
                // Not a package-supplied library, so it is an operating-system one and the runtime's
                // own probing resolves it — exactly as the managed Load above returns null for
                // framework assemblies. Throwing here instead meant a plugin could not reach any
                // Windows API at all: the Claw plugin died on `ole32.dll`, which it needs only
                // because WMI is COM, and the whole device cycle faulted with it.
                //
                // This does not widen the package confinement. What is confined is what the package
                // may SHIP: a name the resolver does answer still has to resolve inside the package
                // directory and must not be a link, which EnsurePackagePath below still enforces.
                return nint.Zero;
            }

            EnsurePackagePath(path);
            return NativeLibrary.Load(path);
        }

        private void EnsurePackagePath(string path)
        {
            string rootPrefix = _packageRoot.TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                || IsLink(fullPath))
            {
                throw new InvalidDataException("A resolved dependency escaped the package directory.");
            }
        }
    }
}

/// <summary>Validated package metadata safe to inspect before plugin code is loaded.</summary>
internal sealed record PluginPackageMetadata(
    string PackageRoot,
    string EntryPath,
    PluginManifest Manifest);
