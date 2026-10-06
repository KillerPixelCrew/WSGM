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
///         points elsewhere, such as the one CSS Loader left behind when its themes were moved to WSGM,
///         is re-pointed: with it, every theme's fonts and images resolve to a folder that no longer has
///         them. Only the link is replaced; the folder it led to is not touched. A real folder in its
///         place is left alone and reported, since it holds someone's files.
///     </para>
/// </remarks>
public static class ThemePaths
{
    /// <summary>The name of the folder Steam serves themes' images from.</summary>
    public const string SteamLinkFolder = "themes_custom";

    /// <summary>The class translations' file name inside the themes folder, as CSS Loader names it.</summary>
    public const string TranslationsFileName = "css_translations.json";

    /// <summary>WSGM's themes folder.</summary>
    /// <param name="context">The owner's explicit user data context.</param>
    /// <returns>The owner's theme directory.</returns>
    public static string DefaultRoot(UserDataContext context)
    {
        return Path.Combine(context.Root, "themes");
    }

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
    /// <returns>What was found or done, for the log.</returns>
    public static string EnsureSteamLink(
        string steamDirectory, string themesRoot)
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

                // Removes the link itself, never what it leads to.
                Directory.Delete(link);
                return CreateLink(link, themesRoot)
                    ? $"{link} led to {target ?? "an unknown target"}; it now leads to {themesRoot}."
                    : $"{link} led to {target ?? "an unknown target"} and could not be linked to {themesRoot}.";
            }

            var created = CreateLink(link, themesRoot);
            return created
                ? $"{link} now leads to {themesRoot}."
                : $"{link} could not be linked to {themesRoot}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{link} could not be checked: {ex.Message}";
        }
    }

    /// <summary>Removes Steam's <c>themes_custom</c> when it is WSGM's link to the themes folder.</summary>
    /// <param name="steamDirectory">Steam's install directory.</param>
    /// <param name="themesRoot">WSGM's themes folder.</param>
    /// <returns>What was found or done, for the log, and whether nothing failed.</returns>
    /// <remarks>
    ///     Uninstall only. The link itself goes, never what it leads to. A real folder, or a link to
    ///     anywhere else, is not WSGM's and stays. WSGM never recorded a link it replaced, so nothing is
    ///     put back in its place.
    /// </remarks>
    public static (string Outcome, bool Succeeded) RemoveSteamLink(string steamDirectory, string themesRoot)
    {
        var link = SteamLinkPath(steamDirectory);
        try
        {
            if (!Directory.Exists(link) && !File.Exists(link))
            {
                return ($"{link} does not exist.", true);
            }

            if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0)
            {
                return ($"{link} is a real folder, not WSGM's link; it was left alone.", true);
            }

            var target = new DirectoryInfo(link).LinkTarget;
            if (target is null || !SamePath(target, themesRoot))
            {
                return ($"{link} leads to {target ?? "an unknown target"}, not WSGM's themes; it was left alone.",
                    true);
            }

            Directory.Delete(link);
            return ($"{link} no longer leads to {themesRoot}.", true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ($"{link} could not be removed: {ex.Message}", false);
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
            return RunJunctionCommand(new ProcessStartInfo("cmd.exe")
                   {
                       ArgumentList = { "/c", "mklink", "/J", link, target },
                       CreateNoWindow = true,
                       UseShellExecute = false,
                       RedirectStandardOutput = true,
                       RedirectStandardError = true
                   }, TimeSpan.FromSeconds(10))
                   && Directory.Exists(link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Themes: junction could not be created: {ex.Message}");
            return false;
        }
    }

    /// <summary>Runs the junction command and stops it when it does not finish in time.</summary>
    /// <param name="start">The command.</param>
    /// <param name="timeout">How long it may run.</param>
    /// <returns>Whether it finished in time and succeeded.</returns>
    internal static bool RunJunctionCommand(ProcessStartInfo start, TimeSpan timeout)
    {
        using var process = Process.Start(start);
        if (process is null)
        {
            return false;
        }

        if (process.WaitForExit(timeout))
        {
            return process.ExitCode == 0;
        }

        try
        {
            process.Kill(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // It ended on its own between the wait and the kill.
        }

        Log.Warn("Themes: mklink did not finish in time and was stopped.");
        return false;
    }
}
