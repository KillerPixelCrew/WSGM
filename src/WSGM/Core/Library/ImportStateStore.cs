using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace WSGM.Core;

/// <summary>Everything WSGM remembers about the entries it created.</summary>
internal sealed class ImportState
{
    /// <summary>One record per generated entry.</summary>
    public List<ImportedEntry> Entries { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ImportState))]
internal sealed partial class ImportStateJsonContext : JsonSerializerContext;

/// <summary>Remembers which Steam entries WSGM created, and from what.</summary>
/// <remarks>
///     <para>
///         The Target and Launch Arguments are stored exactly as they were written, which is what
///         makes "has the user edited this?" an exact comparison rather than a guess.
///     </para>
///     <para>
///         <c>ImportedUtc</c> records when an entry was requested and <c>ConfirmedUtc</c> when its
///         id was actually observed in Steam's library. They are separate because Steam holds
///         shortcuts in memory and writes them out on exit: an entry can be requested and not
///         survive, and nothing should report as persisted on the strength of a request.
///     </para>
/// </remarks>
public sealed class ImportStateStore
{
    /// <summary>More entries than this means something is wrong, not that many games were imported.</summary>
    private const int MaximumEntries = 1024;

    private readonly Lock _gate = new();
    private readonly string _path;
    private ImportState? _state;

    /// <summary>Creates the store over WSGM's own per-user state directory.</summary>
    public ImportStateStore()
        : this(Path.Combine(Log.Directory, "library-import.json"))
    {
    }

    /// <summary>Creates the store over one file.</summary>
    /// <param name="path">Where the state lives.</param>
    public ImportStateStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>Every entry WSGM remembers creating.</summary>
    public IReadOnlyList<ImportedEntry> Entries()
    {
        lock (_gate)
        {
            return [.. Read().Entries];
        }
    }

    /// <summary>Records one entry, replacing any earlier record for the same title.</summary>
    /// <param name="entry">What was created.</param>
    public void Save(ImportedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            var state = Read();
            state.Entries.RemoveAll(existing => Same(existing, entry.Source, entry.Key));
            state.Entries.Add(entry);
            Write(state);
        }
    }

    /// <summary>Forgets one title's record.</summary>
    /// <param name="source">Which source it came from.</param>
    /// <param name="key">Its identity in that source.</param>
    public void Forget(string source, string key)
    {
        lock (_gate)
        {
            var state = Read();
            if (state.Entries.RemoveAll(entry => Same(entry, source, key)) > 0)
            {
                Write(state);
            }
        }
    }

    private static bool Same(ImportedEntry entry, string source, string key)
    {
        return string.Equals(entry.Source, source, StringComparison.OrdinalIgnoreCase)
               && string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase);
    }

    private ImportState Read()
    {
        if (_state is not null)
        {
            return _state;
        }

        try
        {
            if (File.Exists(_path))
            {
                using var stream = File.OpenRead(_path);
                _state = JsonSerializer.Deserialize(stream, ImportStateJsonContext.Default.ImportState);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"The library import state could not be read: {ex.Message}");
        }

        _state ??= new ImportState();
        _state.Entries ??= [];

        // Bounded on load, like every other state file here: a hand-edited or corrupted record must
        // not become a launch command or a removal target.
        _state.Entries =
        [
            .. _state.Entries
                .Where(entry => entry is { Source.Length: > 0 and <= 32, Key.Length: > 0 and <= 512 }
                                && entry.Name.Length <= 256
                                && entry.Target.Length <= 1024
                                && entry.LaunchOptions.Length <= 2048
                                && (entry.Mode == nameof(ImportMode.ControllerOnly)
                                    || entry.Mode == nameof(ImportMode.SteamIntegration)))
                .Take(MaximumEntries)
        ];
        return _state;
    }

    private void Write(ImportState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = _path + ".tmp";
            try
            {
                using (var stream = new FileStream(
                           temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, state, ImportStateJsonContext.Default.ImportState);
                }

                File.Move(temporary, _path, true);
            }
            finally
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception)
                {
                    // A later write reuses the same bounded temporary path.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"The library import state could not be saved: {ex.Message}");
        }
    }
}
