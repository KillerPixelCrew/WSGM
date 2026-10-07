using System;
using System.IO;

namespace WSGM.Core;

/// <summary>Steam-native fields for a custom launch action.</summary>
/// <param name="LaunchOptions">Launch Options for a regular Steam title.</param>
/// <param name="ShortcutTarget">Target for a non-Steam shortcut.</param>
/// <param name="ShortcutArguments">Launch Arguments for a non-Steam shortcut.</param>
internal readonly record struct SteamCustomLaunchFields(
    string LaunchOptions,
    string ShortcutTarget,
    string ShortcutArguments);

/// <summary>Builds Steam-native custom launch commands without a WSGM wrapper.</summary>
internal static class SteamCustomLaunchCommand
{
    /// <summary>Builds regular-game and shortcut launch fields for an EXE or supported script.</summary>
    /// <param name="path">Nonblank EXE, CMD, BAT, or PS1 target path.</param>
    /// <param name="customArguments">Optional single-line arguments; whitespace is trimmed.</param>
    /// <param name="commandProcessor">Optional CMD interpreter override; null resolves ComSpec or System32.</param>
    /// <param name="powerShell">Optional PowerShell executable override; null resolves Windows PowerShell.</param>
    /// <returns>Steam launch fields retaining the regular-title %command% placeholder.</returns>
    /// <exception cref="ArgumentException">The target is blank/unsupported or arguments contain NUL/newline characters.</exception>
    internal static SteamCustomLaunchFields Build(
        string path, string? customArguments, string? commandProcessor = null,
        string? powerShell = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var selected = Quote(path);
        var arguments = NormalizeArguments(customArguments);
        var suffix = arguments.Length == 0 ? "" : " " + arguments;

        return extension switch
        {
            ".exe" => new SteamCustomLaunchFields($"{selected}{suffix} %command%", selected, arguments),
            ".cmd" or ".bat" => BuildScript(
                Quote(commandProcessor ?? ResolveCommandProcessor()),
                $"/d /s /c call {selected}{suffix}"),
            ".ps1" => BuildScript(
                Quote(powerShell ?? ResolvePowerShell()),
                $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File {selected}{suffix}"),
            _ => throw new ArgumentException("Select an EXE, CMD, BAT, or PS1 file.", nameof(path))
        };
    }

    /// <summary>Checks the custom-action extension without testing existence or trust.</summary>
    /// <param name="path">Target path to classify.</param>
    /// <returns>True for EXE, CMD, BAT, or PS1, case-insensitively.</returns>
    internal static bool IsSupported(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase);
    }

    private static SteamCustomLaunchFields BuildScript(string host, string arguments)
    {
        return new SteamCustomLaunchFields($"{host} {arguments} %command%", host, arguments);
    }

    private static string NormalizeArguments(string? arguments)
    {
        var value = arguments?.Trim() ?? "";
        return value.IndexOfAny(['\0', '\r', '\n']) >= 0
            ? throw new ArgumentException("Custom arguments must be a single line.", nameof(arguments))
            : value;
    }

    private static string ResolveCommandProcessor()
    {
        return Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } path
            ? path
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
    }

    private static string ResolvePowerShell()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    /// <summary>
    ///     Always-wrapping quote for Steam-facing command strings (Launch Options and
    ///     shortcut Target fields), shared with the launch-wrapper command builder. Distinct from
    ///     <see cref="SelfElevation.Quote" />, which quotes conditionally for argv round-trips.
    /// </summary>
    /// <param name="value">Non-null Steam-facing field value.</param>
    /// <returns>The always-quoted value with embedded double quotes escaped.</returns>
    internal static string Quote(string value)
    {
        return $"\"{value.Replace("\"", "\\\"")}\"";
    }
}
