using System.IO;
using System.Linq;

namespace WSGM.Core;

/// <summary>PCSX2's native portable.txt binds immutable program versions to their retained managed data.</summary>
internal static class Pcsx2Data
{
    internal static bool Managed(EmulatorInstallation installed)
    {
        return installed.Managed && installed.DefinitionId == "pcsx2";
    }

    internal static EmulatorInstallation Normalize(EmulatorInstallation installed)
    {
        if (!Managed(installed))
        {
            return installed;
        }

        var arguments = installed.LaunchArguments;
        var legacy = arguments.Length >= 2 && arguments[0] == "-datapath" && arguments[1] == "{data}";
        if (!legacy && installed.DataPolicy.DataArguments.Length == 0)
        {
            return installed;
        }

        return installed with
        {
            LaunchArguments = legacy ? arguments.Skip(2).ToArray() : arguments,
            DataPolicy = installed.DataPolicy with { DataArguments = [] }
        };
    }

    internal static void Prepare(EmulatorInstallation installed, string? dataPath = null, bool staged = false)
    {
        if (!Managed(installed))
        {
            return;
        }

        var program = Path.GetDirectoryName(Path.GetFullPath(installed.ExecutablePath))!;
        if (!StoragePaths.IsUnder(installed.Root, program))
        {
            throw new InvalidDataException("The managed PCSX2 program is outside its owned installation root.");
        }

        var relative = Path.GetRelativePath(program, Path.GetFullPath(dataPath ?? installed.DataPath));
        if (Path.IsPathRooted(relative))
        {
            throw new IOException("PCSX2's portable data path must be relative to its managed program volume.");
        }

        var marker = Path.Combine(program, "portable.txt");
        if (File.Exists(marker) && File.ReadAllText(marker).Trim() == relative)
        {
            return;
        }

        if (!staged)
        {
            EmulatorPrerequisites.EnsureStopped(installed);
        }

        // Upstream GetPortableModePath reads UTF-8 text and combines it with AppRoot.
        // Do not copy, move or rewrite configuration, memory cards, saves or BIOS files.
        AtomicFile.WriteText(marker, relative, true);
    }
}
