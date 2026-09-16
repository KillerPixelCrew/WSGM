using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.DeviceLab.Application;

namespace WSGM.DeviceLab.Probes;

/// <summary>Entry point for Device Lab's disposable compatibility-probe self-worker.</summary>
/// <remarks>
/// The request can select only a profile compiled into this assembly. It carries no method, report
/// ID, address, native path, or arbitrary operation, so an imported file cannot turn the host into a
/// generic device-access broker.
/// </remarks>
internal static class ReadProbeWorker
{
    internal const string Mode = "__read-probe";

    private const int MaximumRequestBytes = 262_144;
    private const string Worker = "read-probe worker";
    private static readonly string[] Options = ["--probe", "--request", "--result", "--authorization-handle"];

    internal static int Run(IReadOnlyList<string> args) =>
        SelfWorkerProtocol.Run(args, Worker, Options, RunAsync);

    private static async Task<int> RunAsync(
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        var probeId = options["--probe"];
        var session = await SelfWorkerProtocol.AuthorizeAsync(
            Worker,
            options,
            "probe-request.json",
            "probe-result.json",
            MaximumRequestBytes,
            (stream, token) => JsonSerializer.DeserializeAsync(
                stream,
                DeviceLabJsonContext.Default.ReadProbeWorkerRequest,
                token),
            request => request is null
                || request.SchemaVersion != 1
                || !string.Equals(request.ProbeId, probeId, StringComparison.Ordinal)
                    ? "The read-probe request identity did not match its command envelope."
                    : null,
            request => request.AuthorizationSha256,
            cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return SelfWorkerProtocol.ExitRejected;
        }

        var request = session.Request;
        var mismatch = "The requested probe is not compiled into this Device Lab executable.";
        if (!BuiltInReadProbeRegistry.TryResolve(request.ProbeId, request.ProbeVersion, out var profile)
            || !profile.Descriptor.Matches(request, out mismatch))
        {
            Console.Error.WriteLine(mismatch);
            return SelfWorkerProtocol.ExitRejected;
        }

        var response = await ReadProbeExecutor.ExecuteAsync(
            profile,
            request,
            cancellationToken).ConfigureAwait(false);
        await SelfWorkerProtocol.WriteResultAsync(
            session.ResultPath,
            (stream, token) => JsonSerializer.SerializeAsync(
                stream,
                response,
                DeviceLabJsonContext.Default.ReadProbeWorkerResponse,
                token),
            cancellationToken).ConfigureAwait(false);
        return SelfWorkerProtocol.ExitSuccess;
    }
}
