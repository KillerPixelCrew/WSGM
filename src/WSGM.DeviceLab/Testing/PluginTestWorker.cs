using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Identity;
using WSGM.DeviceLab.Application;
using WSGM.DeviceLab.Packaging;
using WSGM.DeviceLab.Preflight;
using WSGM.DeviceLab.Probes;

namespace WSGM.DeviceLab.Testing;

internal sealed record PluginTestWorkerRequest
{
    public required int SchemaVersion { get; init; }

    public required PluginTestMode Mode { get; init; }

    public required string PackageDirectory { get; init; }

    public required DeviceIdentitySnapshot Identity { get; init; }

    public string? StateDirectory { get; init; }

    public AttendedPluginActionRequest? Action { get; init; }

    public bool Confirmed { get; init; }

    public bool ParentOwnerReserved { get; init; }

    public required string AuthorizationSha256 { get; init; }
}

internal sealed record PluginTestWorkerResponse
{
    public required int SchemaVersion { get; init; }

    public required string AuthorizationSha256 { get; init; }

    public PluginTestReport? Report { get; init; }

    public string? Error { get; init; }
}

/// <summary>Disposable hidden worker which is never trusted without its inherited one-use pipe.</summary>
internal static class PluginTestWorker
{
    internal const string Mode = "__plugin-test";
    internal const string RequestFileName = "plugin-request.json";
    internal const string ResultFileName = "plugin-result.json";

    private const int MaximumRequestBytes = 4 * 1024 * 1024;
    private const string Worker = "plugin worker";
    private static readonly string[] Options = ["--request", "--result", "--authorization-handle"];

    internal static int Run(IReadOnlyList<string> args) =>
        SelfWorkerProtocol.Run(args, Worker, Options, RunAsync);

    private static async Task<int> RunAsync(
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        var session = await SelfWorkerProtocol.AuthorizeAsync(
            Worker,
            options,
            RequestFileName,
            ResultFileName,
            MaximumRequestBytes,
            (stream, token) => JsonSerializer.DeserializeAsync<PluginTestWorkerRequest>(
                stream,
                PluginTestWorkerJson.Options,
                token),
            request => request is null
                || request.SchemaVersion != 1
                || request.Identity is null
                || string.IsNullOrWhiteSpace(request.PackageDirectory)
                || request.Mode is not (PluginTestMode.DetectionOnly or PluginTestMode.AttendedHardware)
                || request.Mode is PluginTestMode.AttendedHardware
                    && (!request.ParentOwnerReserved
                        || request.Action is null
                        || string.IsNullOrWhiteSpace(request.StateDirectory))
                    ? "The plugin worker request was malformed."
                    : null,
            request => request.AuthorizationSha256,
            cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return SelfWorkerProtocol.ExitRejected;
        }

        var request = session.Request;
        PluginTestReport? report = null;
        string? failure = null;
        try
        {
            if (request.Mode is PluginTestMode.DetectionOnly)
            {
                report = await PluginTestWorkflow.TestDetectionInProcessAsync(
                    request.PackageDirectory,
                    request.Identity,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var repositoryRoot = DeviceLabRepositoryLocator.Find(Environment.CurrentDirectory)
                                     ?? DeviceLabRepositoryLocator.Find(AppContext.BaseDirectory);
                report = await PluginTestWorkflow.RunAttendedAsync(
                    request.PackageDirectory,
                    request.Identity,
                    request.StateDirectory!,
                    request.Action!,
                    request.Confirmed,
                    DeviceLabPathBoundaries.ForCurrentUser(repositoryRoot),
                    ParentReservedSafetyEnvironment(),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure = SelfWorkerProtocol.Bound(exception.Message);
        }

        await SelfWorkerProtocol.WriteResultAsync(
            session.ResultPath,
            (stream, token) => JsonSerializer.SerializeAsync(
                stream,
                new PluginTestWorkerResponse
                {
                    SchemaVersion = 1,
                    AuthorizationSha256 = request.AuthorizationSha256,
                    Report = report,
                    Error = failure
                },
                PluginTestWorkerJson.Options,
                token),
            cancellationToken).ConfigureAwait(false);
        return SelfWorkerProtocol.ExitSuccess;
    }

    private static AttendedPluginSafetyEnvironment ParentReservedSafetyEnvironment() => new()
    {
        ReserveOwner = static () => new DeviceLabOwnerReservationResult
        {
            Inspection = new DeviceLabOwnerInspection
            {
                State = DeviceOwnerDiscoveryState.Absent
            },
            // The real machine-wide handle remains in the supervising process. This local handle
            // preserves the in-process lifetime ordering without pretending to own another mutex.
            Reservation = new DeviceLabOwnerReservation(new NoopDisposable())
        },
        IsElevated = DeviceLabEnvironment.IsElevated(),
        IsUserInteractive = Environment.UserInteractive,
        IsContinuousIntegration = DeviceLabEnvironment.IsContinuousIntegration()
    };

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

internal static class PluginTestWorkerJson
{
    internal static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };
}

/// <summary>Supervises all community plugin code behind a hard process-tree deadline.</summary>
internal static class PluginTestWorkerSupervisor
{
    private static readonly TimeSpan DetectionDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AttendedDeadline = TimeSpan.FromSeconds(90);
    private const int MaximumResponseBytes = 8 * 1024 * 1024;

    internal static Task<PluginTestReport> TestDetectionAsync(
        string packageDirectory,
        DeviceIdentitySnapshot identity,
        CancellationToken cancellationToken) => TestDetectionAsync(
            packageDirectory,
            identity,
            DeviceLabExecutable.CurrentPath,
            cancellationToken);

    internal static Task<PluginTestReport> TestDetectionAsync(
        string packageDirectory,
        DeviceIdentitySnapshot identity,
        string executablePath,
        CancellationToken cancellationToken) => TestDetectionAsync(
            packageDirectory,
            identity,
            executablePath,
            DetectionDeadline,
            cancellationToken);

    internal static Task<PluginTestReport> TestDetectionAsync(
        string packageDirectory,
        DeviceIdentitySnapshot identity,
        string executablePath,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(identity);
        return RunAsync(
            new PluginTestWorkerRequest
            {
                SchemaVersion = 1,
                Mode = PluginTestMode.DetectionOnly,
                PackageDirectory = Path.GetFullPath(packageDirectory),
                Identity = identity,
                AuthorizationSha256 = string.Empty
            },
            ownerReservation: null,
            executablePath,
            deadline,
            cancellationToken);
    }

    internal static async Task<PluginTestReport> RunAttendedAsync(
        string packageDirectory,
        DeviceIdentitySnapshot identity,
        string stateDirectory,
        AttendedPluginActionRequest action,
        bool confirmed,
        DeviceLabPathBoundaries boundaries,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(boundaries);
        var validation = PluginPackageWorkflow.ValidateOffline(packageDirectory, cancellationToken);
        if (!validation.Valid)
        {
            return Failed(
                PluginTestMode.AttendedHardware,
                validation.PackageId,
                action,
                "Offline package validation failed.");
        }

        var output = DeviceLabOutputPathPolicy.Evaluate(
            stateDirectory,
            DeviceLabOutputTargetKind.Directory,
            boundaries);
        if (!output.IsAllowed || output.FullPath is null
            || Directory.Exists(output.FullPath) || File.Exists(output.FullPath))
        {
            return Failed(
                PluginTestMode.AttendedHardware,
                validation.PackageId,
                action,
                output.Reason ?? "The plugin state directory must be new.");
        }

        var requirements = new DeviceLabOperationRequirements
        {
            OperationId = "plugin.attended-run",
            ResourceId = validation.PackageId!,
            Access = DeviceLabOperationAccess.AttendedPluginAction,
            ExactDeviceMatched = true,
            RequiresElevation = true
        };
        DeviceLabSafetySnapshot staticSnapshot = new()
        {
            OwnerDiscovery = DeviceOwnerDiscoveryState.Absent,
            IsElevated = DeviceLabEnvironment.IsElevated(),
            IsUserInteractive = Environment.UserInteractive,
            IsContinuousIntegration = DeviceLabEnvironment.IsContinuousIntegration(),
            AttendedActionConfirmed = confirmed
        };
        var staticPreflight = DeviceLabSafetyPreflight.Evaluate(
            requirements,
            staticSnapshot);
        if (staticPreflight.Route is not DeviceLabAccessRoute.DirectAttended)
        {
            return new PluginTestReport
            {
                Mode = PluginTestMode.AttendedHardware,
                Passed = false,
                PackageId = validation.PackageId,
                Preflight = staticPreflight,
                Action = action,
                Error = "The attended hardware action was blocked before plugin loading."
            };
        }

        var owner = DeviceLabOwnerInspector.Reserve();
        using var ownerReservation = owner.Reservation;
        var ownerState = owner.Inspection.State;
        if (ownerState is DeviceOwnerDiscoveryState.Absent != ownerReservation is not null)
        {
            ownerState = DeviceOwnerDiscoveryState.Unknown;
        }

        var ownerPreflight = DeviceLabSafetyPreflight.Evaluate(
            requirements,
            staticSnapshot with { OwnerDiscovery = ownerState });
        if (ownerPreflight.Route is not DeviceLabAccessRoute.DirectAttended)
        {
            return new PluginTestReport
            {
                Mode = PluginTestMode.AttendedHardware,
                Passed = false,
                PackageId = validation.PackageId,
                Preflight = ownerPreflight,
                Action = action,
                Error = "The attended hardware action was blocked before plugin loading."
            };
        }

        var report = await RunAsync(
            new PluginTestWorkerRequest
            {
                SchemaVersion = 1,
                Mode = PluginTestMode.AttendedHardware,
                PackageDirectory = Path.GetFullPath(packageDirectory),
                Identity = identity,
                StateDirectory = output.FullPath,
                Action = action,
                Confirmed = confirmed,
                ParentOwnerReserved = true,
                AuthorizationSha256 = string.Empty
            },
            ownerReservation,
            DeviceLabExecutable.CurrentPath,
            AttendedDeadline,
            cancellationToken).ConfigureAwait(false);

        if (report is { Started: true, CleanedUp: false })
        {
            ownerReservation?.RetainForProcessLifetime();
        }

        return report;
    }

    private static async Task<PluginTestReport> RunAsync(
        PluginTestWorkerRequest request,
        DeviceLabOwnerReservation? ownerReservation,
        string executablePath,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(deadline));
        }

        var authorizationSecret = SelfWorkerAuthorization.CreateSecret();
        request = request with
        {
            AuthorizationSha256 = SelfWorkerAuthorization.Hash(authorizationSecret)
        };

        var workersRoot = Path.GetFullPath(Path.Combine(
            // wsgm-allow-live-data-path: Device Lab's own root beside WSGM's data, never inside it.
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WSGM Device Lab",
            "Workers"));
        var sessionId = $"plugin-{Guid.NewGuid():N}";
        var sessionDirectory = Path.Combine(workersRoot, sessionId);
        var requestPath = Path.Combine(sessionDirectory, PluginTestWorker.RequestFileName);
        var resultPath = Path.Combine(sessionDirectory, PluginTestWorker.ResultFileName);
        var markerPath = Path.Combine(sessionDirectory, ".device-lab-worker-session");
        var sessionCleanupAllowed = true;
        try
        {
            Directory.CreateDirectory(workersRoot);
            Directory.CreateDirectory(sessionDirectory);
            await File.WriteAllTextAsync(markerPath, sessionId, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            await using (FileStream requestStream = new(
                requestPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    requestStream,
                    request,
                    PluginTestWorkerJson.Options,
                    cancellationToken).ConfigureAwait(false);
                await requestStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!SelfWorkerAuthorization.TryConstrainSessionFiles(
                    requestPath,
                    resultPath,
                    PluginTestWorker.RequestFileName,
                    PluginTestWorker.ResultFileName,
                    out _,
                    out _))
            {
                throw new IOException("The plugin worker session path failed its confinement check.");
            }

            string[] arguments =
            [
                PluginTestWorker.Mode,
                "--request", requestPath,
                "--result", resultPath
            ];
            ReadProbeProcessOutcome process;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                sessionCleanupAllowed = false;
                process = await new SystemReadProbeProcessLauncher().RunAsync(
                    executablePath,
                    arguments,
                    deadline,
                    resultPath,
                    authorizationSecret,
                    cancellationToken).ConfigureAwait(false);
                sessionCleanupAllowed = process.ContainmentVerified;
            }
            catch (DisposableWorkerCanceledException exception)
            {
                sessionCleanupAllowed = exception.ContainmentVerified;
                ownerReservation?.RetainForProcessLifetime();
                throw;
            }
            catch (OperationCanceledException)
            {
                ownerReservation?.RetainForProcessLifetime();
                throw;
            }

            if (!process.ContainmentVerified)
            {
                ownerReservation?.RetainForProcessLifetime();
                return Failed(
                    request.Mode,
                    null,
                    request.Action,
                    process.Error ?? "The plugin worker's complete descendant teardown could not be verified.");
            }

            if (!process.Started)
            {
                return Failed(
                    request.Mode,
                    null,
                    request.Action,
                    process.Error ?? "The plugin worker did not start.");
            }

            if (process.TimedOut || process.ExitCode != 0 || !process.ResultProduced)
            {
                ownerReservation?.RetainForProcessLifetime();
                return Failed(
                    request.Mode,
                    null,
                    request.Action,
                    process.Error ?? (process.TimedOut
                        ? "The plugin worker exceeded its hard deadline and was killed."
                        : "The plugin worker did not complete cleanly."));
            }

            PluginTestWorkerResponse? response;
            try
            {
                FileInfo result = new(resultPath);
                if (result.Length is <= 0 or > MaximumResponseBytes)
                {
                    throw new InvalidDataException("The plugin worker response exceeded its size limit.");
                }

                await using FileStream resultStream = new(
                    resultPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                response = await JsonSerializer.DeserializeAsync<PluginTestWorkerResponse>(
                    resultStream,
                    PluginTestWorkerJson.Options,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or JsonException)
            {
                ownerReservation?.RetainForProcessLifetime();
                return Failed(request.Mode, null, request.Action, exception.Message);
            }

            if (response is null
                || response.SchemaVersion != 1
                || !string.Equals(
                    response.AuthorizationSha256,
                    request.AuthorizationSha256,
                    StringComparison.Ordinal))
            {
                ownerReservation?.RetainForProcessLifetime();
                return Failed(
                    request.Mode,
                    null,
                    request.Action,
                    "The plugin worker response did not match its authorized request.");
            }

            if (response.Report is not null && response.Error is null)
            {
                return response.Report;
            }

            ownerReservation?.RetainForProcessLifetime();
            return Failed(
                request.Mode,
                null,
                request.Action,
                response.Error ?? "The plugin worker returned no report.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(authorizationSecret);
            if (sessionCleanupAllowed)
            {
                TryDeleteOwnedSession(workersRoot, sessionDirectory, markerPath, sessionId);
            }
        }
    }

    private static PluginTestReport Failed(
        PluginTestMode mode,
        string? packageId,
        AttendedPluginActionRequest? action,
        string error) => new()
        {
            Mode = mode,
            Passed = false,
            PackageId = packageId,
            Action = action,
            Error = error[..Math.Min(error.Length, 16_384)]
        };

    private static void TryDeleteOwnedSession(
        string workersRoot,
        string sessionDirectory,
        string markerPath,
        string sessionId)
    {
        try
        {
            if (!string.Equals(Path.GetDirectoryName(sessionDirectory), workersRoot,
                    StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(sessionDirectory)
                || (File.GetAttributes(sessionDirectory) & FileAttributes.ReparsePoint) != 0
                || !File.Exists(markerPath)
                || (File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0
                || !string.Equals(File.ReadAllText(markerPath, Encoding.UTF8), sessionId,
                    StringComparison.Ordinal))
            {
                return;
            }

            HashSet<string> expected = new(StringComparer.OrdinalIgnoreCase)
            {
                Path.GetFullPath(markerPath),
                Path.Combine(sessionDirectory, PluginTestWorker.RequestFileName),
                Path.Combine(sessionDirectory, PluginTestWorker.ResultFileName)
            };
            var observed = 0;
            foreach (var path in Directory.EnumerateFileSystemEntries(sessionDirectory))
            {
                if (++observed > expected.Count
                    || !expected.Contains(Path.GetFullPath(path)))
                {
                    return;
                }

                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    return;
                }
            }

            foreach (var path in expected.Where(File.Exists))
            {
                File.Delete(path);
            }

            Directory.Delete(sessionDirectory, recursive: false);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            // A suspicious or locked session is left for inspection instead of widening deletion.
        }
    }
}
