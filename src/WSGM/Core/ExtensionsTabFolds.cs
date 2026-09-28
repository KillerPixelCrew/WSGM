using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace WSGM.Core;

/// <summary>Which sections of the Quick Access Extensions tab the user folded.</summary>
/// <remarks>
///     Presentation state rather than configuration, so it lives in its own small file under WSGM's
///     state directory, like the library import records. Steam rebuilds the tab on every open and
///     WSGM republishes it on every change, so a fold that was not kept would open again on its own.
/// </remarks>
public sealed class ExtensionsTabFolds
{
    private const int MaximumEntries = 256;
    private readonly Lock _gate = new();
    private readonly string _path;
    private HashSet<string>? _folded;

    /// <summary>Creates the store over WSGM's own per-user state directory.</summary>
    public ExtensionsTabFolds()
        : this(Path.Combine(Log.Directory, "extensions-tab.json"))
    {
    }

    /// <summary>Creates the store over one file.</summary>
    /// <param name="path">Where the folds live.</param>
    public ExtensionsTabFolds(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>Whether a section is folded.</summary>
    /// <param name="id">The section's item id.</param>
    public bool IsFolded(string id)
    {
        lock (_gate)
        {
            return Read().Contains(id);
        }
    }

    /// <summary>Folds or unfolds a section and keeps it.</summary>
    /// <param name="id">The section's item id.</param>
    /// <param name="folded">Whether it is folded.</param>
    /// <returns>Null, or why the fold could not be kept.</returns>
    public string? SetFolded(string id, bool folded)
    {
        lock (_gate)
        {
            var folds = Read();
            var changed = folded ? folds.Add(id) : folds.Remove(id);
            if (!changed)
            {
                return null;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                JsonObject document = new()
                {
                    ["folded"] = new JsonArray([.. folds.Order(StringComparer.Ordinal).Select(name => (JsonNode)name)])
                };
                AtomicFile.WriteText(_path, document.ToJsonString(), false);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ex.Message;
            }
        }
    }

    private HashSet<string> Read()
    {
        if (_folded is not null)
        {
            return _folded;
        }

        HashSet<string> folds = new(StringComparer.Ordinal);
        try
        {
            if (File.Exists(_path) && JsonNode.Parse(File.ReadAllText(_path)) is JsonObject document
                                   && document["folded"] is JsonArray folded)
            {
                foreach (var entry in folded.Take(MaximumEntries))
                {
                    if (entry is JsonValue value && value.TryGetValue(out string? id) && !string.IsNullOrWhiteSpace(id))
                    {
                        folds.Add(id);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Extensions tab folds could not be read: {ex.Message}");
        }

        _folded = folds;
        return folds;
    }
}
