using System;
using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>Read-only registry, file and process observation behind <see cref="RtssDiscovery" />.</summary>
internal interface IRtssDiscoveryEnvironment
{
    /// <summary>Reads the RTSS installation folder from the registry.</summary>
    /// <returns>The registered folder, or null when RTSS is not registered.</returns>
    string? ReadInstallLocation();

    /// <summary>Whether a file exists.</summary>
    /// <param name="path">The file.</param>
    /// <returns>True when it exists.</returns>
    bool FileExists(string path);

    /// <summary>Reads a file's product version from its version resource.</summary>
    /// <param name="path">An existing file.</param>
    /// <returns>The product major, minor and build parts.</returns>
    Version ReadProductVersion(string path);

    /// <summary>Reads the running processes named RTSS.</summary>
    /// <returns>Process snapshots whose image path could be read.</returns>
    IReadOnlyList<RtssProcessIdentity> ReadProcesses();
}
