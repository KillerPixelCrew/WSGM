using System;
using System.IO;
using System.Linq;

namespace WSGM.Core;

/// <summary>Finds the installed RTSS 7.3 or newer and the process running it.</summary>
/// <remarks>
///     The rule is the reference one: the registry names the installation folder, <c>RTSS.exe</c> and
///     <c>RTSSHooks64.dll</c> must both exist there, the executable's product version must be at least
///     7.3, and the running RTSS is the process whose image is that executable. Nothing is loaded; the
///     adapter loads the profile API once this accepts the installation.
/// </remarks>
internal sealed class RtssDiscovery
{
    /// <summary>The executable inside the installation folder.</summary>
    internal const string ExecutableName = "RTSS.exe";

    /// <summary>The profile API inside the installation folder.</summary>
    internal const string ProfileApiName = "RTSSHooks64.dll";

    private static readonly Version MinimumVersion = new(7, 3);

    private readonly IRtssDiscoveryEnvironment _environment;

    /// <summary>Creates read-only installation discovery.</summary>
    /// <param name="environment">Observation seam, or null for the Windows environment.</param>
    internal RtssDiscovery(IRtssDiscoveryEnvironment? environment = null)
    {
        _environment = environment ?? new WindowsRtssDiscoveryEnvironment();
    }

    /// <summary>Checks the registered installation, its files and its running process without loading the profile API.</summary>
    /// <returns>An availability result; an accepted running process is AdapterUnavailable until an adapter loads the API.</returns>
    internal RtssProbe Probe()
    {
        string? root;
        try
        {
            root = _environment.ReadInstallLocation();
        }
        catch (Exception ex)
        {
            return Failure(RtssAvailability.Degraded, $"RTSS registration read failed: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            return Failure(RtssAvailability.NotInstalled, "RTSS 7.3 or newer is not installed.");
        }

        var executable = Path.Combine(root, ExecutableName);
        var api = Path.Combine(root, ProfileApiName);
        bool executableExists;
        bool apiExists;
        Version? version = null;
        try
        {
            executableExists = _environment.FileExists(executable);
            apiExists = _environment.FileExists(api);
            if (executableExists)
            {
                version = _environment.ReadProductVersion(executable);
            }
        }
        catch (Exception ex)
        {
            return Failure(
                RtssAvailability.Degraded,
                $"RTSS file read failed: {ex.Message}",
                executable: executable);
        }

        if (!executableExists || version is null)
        {
            return Failure(
                RtssAvailability.NotInstalled,
                $"The registered RTSS folder has no {ExecutableName}.",
                executable: executable);
        }

        var versionText = version.ToString();
        if (!apiExists)
        {
            return Failure(
                RtssAvailability.Incompatible,
                $"The registered RTSS installation has no {ProfileApiName}.",
                versionText,
                executable);
        }

        if (version < MinimumVersion)
        {
            return Failure(
                RtssAvailability.Incompatible,
                $"RTSS {versionText} is older than 7.3.",
                versionText,
                executable);
        }

        RtssProcessIdentity[] processes;
        try
        {
            processes =
            [
                .. _environment.ReadProcesses()
                    .Where(process => SamePath(process.ExecutablePath, executable))
                    .Take(2)
            ];
        }
        catch (Exception ex)
        {
            return Failure(
                RtssAvailability.Degraded,
                $"RTSS process identity read failed: {ex.Message}",
                versionText,
                executable);
        }

        if (processes.Length == 0)
        {
            return Failure(
                RtssAvailability.NotRunning,
                "The installed RTSS is not running.",
                versionText,
                executable);
        }

        if (processes.Length != 1)
        {
            return Failure(
                RtssAvailability.Degraded,
                "Multiple processes match the installed RTSS executable.",
                versionText,
                executable);
        }

        var process = processes[0];
        long generation = HashCode.Combine(
            executable.ToUpperInvariant(),
            version,
            process.ProcessId,
            process.StartedAt.UtcDateTime.Ticks);
        generation = generation == 0 ? 1 : generation;
        return new RtssProbe(
            RtssAvailability.AdapterUnavailable,
            versionText,
            executable,
            generation,
            null,
            "The installed RTSS is running.",
            process.ProcessId);
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static RtssProbe Failure(
        RtssAvailability availability,
        string diagnostic,
        string? version = null,
        string? executable = null)
    {
        return new RtssProbe(
            availability,
            version,
            executable,
            0,
            null,
            diagnostic);
    }
}
