using System;
using System.Collections.Generic;
using System.IO;

namespace WSGM.Core;

/// <summary>The override files Steam plays instead of its own movies.</summary>
/// <remarks>
///     A slot's movie is copied to the name the client asks for under
///     <c>config\uioverrides\movies</c>; a slot on stock has no file there. Animation Changer links
///     rather than copies, but a symbolic link needs a privilege an ordinary user lacks and a file
///     link needs the same volume, and the movies are a few megabytes. A file already holding the
///     movie's bytes is left alone, so applying an unchanged assignment writes nothing.
/// </remarks>
public static class AnimationOverrides
{
    /// <summary>The folder the client serves overrides from.</summary>
    /// <param name="steamDirectory">Steam's install directory.</param>
    public static string Directory(string steamDirectory)
    {
        return Path.Combine(steamDirectory, "config", "uioverrides", "movies");
    }

    /// <summary>Brings the folder in step with an assignment.</summary>
    /// <param name="directory">The override folder.</param>
    /// <param name="assignment">The movie's path per slot, or null for stock.</param>
    /// <returns>Which slots changed, and any problem.</returns>
    public static AnimationApplyReport Apply(string directory, IReadOnlyDictionary<string, string?> assignment)
    {
        List<string> changed = [];
        List<string> errors = [];
        foreach (var slot in AnimationSlots.All)
        {
            var target = Path.Combine(directory, AnimationSlots.FileName(slot));
            var source = assignment.TryGetValue(slot, out var path) ? path : null;
            try
            {
                if (source is null)
                {
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                        changed.Add(slot);
                    }

                    continue;
                }

                if (!File.Exists(source))
                {
                    errors.Add($"{AnimationSlots.Label(slot)}: the movie is missing, so Steam's own plays.");
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                        changed.Add(slot);
                    }

                    continue;
                }

                if (Same(source, target))
                {
                    continue;
                }

                System.IO.Directory.CreateDirectory(directory);
                var temporary = target + ".part";
                File.Copy(source, temporary, true);
                File.Move(temporary, target, true);
                changed.Add(slot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{AnimationSlots.Label(slot)}: {ex.Message}");
            }
        }

        return new AnimationApplyReport(changed, errors);
    }

    /// <summary>Whether the override already holds the movie: the same size and the same bytes.</summary>
    private static bool Same(string source, string target)
    {
        if (!File.Exists(target))
        {
            return false;
        }

        FileInfo left = new(source);
        FileInfo right = new(target);
        if (left.Length != right.Length)
        {
            return false;
        }

        using var a = File.OpenRead(source);
        using var b = File.OpenRead(target);
        var bufferA = new byte[81920];
        var bufferB = new byte[81920];
        while (true)
        {
            var readA = a.ReadAtLeast(bufferA, bufferA.Length, false);
            var readB = b.ReadAtLeast(bufferB, bufferB.Length, false);
            if (readA != readB || !bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB)))
            {
                return false;
            }

            if (readA == 0)
            {
                return true;
            }
        }
    }
}

/// <summary>What applying an assignment did.</summary>
/// <param name="Changed">The slots whose override was written or removed.</param>
/// <param name="Errors">The slots that could not be brought in step, and why.</param>
public sealed record AnimationApplyReport(IReadOnlyList<string> Changed, IReadOnlyList<string> Errors);
