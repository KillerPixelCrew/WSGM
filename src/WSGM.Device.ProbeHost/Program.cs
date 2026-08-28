using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.DeviceLab.Core;
using WSGM.DeviceLab.Core.Probes;

namespace WSGM.Device.ProbeHost;

/// <summary>Entry point for the disposable compatibility-probe host.</summary>
/// <remarks>
/// The request can select only a profile compiled into this assembly. It carries no method, report
/// ID, address, native path, or arbitrary operation, so an imported file cannot turn the host into a
/// generic device-access broker.
/// </remarks>
internal static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitInvalidArguments = 64;
    private const int ExitRejected = 65;
    private const int ExitFailure = 70;
    private const int MaximumRequestBytes = 262_144;

    private static int Main(string[] args)
    {
        if (!TryParseArguments(args, out Arguments? parsed, out string? error))
        {
            Console.Error.WriteLine(error);
            return ExitInvalidArguments;
        }

        try
        {
            return RunAsync(parsed!, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"ProbeHost failed: {exception.Message}");
            return ExitFailure;
        }
    }

    private static async Task<int> RunAsync(Arguments arguments, CancellationToken cancellationToken)
    {
        if (File.Exists(arguments.ResultPath))
        {
            Console.Error.WriteLine("ProbeHost refuses to overwrite an existing result file.");
            return ExitRejected;
        }

        ReadProbeHostRequest? request = await ReadRequestAsync(arguments.RequestPath, cancellationToken)
            .ConfigureAwait(false);
        if (request is null
            || request.SchemaVersion != 1
            || !string.Equals(request.ProbeId, arguments.ProbeId, StringComparison.Ordinal)
            || !string.Equals(request.ImplementationSha256, arguments.ExpectedHash, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("ProbeHost request identity did not match its command envelope.");
            return ExitRejected;
        }

        string processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("ProbeHost executable path is unavailable.");
        string actualHash = ReadProbeHostSupervisor.HashFile(processPath);
        if (!CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(actualHash),
            Convert.FromHexString(arguments.ExpectedHash)))
        {
            Console.Error.WriteLine("ProbeHost executable hash did not match the pinned hash.");
            return ExitRejected;
        }

        string mismatch = "The requested probe is not compiled into this ProbeHost.";
        if (!BuiltInReadProbeRegistry.TryResolve(request.ProbeId, request.ProbeVersion, out IReadProbeProfile profile)
            || !profile.Descriptor.Matches(request, out mismatch))
        {
            Console.Error.WriteLine(mismatch);
            return ExitRejected;
        }

        ReadProbeHostResponse response = await ReadProbeExecutor.ExecuteAsync(
            profile,
            request,
            cancellationToken).ConfigureAwait(false);
        await WriteResultAsync(arguments.ResultPath, response, cancellationToken).ConfigureAwait(false);
        return ExitSuccess;
    }

    private static async Task<ReadProbeHostRequest?> ReadRequestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.Length > MaximumRequestBytes)
        {
            return null;
        }

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync(
            stream,
            DeviceLabJsonContext.Default.ReadProbeHostRequest,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteResultAsync(
        string path,
        ReadProbeHostResponse response,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(
            stream,
            response,
            DeviceLabJsonContext.Default.ReadProbeHostResponse,
            cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool TryParseArguments(
        IReadOnlyList<string> args,
        out Arguments? parsed,
        out string? error)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        for (int index = 0; index < args.Count; index += 2)
        {
            if (index + 1 >= args.Count
                || args[index] is not ("--probe" or "--hash" or "--request" or "--result")
                || !values.TryAdd(args[index], args[index + 1]))
            {
                parsed = null;
                error = "ProbeHost requires exactly --probe, --hash, --request, and --result once each.";
                return false;
            }
        }

        if (values.Count != 4
            || !values.TryGetValue("--probe", out string? probeId)
            || !values.TryGetValue("--hash", out string? expectedHash)
            || !values.TryGetValue("--request", out string? requestPath)
            || !values.TryGetValue("--result", out string? resultPath)
            || string.IsNullOrWhiteSpace(probeId)
            || expectedHash.Length != 64
            || expectedHash.Any(character => !Uri.IsHexDigit(character))
            || !File.Exists(requestPath)
            || string.IsNullOrWhiteSpace(resultPath))
        {
            parsed = null;
            error = "ProbeHost arguments were incomplete or malformed.";
            return false;
        }

        parsed = new Arguments(probeId, expectedHash, requestPath, resultPath);
        error = null;
        return true;
    }

    private sealed record Arguments(
        string ProbeId,
        string ExpectedHash,
        string RequestPath,
        string ResultPath);
}
