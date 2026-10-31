using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>In-memory RTSS adapter used only by the safe overlay-test mode.</summary>
internal sealed class SimulatedRtssAdapter : IRtssAdapter
{
    private static readonly RtssCapabilities Capabilities = new(
        0,
        240,
        new HashSet<int> { 0, 1, 2, 3, 4 });

    private readonly Dictionary<string, PerformanceValues> _profiles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = new PerformanceValues(60, 2)
        };

    private bool _disposed;

    /// <inheritdoc />
    public void ClearOverlay()
    {
        foreach (var name in _profiles.Keys.ToArray())
        {
            _profiles[name] = _profiles[name] with { OverlayLevel = 0 };
        }
    }

    /// <inheritdoc />
    public void ApplyOsdCustomization(RtssOsdCustomSettings settings)
    {
        // No renderer here; the simulated adapter never draws.
    }

    /// <inheritdoc />
    public void ApplyOsdPowerStatus(RtssOsdPowerStatus status)
    {
        // No renderer here; the simulated adapter never draws.
    }

    /// <inheritdoc />
    public RtssOsdMetrics SampleSensors()
    {
        // No provider either. Absent utilization simply narrows AutoTDP to frame delivery.
        return RtssOsdMetrics.Empty;
    }

    /// <inheritdoc />
    public bool ProfileExists(string rtssProfileName)
    {
        return rtssProfileName.Length == 0 || _profiles.ContainsKey(rtssProfileName);
    }

    /// <inheritdoc />
    public Task<RtssProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.FromResult(new RtssProbe(
            RtssAvailability.Ready,
            "overlay-test",
            null,
            1,
            Capabilities,
            "Simulated RTSS state; no external process or profile is accessed."));
    }

    /// <inheritdoc />
    public Task<RtssReadback> ReadAsync(
        string rtssProfileName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var values = _profiles.TryGetValue(rtssProfileName, out var profile)
            ? profile
            : _profiles[string.Empty];
        return Task.FromResult(new RtssReadback(values, DateTimeOffset.UtcNow,
            values.OverlayLevel is > 0 ? true : null));
    }

    /// <inheritdoc />
    public Task<RtssApplyResult> ApplyAsync(
        RtssApplyRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Capabilities.IsValid(request.Control, request.Value))
        {
            return Task.FromResult(new RtssApplyResult(false, "Simulated request is invalid."));
        }

        var current = _profiles.TryGetValue(
            request.RtssProfileName,
            out var profile)
            ? profile
            : _profiles[string.Empty];
        _profiles[request.RtssProfileName] = current.With(request.Control, request.Value);
        return Task.FromResult(new RtssApplyResult(true, null));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _profiles.Clear();
        return ValueTask.CompletedTask;
    }
}
