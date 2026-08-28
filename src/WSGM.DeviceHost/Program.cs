using System;

namespace WSGM.DeviceHost;

/// <summary>
/// Entry point for the per-package device plugin host.
/// </summary>
/// <remarks>
/// One host process loads exactly one plugin package and is always started by the WSGM device-cycle
/// owner, never by a user. The argument gate below is that rule made executable: without the
/// owner-supplied package path and control-pipe name there is nothing to connect to, and starting
/// hardware acquisition anyway would produce a second, unsupervised owner of the device.
/// </remarks>
internal static class Program
{
    /// <summary>The host completed normally.</summary>
    private const int ExitSuccess = 0;

    /// <summary>Required arguments were missing or malformed.</summary>
    /// <remarks>
    /// Also the exit code a human gets for launching the executable directly, which is the intended
    /// outcome: DeviceHost is not a diagnostic tool. Device Lab observes a running plugin through the
    /// owner's bounded read-only diagnostic session instead of starting its own host.
    /// </remarks>
    private const int ExitInvalidArguments = 64;

    private static int Main(string[] args)
    {
        string? packagePath = null;
        string? pipeName = null;

        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--package":
                    packagePath = args[++i];
                    break;
                case "--pipe":
                    pipeName = args[++i];
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(packagePath) || string.IsNullOrWhiteSpace(pipeName))
        {
            Console.Error.WriteLine(
                "WSGM.DeviceHost is started by WSGM, not run directly. "
                    + "Required: --package <path> --pipe <name>.");
            return ExitInvalidArguments;
        }

        // P4.3 owns the supervised lifecycle: handshake on the control pipe, load the one package,
        // negotiate the capability protocol, then run until the owner asks it to stop.
        return ExitSuccess;
    }
}
