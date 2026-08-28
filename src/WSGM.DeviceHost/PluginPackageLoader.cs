using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using WSGM.Device.Contracts.Packaging;
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
        byte[] manifestBytes = File.ReadAllBytes(manifestPath);
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

        string entryPath = Constrain(root, manifest.EntryPoint);
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
        try
        {
            Assembly assembly = context.LoadFromAssemblyPath(metadata.EntryPath);
            Type[] entryTypes = assembly.GetTypes()
                .Where(type => !type.IsAbstract
                    && !type.IsInterface
                    && typeof(IDevicePlugin).IsAssignableFrom(type))
                .ToArray();
            if (entryTypes.Length != 1)
            {
                throw new InvalidDataException(
                    $"The entry assembly must contain exactly one IDevicePlugin; found {entryTypes.Length}.");
            }

            if (Activator.CreateInstance(entryTypes[0]) is not IDevicePlugin plugin)
            {
                throw new InvalidDataException("The plugin entry type needs a public parameterless constructor.");
            }

            if (!string.Equals(plugin.PackageId, metadata.Manifest.Id, StringComparison.Ordinal))
            {
                plugin.DisposeAsync().AsTask().GetAwaiter().GetResult();
                throw new InvalidDataException("The plugin code and manifest package identifiers differ.");
            }

            return new PluginPackageLoader(
                metadata.PackageRoot,
                metadata.Manifest,
                context,
                plugin);
        }
        catch
        {
            context.Unload();
            throw;
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
        private static readonly string ContractsName = typeof(PluginManifest).Assembly.GetName().Name!;
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
            if (string.Equals(assemblyName.Name, ContractsName, StringComparison.Ordinal)
                || string.Equals(assemblyName.Name, SdkName, StringComparison.Ordinal))
            {
                return null;
            }

            string path = _resolver.ResolveAssemblyToPath(assemblyName)
                ?? throw new FileNotFoundException(
                    $"Package-local dependency '{assemblyName.Name}' was not resolved.");
            EnsurePackagePath(path);
            return LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            string path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName)
                ?? throw new DllNotFoundException(
                    $"Package-local native dependency '{unmanagedDllName}' was not resolved.");
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
