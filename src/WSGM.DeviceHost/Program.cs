using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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
    /// outcome: DeviceHost is not a diagnostic tool. Device Lab owns its explicit local test
    /// lifecycle and never repurposes this production host as a diagnostic broker.
    /// </remarks>
    private const int ExitInvalidArguments = 64;

    /// <summary>A package failed validation before any plugin code loaded.</summary>
    private const int ExitInvalidPackage = 65;

    /// <summary>The authenticated protocol or supervised runtime failed.</summary>
    private const int ExitRuntimeFault = 70;

    private static async Task<int> Main(string[] args)
    {
        if (!HostArguments.TryParse(args, out HostArguments? arguments, out string error)
            || arguments is null)
        {
            Console.Error.WriteLine(error + Environment.NewLine
                +
                "WSGM.DeviceHost is started by WSGM, not run directly. "
                    + "Required: --package, --package-id, --pipe, --nonce, --session, "
                    + "and --generation.");
            return ExitInvalidArguments;
        }

        PluginPackageMetadata metadata;
        try
        {
            metadata = PluginPackageLoader.ReadMetadata(arguments.PackagePath, arguments.PackageId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"DeviceHost package rejected: {ex.Message}");
            return ExitInvalidPackage;
        }

        using CancellationTokenSource stopping = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stopping.Cancel();
        };

        try
        {
            await using DeviceHostSession session = new(arguments, metadata);
            await session.RunAsync(stopping.Token).ConfigureAwait(false);
            return ExitSuccess;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            return ExitSuccess;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"DeviceHost runtime fault: {ex.GetType().Name}: {ex.Message}");
            return ExitRuntimeFault;
        }
    }
}
