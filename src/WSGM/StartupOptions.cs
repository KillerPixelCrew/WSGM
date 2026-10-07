using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM;

/// <summary>Immutable mode selection and process-local modifiers; maintenance one-shots are handled by Program.</summary>
/// <param name="Mode">The selected application lifetime.</param>
/// <param name="ServiceBoot">Whether --boot requests the unconditional Game Mode boot takeover.</param>
/// <param name="DesktopResident">Whether a non-boot shell starts without taking over Explorer.</param>
/// <param name="Activate">Whether a shell launch signals overlay activation, including an existing resident.</param>
/// <param name="Verbose">Whether debug logging overrides the stored setting for this process.</param>
internal sealed record StartupOptions(
    RunMode Mode,
    bool ServiceBoot,
    bool DesktopResident,
    bool Activate,
    bool Verbose)
{
    /// <summary>Suppresses common-plugin CEF frontend injection for this process only.</summary>
    internal bool CefPluginsOff { get; init; }

    /// <summary>Selects Shell before Settings before OverlayTest; no mode flag selects Settings.</summary>
    /// <param name="args">Arguments compared case-insensitively; unrelated flags remain for the entry point.</param>
    /// <returns>The selected mode and independent modifiers, without performing any startup action.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args" /> is null.</exception>
    internal static StartupOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        HashSet<string> flags = new(args, StringComparer.OrdinalIgnoreCase);
        var serviceBoot = flags.Contains("--boot");
        var mode = flags.Contains("--shell") || serviceBoot
            ? RunMode.Shell
            : flags.Contains("--settings")
                ? RunMode.Settings
                : flags.Contains("--overlay-test")
                    ? RunMode.OverlayTest
                    : RunMode.Settings;
        return new StartupOptions(mode, serviceBoot, flags.Contains("--desktop-resident"),
                flags.Contains("--activate"), flags.Contains("--verbose"))
            { CefPluginsOff = flags.Contains("--cef-plugins-off") };
    }

    /// <summary>Finds the first nonblank value attached to a case-insensitive argument prefix.</summary>
    /// <param name="args">The complete argument array.</param>
    /// <param name="prefix">The option prefix, including its separator, such as --answers=.</param>
    /// <returns>The unchanged suffix, or null when no nonblank value was supplied.</returns>
    internal static string? ArgumentValue(string[] args, string prefix)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Where(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(argument => argument[prefix.Length..])
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
