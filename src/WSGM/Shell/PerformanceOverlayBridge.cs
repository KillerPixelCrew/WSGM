using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Overlay;

namespace WSGM.Shell;

/// <summary>
/// Projects the session-owned performance service into closed overlay descriptors without owning
/// RTSS or retaining an overlay window.
/// </summary>
internal sealed class PerformanceOverlayBridge : IPerformanceOverlaySource, IDisposable
{
    private static readonly int[] PreferredFrameLimits = [0, 30, 40, 45, 60, 90, 120, 144, 165, 240];
    private readonly PerformanceService _service;
    private bool _disposed;

    internal PerformanceOverlayBridge(PerformanceService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _service.StateChanged += OnStateChanged;
    }

    public event Action? Changed;

    public IDisposable AcquireObservation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _service.AcquireObservation();
    }

    public PerformanceOverlaySnapshot Snapshot()
    {
        PerformanceState state = _service.Current;
        if (!_service.Enabled)
        {
            return new PerformanceOverlaySnapshot(false, string.Empty, []);
        }

        RtssCapabilities? capabilities = state.Probe.Capabilities;
        bool ready = state.Probe.Availability == RtssAvailability.Ready && capabilities is not null;
        List<DescriptorRow> rows = [];
        rows.Add(BuildRow(
            "frame-limit",
            "Frame limit",
            DescribeLayer(state.FrameLimitLayer, state.Target),
            FormatFrameLimit(state),
            ready && capabilities!.Supports(PerformanceControl.FrameLimit),
            StatusFor(state, PerformanceControl.FrameLimit)));
        rows.Add(BuildRow(
            "overlay-level",
            "Performance overlay",
            DescribeLayer(state.OverlayLevelLayer, state.Target),
            FormatOverlayLevel(state),
            ready && capabilities!.Supports(PerformanceControl.OverlayLevel),
            StatusFor(state, PerformanceControl.OverlayLevel)));
        return new PerformanceOverlaySnapshot(true, DescribeStatus(state), rows);
    }

    public async Task InvokeAsync(
        DescriptorRow row,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(row);
        PerformanceControl control = row.Id switch
        {
            "frame-limit" => PerformanceControl.FrameLimit,
            "overlay-level" => PerformanceControl.OverlayLevel,
            _ => throw new InvalidOperationException("The performance row is not actionable."),
        };
        PerformanceState state = _service.Current;
        RtssCapabilities capabilities = state.Probe.Capabilities
            ?? throw new InvalidOperationException("RTSS capabilities are unavailable.");
        int next = control == PerformanceControl.FrameLimit
            ? NextFrameLimit(state, capabilities)
            : NextOverlayLevel(state, capabilities);
        await _service.SetAsync(
            control,
            next,
            PerformancePersistenceTarget.Automatic,
            "overlay",
            Guid.NewGuid().ToString("N"),
            cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _service.StateChanged -= OnStateChanged;
    }

    private void OnStateChanged(PerformanceState _) => Changed?.Invoke();

    private static DescriptorRow BuildRow(
        string id,
        string title,
        string description,
        string trailing,
        bool canInvoke,
        DescriptorStatus status) => new(
        id,
        title,
        description,
        trailing,
        canInvoke,
        status);

    private static string DescribeStatus(PerformanceState state)
    {
        if (state.Command.Phase is PerformanceCommandPhase.Queued or PerformanceCommandPhase.Applying)
        {
            return "Applying RTSS performance setting…";
        }

        if (state.Command.Phase is PerformanceCommandPhase.Rejected
            or PerformanceCommandPhase.TimedOut
            or PerformanceCommandPhase.Indeterminate
            or PerformanceCommandPhase.Failed)
        {
            return state.Command.Diagnostic ?? "The last RTSS command did not complete.";
        }

        return state.Probe.Availability switch
        {
            RtssAvailability.Ready => state.Target is null
                ? "RTSS · global profile"
                : $"RTSS · {state.Target.RtssProfileName}",
            RtssAvailability.Unknown => "Checking RTSS…",
            RtssAvailability.NotInstalled => "RTSS is not installed.",
            RtssAvailability.NotRunning => "RTSS is not running.",
            RtssAvailability.Incompatible => "The installed RTSS version is not supported.",
            RtssAvailability.AdapterUnavailable => "The RTSS profile API is unavailable.",
            _ => state.Probe.Diagnostic ?? "RTSS performance controls are unavailable.",
        };
    }

    private static string DescribeLayer(
        PerformancePolicyLayer layer,
        RtssApplicationTarget? target) => layer switch
        {
            PerformancePolicyLayer.Application when target is not null =>
                $"Application override · {target.RtssProfileName}",
            PerformancePolicyLayer.Global => "Global default",
            _ => "RTSS profile",
        };

    private static string FormatFrameLimit(PerformanceState state)
    {
        int? value = PreferredValue(state, PerformanceControl.FrameLimit);
        return value switch
        {
            null => "Unavailable",
            0 => "Off",
            _ => string.Create(CultureInfo.InvariantCulture, $"{value} FPS"),
        };
    }

    private static string FormatOverlayLevel(PerformanceState state)
    {
        int? value = PreferredValue(state, PerformanceControl.OverlayLevel);
        return value switch
        {
            0 => "Off",
            1 => "On",
            null => "Unavailable",
            _ => value.Value.ToString(CultureInfo.InvariantCulture),
        };
    }

    private static int? PreferredValue(PerformanceState state, PerformanceControl control)
        => state.Observed.ValueFor(control) ?? state.Desired.ValueFor(control);

    private static DescriptorStatus StatusFor(PerformanceState state, PerformanceControl control)
    {
        if (state.Command.Control == control)
        {
            switch (state.Command.Phase)
            {
                case PerformanceCommandPhase.Queued:
                case PerformanceCommandPhase.Applying:
                    return DescriptorStatus.Progress;
                case PerformanceCommandPhase.Rejected:
                case PerformanceCommandPhase.TimedOut:
                case PerformanceCommandPhase.Indeterminate:
                case PerformanceCommandPhase.Failed:
                    return DescriptorStatus.Faulted;
            }
        }

        PerformanceReadbackQuality quality = control == PerformanceControl.FrameLimit
            ? state.FrameLimitQuality
            : state.OverlayLevelQuality;
        return quality switch
        {
            PerformanceReadbackQuality.Verified => DescriptorStatus.Available,
            PerformanceReadbackQuality.AppliedUnverified => DescriptorStatus.Warning,
            PerformanceReadbackQuality.Stale => DescriptorStatus.Stale,
            _ => state.Probe.Availability == RtssAvailability.Ready
                ? DescriptorStatus.Warning
                : DescriptorStatus.Unsupported,
        };
    }

    private static int NextFrameLimit(PerformanceState state, RtssCapabilities capabilities)
    {
        int current = PreferredValue(state, PerformanceControl.FrameLimit)
            ?? capabilities.MinimumFrameLimit;
        int[] choices = PreferredFrameLimits
            .Where(value => capabilities.IsValid(PerformanceControl.FrameLimit, value))
            .Distinct()
            .Order()
            .ToArray();
        if (choices.Length == 0)
        {
            throw new InvalidOperationException("RTSS published no usable frame-limit values.");
        }

        return choices.FirstOrDefault(value => value > current, choices[0]);
    }

    private static int NextOverlayLevel(PerformanceState state, RtssCapabilities capabilities)
    {
        int current = PreferredValue(state, PerformanceControl.OverlayLevel) ?? int.MinValue;
        int[] choices = capabilities.OverlayLevels.Order().ToArray();
        if (choices.Length == 0)
        {
            throw new InvalidOperationException("RTSS published no usable overlay levels.");
        }

        return choices.FirstOrDefault(value => value > current, choices[0]);
    }
}
