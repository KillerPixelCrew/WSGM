using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WSGM.DeviceLab.Core;
using WSGM.DeviceLab.Core.Application;
using WSGM.DeviceLab.Core.Capture;
using WSGM.DeviceLab.Core.Inventory;
using WSGM.DeviceLab.Core.Packaging;
using WSGM.DeviceLab.Core.Preflight;
using WSGM.DeviceLab.Core.Probes;
using WSGM.DeviceLab.Core.Scaffolding;

namespace WSGM.DeviceLab.Cli;

/// <summary>Thin command surface over the shared Device Lab application workflows.</summary>
internal static class Program
{
    private const int Success = 0;
    private const int Usage = 64;
    private const int Failed = 70;

    private static readonly JsonSerializerOptions OutputJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            WriteUsage(Console.Out);
            return args.Length == 0 ? Usage : Success;
        }

        try
        {
            return args[0] switch
            {
                "doctor" => RunDoctor(args.AsSpan(1)),
                "inventory" => RunInventory(args.AsSpan(1)),
                "candidates" => RunCandidates(args.AsSpan(1)),
                "probe" => await RunProbeAsync(args[1..]).ConfigureAwait(false),
                "capture" => await RunCaptureAsync(args.AsMemory(1)).ConfigureAwait(false),
                "inspect" => RunInspect(args.AsSpan(1)),
                "diff" => RunDiff(args.AsSpan(1)),
                "correlate" => RunCorrelate(args.AsSpan(1)),
                "fixture" => RunFixture(args.AsSpan(1)),
                "plugin" => RunPlugin(args.AsSpan(1)),
                "glyph" => RunGlyph(args.AsSpan(1)),
                "validate" => RunValidate(args.AsSpan(1)),
                "pack" => RunPack(args.AsSpan(1)),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or JsonException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"Command failed: {exception.Message}");
            return Failed;
        }
    }

    private static int RunDoctor(ReadOnlySpan<string> args)
    {
        if (args.Length != 2 || args[0] is not ("--out-dir" or "-o"))
        {
            return UsageError("doctor requires exactly --out-dir <directory>.");
        }

        DeviceLabDoctorReport report = Application().Doctor(args[1], DateTimeOffset.UtcNow);
        Console.Out.WriteLine(DeviceLabJson.Serialize(report));
        return report.Status is DeviceLabDoctorStatus.Blocked ? Failed : Success;
    }

    private static int RunInventory(ReadOnlySpan<string> args)
    {
        string? output = Option(args, "--out-dir", "-o");
        if (output is null)
        {
            return UsageError("inventory requires --out-dir <directory> and accepts --shareable.");
        }

        DeviceLabInventoryResult result = Application().Inventory(
            output,
            Flag(args, "--shareable"),
            DateTimeOffset.UtcNow);
        if (result.Status is not DeviceLabInventoryStatus.Success || result.Json is null)
        {
            Console.Error.WriteLine($"Inventory failed ({result.Status}): {result.Error}");
            return Failed;
        }

        Console.Out.WriteLine(result.Json);
        Console.Error.WriteLine($"Inventory written to {result.OutputPath}");
        return Success;
    }

    private static int RunCandidates(ReadOnlySpan<string> args)
    {
        string? input = Option(args, "--from", "-f");
        if (input is null)
        {
            return UsageError("candidates requires --from <inventory.json>.");
        }

        WriteJson(Application().Candidates(input, Option(args, "--device-id")));
        return Success;
    }

    private static async Task<int> RunProbeAsync(string[] args)
    {
        if (args.Length == 0)
        {
            return UsageError("probe requires 'known --read-only' or 'run <trial-id>'.");
        }

        if (args[0] is "known")
        {
            ReadOnlySpan<string> options = args.AsSpan(1);
            string? input = Option(options, "--from", "-f");
            if (!Flag(options, "--read-only") || input is null)
            {
                return UsageError("probe known requires --read-only --from <inventory.json>.");
            }

            string host = Option(options, "--probe-host") ?? ProbeHostPath();
            DeviceLabCandidateResult result = new DeviceLabApplication(RepositoryRoot(), host).Candidates(input);
            string? runId = Option(options, "--run");
            if (runId is not null)
            {
                string? output = Option(options, "--out-dir", "-o");
                if (output is null)
                {
                    return UsageError("probe known --run requires --out-dir <directory>.");
                }

                DeviceLabReadProbeExecutionResult execution = await new DeviceLabApplication(
                    RepositoryRoot(),
                    host).RunReadProbeAsync(input, runId, output, CancellationToken.None).ConfigureAwait(false);
                WriteJson(execution);
                return execution.Run?.Status is ReadProbeRunStatus.Accepted ? Success : Failed;
            }

            WriteJson(new
            {
                probes = result.ReadOnlyProbes,
                probeHost = host,
                mode = "reviewed-read-only",
                mutationAuthorized = false,
            });
            return Success;
        }

        if (args[0] is not "run" || args.Length != 2 || args.Contains("--yes", StringComparer.Ordinal))
        {
            return UsageError("probe run requires one reviewed trial ID and never accepts --yes.");
        }

        if (Console.IsInputRedirected || Console.IsOutputRedirected || !Environment.UserInteractive
            || string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("probe run refused: a local interactive terminal is mandatory.");
            return Failed;
        }

        Console.Error.WriteLine($"probe run refused: reviewed trial '{args[1]}' is not installed locally.");
        return Failed;
    }

    private static async Task<int> RunCaptureAsync(ReadOnlyMemory<string> arguments)
    {
        ReadOnlySpan<string> args = arguments.Span;
        if (args.Length == 0 || args[0] is not "run")
        {
            return UsageError("capture requires 'run --recipe <recipe.json> --out-dir <directory>'.");
        }

        ReadOnlySpan<string> options = args[1..];
        string? recipe = Option(options, "--recipe");
        string? output = Option(options, "--out-dir", "-o");
        if (recipe is null || output is null)
        {
            return UsageError("capture run requires --recipe <recipe.json> --out-dir <directory>.");
        }

        bool interactive = Environment.UserInteractive
            && !Console.IsInputRedirected
            && !Console.IsOutputRedirected
            && !string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);
        if (!interactive)
        {
            Console.Error.WriteLine("capture run refused: a local interactive terminal is mandatory.");
            return Failed;
        }

        ObserveOnlyRecipeReview review = Application().ReviewCaptureRecipe(recipe);
        Console.Error.WriteLine("Observe-only capture scope: read-only inventory and locally compiled passive observers only.");
        Console.Error.WriteLine("Unknown observers remain unavailable; imported recipe data cannot open a device or authorize mutation.");
        Console.Error.WriteLine(JsonSerializer.Serialize(review, OutputJson));
        Console.Error.Write("Type OBSERVE to prepare the private session: ");
        if (!string.Equals(Console.ReadLine(), "OBSERVE", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Capture cancelled before observation.");
            return Failed;
        }

        using CancellationTokenSource cancellation = new();
        void handler(object? _, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        }
        Console.CancelKeyPress += handler;
        ObserveOnlyCaptureResult prepared;
        try
        {
            prepared = await Application().PrepareCaptureAsync(
                new ObserveOnlyCaptureRequest
                {
                    RecipePath = recipe,
                    OutputDirectory = output,
                    ReviewedRecipeSha256 = review.RecipeSha256,
                    IsLocalInteractive = true,
                    ObservationScopeConfirmed = true,
                },
                DateTimeOffset.UtcNow,
                cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Capture cancelled. No shareable bundle was written.");
            return Failed;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }

        if (prepared.Status is not ObserveOnlyCaptureStatus.ReadyForExport || prepared.ExportPlan is null)
        {
            Console.Error.WriteLine($"Capture failed ({prepared.Status}): {prepared.Error}");
            return Failed;
        }

        CaptureExportPlan plan = prepared.ExportPlan;
        Console.Error.WriteLine($"Private session: {plan.PrivateWorkingDirectory}");
        Console.Error.WriteLine("Redaction preview:");
        Console.Error.WriteLine(JsonSerializer.Serialize(plan.Redaction, OutputJson));
        Console.Error.Write("Type EXPORT to write the sanitized .wsgmcap, or press Enter to keep it private: ");
        bool exportConfirmed = string.Equals(Console.ReadLine(), "EXPORT", StringComparison.Ordinal);
        CaptureExportResult exported = Application().ExportCapture(plan, exportConfirmed);
        WriteJson(new
        {
            prepared.Status,
            plan.PrivateWorkingDirectory,
            shareableOutputPath = exported.OutputPath,
            exported.Exported,
            exported.Error,
            observationAuthority = "observe-only",
            mutationAuthorized = false,
        });
        return exported.Exported ? Success : Failed;
    }

    private static int RunInspect(ReadOnlySpan<string> args)
    {
        if (args.Length != 1)
        {
            return UsageError("inspect requires one .wsgmcap path.");
        }

        WriteJson(Application().Inspect(args[0]));
        return Success;
    }

    private static int RunDiff(ReadOnlySpan<string> args)
    {
        if (args.Length != 2)
        {
            return UsageError("diff requires two .wsgmcap paths.");
        }

        WriteJson(new
        {
            differences = Application().Diff(args[0], args[1]),
            authority = "comparison-only",
            mutationAuthorized = false,
        });
        return Success;
    }

    private static int RunCorrelate(ReadOnlySpan<string> args)
    {
        if (args.Length == 0)
        {
            return UsageError("correlate requires <capture> --action <id> --sources <id,id>.");
        }

        string? action = Option(args[1..], "--action");
        string? sources = Option(args[1..], "--sources");
        if (action is null || sources is null)
        {
            return UsageError("correlate requires <capture> --action <id> --sources <id,id>.");
        }

        WriteJson(Application().Correlate(
            args[0],
            action,
            sources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal)));
        return Success;
    }

    private static int RunFixture(ReadOnlySpan<string> args)
    {
        if (args.Length == 0 || args[0] is not "extract")
        {
            return UsageError("fixture extract requires --from, --id, and --out-dir.");
        }

        ReadOnlySpan<string> options = args[1..];
        string? from = Option(options, "--from", "-f");
        string? id = Option(options, "--id");
        string? output = Option(options, "--out-dir", "-o");
        if (from is null || id is null || output is null)
        {
            return UsageError("fixture extract requires --from, --id, and --out-dir.");
        }

        WriteJson(Application().ExtractFixture(from, id, output));
        return Success;
    }

    private static int RunPlugin(ReadOnlySpan<string> args)
    {
        if (args.Length == 0 || args[0] is not "scaffold")
        {
            return UsageError("plugin scaffold requires --from and --out-dir.");
        }

        ReadOnlySpan<string> options = args[1..];
        string? from = Option(options, "--from", "-f");
        string? output = Option(options, "--out-dir", "-o");
        if (from is null || output is null)
        {
            return UsageError("plugin scaffold requires --from and --out-dir.");
        }

        ScaffoldGenerationPlan plan = Application().Scaffold(
            from,
            output,
            Option(options, "--publisher") ?? "Unverified Device Lab contributor");
        WriteJson(new
        {
            plan.Output,
            plan.UnavailableCapabilities,
            grantsTrust = false,
            grantsPrivilege = false,
            grantsHardwareVerification = false,
            grantsRetailSupport = false,
        });
        return Success;
    }

    private static int RunValidate(ReadOnlySpan<string> args)
    {
        if (args.Length != 2 || args[0] is not ("offline" or "hardware"))
        {
            return UsageError("validate requires 'offline <dir>' or 'hardware <dir>'.");
        }

        PluginPackageValidationReport report = Application().ValidateOffline(args[1]);
        if (args[0] is "hardware")
        {
            WriteJson(new
            {
                offline = report,
                complete = false,
                requiredTrials = new[]
                {
                    "exact-detection-and-endpoint-binding",
                    "activation-operation-acceptance",
                    "per-resource-restore-and-cleanup",
                },
                lifecycleActivated = false,
                reason = "Requires Developer Mode, a reviewed local acceptance manifest, and explicit interactive execution.",
            });
            return Failed;
        }

        WriteJson(report);
        return report.Valid ? Success : Failed;
    }

    private static int RunGlyph(ReadOnlySpan<string> args)
    {
        if (args.Length < 4 || args[0] is not "import"
            || Option(args[2..], "--out-dir", "-o") is not { } output)
        {
            return UsageError("glyph import requires <package-directory> --out-dir <new-directory>.");
        }

        GlyphPackageGenerationReport report = Application().GenerateGlyphs(args[1], output);
        WriteJson(new
        {
            report,
            output = report.Valid ? Path.GetFullPath(output) : null,
        });
        return report.Valid ? Success : Failed;
    }

    private static int RunPack(ReadOnlySpan<string> args)
    {
        if (args.Length < 3 || Option(args[1..], "--out", "-o") is not { } output)
        {
            return UsageError("pack requires <package-directory> --out <new-package.wsgmpkg>.");
        }

        PluginPackageValidationReport report = Application().Pack(args[0], output);
        WriteJson(new
        {
            validation = report,
            output = report.Valid ? Path.GetFullPath(output) : null,
            grantsTrust = false,
            grantsPrivilege = false,
            grantsHardwareVerification = false,
            grantsRetailSupport = false,
        });
        return report.Valid ? Success : Failed;
    }

    private static string ProbeHostPath() => Path.Combine(AppContext.BaseDirectory, "WSGM.Device.ProbeHost.exe");

    private static DeviceLabApplication Application() => new(RepositoryRoot(), ProbeHostPath());

    private static string? RepositoryRoot() => DeviceLabRepositoryLocator.Find(Environment.CurrentDirectory)
        ?? DeviceLabRepositoryLocator.Find(AppContext.BaseDirectory);

    private static bool Flag(ReadOnlySpan<string> args, string name)
    {
        foreach (string value in args)
        {
            if (value == name)
            {
                return true;
            }
        }

        return false;
    }

    private static string? Option(ReadOnlySpan<string> args, params string[] names)
    {
        for (int index = 0; index + 1 < args.Length; index++)
        {
            if (names.Contains(args[index], StringComparer.Ordinal))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static void WriteJson<T>(T value) => Console.Out.WriteLine(JsonSerializer.Serialize(value, OutputJson));

    private static int UsageError(string message)
    {
        Console.Error.WriteLine(message);
        return Usage;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        WriteUsage(Console.Error);
        return Usage;
    }

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("wsgm-device doctor|inventory|candidates|probe|capture|inspect|diff|correlate|fixture|plugin|glyph|validate|pack");
        writer.WriteLine("Use --help with repository documentation for exact options.");
        writer.WriteLine("All commands except 'probe run' are incapable of hardware mutation.");
    }
}
