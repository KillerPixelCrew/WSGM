using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>Production RTSS adapter over the installed architecture-matched profile API.</summary>
internal sealed class RtssNativeAdapter : IRtssAdapter
{
    private const string FrameLimitProperty = "FramerateLimit";
    private const string OverlayLevelProperty = "EnableStat";
    private readonly RtssDiscovery _discovery;
    private RtssProfileApi? _api;
    private RtssProbe? _lastProbe;
    private long _generation;
    private bool _disposed;

    internal RtssNativeAdapter(RtssDiscovery? discovery = null)
    {
        _discovery = discovery ?? new RtssDiscovery();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Every entry point of this adapter runs on the thread pool. All of the work below is
    /// synchronous — registry reads, filesystem and signature checks, PE-export inspection, process
    /// enumeration, and the profile API's own blocking calls — and the callers reach it from a
    /// completed semaphore wait on an overlay or QAM click handler, which is the Avalonia UI
    /// thread. Without the hop, interacting with a performance control froze the UI for as long as
    /// discovery took.
    /// </remarks>
    public Task<RtssProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.Run(() => ProbeCore(cancellationToken), cancellationToken);
    }

    private RtssProbe ProbeCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RtssProbe probe = _discovery.Probe();
        if (probe.Availability != RtssAvailability.AdapterUnavailable
            || probe.ExecutablePath is null)
        {
            ReleaseApi();
            _lastProbe = probe;
            return probe;
        }

        try
        {
            EnsureApi(probe);
            RtssProbe ready = probe with
            {
                Availability = RtssAvailability.Ready,
                Capabilities = new RtssCapabilities(
                    0,
                    1000,
                    new HashSet<int> { 0, 1 },
                    FrameLimitReadback: true,
                    OverlayLevelReadback: true),
                Diagnostic = "RTSS profile API is ready.",
            };
            _lastProbe = ready;
            return ready;
        }
        catch (Exception ex)
        {
            ReleaseApi();
            RtssProbe degraded = probe with
            {
                Availability = RtssAvailability.Degraded,
                Diagnostic = $"RTSS profile API load failed: {ex.Message}",
            };
            _lastProbe = degraded;
            return degraded;
        }
    }

    public async Task<RtssReadback> ReadAsync(
        string rtssProfileName,
        long generation,
        CancellationToken cancellationToken)
    {
        await RequireReadyAsync(generation, cancellationToken).ConfigureAwait(false);
        return await Task.Run(
            () => ReadCore(rtssProfileName),
            cancellationToken).ConfigureAwait(false);
    }

    private RtssReadback ReadCore(string rtssProfileName)
    {
        RtssProfileApi api = _api
            ?? throw new InvalidOperationException("RTSS profile API is not loaded.");
        api.LoadProfile(rtssProfileName);
        if (!api.TryGetUInt32(FrameLimitProperty, out uint frameLimit)
            || frameLimit > int.MaxValue)
        {
            throw new InvalidDataException("RTSS did not return a valid frame-limit value.");
        }

        if (!api.TryGetUInt32(OverlayLevelProperty, out uint overlayLevel)
            || overlayLevel > 1)
        {
            throw new InvalidDataException("RTSS did not return a valid own-statistics value.");
        }

        return new RtssReadback(
            new PerformanceValues((int)frameLimit, (int)overlayLevel),
            PerformanceReadbackQuality.Verified,
            PerformanceReadbackQuality.Verified,
            RtssTelemetryHealth.Unavailable,
            DateTimeOffset.UtcNow);
    }

    public async Task<RtssApplyResult> ApplyAsync(
        RtssApplyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Control is PerformanceControl.FrameLimit
            && request.Value is < 0 or > 1000)
        {
            return new(false, "The frame-limit value is outside the verified RTSS range.");
        }

        if (request.Control is PerformanceControl.OverlayLevel
            && request.Value is not (0 or 1))
        {
            return new(false, "The RTSS own-statistics value must be off or on.");
        }

        await RequireReadyAsync(request.Generation, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => ApplyCore(request), cancellationToken).ConfigureAwait(false);
    }

    private RtssApplyResult ApplyCore(RtssApplyRequest request)
    {
        RtssProfileApi api = _api
            ?? throw new InvalidOperationException("RTSS profile API is not loaded.");
        api.LoadProfile(request.RtssProfileName);
        string property = request.Control switch
        {
            PerformanceControl.FrameLimit => FrameLimitProperty,
            PerformanceControl.OverlayLevel => OverlayLevelProperty,
            _ => string.Empty,
        };
        if (property.Length == 0
            || !api.TrySetUInt32(property, checked((uint)request.Value)))
        {
            return new(false, "RTSS rejected the performance-profile value.");
        }

        api.SaveProfile(request.RtssProfileName);
        api.UpdateProfiles();
        return new(true, null);
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            ReleaseApi();
        }

        return ValueTask.CompletedTask;
    }

    private async Task<RtssProbe> RequireReadyAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        RtssProbe probe = _lastProbe is { Availability: RtssAvailability.Ready } cached
            && cached.Generation == generation
            ? cached
            : await ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (probe.Availability != RtssAvailability.Ready || probe.Generation != generation)
        {
            throw new InvalidOperationException(
                "RTSS availability or process generation changed before the operation.");
        }

        return probe;
    }

    private void EnsureApi(RtssProbe probe)
    {
        if (_api is not null && _generation == probe.Generation)
        {
            return;
        }

        ReleaseApi();
        string executable = probe.ExecutablePath
            ?? throw new InvalidDataException("RTSS discovery returned no executable path.");
        string directory = Path.GetDirectoryName(executable)
            ?? throw new InvalidDataException("RTSS executable has no installation directory.");
        string library = Path.Combine(
            directory,
            Environment.Is64BitProcess ? "RTSSHooks64.dll" : "RTSSHooks.dll");
        _api = new RtssProfileApi(library);
        _generation = probe.Generation;
    }

    private void ReleaseApi()
    {
        _api?.Dispose();
        _api = null;
        _generation = 0;
        _lastProbe = null;
    }
}

/// <summary>In-memory RTSS adapter used only by the safe overlay-test mode.</summary>
internal sealed class SimulatedRtssAdapter : IRtssAdapter
{
    private static readonly RtssCapabilities Capabilities = new(
        0,
        240,
        new HashSet<int> { 0, 1, 2, 3, 4 },
        FrameLimitReadback: true,
        OverlayLevelReadback: true);
    private readonly Dictionary<string, PerformanceValues> _profiles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = new PerformanceValues(60, 2),
        };
    private bool _disposed;

    public Task<RtssProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.FromResult(new RtssProbe(
            RtssAvailability.Ready,
            "overlay-test",
            null,
            null,
            DateTimeOffset.UtcNow,
            1,
            Capabilities,
            "Simulated RTSS state; no external process or profile is accessed."));
    }

    public Task<RtssReadback> ReadAsync(
        string rtssProfileName,
        long generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (generation != 1)
        {
            throw new InvalidOperationException("Simulated RTSS generation changed.");
        }

        PerformanceValues values = _profiles.TryGetValue(rtssProfileName, out PerformanceValues? profile)
            && profile is not null
            ? profile
            : _profiles[string.Empty];
        return Task.FromResult(new RtssReadback(
            values,
            PerformanceReadbackQuality.Verified,
            PerformanceReadbackQuality.Verified,
            RtssTelemetryHealth.Healthy,
            DateTimeOffset.UtcNow));
    }

    public Task<RtssApplyResult> ApplyAsync(
        RtssApplyRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (request.Generation != 1 || !Capabilities.IsValid(request.Control, request.Value))
        {
            return Task.FromResult(new RtssApplyResult(false, "Simulated request is invalid."));
        }

        PerformanceValues current = _profiles.TryGetValue(
            request.RtssProfileName,
            out PerformanceValues? profile)
            && profile is not null
            ? profile
            : _profiles[string.Empty];
        _profiles[request.RtssProfileName] = current.With(request.Control, request.Value);
        return Task.FromResult(new RtssApplyResult(true, null));
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _profiles.Clear();
        return ValueTask.CompletedTask;
    }
}
