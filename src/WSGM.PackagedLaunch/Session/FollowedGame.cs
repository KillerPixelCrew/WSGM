using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
///         Each process is judged once. The verdict is kept for as long as the process stays in the
///         snapshots under the same id, parent and name, so a poll opens no handle to a process it has
///         already judged, accepted or not, and the memory is pruned to the processes still running.
///         A process id is only reused after its process is gone, and a gone process leaves the
///         snapshot and the memory with it; a new process that took over the id, the parent and the
///         name between two polls half a second apart is the one case this does not tell apart.
///     </para>
/// </remarks>
internal sealed class FollowedGame : IGameProcesses
{
    private readonly FollowedGameTarget _target;
    private Dictionary<int, Verdict> _judged = [];

    /// <summary>Creates the recogniser for a follow request.</summary>
    /// <param name="directory">The game's install folder, or empty.</param>
    /// <param name="marker">A path the game's command line carries, or empty.</param>
    /// <param name="markerImages">The runtime images a marker is looked for in.</param>
    /// <param name="launcher">The launcher program, which is never the game.</param>
    internal FollowedGame(string directory, string marker, IReadOnlyList<string> markerImages, string launcher)
    {
        _target = new FollowedGameTarget(
            [.. SpellingsOf(directory).Select(FollowedGameRule.Folder)],
            [.. SpellingsOf(marker, true).Select(spelling => FollowedGameRule.Normalize(spelling).TrimEnd('\\'))],
            markerImages,
            [.. SpellingsOf(launcher).Select(FollowedGameRule.Normalize)]);
        Description = directory.Length > 0
            ? $"a process running from {directory.TrimEnd('\\')}"
            : "the Java process of the instance";
    }

    /// <inheritdoc />
    public string Description { get; }

    /// <inheritdoc />
    public IReadOnlyList<ProcessFacts> Find(IReadOnlyList<ProcessEntry> snapshot)
    {
        Dictionary<int, Verdict> judged = new(snapshot.Count);
        List<ProcessFacts> found = [];
        foreach (var entry in snapshot)
        {
            if (!_judged.TryGetValue(entry.Id, out var verdict)
                || verdict.ParentId != entry.ParentId
                || !string.Equals(verdict.Name, entry.Name, StringComparison.OrdinalIgnoreCase))
            {
                verdict = new Verdict(entry.ParentId, entry.Name, Judge(entry));
            }

            judged[entry.Id] = verdict;
            if (verdict.Facts is { } facts)
            {
                found.Add(facts);
            }
        }

        _judged = judged;
        return found;
    }

    /// <summary>The game's facts when a process is part of it, or null.</summary>
    private ProcessFacts? Judge(ProcessEntry entry)
    {
        return ProcessInspector.ImagePathOf(entry.Id) is { } path
               && FollowedGameRule.Matches(_target, path, () => ProcessInspector.CommandLineOf(entry.Id))
            ? ProcessInspector.Describe(entry.Id, entry.Name)
            : null;
    }

    /// <summary>A path as it was written and as Windows reports it, each once.</summary>
    /// <param name="path">The path, or empty.</param>
    /// <param name="shortForm">
    ///     Whether its 8.3 form counts too: Prism writes an instance folder that way when its name does
    ///     not fit the system code page.
    /// </param>
    /// <remarks>
    ///     A process's image is reported after junctions are resolved, and a launcher may write the
    ///     path with forward slashes, as Qt does, or in its short form. All are looked for.
    /// </remarks>
    private static IEnumerable<string> SpellingsOf(string path, bool shortForm = false)
    {
        if (path.Length == 0)
        {
            return [];
        }

        HashSet<string> spellings = new(StringComparer.OrdinalIgnoreCase) { path };
        if (ProcessInspector.FinalPathOf(path) is { Length: > 0 } final)
        {
            spellings.Add(final);
        }

        if (shortForm && ProcessInspector.ShortPathOf(path) is { Length: > 0 } shortened)
        {
            spellings.Add(shortened);
        }

        return spellings;
    }

    private sealed record Verdict(int ParentId, string Name, ProcessFacts? Facts);
}

/// <summary>What identifies one followed game: where it runs from, what it carries, and what it is not.</summary>
/// <param name="Directories">
///     Its install folder in every spelling that counts, each as <see cref="FollowedGameRule.Folder" />
///     returns it, or none.
/// </param>
/// <param name="Markers">
///     Paths its command line carries, normalized and without a trailing separator, or none.
/// </param>
/// <param name="MarkerImages">The runtime images whose command line is read for a marker.</param>
/// <param name="Launchers">The launcher program in every spelling that counts, normalized.</param>
public sealed record FollowedGameTarget(
    IReadOnlyList<string> Directories,
    IReadOnlyList<string> Markers,
    IReadOnlyList<string> MarkerImages,
    IReadOnlyList<string> Launchers);

/// <summary>The pure rule that says whether a process is a followed game.</summary>
public static class FollowedGameRule
{
    /// <summary>Whether a process is the game.</summary>
    /// <param name="target">What identifies the game.</param>
    /// <param name="imagePath">The process's image path.</param>
    /// <param name="commandLine">Reads its command line, only when a marker needs it.</param>
    /// <returns>
    ///     True for a process running from an install folder, or a marker image whose command line
    ///     names a marker as a whole path. Never the launcher.
    /// </returns>
    public static bool Matches(FollowedGameTarget target, string imagePath, Func<string?> commandLine)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(imagePath);
        ArgumentNullException.ThrowIfNull(commandLine);
        var normalized = Normalize(imagePath);
        if (target.Launchers.Any(launcher => string.Equals(normalized, launcher, StringComparison.Ordinal)))
        {
            return false;
        }

        if (target.Directories.Any(directory =>
                directory.Length > 0 && normalized.StartsWith(directory, StringComparison.Ordinal)))
        {
            return true;
        }

        var image = Path.GetFileName(imagePath);
        if (target.Markers.Count == 0
            || !target.MarkerImages.Any(candidate =>
                string.Equals(candidate, image, StringComparison.OrdinalIgnoreCase))
            || commandLine() is not { Length: > 0 } line)
        {
            return false;
        }

        var arguments = Normalize(line);
        foreach (var marker in target.Markers)
        {
            // The folder itself or something inside it, never a sibling that shares its name as a
            // prefix. A space does not end the path: an instance called Pack must not match one
            // called Pack 2, and a quote, a separator or a classpath semicolon does end it.
            var at = arguments.IndexOf(marker, StringComparison.Ordinal);
            while (at >= 0)
            {
                var end = at + marker.Length;
                if (end == arguments.Length || arguments[end] is '\\' or '"' or ';')
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
