using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.DeviceLab.Application;

/// <summary>A worker request that passed session, request and supervisor authorization checks.</summary>
/// <param name="Request">The validated request.</param>
/// <param name="ResultPath">The constrained create-new result file.</param>
internal sealed record SelfWorkerSession<TRequest>(TRequest Request, string ResultPath);

/// <summary>The command envelope shared by Device Lab's disposable self-workers.</summary>
/// <remarks>
/// Each worker takes one value per option, proves through the inherited one-use secret that its
/// supervisor started it, reads its request from the constrained session directory, and writes one
/// create-new result. Refusals go to standard error under the worker's own label.
/// </remarks>
internal static class SelfWorkerProtocol
{
    internal const int ExitSuccess = 0;
    private const int ExitInvalidArguments = 64;
    internal const int ExitRejected = 65;
    private const int ExitFailure = 70;

    private const int MaximumMessageLength = 16_384;
    private static readonly TimeSpan AuthorizationDeadline = TimeSpan.FromSeconds(5);

    /// <summary>Parses the options and runs the worker, mapping failures to Device Lab exit codes.</summary>
    /// <param name="args">Arguments after the worker mode.</param>
    /// <param name="worker">Label used in diagnostics, for example "plugin worker".</param>
    /// <param name="options">Options that must each appear exactly once with a value.</param>
    /// <param name="run">The worker body.</param>
    /// <returns>The worker's exit code.</returns>
    internal static int Run(
        IReadOnlyList<string> args,
        string worker,
        IReadOnlyList<string> options,
        Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<int>> run)
    {
        if (!TryParseOptions(args, worker, options, out var values, out var error))
        {
            Console.Error.WriteLine(error);
            return ExitInvalidArguments;
        }

        try
        {
            return run(values!, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Device Lab {worker} failed: {Bound(exception.Message)}");
            return ExitFailure;
        }
    }

    /// <summary>Authorizes a worker session and reads its request.</summary>
    /// <param name="worker">Label used in diagnostics.</param>
    /// <param name="options">Parsed options holding --request, --result and --authorization-handle.</param>
    /// <param name="requestFileName">Required request file name inside the session directory.</param>
    /// <param name="resultFileName">Required result file name inside the session directory.</param>
    /// <param name="maximumRequestBytes">Upper bound for the request file.</param>
    /// <param name="deserialize">Reads the request from its stream.</param>
    /// <param name="rejectRequest">Returns a refusal for a missing or malformed request, otherwise null.</param>
    /// <param name="authorizationSha256">Selects the supervisor's secret hash from the request.</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    /// <returns>The authorized session, or null after writing the refusal.</returns>
    internal static async Task<SelfWorkerSession<TRequest>?> AuthorizeAsync<TRequest>(
        string worker,
        IReadOnlyDictionary<string, string> options,
        string requestFileName,
        string resultFileName,
        long maximumRequestBytes,
        Func<Stream, CancellationToken, ValueTask<TRequest?>> deserialize,
        Func<TRequest?, string?> rejectRequest,
        Func<TRequest, string?> authorizationSha256,
        CancellationToken cancellationToken)
        where TRequest : class
    {
        byte[]? secret;
        using (var authorization =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            authorization.CancelAfter(AuthorizationDeadline);
            secret = await SelfWorkerAuthorization.ReadSecretAsync(
                options["--authorization-handle"],
                authorization.Token).ConfigureAwait(false);
        }

        if (secret is null)
        {
            await Console.Error.WriteLineAsync($"The {worker} was not authorized by its supervisor.").ConfigureAwait(false);
            return null;
        }

        try
        {
            if (!SelfWorkerAuthorization.TryConstrainSessionFiles(
                    options["--request"],
                    options["--result"],
                    requestFileName,
                    resultFileName,
                    out var requestPath,
                    out var resultPath))
            {
                await Console.Error.WriteLineAsync($"The {worker} session paths were rejected.").ConfigureAwait(false);
                return null;
            }

            var request = await ReadRequestAsync(
                requestPath!,
                maximumRequestBytes,
                deserialize,
                cancellationToken).ConfigureAwait(false);
            var rejection = rejectRequest(request);
            if (rejection is not null || request is null)
            {
                await Console.Error.WriteLineAsync(rejection ?? $"The {worker} request was malformed.").ConfigureAwait(false);
                return null;
            }

            if (SelfWorkerAuthorization.VerifySecret(secret, authorizationSha256(request)))
            {
                return new SelfWorkerSession<TRequest>(request, resultPath!);
            }

            await Console.Error.WriteLineAsync($"The {worker} was not authorized by its supervisor.").ConfigureAwait(false);
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>Writes the single result as a new file that reaches the disk before the worker exits.</summary>
    /// <param name="path">The constrained result path.</param>
    /// <param name="serialize">Writes the response to the stream.</param>
    /// <param name="cancellationToken">Cancels writing.</param>
    /// <returns>A task that completes once the result is flushed.</returns>
    internal static async Task WriteResultAsync(
        string path,
        Func<Stream, CancellationToken, Task> serialize,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await serialize(stream, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Bounds diagnostic text a worker reports.</summary>
    /// <param name="value">Diagnostic text.</param>
    /// <returns>At most 16 KiB characters of it.</returns>
    internal static string Bound(string value) =>
        value[..Math.Min(value.Length, MaximumMessageLength)];

    private static async Task<TRequest?> ReadRequestAsync<TRequest>(
        string path,
        long maximumBytes,
        Func<Stream, CancellationToken, ValueTask<TRequest?>> deserialize,
        CancellationToken cancellationToken)
        where TRequest : class
    {
        FileInfo info = new(path);
        if (!info.Exists || info.Length <= 0 || info.Length > maximumBytes)
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
        return await deserialize(stream, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryParseOptions(
        IReadOnlyList<string> args,
        string worker,
        IReadOnlyList<string> options,
        out Dictionary<string, string>? values,
        out string? error)
    {
        Dictionary<string, string> parsed = new(StringComparer.Ordinal);
        for (var index = 0; index < args.Count; index += 2)
        {
            if (index + 1 < args.Count
                && options.Contains(args[index], StringComparer.Ordinal)
                && parsed.TryAdd(args[index], args[index + 1]))
            {
                continue;
            }

            values = null;
            error = $"The {worker} requires exactly "
                + $"{string.Join(", ", options.Take(options.Count - 1))}, and {options[^1]} once each.";
            return false;
        }

        if (parsed.Count != options.Count
            || options.Any(option => !parsed.TryGetValue(option, out var value) || string.IsNullOrWhiteSpace(value)))
        {
            values = null;
            error = $"The {worker} arguments were incomplete or malformed.";
            return false;
        }

        values = parsed;
        error = null;
        return true;
    }
}
