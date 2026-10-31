using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WSGM.Core;

/// <summary>
///     Package files the Plugins page removed while they were loaded. WSGM deletes them once at startup,
///     before it opens any package. Only files directly in the Plugins folder are ever deleted.
/// </summary>
/// <param name="path">The file the list is kept in.</param>
internal sealed class PendingPluginRemovalStore(string path)
{
    /// <summary>Reads the list.</summary>
    /// <param name="entries">The pending files; empty when there is no list.</param>
    /// <returns>False when a list exists but cannot be read, so nothing may write over it.</returns>
    internal bool TryRead(out string[] entries)
    {
        entries = [];
        try
        {
            var read = JsonSerializer.Deserialize(File.ReadAllText(path),
                PendingRemovalsJsonContext.Default.StringArray);
            if (read is null || read.Any(entry => entry is null))
            {
                Log.Warn("Plugins: the pending removal list is malformed and is left as it is.");
                return false;
            }

            entries = read;
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn("Plugins: the pending removal list could not be read and is left as it is: " + ex.Message);
            return false;
        }
    }

    /// <summary>Records a file to delete at the next start.</summary>
    /// <param name="packagePath">The installed file.</param>
    internal void Add(string packagePath)
    {
        if (TryRead(out var entries))
        {
            Write([.. entries.Append(packagePath).Distinct(StringComparer.OrdinalIgnoreCase)]);
        }
    }

    /// <summary>Drops a file from the list, because it was installed again.</summary>
    /// <param name="packagePath">The installed file.</param>
    internal void Forget(string packagePath)
    {
        if (!TryRead(out var entries))
        {
            return;
        }

        var remaining = entries
            .Where(entry => !string.Equals(entry, packagePath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (remaining.Length != entries.Length)
        {
            Write(remaining);
        }
    }

    /// <summary>Deletes what can be deleted now and keeps the rest.</summary>
    /// <param name="pluginsRoot">The Plugins folder.</param>
    internal void Apply(string pluginsRoot)
    {
        if (!TryRead(out var pending) || pending.Length == 0)
        {
            return;
        }

        List<string> kept = [];
        foreach (var file in pending)
        {
            if (!PluginPackageManager.IsInside(file, pluginsRoot))
            {
                continue;
            }

            try
            {
                File.Delete(file);
                Log.Info($"Plugins: removed {Path.GetFileName(file)} as asked on the Plugins page.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Plugins: {Path.GetFileName(file)} could not be removed yet and stays pending: {ex.Message}");
                kept.Add(file);
            }
        }

        Write(kept);
    }

    private void Write(IReadOnlyList<string> entries)
    {
        try
        {
            if (entries.Count == 0)
            {
                File.Delete(path);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteText(path,
                JsonSerializer.Serialize(entries.ToArray(), PendingRemovalsJsonContext.Default.StringArray), true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Plugins: the pending removal list could not be written: " + ex.Message);
        }
    }
}

/// <summary>Source-generated JSON metadata for the pending removal list.</summary>
[JsonSerializable(typeof(string[]))]
internal sealed partial class PendingRemovalsJsonContext : JsonSerializerContext;
