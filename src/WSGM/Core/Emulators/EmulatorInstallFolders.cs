using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace WSGM.Core;

/// <summary>
///     The managed emulators' program and data folders: the immutable program versions, the staging and removed
///     installations left behind, and the user data each of them is copied into before it is deleted.
/// </summary>
/// <remarks>
///     A program version is never deleted before its portable data is preserved under the installation's data
///     folder. A deletion that fails is logged and left for a later cleanup.
/// </remarks>
internal sealed class EmulatorInstallFolders
{
    private readonly string _root;

    /// <summary>Creates the folders' owner for one user's data root.</summary>
    /// <param name="root">The user data root.</param>
    internal EmulatorInstallFolders(string root)
    {
        _root = root;
    }

    /// <summary>Where each managed installation keeps its program versions.</summary>
    internal string ProgramRoot => Path.Combine(_root, "Emulators");

    /// <summary>Where each managed installation keeps its user data.</summary>
    internal string DataRoot => Path.Combine(_root, "EmulatorData");

    /// <summary>Deletes every program version except the current and previous one, preserving each one's data first.</summary>
    /// <param name="installed">The installation.</param>
    /// <param name="token">Cancels the data copy.</param>
    internal void PruneVersions(EmulatorInstallation installed, CancellationToken token)
    {
        var versions = Path.Combine(installed.Root, "versions");
        if (!Directory.Exists(versions))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(versions))
        {
            if (StoragePaths.IsUnder(directory, installed.ExecutablePath)
                || (installed.PreviousExecutablePath.Length > 0 &&
                    StoragePaths.IsUnder(directory, installed.PreviousExecutablePath)))
            {
                continue;
            }

            EmulatorPortableSetup.PreserveVersion(installed, directory, Path.Combine(DataRoot, installed.Id), token);
            DeleteRetired(directory, installed.Root);
        }
    }

    /// <summary>Copies an installation's data and every version's portable data into its retained data folder.</summary>
    /// <param name="installed">The installation about to be removed.</param>
    /// <param name="token">Cancels the copy.</param>
    internal void PreserveVersions(EmulatorInstallation installed, CancellationToken token)
    {
        var retained = Path.Combine(DataRoot, installed.Id);
        EmulatorPortableSetup.CopyData(installed.DataPath, retained, token);
        var versions = Path.Combine(installed.Root, "versions");
        if (!Directory.Exists(versions))
        {
            return;
        }

        foreach (var version in Directory.EnumerateDirectories(versions))
        {
            EmulatorPortableSetup.PreserveVersion(installed, version, retained, token);
        }
    }

    /// <summary>Copies an RPCS3 configuration folder that predates its portable layout into the retained data.</summary>
    /// <param name="installed">The installation.</param>
    /// <param name="retained">The folder the data is copied into.</param>
    /// <param name="token">Cancels the copy.</param>
    internal void PreserveLegacyRpcData(EmulatorInstallation installed, string retained, CancellationToken token)
    {
        if (installed.DefinitionId != "rpcs3" ||
            Directory.Exists(Path.Combine(Path.GetDirectoryName(installed.ExecutablePath)!, "portable")) ||
            !installed.Environment.TryGetValue("RPCS3_CONFIG_DIR", out var setting))
        {
            return;
        }

        var expanded = setting.Replace("{data}", installed.DataPath, StringComparison.Ordinal);
        var actual = Path.EndsInDirectorySeparator(expanded) ? expanded : Path.GetDirectoryName(expanded)!;
        if (!Directory.Exists(actual) || !StoragePaths.IsUnder(DataRoot, actual) ||
            !(File.Exists(Path.Combine(actual, "config.yml")) || Directory.Exists(Path.Combine(actual, "dev_hdd0"))))
        {
            return;
        }

        var otherRoots = EmulatorStorage.ReadInstallations(_root)
            .Where(item => !string.Equals(item.DataPath, actual, StringComparison.OrdinalIgnoreCase) &&
                           StoragePaths.IsUnder(actual, item.DataPath))
            .Select(item => Path.GetRelativePath(actual, item.DataPath).Split(Path.DirectorySeparatorChar)[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        EmulatorPortableSetup.CopyData(actual, retained, token, excludedNames: otherRoots);
    }

    /// <summary>
    ///     Deletes program folders no installation records, after preserving their data, and every staging folder
    ///     an interrupted install left.
    /// </summary>
    /// <param name="managed">The recorded managed installations.</param>
    /// <param name="token">Cancels the data copy.</param>
    internal void SweepOrphans(IReadOnlyCollection<EmulatorInstallation> managed, CancellationToken token)
    {
        if (!Directory.Exists(ProgramRoot))
        {
            return;
        }

        foreach (var root in Directory.EnumerateDirectories(ProgramRoot))
        {
            if (!managed.Any(installed => installed.Root.Equals(root, StringComparison.OrdinalIgnoreCase)))
            {
                EmulatorPortableSetup.CopyData(root,
                    Path.Combine(DataRoot, Path.GetFileName(root), ".portable-snapshots", "removed-installation"),
                    token);
                DeleteRetired(root, ProgramRoot);
                continue;
            }

            foreach (var stage in Directory.EnumerateDirectories(root, "staging-*"))
            {
                DeleteRetired(stage, ProgramRoot);
            }
        }
    }

    /// <summary>Deletes a removed installation's program folder; its data stays.</summary>
    /// <param name="installed">The removed installation.</param>
    internal void DeleteProgram(EmulatorInstallation installed)
    {
        DeleteRetired(installed.Root, ProgramRoot);
    }

    private static void DeleteRetired(string path, string root)
    {
        try
        {
            EmulatorPackages.DeleteOwned(path, root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Emulator files remain for later cleanup: " + ex.Message);
        }
    }
}
