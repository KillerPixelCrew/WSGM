using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace WSGM.Core;

/// <summary>Which sections of Steam's Quick Access tabs the user opened.</summary>
/// <remarks>
///     Every section starts folded, so the store remembers what was opened, not what was closed. An
///     Extensions tab section is named by its item id, a theme's settings by the item id and the
///     theme's setting key, a Performance or Quick Settings section by its title. Presentation state
///     rather than configuration, so it lives in its own small file under WSGM's state directory,
///     like the library import records. Steam rebuilds a tab on every open and WSGM republishes it
///     on every change, so a section that was opened and not kept would fold again on its own.
/// </remarks>
public sealed class QuickAccessFolds
{
    private readonly Lock _gate = new();
    private readonly string _path;
    private HashSet<string>? _open;

    /// <summary>Creates the store over WSGM's own per-user state directory.</summary>
    public QuickAccessFolds()
        : this(Path.Combine(Log.Directory, "quick-access-folds.json"))
    {
    }

    /// <summary>Creates the store over one file.</summary>
    /// <param name="path">Where the folds live.</param>
    public QuickAccessFolds(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>Every open section, in name order.</summary>
    public IReadOnlyList<string> Open
    {
        get
        {
            lock (_gate)
            {
                return [.. Read().Order(StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>Whether a section is open. One never touched is folded.</summary>
    /// <param name="id">The section's id.</param>
    public bool IsOpen(string id)
    {
        lock (_gate)
        {
            return Read().Contains(id);
        }
    }

    /// <summary>Opens or folds a section and keeps it.</summary>
    /// <param name="id">The section's id.</param>
    /// <param name="open">Whether it is open.</param>
    /// <returns>Null, or why the fold could not be kept.</returns>
    public string? SetOpen(string id, bool open)
    {
        lock (_gate)
        {
            var sections = Read();
            var changed = open ? sections.Add(id) : sections.Remove(id);
            if (!changed)
            {
                return null;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                JsonObject document = new()
                {
                    ["open"] = new JsonArray([.. sections.Order(StringComparer.Ordinal).Select(name => (JsonNode)name)])
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
        if (_open is not null)
        {
            return _open;
        }

        HashSet<string> sections = new(StringComparer.Ordinal);
        try
        {
            if (File.Exists(_path) && JsonNode.Parse(File.ReadAllText(_path)) is JsonObject document
                                   && document["open"] is JsonArray open)
            {
                foreach (var entry in open)
                {
                    if (entry is JsonValue value && value.TryGetValue(out string? id) && !string.IsNullOrWhiteSpace(id))
                    {
                        sections.Add(id);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Quick Access folds could not be read: {ex.Message}");
        }

        _open = sections;
        return sections;
    }
}
