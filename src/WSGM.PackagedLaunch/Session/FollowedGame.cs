using System;
using System.Collections.Generic;
using System.IO;

namespace WSGM.PackagedLaunch;

/// <summary>Recognises the processes of a game another launcher started.</summary>
/// <remarks>
///     <para>
///         A launcher's game is not this process's child, and its process id is not known in
///         advance: the launcher starts it when it is ready, sometimes after an update or a sign-in,
///         sometimes through a bootstrapper. It is recognised by where it runs from - any process
///         whose image is inside the game's install folder - or, for a game that runs from a shared
///         runtime, by a path its command line carries: a Minecraft instance's folder on a Java
///         command line. <see cref="FollowedGameRule" /> is that rule.
///     </para>
///     <para>
///         A process already rejected is remembered by id and start time, so discovery reads each
///         process once rather than on every poll.
///     </para>
/// </remarks>
internal sealed class FollowedGame
{
    private readonly Func<int, string?> _commandLine;
    private readonly Func<int, ProcessFacts?> _describe;
    private readonly string _directory;
    private readonly Func<int, string?> _imagePath;
    private readonly string _launcher;
    private readonly IReadOnlyList<string> _markers;
    private readonly Dictionary<int, DateTime?> _rejected = [];

    /// <summary>Creates the recogniser over the real process readers.</summary>
    /// <param name="directory">The game's install folder, or empty.</param>
    /// <param name="marker">A path the game's command line carries, or empty.</param>
    /// <param name="launcher">The launcher program, which is never the game.</param>
    internal FollowedGame(string directory, string marker, string launcher)
    {
        _directory = FollowedGameRule.Folder(directory);
        _launcher = FollowedGameRule.Normalize(launcher);
        _imagePath = ProcessInspector.ImagePathOf;
        _commandLine = ProcessInspector.CommandLineOf;
        _describe = id => ProcessInspector.Describe(id);

        // A launcher may write the path with forward slashes, as Qt does, or in its 8.3 form, as
        // Prism does for a folder whose name the system code page cannot hold. All are looked for.
        List<string> markers = [];
        if (marker.Length > 0)
        {
            markers.Add(FollowedGameRule.Normalize(marker).TrimEnd('\\'));
            if (ProcessInspector.ShortPathOf(marker) is { Length: > 0 } shortened)
            {
                markers.Add(FollowedGameRule.Normalize(shortened).TrimEnd('\\'));
            }
        }

        _markers = markers;
    }

    /// <summary>What the session is waiting for, for the log.</summary>
    internal string Description => _directory.Length > 0
        ? $"a process running from {_directory.TrimEnd('\\')}"
        : "the Java process of the instance";

    /// <summary>The game's facts when a process is part of it, or null.</summary>
    /// <param name="entry">One process from a snapshot.</param>
    internal ProcessFacts? Identify(ProcessEntry entry)
    {
        if (_rejected.TryGetValue(entry.Id, out var started))
        {
            if (started == ProcessInspector.StartedAt(entry.Id))
            {
                return null;
            }

            _rejected.Remove(entry.Id);
        }

        if (_imagePath(entry.Id) is { } path
            && FollowedGameRule.Matches(_directory, _markers, _launcher, path, () => _commandLine(entry.Id)))
        {
            return _describe(entry.Id);
        }

        _rejected[entry.Id] = ProcessInspector.StartedAt(entry.Id);
        return null;
    }
}

/// <summary>The pure rule that says whether a process is a followed game.</summary>
public static class FollowedGameRule
{
    private static readonly string[] JavaImages = ["java.exe", "javaw.exe"];

    /// <summary>Whether a process is the game.</summary>
    /// <param name="directory">The game's install folder as <see cref="Folder" /> returns it, or empty.</param>
    /// <param name="markers">Normalized paths the game's command line carries, or none.</param>
    /// <param name="launcher">The launcher program, normalized, which is never the game.</param>
    /// <param name="imagePath">The process's image path.</param>
    /// <param name="commandLine">Reads its command line, only when a marker needs it.</param>
    /// <returns>True for a process running from the folder, or a Java process naming a marker.</returns>
    public static bool Matches(
        string directory, IReadOnlyList<string> markers, string launcher, string imagePath,
        Func<string?> commandLine)
    {
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(commandLine);
        var normalized = Normalize(imagePath);
        if (string.Equals(normalized, launcher, StringComparison.Ordinal))
        {
            return false;
        }

        if (directory.Length > 0 && normalized.StartsWith(directory, StringComparison.Ordinal))
        {
            return true;
        }

        if (markers.Count == 0
            || Array.IndexOf(JavaImages, Path.GetFileName(imagePath).ToLowerInvariant()) < 0
            || commandLine() is not { Length: > 0 } line)
        {
            return false;
        }

        var arguments = Normalize(line);
        foreach (var marker in markers)
        {
            // The folder itself or something inside it, never a sibling that shares its name as a
            // prefix: an instance called Pack must not match one called Pack 2.
            var at = arguments.IndexOf(marker, StringComparison.Ordinal);
            while (at >= 0)
            {
                var end = at + marker.Length;
                if (end == arguments.Length || arguments[end] is '\\' or '"' or ' ' or ';')
                {
                    return true;
                }

                at = arguments.IndexOf(marker, end, StringComparison.Ordinal);
            }
        }

        return false;
    }

    /// <summary>A path in the one form the rule compares: backslashes, upper case.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The normalized path.</returns>
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Replace('/', '\\').ToUpperInvariant();
    }

    /// <summary>A folder in the form <see cref="Matches" /> takes: normalized, ending in a separator.</summary>
    /// <param name="directory">The folder, or empty.</param>
    /// <returns>The folder, or empty.</returns>
    public static string Folder(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return directory.Length == 0 ? string.Empty : Normalize(directory).TrimEnd('\\') + "\\";
    }
}
