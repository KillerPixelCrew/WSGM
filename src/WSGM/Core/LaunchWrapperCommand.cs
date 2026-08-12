using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace WSGM.Core;

/// <summary>Which behaviours a game's launch wrapper should apply.</summary>
[Flags]
public enum LaunchWrapperMode
{
    /// <summary>No wrapper; the game launches the way Steam normally would.</summary>
    None = 0,

    /// <summary>Run the game at medium integrity under elevated Steam.</summary>
    Deelevate = 1,

    /// <summary>Block Steam Input for the game's lifetime.</summary>
    InputLease = 2,

    /// <summary>Both behaviours in one wrapper process.</summary>
    Both = Deelevate | InputLease,
}

/// <summary>
/// Builds the launch configuration that hands a game to <c>WSGM.Launch.exe</c>.
/// </summary>
/// <remarks>
/// Steam takes two different routes to the same wrapper. A real Steam title uses
/// its launch options, where <c>%command%</c> expands to the game's own command.
/// A non-Steam shortcut cannot: Steam ignores an exe-replacing launch option there
/// and runs the original target anyway (device-verified), so the wrapper goes in
/// the shortcut's Target and the real program moves into its Launch Arguments.
/// </remarks>
internal static class LaunchWrapperCommand
{
    internal const string HelperFileName = "WSGM.Launch.exe";

    /// <summary>Resolves the wrapper beside the running WSGM executable.</summary>
    /// <returns>The absolute path a configured game will reference.</returns>
    internal static string HelperPathForCurrentDeployment()
    {
        var directory = Path.GetDirectoryName(Environment.ProcessPath);
        return Path.Combine(directory ?? Installer.InstallDir, HelperFileName);
    }

    /// <summary>Builds the value written into a real Steam title's launch options.</summary>
    /// <param name="helperPath">Absolute path of the wrapper executable.</param>
    /// <param name="mode">Which wrapper behaviours to enable.</param>
    /// <returns>The launch-option string, quoted for paths containing spaces.</returns>
    /// <exception cref="ArgumentException"><paramref name="helperPath"/> is missing, or
    /// <paramref name="mode"/> selects no behaviour.</exception>
    internal static string SteamLaunchOptions(string helperPath, LaunchWrapperMode mode)
        => $"{Quote(helperPath)} {FlagsFor(mode)} -- %command%";

    /// <summary>Builds the value written into a non-Steam shortcut's Target field.</summary>
    /// <param name="helperPath">Absolute path of the wrapper executable.</param>
    /// <returns>The quoted wrapper path.</returns>
    /// <remarks>
    /// Steam stores this verbatim — it neither adds nor strips quotes — and its own
    /// shortcuts carry the quoted form, so the quotes must be supplied here.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="helperPath"/> is missing.</exception>
    internal static string ShortcutTarget(string helperPath) => Quote(helperPath);

    /// <summary>Builds the value written into a non-Steam shortcut's Launch Arguments.</summary>
    /// <param name="mode">Which wrapper behaviours to enable.</param>
    /// <param name="originalTarget">The shortcut's original Target, quoted or bare.</param>
    /// <param name="originalArguments">The shortcut's original Launch Arguments, if any.</param>
    /// <returns>The wrapper flags, the separator, then the program the shortcut used to run.</returns>
    /// <exception cref="ArgumentException"><paramref name="originalTarget"/> is missing, or
    /// <paramref name="mode"/> selects no behaviour.</exception>
    internal static string ShortcutArguments(
        LaunchWrapperMode mode,
        string originalTarget,
        string? originalArguments)
    {
        if (string.IsNullOrWhiteSpace(originalTarget))
        {
            throw new ArgumentException("An original target is required.", nameof(originalTarget));
        }

        // Steam's own Exe field is already quoted, so re-quoting it would produce a
        // doubly quoted path the wrapper could not resolve. Quote only bare values.
        var target = originalTarget.Trim();
        var command = target.StartsWith('"') ? target : Quote(target);
        return string.IsNullOrWhiteSpace(originalArguments)
            ? $"{FlagsFor(mode)} -- {command}"
            : $"{FlagsFor(mode)} -- {command} {originalArguments.Trim()}";
    }

    /// <summary>Reads back which behaviours a stored launch configuration selects.</summary>
    /// <param name="value">A launch-option or shortcut-argument string, possibly empty.</param>
    /// <returns>The behaviours the value enables, or <see cref="LaunchWrapperMode.None"/>.</returns>
    /// <remarks>
    /// Used to show what a game is already configured with. A value that does not
    /// reference the wrapper reports <see cref="LaunchWrapperMode.None"/> even when
    /// it contains the flag words, so unrelated launch options are never misread.
    /// </remarks>
    internal static LaunchWrapperMode ModeFor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.IndexOf(HelperFileName, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return LaunchWrapperMode.None;
        }

        var mode = LaunchWrapperMode.None;
        if (value.Contains(DeelevateFlag, StringComparison.OrdinalIgnoreCase))
        {
            mode |= LaunchWrapperMode.Deelevate;
        }
        if (value.Contains(InputLeaseFlag, StringComparison.OrdinalIgnoreCase))
        {
            mode |= LaunchWrapperMode.InputLease;
        }
        return mode;
    }

    /// <summary>Whether a shortcut's Target already points at the wrapper.</summary>
    /// <param name="target">The shortcut's current Target value.</param>
    /// <returns>Whether WSGM owns this shortcut's Target.</returns>
    internal static bool TargetsHelper(string? target) =>
        !string.IsNullOrWhiteSpace(target) &&
        target.IndexOf(HelperFileName, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Stops any running wrapper processes.</summary>
    /// <param name="reason">Why they are being stopped, for the log.</param>
    internal static void StopRunningHelpers(string reason)
    {
        foreach (var process in Process.GetProcessesByName(
                     Path.GetFileNameWithoutExtension(HelperFileName)))
        {
            try
            {
                Log.Info($"Stopping launch wrapper pid {process.Id} ({reason}).");
                // The medium child owns the launched game/emulator. Ending its
                // complete tree releases both the wrapper executable and target
                // before an update/uninstall replaces or removes the helper.
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not stop launch wrapper pid {process.Id}: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private const string DeelevateFlag = "--deelevate";
    private const string InputLeaseFlag = "--input-lease";

    private static string Quote(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A helper path is required.", nameof(path));
        }
        return $"\"{path}\"";
    }

    private static string FlagsFor(LaunchWrapperMode mode)
    {
        var flags = new List<string>(2);
        if (mode.HasFlag(LaunchWrapperMode.Deelevate))
        {
            flags.Add(DeelevateFlag);
        }
        if (mode.HasFlag(LaunchWrapperMode.InputLease))
        {
            flags.Add(InputLeaseFlag);
        }
        if (flags.Count == 0)
        {
            throw new ArgumentException("At least one wrapper behaviour is required.", nameof(mode));
        }
        return string.Join(' ', flags);
    }
}
