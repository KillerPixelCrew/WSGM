using System;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>
///     Renders the WSGM-owned overlay levels into RTSS's OSD and carries the whole selector state:
///     0 clears and parks the loop, 1 to 3 render the fixed presets, and 4 renders the
///     user-configured Custom layout. The loop wakes at each clock second, the metrics' own cadence,
///     and immediately when the level, power status or Custom layout changes.
/// </summary>
internal sealed class RtssOsdRenderer : IAsyncDisposable
{
    private readonly SystemMetricsSource _metrics;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _wake = new(0, 1);

    private readonly RtssOsdWriter _writer = new();
    private readonly Func<string, bool> _write;
    private readonly Func<RtssOsdMetrics> _sample;
    private volatile RtssOsdCustomSettings _custom = RtssOsdCustomSettings.Default;
    private bool _disposed;
    private volatile int _level;
    private Task? _loop;
    private volatile RtssOsdPowerStatus _powerStatus = RtssOsdPowerStatus.Empty;

    /// <summary>Creates an OSD renderer with its own sensor source and mapping writer.</summary>
    /// <param name="write">Optional isolated OSD sink; null uses the owned shared-memory writer.</param>
    /// <param name="sample">Optional isolated telemetry source; null uses the owned sensor source.</param>
    internal RtssOsdRenderer(Func<string, bool>? write = null, Func<RtssOsdMetrics>? sample = null)
    {
        _metrics = new SystemMetricsSource();
        _write = write ?? _writer.TryUpdate;
        _sample = sample ?? _metrics.Sample;
    }

    /// <summary>Gets the level currently rendered — the adapter's overlay readback.</summary>
    internal int Level => _level;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }

        try
        {
            await (_loop ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the normal exit.
        }
        catch (TimeoutException)
        {
            Log.Warn("RTSS renderer shutdown is still running; its resources are retained.");
            Log.Observe(RetireAsync(_loop!), "Late RTSS renderer shutdown", true);
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("RTSS renderer loop failed during shutdown", ex);
        }

        ReleaseResources();
    }

    /// <summary>Starts the single rendering loop; repeated calls reuse the existing task.</summary>
    internal void Start()
    {
        _loop ??= Task.Run(RenderLoopAsync);
    }

    private async Task RetireAsync(Task loop)
    {
        try
        {
            await loop.ConfigureAwait(false);
        }
        finally
        {
            ReleaseResources();
        }
    }

    private void ReleaseResources()
    {
        _writer.Dispose();
        _metrics.Dispose();
        _shutdown.Dispose();
    }

    /// <summary>Reads the shared sensor sample, whatever the renderer's own level is.</summary>
    /// <returns>The latest sample, cached for a second by the source.</returns>
    /// <remarks>
    ///     AutoTDP classifies stalls with the same sample the OSD draws, so the source, its cache and
    ///     its provider-start cooldown are shared rather than duplicated. At OSD level 0 the render
    ///     loop never samples, and this is then the only caller keeping the source current.
    /// </remarks>
    internal RtssOsdMetrics SampleSensors()
    {
        return _disposed ? RtssOsdMetrics.Empty : _metrics.Sample();
    }

    /// <summary>
    ///     Applies the Custom overlay's configuration; live at once when level 4 is showing.
    /// </summary>
    /// <param name="settings">The widget order and per-widget detail.</param>
    internal void ApplyCustom(RtssOsdCustomSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _custom = settings;
        Wake();
    }

    /// <summary>Applies the latest device and AutoTDP projection; live at once.</summary>
    /// <param name="status">The cached power status from the session owner.</param>
    internal void ApplyPowerStatus(RtssOsdPowerStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        _powerStatus = status;
        Wake();
    }

    /// <summary>Switches the selector level.</summary>
    /// <param name="level">
    ///     0 clears; 1 to 3 render the presets, 4 the Custom layout. Values
    ///     outside are clamped into range.
    /// </param>
    internal void SetLevel(int level)
    {
        var bounded = Math.Clamp(level, 0, 4);
        if (_level == bounded)
        {
            return;
        }

        _level = bounded;
        Log.Change("rtss-osd-level", $"RTSS OSD level {bounded}.");
        Wake();
    }

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // A pending wake already covers this change.
        }
    }

    // Waits for an explicit change or the next clock second, whichever comes first. Both the
    // clock text and the cached metrics change at most once a second.
    private Task WaitForNextTickAsync(CancellationToken cancellationToken)
    {
        var untilNextSecond = TimeSpan.TicksPerSecond - DateTime.UtcNow.Ticks % TimeSpan.TicksPerSecond;
        return _wake.WaitAsync(TimeSpan.FromTicks(untilNextSecond), cancellationToken);
    }

    private async Task RenderLoopAsync()
    {
        var cancellationToken = _shutdown.Token;
        var cleared = true;
        var written = false;
        var writtenLevel = 0;
        long writtenSecond = 0;
        RtssOsdMetrics? writtenSample = null;
        RtssOsdPowerStatus? writtenPowerStatus = null;
        RtssOsdCustomSettings? writtenCustom = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var level = _level;
                if (level == 0)
                {
                    if (!cleared)
                    {
                        cleared = _write(string.Empty);
                        written = false;
                        if (!cleared)
                        {
                            await WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                    }

                    await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    // Publish the new layout before collecting optional telemetry. Level 1 is
                    // entirely RTSS-owned FPS text and must never wait for sensor/provider work.
                    if (!written || level != writtenLevel)
                    {
                        written = _write(level == 4
                            ? RtssOsdContent.BuildCustom(_custom, RtssOsdMetrics.Empty, _powerStatus)
                            : RtssOsdContent.Build(level, RtssOsdMetrics.Empty, _powerStatus));
                        if (written)
                        {
                            cleared = false;
                        }
                    }

                    var sample = level == 1 ? RtssOsdMetrics.Empty : _sample();
                    var powerStatus = _powerStatus;
                    var custom = _custom;
                    var second = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
                    // An explicit wake can arrive inside an already written second, so unchanged
                    // text is not rewritten. The second counts as a change: the clock stays current,
                    // and a write still lands every second to reclaim the slot after an RTSS restart.
                    if (!written || level != writtenLevel || second != writtenSecond
                        || sample != writtenSample || powerStatus != writtenPowerStatus
                        || !ReferenceEquals(custom, writtenCustom))
                    {
                        written = _write(level == 4
                            ? RtssOsdContent.BuildCustom(custom, sample, powerStatus)
                            : RtssOsdContent.Build(level, sample, powerStatus));
                        writtenLevel = level;
                        writtenSecond = second;
                        writtenSample = sample;
                        writtenPowerStatus = powerStatus;
                        writtenCustom = custom;
                        if (written)
                        {
                            cleared = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    written = false;
                    // One bad sample or write must not silently kill the renderer for the
                    // session; the next tick retries with fresh state.
                    Log.Change(
                        "rtss-osd-tick",
                        $"RTSS OSD render tick failed: {ex.GetType().Name}: {ex.Message}");
                }

                await WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            Log.Error("RTSS OSD renderer stopped unexpectedly", ex);
        }
    }
}
