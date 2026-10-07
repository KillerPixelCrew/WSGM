using System;
using System.IO;
using System.Linq;
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
        var probeLocal = definition.DataPolicy.ProbeDirectory.Length > 0
            ? Path.Combine(program, definition.DataPolicy.ProbeDirectory)
            : "";
        Directory.CreateDirectory(temporary);
        if (probeLocal.Length > 0)
        {
            Directory.CreateDirectory(probeLocal);
        }

        string Expand(string value)
        {
            return value.Replace("{data}", temporary, StringComparison.Ordinal)
                .Replace("{config}", Path.Combine(temporary, definition.DataPolicy.ConfigFile),
                    StringComparison.Ordinal);
        }

        try
        {
            var arguments = definition.DataPolicy.DataArguments.Select(Expand).Concat(definition.ValidationArguments);
            var environment =
                definition.DataPolicy.Environment.ToDictionary(pair => pair.Key, pair => Expand(pair.Value));
            var result = await ConsoleTool.RunAsync(installed.ExecutablePath,
                LaunchArguments.Join(arguments), cancellationToken: cancellationToken,
                workingDirectory: program, environment: environment).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.ExitCode is not { } exit || !definition.ValidationExitCodes.Contains(exit)
                                                || (definition.ValidationOutput.Length > 0 && !result.Output.Contains(
                                                    definition.ValidationOutput, StringComparison.OrdinalIgnoreCase)))
            {
                throw new IOException(
                    "The staged emulator failed its startup check. The previous version is retained. " + result.Output);
            }
        }
        finally
        {
            EmulatorPackages.DeleteOwned(temporary, installed.Root);
            if (probeLocal.Length > 0)
            {
                EmulatorPackages.DeleteOwned(probeLocal, installed.Root);
            }
        }
    }
}
