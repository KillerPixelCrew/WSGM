using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Owns the AutoTDP trace: one CSV per control generation while the trace is switched on.</summary>
/// <remarks>
///     <see cref="AutoTdpService" /> fills a row with what it already knows; this adds the context
///     around it — timing, Windows power state, sensors, probe numbering, duplicate RTSS windows — and
///     queues it. Nothing here feeds back into control. Sensors are sampled after the decision and
///     its write, so a slow sensor read delays the row, never the decision.
///     <para>
///         One background task owns the file and writes every row. A manual change on the UI thread only
///         appends to a queue and never waits for the disk.
///     </para>
/// </remarks>
internal sealed class AutoTdpTraceRecorder : IAsyncDisposable
{
    private readonly Func<(string? Package, string? Version)> _device;
    private readonly string _directory;
    private readonly AutoTdpTraceSystemContext? _context;
    private readonly Lock _gate = new();

    private readonly Channel<Entry> _queue = Channel.CreateUnbounded<Entry>(new UnboundedChannelOptions
    {
        SingleReader = true
    });

    private readonly Task _writerTask;
    private readonly string _wsgmVersion;
    private bool _disposed;
    private volatile bool _enabled;
    private long? _lastDecisionTimestamp;
    private RtssFrametimeSample? _lastFrametime;
    private long? _lastTickTimestamp;
    private long? _lastWriteTimestamp;
    private (string? Package, string? Version) _package;
    private long _probeId;
    private long _row;
    private long _startTimestamp;
    private string? _traceId;

    /// <summary>Creates a recorder and starts its writer.</summary>
    /// <param name="directory">Folder the CSV files go into.</param>
    /// <param name="device">The installed device package identity.</param>
    /// <param name="context">Windows power and sensor sampling owned by the recorder, or null for none.</param>
    internal AutoTdpTraceRecorder(
        string directory,
        Func<(string? Package, string? Version)> device,
        AutoTdpTraceSystemContext? context)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(device);
        _directory = directory;
        _device = device;
        _context = context;
        _wsgmVersion = typeof(AutoTdpTraceRecorder).Assembly.GetName().Version?.ToString() ?? string.Empty;
        _writerTask = Task.Run(WriteAllAsync);
    }

    /// <summary>The default trace folder beside <c>wsgm.log</c>.</summary>
    internal static string DefaultDirectory => Path.Combine(Log.Directory, "autotdp-traces");

    /// <summary>Whether rows are currently recorded.</summary>
    internal bool Enabled => _enabled;

    /// <summary>The file the writer last created, once it exists.</summary>
    internal string? LastPath { get; private set; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _enabled = false;
            EndGenerationCore();
            _queue.Writer.TryComplete();
        }

        await _writerTask.ConfigureAwait(false);
        _context?.Dispose();
    }

    /// <summary>Switches recording on or off. Switching off ends the current file.</summary>
    /// <param name="enabled">Whether to record.</param>
    internal void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed || _enabled == enabled)
            {
                return;
            }

            _enabled = enabled;
            if (!enabled)
            {
                EndGenerationCore();
            }
        }
    }

    /// <summary>Ends the current file so the next row starts a new one.</summary>
    internal void EndGeneration()
    {
        lock (_gate)
        {
            EndGenerationCore();
        }
    }

    /// <summary>Waits until every row queued so far has reached the file.</summary>
    /// <returns>A task that completes once those rows are written.</returns>
    internal Task FlushAsync()
    {
        TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new Entry(null, null, drained)))
        {
            drained.TrySetResult();
        }

        return drained.Task;
    }

    /// <summary>Starts a row, or returns null when nothing is recorded.</summary>
    /// <param name="kind">What caused the row.</param>
    /// <returns>The row to fill and commit.</returns>
    internal AutoTdpTraceRow? Begin(AutoTdpTraceEvent kind)
    {
        return _enabled
            ? new AutoTdpTraceRow
            {
                Event = kind,
                WallClock = DateTimeOffset.UtcNow
            }
            : null;
    }

    /// <summary>Queues a row, adding its timing and context.</summary>
    /// <param name="row">The filled row; null is ignored.</param>
    internal void Commit(AutoTdpTraceRow? row)
    {
        if (row is null || !_enabled)
        {
            return;
        }

        var tick = row.Event == AutoTdpTraceEvent.Tick;
        (string? Package, string? Version) package = default;
        if (tick)
        {
            // Only the tick loop reads the device identity: event rows can be committed by a caller
            // that is inside another component's lock, and they reuse the last identity instead.
            _context?.Sample(row);
            package = _device();
        }

        row.WsgmVersion = _wsgmVersion;
        var now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (_disposed || !_enabled)
            {
                return;
            }

            if (tick)
            {
                _package = package;
            }

            (row.Package, row.PackageVersion) = _package;

            if (_traceId is null)
            {
                _traceId = Guid.NewGuid().ToString("N")[..8];
                _row = 0;
                _probeId = 0;
                _startTimestamp = now;
                _lastTickTimestamp = null;
                _lastDecisionTimestamp = null;
                _lastWriteTimestamp = null;
                _lastFrametime = null;
            }

            row.TraceId = _traceId;
            row.Row = ++_row;
            row.ElapsedMs = Stopwatch.GetElapsedTime(_startTimestamp, now).TotalMilliseconds;
            if (tick)
            {
                row.TickIntervalMs = Since(_lastTickTimestamp, now);
                _lastTickTimestamp = now;
                NoteFrametime(row);
            }

            row.SinceDecisionMs = Since(_lastDecisionTimestamp, now);
            row.SinceWriteMs = Since(_lastWriteTimestamp, now);
            if (row.Decision?.Action == AutoTdpAction.Probe)
            {
                _probeId++;
            }

            if (row.Controller?.IsProbing == true
                || row.Decision?.Action is AutoTdpAction.Probe or AutoTdpAction.Restore)
            {
                row.ProbeId = _probeId;
            }

            if (row.Decision is { RequiresWrite: true })
            {
                _lastDecisionTimestamp = now;
            }

            if (row.WriteDispatched == true)
            {
                _lastWriteTimestamp = now;
            }

            _queue.Writer.TryWrite(new Entry(row, _traceId, null));
        }
    }

    private static double? Since(long? then, long now)
    {
        return then is { } start ? Stopwatch.GetElapsedTime(start, now).TotalMilliseconds : null;
    }

    private void EndGenerationCore()
    {
        if (_traceId is null)
        {
            return;
        }

        _traceId = null;
        _queue.Writer.TryWrite(new Entry(null, null, null));
    }

    private void NoteFrametime(AutoTdpTraceRow row)
    {
        if (row.Frametime is not { } sample)
        {
            return;
        }

        if (_lastFrametime is { } previous && previous.ProcessId == sample.ProcessId)
        {
            row.WindowRepeat = previous.WindowStartTicks == sample.WindowStartTicks
                               && previous.WindowEndTicks == sample.WindowEndTicks;
            row.WindowGapMs = unchecked((int)(sample.WindowStartTicks - previous.WindowEndTicks));
        }

        _lastFrametime = sample;
    }

    private async Task WriteAllAsync()
    {
        AutoTdpTraceWriter? writer = null;
        string? writerTraceId = null;
        string? failedTraceId = null;
        long rows = 0;
        try
        {
            await foreach (var entry in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (entry.Drained is { } drained)
                {
                    writer?.Flush();
                    drained.TrySetResult();
                    continue;
                }

                if (entry.Row is not { } row)
                {
                    Close();
                    continue;
                }

                if (entry.TraceId == failedTraceId)
                {
                    continue;
                }

                if (writer is null || writerTraceId != entry.TraceId)
                {
                    Close();
                    writer = AutoTdpTraceWriter.Create(_directory, entry.TraceId!, DateTimeOffset.Now);
                    if (writer is null)
                    {
                        // One failed create drops that generation rather than retrying the disk for
                        // every row. The next generation tries again.
                        failedTraceId = entry.TraceId;
                        continue;
                    }

                    writerTraceId = entry.TraceId;
                    LastPath = writer.Path;
                    Log.Info($"AutoTDP trace writing {writer.Path}.");
                }

                writer.Write(AutoTdpTraceCsv.Format(row));
                rows++;
                if (!_queue.Reader.TryPeek(out _))
                {
                    writer.Flush();
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("The AutoTDP trace writer stopped", ex);
        }
        finally
        {
            Close();
        }

        return;

        void Close()
        {
            if (writer is null)
            {
                return;
            }

            Log.Info($"AutoTDP trace closed {writer.Path} after {rows} rows.");
            writer.Dispose();
            writer = null;
            writerTraceId = null;
            rows = 0;
        }
    }

    /// <summary>One queued item: a row, a file boundary when it has no row, or a drain marker.</summary>
    private sealed record Entry(AutoTdpTraceRow? Row, string? TraceId, TaskCompletionSource? Drained);
}

/// <summary>Samples the Windows power state and sensors for AutoTDP trace rows.</summary>
/// <remarks>
///     Read-only. The sensor source only reads what RTSS's provider already publishes and never starts
///     it, so enabling the trace starts no process.
/// </remarks>
internal sealed class AutoTdpTraceSystemContext : IDisposable
{
    private readonly RtssOsdMetricsSource _metrics = new();

    /// <inheritdoc />
    public void Dispose()
    {
        _metrics.Dispose();
    }

    /// <summary>Fills the power and sensor columns of a tick row.</summary>
    /// <param name="row">The row.</param>
    internal void Sample(AutoTdpTraceRow row)
    {
        try
        {
            if (WindowsPower.TryGetStatus(out var power))
            {
                row.AcPower = power.ACLineStatus switch
                {
                    0 => false,
                    1 => true,
                    _ => null
                };
                row.BatteryPercent = power.BatteryLifePercent == 255 ? null : power.BatteryLifePercent;
            }

            row.PowerMode = PowerModeName(WindowsPower.GetEffectiveMode());
            row.PowerScheme = WindowsPower.GetActiveScheme().ToString("D");
        }
        catch (Win32Exception)
        {
            // The trace records what it could read; a missing power field stays empty.
        }

        try
        {
            row.Metrics = _metrics.Sample();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Change("autotdp-trace-sensors", $"AutoTDP trace sensors unavailable: {ex.Message}");
        }
    }

    private static string PowerModeName(Guid mode)
    {
        if (mode == Guid.Empty)
        {
            return "balanced";
        }

        if (mode == WindowsPowerModes.Id(DevicePowerMode.BetterBattery))
        {
            return "better-battery";
        }

        return mode == WindowsPowerModes.Id(DevicePowerMode.BestPerformance)
            ? "best-performance"
            : mode.ToString("D");
    }
}
