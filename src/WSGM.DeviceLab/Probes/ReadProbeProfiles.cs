using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.DeviceLab.Probes;

// These typed profiles live only in Device Lab's disposable self-worker; the production plugin
// runtime never references this assembly, so normal device activation cannot invoke them.
internal interface IReadProbeProfile
{
    CompiledReadProbeDescriptor Descriptor { get; }

    ValueTask<ReadProbeSample> ReadOnceAsync(CancellationToken cancellationToken);
}

internal sealed record CompiledReadProbeDescriptor(
    string Id,
    int Version,
    string FamilyId,
    string EndpointId,
    ReadProbeFamily Family,
    int MaximumReadsPerSecond,
    int TimeoutMilliseconds,
    int Repetitions)
{
    public bool Matches(ReadProbeWorkerRequest request, out string mismatch)
    {
        var matches = string.Equals(Id, request.ProbeId, StringComparison.Ordinal)
                      && Version == request.ProbeVersion
                      && string.Equals(FamilyId, request.FamilyId, StringComparison.Ordinal)
                      && string.Equals(EndpointId, request.EndpointId, StringComparison.Ordinal)
                      && Family == request.Family
                      && request.MaximumReadsPerSecond == MaximumReadsPerSecond
                      && request.TimeoutMilliseconds == TimeoutMilliseconds
                      && request.Repetitions == Repetitions;
        mismatch = matches
            ? string.Empty
            : "Probe request did not exactly match its compiled profile bounds.";
        return matches;
    }
}

internal static class BuiltInReadProbeRegistry
{
    private static readonly Dictionary<(string Id, int Version), IReadProbeProfile> Profiles =
        new()
        {
            [(MsiWmiVersionProbe.ProbeId, 1)] = new MsiWmiVersionProbe(),
            [(MsiEmbeddedControllerVersionProbe.ProbeId, 1)] = new MsiEmbeddedControllerVersionProbe(),
            [(MsiScenarioStatusProbe.ProbeId, 1)] = new MsiScenarioStatusProbe(),
            [(MsiFanRpmProbe.ProbeId, 1)] = new MsiFanRpmProbe(),
            [(MsiChargeLimitProbe.ProbeId, 1)] = new MsiChargeLimitProbe()
        };

    public static bool TryResolve(string id, int version, out IReadProbeProfile profile)
    {
        return Profiles.TryGetValue((id, version), out profile!);
    }
}

internal static class ReadProbeExecutor
{
    public static async Task<ReadProbeWorkerResponse> ExecuteAsync(
        IReadProbeProfile profile,
        ReadProbeWorkerRequest request,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.TimeoutMilliseconds);
        List<ReadProbeSample> samples = [];
        var minimumDelay = checked((int)Math.Ceiling(2000d / request.MaximumReadsPerSecond));

        try
        {
            for (var repetition = 0; repetition < request.Repetitions; repetition++)
            {
                if (repetition != 0)
                {
                    await Task.Delay(minimumDelay, deadline.Token).ConfigureAwait(false);
                }

                samples.Add(await profile.ReadOnceAsync(deadline.Token).ConfigureAwait(false));
            }

            return Response(ReadProbeWorkerStatus.Completed, samples);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Response(ReadProbeWorkerStatus.AccessDenied, samples, exception.Message);
        }
        catch (FileNotFoundException exception)
        {
            return Response(ReadProbeWorkerStatus.PrerequisiteMissing, samples, exception.Message);
        }
        catch (IOException exception)
        {
            return Response(ReadProbeWorkerStatus.Disconnected, samples, exception.Message);
        }
        catch (InvalidDataException exception)
        {
            return Response(ReadProbeWorkerStatus.Rejected, samples, exception.Message);
        }
        catch (ManagementException exception)
        {
            var status = exception.ErrorCode switch
            {
                ManagementStatus.AccessDenied => ReadProbeWorkerStatus.AccessDenied,
                ManagementStatus.InvalidNamespace or ManagementStatus.InvalidClass
                    => ReadProbeWorkerStatus.PrerequisiteMissing,
                _ => ReadProbeWorkerStatus.Disconnected
            };
            return Response(status, samples, exception.ErrorCode.ToString());
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return Response(ReadProbeWorkerStatus.Rejected, samples, "Compiled probe exceeded its deadline.");
        }

        ReadProbeWorkerResponse Response(
            ReadProbeWorkerStatus status,
            IReadOnlyList<ReadProbeSample> observed,
            string? error = null)
        {
            return new ReadProbeWorkerResponse
            {
                SchemaVersion = 1,
                ProbeId = request.ProbeId,
                ProbeVersion = request.ProbeVersion,
                Status = status,
                Samples = observed,
                Error = error,
                HardwareMutationObserved = false
            };
        }
    }
}

// These MSI profiles compile the exact reviewed getter, request byte, response shape, board family,
// endpoint, and rate into the disposable self-worker. The request envelope cannot substitute a method or address.
// Get_* still crosses the vendor provider and is therefore an explicit local read; it is never
// exposed as a production runtime command and it never falls back to a Set_* method.
internal abstract class MsiWmiReadProbeProfile(
    string id,
    ReadProbeFamily family,
    string endpoint,
    int repetitions = 2) : IReadProbeProfile
{
    public CompiledReadProbeDescriptor Descriptor { get; } = new(
        id,
        1,
        "msi.claw-a2vm.ms-1t52",
        endpoint,
        family,
        2,
        5_000,
        repetitions);

    public abstract ValueTask<ReadProbeSample> ReadOnceAsync(CancellationToken cancellationToken);

    protected static byte[] InvokeGetter(string methodName, byte firstInputByte)
    {
        using ManagementObjectSearcher searcher = new(
            "root\\WMI",
            "SELECT * FROM MSI_ACPI WHERE Active = TRUE");
        ManagementObject? instance = null;
        foreach (var candidate in searcher.Get())
        {
            if (instance is null)
            {
                instance = (ManagementObject)candidate;
            }
            else
            {
                candidate.Dispose();
                instance.Dispose();
                throw new InvalidDataException("The reviewed MSI_ACPI profile requires exactly one active instance.");
            }
        }

        if (instance is null)
        {
            throw new FileNotFoundException("The reviewed MSI_ACPI instance was not present.");
        }

        using (instance)
        using (var input = instance.GetMethodParameters(methodName))
        using (ManagementClass packageClass = new("root\\WMI", "Package_32", null))
        using (var package = packageClass.CreateInstance())
        {
            var request = new byte[32];
            request[0] = firstInputByte;
            package["Bytes"] = request;
            input["Data"] = package;

            using var output = instance.InvokeMethod(methodName, input, null)
                               ?? throw new IOException($"{methodName} returned no response.");
            if (output["Data"] is not ManagementBaseObject returned
                || returned["Bytes"] is not byte[] { Length: 32 } response)
            {
                throw new InvalidDataException($"{methodName} did not return the reviewed Package_32 shape.");
            }

            using (returned)
            {
                return response[0] != 0x01
                    ? throw new InvalidDataException($"{methodName} returned status 0x{response[0]:x2}.")
                    : response;
            }
        }
    }

    protected static ReadProbeSample Numeric(
        long value,
        long crossCheck,
        int length,
        long elapsedMilliseconds)
    {
        return new ReadProbeSample
        {
            ValueKind = ReadProbeValueKind.Integer,
            StatusCode = 1,
            Length = length,
            NumericValue = value,
            NormalizedValue = value.ToString(CultureInfo.InvariantCulture),
            ElapsedMilliseconds = checked((int)elapsedMilliseconds),
            CrossCheckValue = crossCheck.ToString(CultureInfo.InvariantCulture),
            CrossCheckNumericValue = crossCheck
        };
    }
}

internal sealed class MsiWmiVersionProbe()
    : MsiWmiReadProbeProfile(ProbeId, ReadProbeFamily.Version, "root/WMI:MSI_ACPI.Get_WMI")
{
    public const string ProbeId = "msi.claw-a2vm.wmi-version";

    public override ValueTask<ReadProbeSample> ReadOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var response = InvokeGetter("Get_WMI", 0);
        var corroboration = InvokeGetter("Get_WMI", 0);
        var primary = $"{response[2]}.{response[3]}";
        var crossCheck = $"{corroboration[2]}.{corroboration[3]}";
        stopwatch.Stop();
        return ValueTask.FromResult(ReadProbeSamples.Text(
            ReadProbeValueKind.Version,
            primary,
            crossCheck,
            stopwatch.ElapsedMilliseconds,
            1,
            4));
    }
}

internal sealed class MsiEmbeddedControllerVersionProbe()
    : MsiWmiReadProbeProfile(ProbeId, ReadProbeFamily.EmbeddedController, "root/WMI:MSI_ACPI.Get_EC")
{
    public const string ProbeId = "msi.claw-a2vm.ec-version";

    public override ValueTask<ReadProbeSample> ReadOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var response = InvokeGetter("Get_EC", 0);
        var corroboration = InvokeGetter("Get_EC", 0);
        var primary = Convert.ToHexString(response).ToLowerInvariant();
        var crossCheck = Convert.ToHexString(corroboration).ToLowerInvariant();
        stopwatch.Stop();
        return ValueTask.FromResult(ReadProbeSamples.Text(
            ReadProbeValueKind.Bytes,
            primary,
            crossCheck,
            stopwatch.ElapsedMilliseconds,
            1,
            response.Length));
    }
}

internal sealed class MsiScenarioStatusProbe()
    : MsiWmiReadProbeProfile(ProbeId, ReadProbeFamily.WmiStatus, "root/WMI:MSI_ACPI.Get_Data:0xd2")
{
    public const string ProbeId = "msi.claw-a2vm.scenario-status";

    public override ValueTask<ReadProbeSample> ReadOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        long value = InvokeGetter("Get_Data", 0xd2)[1];
        long crossCheck = InvokeGetter("Get_Data", 0xd2)[1];
        stopwatch.Stop();
        return ValueTask.FromResult(Numeric(value, crossCheck, 2, stopwatch.ElapsedMilliseconds));
    }
}

internal sealed class MsiFanRpmProbe()
    : MsiWmiReadProbeProfile(ProbeId, ReadProbeFamily.FanRpm, "root/WMI:MSI_ACPI.Get_Fan:0")
{
    public const string ProbeId = "msi.claw-a2vm.fan-rpm";

    public override ValueTask<ReadProbeSample> ReadOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var primary = Decode(InvokeGetter("Get_Fan", 0));
        var crossCheck = Decode(InvokeGetter("Get_Fan", 0));
        stopwatch.Stop();
        return ValueTask.FromResult(ReadProbeSamples.Text(
            ReadProbeValueKind.Text,
            primary,
            crossCheck,
            stopwatch.ElapsedMilliseconds,
            1,
            5));
    }

    private static string Decode(byte[] response)
    {
        var firstDivisor = (response[2] << 8) | response[3];
        var secondDivisor = (response[4] << 8) | response[5];
        if (firstDivisor == 0 || secondDivisor == 0)
        {
            throw new InvalidDataException("Get_Fan returned a zero tachometer divisor.");
        }

        return $"{480000 / firstDivisor},{480000 / secondDivisor}";
    }
}

internal sealed class MsiChargeLimitProbe()
    : MsiWmiReadProbeProfile(ProbeId, ReadProbeFamily.ChargeState, "root/WMI:MSI_ACPI.Get_Data:0xd7")
{
    public const string ProbeId = "msi.claw-a2vm.charge-limit";

    public override ValueTask<ReadProbeSample> ReadOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        long value = InvokeGetter("Get_Data", 0xd7)[1];
        long crossCheck = InvokeGetter("Get_Data", 0xd7)[1];
        stopwatch.Stop();
        return ValueTask.FromResult(Numeric(value, crossCheck, 2, stopwatch.ElapsedMilliseconds));
    }
}

internal static class ReadProbeSamples
{
    public static ReadProbeSample Text(
        ReadProbeValueKind kind,
        string primary,
        string crossCheck,
        long elapsedMilliseconds,
        int statusCode = 0,
        int? encodedLength = null)
    {
        return new ReadProbeSample
        {
            ValueKind = kind,
            StatusCode = statusCode,
            Length = encodedLength ?? Encoding.UTF8.GetByteCount(primary),
            NormalizedValue = primary,
            ElapsedMilliseconds = checked((int)elapsedMilliseconds),
            CrossCheckValue = crossCheck
        };
    }
}
