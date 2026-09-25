using System;
using System.IO;

namespace WSGM.Core;

/// <summary>Administrator-protected locations owned by the WSGM installer.</summary>
internal static class DeviceInstallationPaths
{
    internal static string ProtectedRoot
    {
        get
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (string.IsNullOrWhiteSpace(programFiles))
            {
                throw new DirectoryNotFoundException(
                    "Windows did not report the protected Program Files directory.");
            }

            return Path.Combine(programFiles, "WSGM");
        }
    }

    /// <summary>The folder of <c>.wsgmpkg</c> files, device and common alike.</summary>
    internal static string PluginsRoot => Path.Combine(ProtectedRoot, "Plugins");
}
