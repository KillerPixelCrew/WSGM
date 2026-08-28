using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.DeviceLab.Core.Preflight;

namespace WSGM.DeviceLab.Core.Probes;

/// <summary>Starts exactly one disposable ProbeHost process under a hard deadline.</summary>
public interface IReadProbeProcessLauncher
{
    /// <summary>Runs one host and kills its full process tree if the deadline expires.</summary>
    /// <param name="hostPath">Exact locally hashed executable.</param>
    /// <param name="arguments">Fixed ProbeHost arguments.</param>
    /// <param name="timeout">Hard process deadline.</param>
    /// <param name="resultPath">Result file which must not exist before launch.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <returns>Observed process lifecycle.</returns>
    Task<ReadProbeProcessOutcome> RunAsync(
        string hostPath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string resultPath,
        CancellationToken cancellationToken);
}

/// <summary>Production process launcher for the disposable ProbeHost executable.</summary>
public sealed class SystemReadProbeProcessLauncher : IReadProbeProcessLauncher
{
    private const int MaximumErrorLength = 16_384;

    /// <inheritdoc/>
    public async Task<ReadProbeProcessOutcome> RunAsync(
        string hostPath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string resultPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPath);
        ArgumentNullException.ThrowIfNull(arguments);

        ProcessStartInfo startInfo = new()
        {
            FileName = hostPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return Failed("ProbeHost did not start.");
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Failed(exception.Message);
        }

        Task<string> errorRead = process.StandardError.ReadToEndAsync(cancellationToken);
        _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new ReadProbeProcessOutcome
            {
                Started = true,
                TimedOut = true,
                ResultProduced = File.Exists(resultPath),
                Error = "ProbeHost exceeded its deadline and was killed.",
            };
        }

        string error = await errorRead.ConfigureAwait(false);
        return new ReadProbeProcessOutcome
        {
            Started = true,
            TimedOut = false,
            ExitCode = process.ExitCode,
            ResultProduced = File.Exists(resultPath),
            Error = string.IsNullOrWhiteSpace(error)
                ? null
                : error[..Math.Min(error.Length, MaximumErrorLength)],
        };
    }

    private static ReadProbeProcessOutcome Failed(string error) => new()
    {
        Started = false,
        TimedOut = false,
        ResultProduced = false,
        Error = error,
    };

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It exited between the deadline firing and the kill. There is no durable host state.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The supervisor still reports a deadline failure. The OS owns final process teardown.
        }
    }
}

/// <summary>Runs the complete admission, hash, preflight, disposable-host, and validation sequence.</summary>
public static class ReadProbeHostSupervisor
{
    private const int MaximumResponseBytes = 1_048_576;

    /// <summary>Executes one reviewed read probe in a fresh host process.</summary>
    /// <param name="metadata">Cataloged probe contract.</param>
    /// <param name="admissionContext">Exact machine/install/operator context.</param>
    /// <param name="preflight">Already evaluated ownership and safety decision.</param>
    /// <param name="hostPath">Locally installed ProbeHost executable.</param>
    /// <param name="sessionDirectory">New, explicit output directory for request and result files.</param>
    /// <param name="launcher">Disposable process launcher.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <returns>Classified run result.</returns>
    public static async Task<ReadProbeRunResult> RunAsync(
        ReadProbeMetadata metadata,
        ReadProbeAdmissionContext admissionContext,
        DeviceLabPreflightDecision preflight,
        string hostPath,
        string sessionDirectory,
        IReadProbeProcessLauncher launcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(admissionContext);
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(launcher);

        ReadProbeAdmissionDecision admission = ReadProbeAdmission.Evaluate(metadata, admissionContext);
        if (!admission.Allowed)
        {
            return Result(ReadProbeRunStatus.Rejected, admission.Message);
        }

        if (preflight.Status is DeviceLabDoctorStatus.Blocked
            || preflight.Route is not DeviceLabAccessRoute.DirectReadOnly
            || !string.Equals(preflight.ResourceId, metadata.ResourceId, StringComparison.Ordinal))
        {
            return Result(
                ReadProbeRunStatus.Rejected,
                "Safety preflight did not authorize direct read-only access to the exact resource.");
        }

        string calculatedHash;
        try
        {
            calculatedHash = HashFile(hostPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result(ReadProbeRunStatus.LaunchFailed, exception.Message);
        }

        if (!string.Equals(calculatedHash, metadata.ImplementationSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Result(ReadProbeRunStatus.HashMismatch, "ProbeHost changed after admission.");
        }

        if (Directory.Exists(sessionDirectory) || File.Exists(sessionDirectory))
        {
            return Result(ReadProbeRunStatus.Rejected, "Probe session output must be a new directory.");
        }

        DeviceLabOutputPathDecision output = DeviceLabOutputPathPolicy.Evaluate(
            sessionDirectory,
            DeviceLabOutputTargetKind.Directory,
            DeviceLabPathBoundaries.ForCurrentUser(
                DeviceLabRepositoryLocator.Find(Environment.CurrentDirectory)));
        if (!output.IsAllowed || output.FullPath is null)
        {
            return Result(ReadProbeRunStatus.Rejected, output.Reason ?? "Probe session output was rejected.");
        }

        Directory.CreateDirectory(output.FullPath);
        string requestPath = Path.Combine(output.FullPath, "probe-request.json");
        string resultPath = Path.Combine(output.FullPath, "probe-result.json");
        if (File.Exists(requestPath) || File.Exists(resultPath))
        {
            return Result(ReadProbeRunStatus.Rejected, "Probe session files already exist; overwrite is forbidden.");
        }

        ReadProbeHostRequest request = new()
        {
            SchemaVersion = 1,
            ProbeId = metadata.Id,
            ProbeVersion = metadata.Version,
            FamilyId = metadata.FamilyId,
            EndpointId = metadata.EndpointId,
            Family = metadata.Family,
            ImplementationSha256 = metadata.ImplementationSha256,
            MaximumReadsPerSecond = metadata.MaximumReadsPerSecond,
            TimeoutMilliseconds = metadata.TimeoutMilliseconds,
            Repetitions = metadata.Repetitions,
        };
        await File.WriteAllTextAsync(
            requestPath,
            DeviceLabJson.Serialize(request),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);

        string[] arguments =
        [
            "--probe", metadata.Id,
            "--hash", metadata.ImplementationSha256,
            "--request", requestPath,
            "--result", resultPath,
        ];
        ReadProbeProcessOutcome process = await launcher.RunAsync(
            hostPath,
            arguments,
            TimeSpan.FromMilliseconds(metadata.TimeoutMilliseconds),
            resultPath,
            cancellationToken).ConfigureAwait(false);

        ReadProbeRunResult? processFailure = ReadProbeOutcomeClassifier.ClassifyProcess(process);
        if (processFailure is not null)
        {
            return processFailure;
        }

        ReadProbeHostResponse? response;
        try
        {
            FileInfo resultInfo = new(resultPath);
            if (resultInfo.Length > MaximumResponseBytes)
            {
                return Result(ReadProbeRunStatus.MalformedResponse, "ProbeHost response exceeded the size limit.");
            }

            await using FileStream stream = new(
                resultPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            response = await JsonSerializer.DeserializeAsync(
                stream,
                DeviceLabJsonContext.Default.ReadProbeHostResponse,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Result(ReadProbeRunStatus.MalformedResponse, exception.Message);
        }

        if (response is null)
        {
            return Result(ReadProbeRunStatus.MalformedResponse, "ProbeHost response was empty.");
        }

        return ReadProbeOutcomeClassifier.ClassifyResponse(metadata, response);
    }

    /// <summary>Calculates lower-case SHA-256 for one local executable.</summary>
    /// <param name="path">File to hash.</param>
    /// <returns>Lower-case hexadecimal digest.</returns>
    public static string HashFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static ReadProbeRunResult Result(ReadProbeRunStatus status, string message) => new()
    {
        Status = status,
        Message = message,
    };
}

/// <summary>Maps process and typed-response failure modes to stable Device Lab results.</summary>
public static class ReadProbeOutcomeClassifier
{
    /// <summary>Classifies launch, crash, hang, and missing-result states.</summary>
    /// <param name="process">Observed disposable-process lifecycle.</param>
    /// <returns>A terminal failure, or null when the response document should be read.</returns>
    public static ReadProbeRunResult? ClassifyProcess(ReadProbeProcessOutcome process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!process.Started)
        {
            return Result(ReadProbeRunStatus.LaunchFailed, process.Error ?? "ProbeHost did not start.");
        }

        if (process.TimedOut)
        {
            return Result(ReadProbeRunStatus.HostHung, process.Error ?? "ProbeHost exceeded its deadline.");
        }

        if (process.ExitCode != 0)
        {
            return Result(ReadProbeRunStatus.HostCrashed, process.Error ?? $"ProbeHost exited with code {process.ExitCode}.");
        }

        return process.ResultProduced
            ? null
            : Result(ReadProbeRunStatus.MalformedResponse, "ProbeHost exited without a result document.");
    }

    /// <summary>Classifies typed endpoint failures and validates completed responses.</summary>
    /// <param name="metadata">Cataloged response contract.</param>
    /// <param name="response">Parsed host response.</param>
    /// <returns>Stable run result.</returns>
    public static ReadProbeRunResult ClassifyResponse(
        ReadProbeMetadata metadata,
        ReadProbeHostResponse response)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(response);
        if (response.Status is ReadProbeHostStatus.AccessDenied)
        {
            return WithResponse(ReadProbeRunStatus.AccessDenied, response.Error ?? "ProbeHost was denied access.", response);
        }

        if (response.Status is ReadProbeHostStatus.Disconnected)
        {
            return WithResponse(ReadProbeRunStatus.Disconnected, response.Error ?? "The exact endpoint disconnected.", response);
        }

        ReadProbeValidationResult validation = ReadProbeResponseValidator.Validate(metadata, response);
        return WithResponse(
            validation.Accepted ? ReadProbeRunStatus.Accepted : ReadProbeRunStatus.MalformedResponse,
            validation.Message,
            response);
    }

    private static ReadProbeRunResult Result(ReadProbeRunStatus status, string message) => new()
    {
        Status = status,
        Message = message,
    };

    private static ReadProbeRunResult WithResponse(
        ReadProbeRunStatus status,
        string message,
        ReadProbeHostResponse response) => new()
    {
        Status = status,
        Message = message,
        Response = response,
    };
}
