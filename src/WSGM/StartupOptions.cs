using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM;

internal sealed record StartupOptions(
    RunMode Mode,
    bool ServiceBoot,
    bool DesktopResident,
    bool Activate,
    bool Verbose)
{
    internal static StartupOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        HashSet<string> flags = new(args, StringComparer.OrdinalIgnoreCase);
        var serviceBoot = flags.Contains("--boot");
        var mode = flags.Contains("--shell") || serviceBoot
            ? RunMode.Shell
            : flags.Contains("--settings")
                ? RunMode.Settings
                : flags.Contains("--overlay-test") ? RunMode.OverlayTest : RunMode.Settings;
        return new StartupOptions(mode, serviceBoot, flags.Contains("--desktop-resident"),
            flags.Contains("--activate"), flags.Contains("--verbose"));
    }

    internal static string? ArgumentValue(string[] args, string prefix)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Where(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(argument => argument[prefix.Length..])
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
