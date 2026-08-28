using System;
using System.Collections.Generic;
using System.IO;
using WSGM.DeviceLab.Core;
using WSGM.DeviceLab.Core.Capture;
using WSGM.DeviceLab.Core.Inventory;

namespace WSGM.DeviceLab.Cli;

/// <summary>
/// Entry point for the <c>wsgm-device</c> developer CLI.
/// </summary>
/// <remarks>
/// Every command routes through <c>WSGM.DeviceLab.Core</c>, which the Device Lab GUI also consumes,
/// so a workflow cannot exist in one surface and be missing from the other. Exactly one command
/// mutates hardware — <c>probe run</c> — and it refuses unattended execution; everything else is
/// read-only by construction.
/// </remarks>
internal static class Program
{
    /// <summary>The command completed successfully.</summary>
    private const int ExitSuccess = 0;

    /// <summary>No command was given, or the command is not recognized.</summary>
    private const int ExitUsage = 64;

    /// <summary>The command ran but could not complete its work.</summary>
    private const int ExitFailed = 70;

    /// <summary>
    /// WMI classes whose presence the sweep records.
    /// </summary>
    /// <remarks>
    /// Presence and method signatures only; nothing here is invoked. The thermal-zone class is
    /// included deliberately as a control: it is independently known to require elevation, so
    /// comparing its result against a vendor class distinguishes "this provider is missing" from
    /// "this process is not elevated" without guessing.
    /// </remarks>
    private static readonly (string Namespace, string ClassName)[] ProbedWmiClasses =
    [
        ("root\\WMI", "MSI_ACPI"),
        ("root\\WMI", "MSI_Event"),
        ("root\\WMI", "BatteryStatus"),
        ("root\\WMI", "MSAcpi_ThermalZoneTemperature"),
    ];

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            WriteUsage(Console.Out);
            return args.Length == 0 ? ExitUsage : ExitSuccess;
        }

        return args[0] switch
        {
            "inventory" => RunInventory(args.AsSpan(1)),
            _ => UnknownCommand(args[0]),
        };
    }

    private static int RunInventory(ReadOnlySpan<string> args)
    {
        string? outputPath = null;
        bool shareable = false;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--shareable")
            {
                shareable = true;
            }
            else if (args[i] is "--out" or "-o" && i + 1 < args.Length)
            {
                outputPath = args[i + 1];
            }
        }

        MachineInventory inventory;
        try
        {
            inventory = WindowsInventoryCollector.Collect(DateTimeOffset.UtcNow, ProbedWmiClasses);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"Inventory failed: {ex.Message}");
            return ExitFailed;
        }

        // Unique identifiers live only in the private capture. The shareable view replaces them with
        // stable session-local tokens, so a developer can still follow one device through a sequence
        // without learning whose machine it was.
        if (shareable)
        {
            inventory = InventoryRedaction.ToShareable(inventory, out IReadOnlyList<RedactionSummary> removed);
            foreach (RedactionSummary summary in removed)
            {
                Console.Error.WriteLine($"redacted {summary.Category}: {summary.Occurrences}");
            }
        }

        string json = DeviceLabJson.Serialize(inventory);

        if (outputPath is null)
        {
            Console.Out.WriteLine(json);
            return ExitSuccess;
        }

        // An explicit output path is validated before anything is written. The rejected locations are
        // the ones where a developer running a sweep would destroy something they care about, and the
        // live WSGM directory is first on that list for a reason.
        if (!OutputPathPolicy.IsAcceptable(outputPath, out string? reason))
        {
            Console.Error.WriteLine($"Refusing to write to '{outputPath}': {reason}");
            return ExitFailed;
        }

        // The policy above decides whether a location is *allowed*; the filesystem decides whether it
        // is *writable*, and only trying finds out. A protected or read-only directory is an ordinary
        // outcome for a developer tool that takes an arbitrary path, so it is reported rather than
        // thrown - a stack trace tells the operator nothing they can act on.
        try
        {
            string fullPath = Path.GetFullPath(outputPath);
            string? directory = Path.GetDirectoryName(fullPath);
            if (directory is { Length: > 0 })
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, json);
            Console.Error.WriteLine($"Inventory written to {fullPath}");
            return ExitSuccess;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            Console.Error.WriteLine($"Could not write to '{outputPath}': {ex.Message}");
            return ExitFailed;
        }
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        WriteUsage(Console.Error);
        return ExitUsage;
    }

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("wsgm-device <command> [options]");
        writer.WriteLine();
        writer.WriteLine("  inventory [--out <path>]   Read-only sweep of this machine.");
        writer.WriteLine();
        writer.WriteLine("Results go to stdout and diagnostics to stderr, so one can be piped");
        writer.WriteLine("without the other. No command here can mutate hardware.");
    }
}

/// <summary>
/// Decides whether Device Lab may write to a path.
/// </summary>
/// <remarks>
/// A throwaway probe once destroyed the developer's real <c>config.json</c>. This is the rule that
/// followed, applied before any file is opened rather than trusted to whoever wrote the command.
/// </remarks>
public static class OutputPathPolicy
{
    /// <summary>
    /// Whether a path is an acceptable Device Lab output location.
    /// </summary>
    /// <param name="path">The requested output path.</param>
    /// <param name="reason">Why it was refused, when it was.</param>
    /// <returns><see langword="true"/> when writing there is allowed.</returns>
    public static bool IsAcceptable(string path, out string? reason)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "the path is empty.";
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            reason = "the path is malformed.";
            return false;
        }

        // wsgm-allow-live-data-path: this resolves the live directory in order to refuse it. It is
        // the only place in Device Lab permitted to name it, and it never opens anything there.
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string wsgmData = Path.Combine(localAppData, "WSGM");

        if (IsUnderneath(full, wsgmData))
        {
            reason = "it is inside the live WSGM data directory.";
            return false;
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar),
                profile.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            reason = "it is the user profile root.";
            return false;
        }

        string? root = Path.GetPathRoot(full);
        if (root is not null
            && string.Equals(full.TrimEnd(Path.DirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            reason = "it is a drive root.";
            return false;
        }

        reason = null;
        return true;
    }

    private static bool IsUnderneath(string candidate, string directory)
    {
        string normalizedDirectory = directory.TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return candidate.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                candidate.TrimEnd(Path.DirectorySeparatorChar),
                directory.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }
}
