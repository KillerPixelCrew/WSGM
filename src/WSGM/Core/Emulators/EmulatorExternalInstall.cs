using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace WSGM.Core;

/// <summary>
///     Identifies an emulator the user installed outside WSGM: where it is commonly found, whether an executable is
///     the definition's, and the installation record that uses it without taking ownership of its files or updates.
/// </summary>
internal static class EmulatorExternalInstall
{
    /// <summary>The stable installation id of an external executable.</summary>
    /// <param name="definitionId">The emulator definition.</param>
    /// <param name="executable">The executable; compared by full path, ignoring case.</param>
    /// <returns>The id.</returns>
    internal static string Id(string definitionId, string executable)
    {
        return definitionId + "-external-"
                            + Convert.ToHexString(
                                SHA256.HashData(
                                    Encoding.UTF8.GetBytes(Path.GetFullPath(executable).ToUpperInvariant())))[..20];
    }

    /// <summary>Where an external copy of the emulator is commonly installed, for one of its executable names.</summary>
    /// <param name="definition">The emulator definition.</param>
    /// <param name="executable">One of the definition's executable names.</param>
    /// <returns>The candidate paths, which need not exist: the usual install folders, then App Paths registrations.</returns>
    internal static List<string> Candidates(EmulatorPackageDefinition definition, string executable)
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs",
                definition.Name, executable),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "emudeck",
                "Emulators", definition.Id, executable),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "apps",
                definition.Id, "current", executable)
        }.ToList();
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key =
                hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + executable);
            if (key?.GetValue(null) is string path)
            {
                candidates.Add(path.Trim('"'));
            }
        }

        return candidates;
    }

    /// <summary>Checks that an executable is the definition's and runs on a supported architecture.</summary>
    /// <param name="definition">The emulator definition.</param>
    /// <param name="executable">The full executable path.</param>
    /// <returns>The executable's architecture.</returns>
    /// <exception cref="InvalidDataException">It is not the expected executable or architecture.</exception>
    internal static string Validate(EmulatorPackageDefinition definition, string executable)
    {
        if (!File.Exists(executable) ||
            !definition.ExecutableNames.Contains(Path.GetFileName(executable), StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Choose the expected " + definition.Name + " executable.");
        }

        var architecture = PeArchitecture(executable);
        if (!definition.Architectures.Contains(architecture, StringComparer.Ordinal))
        {
            throw new InvalidDataException("This external emulator architecture is not supported.");
        }

        return architecture;
    }

    /// <summary>The verified installation record for a validated external executable.</summary>
    /// <param name="definition">The emulator definition.</param>
    /// <param name="executable">The full executable path, already validated.</param>
    /// <param name="architecture">Its architecture, as <see cref="Validate" /> answered.</param>
    /// <param name="previous">The record it replaces, whose shortcut preferences are kept; null for a new one.</param>
    /// <returns>The record, with its portable data verified and its prerequisites read.</returns>
    internal static EmulatorInstallation Describe(EmulatorPackageDefinition definition, string executable,
        string architecture, EmulatorInstallation? previous)
    {
        var directory = Path.GetDirectoryName(executable)!;
        var externalData = EmulatorPortable.ReadExternalData(definition.Id, directory);
        var args = definition.DataPolicy.DataArguments.Concat(definition.LaunchArguments).ToArray();
        var version = FileVersionInfo.GetVersionInfo(executable).ProductVersion ?? "External";
        EmulatorInstallation installed = new()
        {
            Id = Id(definition.Id, executable), DefinitionId = definition.Id, Name = definition.Name, Managed = false,
            SteamShortcutEnabled = previous?.SteamShortcutEnabled ?? true,
            SteamShortcutLaunchMode = previous?.SteamShortcutLaunchMode ?? 2,
            Channel = definition.Sources.Keys.First(), Architecture = architecture, Version = version,
            Root = directory, ExecutablePath = executable, DataPath = externalData, OwnsData = false,
            Source = "External", Systems = definition.Systems, LaunchArguments = [.. args],
            DataPolicy = definition.DataPolicy,
            Cores = definition.DataPolicy.HasCores && Directory.Exists(Path.Combine(directory, "cores"))
                ? Directory.EnumerateFiles(Path.Combine(directory, "cores"), "*.dll").Select(path => new EmulatorCore
                {
                    Id = Path.GetFileNameWithoutExtension(path), Name = Path.GetFileNameWithoutExtension(path),
                    Path = path, MetadataMissing = true
                }).ToArray()
                : []
        };
        EmulatorPortable.Verify(installed);
        return EmulatorPrerequisites.RefreshState(installed);
    }

    private static string PeArchitecture(string path)
    {
        using var stream = File.OpenRead(path);
        using PEReader reader = new(stream);
        return reader.PEHeaders.CoffHeader.Machine switch
        {
            Machine.Amd64 => "x64",
            Machine.I386 => "x86",
            Machine.Arm64 => "arm64",
            _ => throw new InvalidDataException("This Windows emulator architecture is unsupported.")
        };
    }
}
