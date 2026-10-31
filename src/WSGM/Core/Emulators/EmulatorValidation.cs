using System;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Reviewed startup checks using the application's owned console runner.</summary>
internal static class EmulatorValidation
{
    public static async Task ProbeAsync(EmulatorPackageDefinition definition, EmulatorInstallation installed,
        CancellationToken cancellationToken)
    {
        var program = Path.GetDirectoryName(installed.ExecutablePath)!;
        var temporary = Path.Combine(installed.Root, "validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);

        try
        {
            var probe = EmulatorPortableSetup.Prepare(installed, temporary, null, cancellationToken, true);
            EmulatorManager.ConfigureData(probe);
            EmulatorPortable.Verify(probe);

            using (var input = File.OpenRead(installed.ExecutablePath))
            using (PEReader image = new(input))
            {
                var architecture = image.PEHeaders.CoffHeader.Machine switch
                {
                    System.Reflection.PortableExecutable.Machine.Amd64 => "x64",
                    System.Reflection.PortableExecutable.Machine.Arm64 => "arm64",
                    System.Reflection.PortableExecutable.Machine.I386 => "x86",
                    _ => ""
                };
                if (image.PEHeaders.PEHeader is not { AddressOfEntryPoint: > 0 } || architecture != installed.Architecture)
                {
                    throw new InvalidDataException("The package has no Windows executable matching the selected architecture.");
                }
            }

            if (definition.ValidationArguments.SequenceEqual(["{portable-layout}"]))
            {
                // Some GUI-only upstreams have no noninteractive version/help command. Validate
                // their PE image and real portable layout without launching a visible application.
                return;
            }

            string Expand(string value)
            {
                return value.Replace("{data}", probe.DataPath, StringComparison.Ordinal)
                    .Replace("{config}", Path.Combine(probe.DataPath, definition.DataPolicy.ConfigFile),
                        StringComparison.Ordinal);
            }

            var arguments = definition.DataPolicy.DataArguments.Select(Expand).Concat(definition.ValidationArguments);
            var environment =
                definition.DataPolicy.Environment.ToDictionary(pair => pair.Key, pair => Expand(pair.Value));
            var result = await ConsoleTool.RunAsync(installed.ExecutablePath,
                LaunchArguments.Join(arguments), cancellationToken: cancellationToken,
                workingDirectory: program, environment: environment).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!Accepted(definition, result.ExitCode, result.Output))
            {
                throw new IOException(
                    "The staged emulator failed its startup check. The previous version is retained. " + result.Output);
            }
        }
        finally
        {
            if (installed.DefinitionId is "pcsx2" or "duckstation" or "dolphin")
            {
                FileCleanup.TryDelete(Path.Combine(program, "portable.txt"));
            }

            var alias = EmulatorPortableProfiles.Alias(installed.DefinitionId);
            if (alias.Length > 0)
            {
                EmulatorPackages.DeleteOwned(Path.Combine(program, alias), installed.Root);
            }

            EmulatorPackages.DeleteOwned(temporary, installed.Root);
        }
    }

    internal static bool Accepted(EmulatorPackageDefinition definition, int? exitCode, string output)
    {
        // PCSX2 returns EXIT_FAILURE after a successful -version print. A command-line error also
        // exits with 1, so its startup banner alone must not turn a refused option into a valid probe.
        return exitCode is { } exit && definition.ValidationExitCodes.Contains(exit)
                                    && (definition.ValidationOutput.Length == 0 || output.Contains(
                                        definition.ValidationOutput,
                                        StringComparison.OrdinalIgnoreCase))
                                    && (definition.Id is not ("pcsx2" or "armsx2") || !output.Contains("Unknown parameter",
                                        StringComparison.OrdinalIgnoreCase));
    }
}
