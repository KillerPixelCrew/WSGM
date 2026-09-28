using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace WSGM.Core;

/// <summary>Where themes live, and the link that lets Steam serve their images.</summary>
/// <remarks>
///     <para>
///         CSS Loader keeps themes under its own folder and links Steam's <c>steamui\themes_custom</c>
///         to it, because a theme's images are referenced as <c>/themes_custom/…</c>, which Steam
///         serves from <c>steamui</c>. WSGM does the same with its own folder.
///     </para>
///     <para>
///         The link is a directory junction where a symbolic link is refused: creating a symbolic
///         link needs a privilege an ordinary user does not have, and a junction does not. A link that
///         already exists and points elsewhere is left alone and reported, since it is another tool's.
///     </para>
/// </remarks>
public static class ThemePaths
{
    /// <summary>The name of the folder Steam serves themes' images from.</summary>
    public const string SteamLinkFolder = "themes_custom";

    /// <summary>The class translations' file name inside the themes folder, as CSS Loader names it.</summary>
    public const string TranslationsFileName = "css_translations.json";

    /// <summary>WSGM's themes folder.</summary>
    public static string DefaultRoot => Path.Combine(Log.Directory, "themes");

    /// <summary>The link inside a Steam installation.</summary>
    /// <param name="steamDirectory">Steam's install directory.</param>
    /// <returns>The link's full path.</returns>
    public static string SteamLinkPath(string steamDirectory)
    {
        return Path.Combine(steamDirectory, "steamui", SteamLinkFolder);
    }

    /// <summary>Whether Steam is on a beta branch, which is what decides the translation table.</summary>
    /// <param name="steamDirectory">Steam's install directory.</param>
    /// <returns>True when <c>package\beta</c> names a branch other than the stable ones CSS Loader knows.</returns>
    public static bool IsSteamBetaActive(string steamDirectory)
    {
        try
        {
            var beta = Path.Combine(steamDirectory, "package", "beta");
            if (!File.Exists(beta))
            {
                return false;
            }

            var content = File.ReadAllText(beta).Trim();
            return content != "steamdeck_stable";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Makes sure Steam's <c>themes_custom</c> leads to the themes folder.</summary>
    /// <param name="steamDirectory">Steam's install directory.</param>
    /// <param name="themesRoot">The themes folder.</param>
    /// <param name="createLink">Creates a link at a path to a target, or null for the real one.</param>
    /// <returns>What was found or done, for the log.</returns>
    public static string EnsureSteamLink(
        string steamDirectory, string themesRoot, Func<string, string, bool>? createLink = null)
    {
        var link = SteamLinkPath(steamDirectory);
        try
        {
            Directory.CreateDirectory(themesRoot);
            var steamUi = Path.GetDirectoryName(link);
            if (steamUi is null || !Directory.Exists(steamUi))
            {
                return $"Steam's steamui folder is absent; {SteamLinkFolder} not linked.";
            }

            if (Directory.Exists(link) || File.Exists(link))
            {
                var attributes = File.GetAttributes(link);
                if ((attributes & FileAttributes.ReparsePoint) == 0)
                {
                    return $"{link} is a real folder, not a link; it was left alone.";
                }

                var target = new DirectoryInfo(link).LinkTarget;
                if (target is not null && SamePath(target, themesRoot))
                {
                    return $"{link} already leads to {themesRoot}.";
                }

                return $"{link} leads to {target ?? "an unknown target"}, another tool's; it was left alone.";
            }

            var created = (createLink ?? CreateLink)(link, themesRoot);
            return created
                ? $"{link} now leads to {themesRoot}."
                : $"{link} could not be linked to {themesRoot}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{link} could not be checked: {ex.Message}";
        }
    }

    private static bool SamePath(string left, string right)
    {
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool CreateLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Info($"Themes: symbolic link refused ({ex.Message}); creating a junction instead.");
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", link, target },
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10000);
            return process.HasExited && process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Themes: junction could not be created: {ex.Message}");
            return false;
        }
    }
}
