using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace WSGM.Core;

/// <summary>Supplies local BIOS and firmware data through each emulator's native paths and installation rules.</summary>
internal static class EmulatorPrerequisites
{
    internal static void EnsureStopped(EmulatorInstallation installed)
    {
        var name = Path.GetFileNameWithoutExtension(installed.ExecutablePath);
        if (name.Length == 0)
        {
            return;
        }

        var processes = Process.GetProcessesByName(name);
        try
        {
            if (processes.Length > 0)
            {
                throw new InvalidOperationException(
                    "Close " + installed.Name + " before changing its BIOS or firmware.");
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public static EmulatorInstallation Configure(EmulatorInstallation installed, string path, string kind,
        CancellationToken cancellationToken)
    {
        EnsureStopped(installed);
        var requirement = installed.DataPolicy.Prerequisites.SingleOrDefault(item => item.Kind == kind)
                          ?? throw new InvalidOperationException("This emulator does not support that setup action.");
        path = Path.GetFullPath(path);
        if (!File.Exists(path) && !(requirement.AllowDirectory && Directory.Exists(path)))
        {
            throw new FileNotFoundException("Choose an existing local prerequisite file or supported folder.", path);
        }

        if (File.Exists(path) && requirement.Extensions.Length > 0
                              && !requirement.Extensions.Contains(Path.GetExtension(path),
                                  StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("That prerequisite file type is not supported.");
        }

        Directory.CreateDirectory(installed.DataPath);
        if (requirement.NativeInstaller)
        {
            if (installed.DefinitionId == "eden")
            {
                EmulatorFirmware.InstallEden(installed, requirement, path, cancellationToken);
            }
            else
            {
                ProcessStartInfo start = new(installed.ExecutablePath)
                {
                    WorkingDirectory = Path.GetDirectoryName(installed.ExecutablePath)!,
                    UseShellExecute = false
                };
                foreach (var argument in requirement.InstallerArguments)
                {
                    start.ArgumentList.Add(argument.Replace("{source}", path, StringComparison.Ordinal));
                }

                foreach (var (key, value) in installed.Environment)
                {
                    start.Environment[key] = value.Replace("{data}", installed.DataPath, StringComparison.Ordinal);
                }

                using var process = Process.Start(start)
                                    ?? throw new InvalidOperationException(
                                        "The emulator configuration could not be opened.");
                process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        "The emulator firmware installer failed. Recheck setup before retrying.");
                }
            }
        }
        else
        {
            var destination = EmulatorStorage.PrerequisitePath(installed, requirement);
            Directory.CreateDirectory(destination);
            if (Directory.Exists(path) && StoragePaths.IsUnder(path, destination)
                                       && !Path.GetFullPath(destination)
                                           .Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Choose the prerequisite folder itself, not a parent containing its destination.");
            }

            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (requirement.Extensions.Length > 0 && !requirement.Extensions.Contains(Path.GetExtension(file),
                            StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Copy(file, Path.Combine(destination, Path.GetRelativePath(path, file)));
                }
            }
            else
            {
                Copy(path, Path.Combine(destination, Path.GetFileName(path)));
            }

            if (requirement.IniFile.Length > 0)
            {
                IniFile.SetValue(Path.Combine(installed.DataPath, requirement.IniFile), requirement.IniKey,
                    destination, requirement.IniSection);
            }
        }

        return installed with
        {
            ConfiguredPrerequisites = new Dictionary<string, string>(installed.ConfiguredPrerequisites)
            {
                [kind] = EmulatorStorage.PrerequisitePath(installed, requirement)
            }
        };
    }

    private static void Copy(string source, string destination)
    {
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = File.OpenRead(source);
        AtomicFile.Write(destination, output =>
        {
            input.CopyTo(output);
            return true;
        }, true);
    }

    public static EmulatorInstallation RefreshState(EmulatorInstallation installed)
    {
        var missing = EmulatorStorage.MissingPrerequisites(installed);
        return installed.MissingRequirements.SequenceEqual(missing)
            ? installed
            : installed with { MissingRequirements = missing };
    }
}
