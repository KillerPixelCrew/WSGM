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
    private string? _readFailure;

    /// <summary>Creates the store over the owner's explicit user directory.</summary>
    /// <param name="context">The owner's data context.</param>
    public QuickAccessFolds(UserDataContext context)
        : this(Path.Combine(context.Root, "quick-access-folds.json"))
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
            if (_readFailure is not null)
            {
                _open = null;
                return _readFailure;
            }
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
                _open = null;
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
        _readFailure = null;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(_path)) is not JsonObject document
                || document["open"] is not JsonArray open)
            {
                throw new JsonException("Quick Access folds have an invalid shape.");
            }

            foreach (var entry in open)
            {
                if (entry is JsonValue value && value.TryGetValue(out string? id) && !string.IsNullOrWhiteSpace(id))
                {
                    sections.Add(id);
                }
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Quick Access folds could not be read: {ex.Message}");
            _readFailure = "Quick Access folds could not be read; the stored folds were left unchanged.";
        }

        _open = sections;
        return sections;
    }
}
