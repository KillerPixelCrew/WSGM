using System;
using System.Globalization;
using System.IO;

namespace WSGM.Core;

/// <summary>The override file Steam plays instead of its own boot movie.</summary>
/// <remarks>
///     <para>
///         Steam's client serves <c>/uioverrides/&lt;path&gt;</c> from <c>config\uioverrides</c> and
///         HEAD-requests the override of its startup movie before falling back to its own copy
///         (<c>steamui</c> bundle, the overrideable-resource hook; live on 2026-09-28). Everywhere but
///         SteamOS the movie it asks for is <c>/movies/bigpicture_startup.webm</c>, so that is the
///         override name on Windows. The lookup is cached for the life of the document, so an override
///         written while Steam runs shows at the next Steam start, and Steam's own Startup Movie
///         setting must be the default for the override to be asked for at all.
///     </para>
///     <para>
///         The movie is copied, not linked as Animation Changer links it: a symbolic link needs a
///         privilege an ordinary user lacks, and the movies are a few megabytes. A copy keeps the
///         source's size and write time, so a file the last apply left is recognized by both and left
///         alone.
///     </para>
///     <para>
///         WSGM removes only an override it wrote. A marker beside the override records the size and
///         write time of WSGM's copy; a file that does not match it is someone else's, put there by
///         hand or by another tool. Choosing a movie sets such a file aside under
///         <see cref="OriginalSuffix" /> and returning to Steam's own puts it back, so WSGM never
///         deletes a movie it did not write.
///     </para>
///     <para>
///         Steam's suspend movies are overridable the same way, but nothing on Windows drives Steam's
///         suspend flow: the power button delivers one press edge and no release (#116), so WSGM
///         sleeps Windows directly (#21) and Steam never plays them. Only the boot movie is offered.
///     </para>
/// </remarks>
public static class AnimationOverrides
{
    /// <summary>The name the Windows client asks the override route for.</summary>
    public const string BootFileName = "bigpicture_startup.webm";

    /// <summary>The suffix of the marker naming WSGM's copy.</summary>
    public const string MarkerSuffix = ".wsgm";

    /// <summary>The suffix under which someone else's override is kept while WSGM's plays.</summary>
    public const string OriginalSuffix = ".wsgm-original";

    /// <summary>The folder the client serves movie overrides from.</summary>
    /// <param name="steamDirectory">Steam's install directory.</param>
    /// <returns>The folder.</returns>
    public static string Directory(string steamDirectory)
    {
        return Path.Combine(steamDirectory, "config", "uioverrides", "movies");
    }

    /// <summary>Brings the boot override in step with a choice.</summary>
    /// <param name="directory">The override folder.</param>
    /// <param name="source">The movie's path, or null for Steam's own.</param>
    /// <returns>Whether the override changed, and any problem.</returns>
    public static AnimationApplyReport Apply(string directory, string? source)
    {
        var target = Path.Combine(directory, BootFileName);
        var marker = target + MarkerSuffix;
        var original = target + OriginalSuffix;
        try
        {
            if (source is null || !File.Exists(source))
            {
                var changed = false;
                if (Owned(target, marker))
                {
                    File.Delete(target);
                    changed = true;
                }

                File.Delete(marker);
                // What WSGM set aside comes back once WSGM's copy is gone, and only then: a file
                // someone put there since stays theirs.
                if (!File.Exists(target) && File.Exists(original))
                {
                    File.Move(original, target);
                    changed = true;
                }

                return new AnimationApplyReport(changed,
                    source is null ? null : "The chosen movie is missing, so Steam's own plays.");
            }

            var owned = Owned(target, marker);
            if (owned && Same(source, target))
            {
                return new AnimationApplyReport(false, null);
            }

            System.IO.Directory.CreateDirectory(directory);
            if (!owned && File.Exists(target))
            {
                File.Move(target, original, true);
            }

            var temporary = target + ".part";
            try
            {
                File.Copy(source, temporary, true);
                File.Move(temporary, target, true);
            }
            catch
            {
                File.Delete(temporary);
                throw;
            }

            FileInfo written = new(target);
            File.WriteAllText(marker, Stamp(written));
            return new AnimationApplyReport(true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AnimationApplyReport(false, ex.Message);
        }
    }

    /// <summary>Whether the override is the copy WSGM last wrote, as its marker records it.</summary>
    private static bool Owned(string target, string marker)
    {
        FileInfo file = new(target);
        return file.Exists && File.Exists(marker) && File.ReadAllText(marker).Trim() == Stamp(file);
    }

    private static string Stamp(FileInfo file)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{file.Length}|{file.LastWriteTimeUtc.Ticks}");
    }

    /// <summary>Whether the override already holds the movie: the same size and the same write time.</summary>
    private static bool Same(string source, string target)
    {
        FileInfo from = new(source);
        FileInfo to = new(target);
        return to.Exists && from.Length == to.Length && from.LastWriteTimeUtc == to.LastWriteTimeUtc;
    }
}

/// <summary>What applying a choice did.</summary>
/// <param name="Changed">Whether the override was written, removed or given back.</param>
/// <param name="Error">Why it could not be brought in step, or null.</param>
public sealed record AnimationApplyReport(bool Changed, string? Error);
