using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>Loads a validated package's entry type into a package-local collectible context.</summary>
/// <remarks>
///     The device package and the common packages share every rule here; only the entry contract and the
///     graphics category's <see cref="ICapabilityPlugin" /> requirement differ.
/// </remarks>
internal static class PluginLoader
{
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
}
