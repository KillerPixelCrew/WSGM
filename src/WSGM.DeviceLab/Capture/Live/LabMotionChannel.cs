using System;
using System.Collections.Generic;
using System.Threading;

namespace WSGM.DeviceLab.Capture.Live;

/// <summary>Running statistics of one field over a step.</summary>
/// <param name="Name">Field name, for example <c>x</c> or a legacy property key.</param>
/// <param name="Count">Finite values seen.</param>
/// <param name="Mean">Mean, when any value was seen.</param>
/// <param name="StdDev">Sample standard deviation, when at least two values were seen.</param>
/// <param name="Min">Smallest value.</param>
/// <param name="Max">Largest value.</param>
internal sealed record LabMotionFieldStats(
    string Name,
    long Count,
    double? Mean,
    double? StdDev,
    double? Min,
    double? Max);

/// <summary>What one sensor recorded during one step.</summary>
internal sealed record LabMotionChannelRecord
{
    /// <summary>Distinct readings received.</summary>
    public required long Count { get; init; }

    /// <summary>Polls that returned the previous reading again; not counted in <see cref="Count" />.</summary>
    public long Duplicates { get; init; }

    /// <summary>Host time between the first and the last reading, in milliseconds.</summary>
    public double? DurationMs { get; init; }

    /// <summary>Readings per second by host receipt time.</summary>
    public double? HostRateHz { get; init; }

    /// <summary>Readings per second by the sensor's own timestamps, when it has them.</summary>
    public double? SensorRateHz { get; init; }

    /// <summary>Every how many readings one was kept in <see cref="Samples" />.</summary>
    public int SampleStride { get; init; } = 1;

    /// <summary>Statistics per field, over every reading.</summary>
    public required IReadOnlyList<LabMotionFieldStats> Fields { get; init; }

    /// <summary>
    ///     Kept readings, each
    ///     <c>
    ///         [host ms since the step began, sensor ms since the first reading, field
    ///         values...]
    ///     </c>
    ///     ; null where a value is missing or not a number.
    /// </summary>
    public required IReadOnlyList<double?[]> Samples { get; init; }
}

/// <summary>
///     Accumulates one sensor's readings for a step without allocating per reading.
/// </summary>
/// <remarks>
///     Statistics cover every reading. Raw rows are kept in a fixed buffer of
///     <see cref="MaximumSamples" />; when it fills, every other row is dropped and the stride doubles,
///     so the kept rows always span the whole step evenly. Readings arriving outside a step are ignored.
///     Safe to feed from one sensor thread while another thread begins and ends steps.
/// </remarks>
internal sealed class LabMotionChannel
{
    /// <summary>Most raw rows kept per step.</summary>
    public const int MaximumSamples = 2000;

    private const long NoTimestamp = long.MinValue;

    private readonly Lock _gate = new();
    private readonly double[] _latest;
    private readonly double[] _m2;
    private readonly double[] _max;
    private readonly double[] _mean;
    private readonly double[] _min;
    private readonly string[] _names;
    private readonly double[] _rows;
    private readonly long[] _valid;
    private readonly int _width;
    private bool _active;
    private long _count;
    private long _duplicates;
    private double _firstHost;
    private long _firstSensor = NoTimestamp;
    private double _lastHost;
    private long _lastSensor = NoTimestamp;
    private long _sensorCount;
    private double _stepStart;
    private int _stored;
    private int _stride = 1;

    /// <summary>Creates a channel for a fixed set of fields.</summary>
    /// <param name="names">Field names, in the order readings supply them.</param>
    public LabMotionChannel(IReadOnlyList<string> names)
    {
        _names = [.. names];
        _width = 2 + _names.Length;
        _rows = new double[MaximumSamples * _width];
        _mean = new double[_names.Length];
        _m2 = new double[_names.Length];
        _min = new double[_names.Length];
        _max = new double[_names.Length];
        _valid = new long[_names.Length];
        _latest = new double[_names.Length];
    }

    /// <summary>Number of fields per reading.</summary>
    public int FieldCount => _names.Length;

    /// <summary>Whether a step is being recorded.</summary>
    public bool Active
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    /// <summary>Clears everything and starts recording.</summary>
    /// <param name="hostMs">Host time the step began, in milliseconds.</param>
    public void Begin(double hostMs)
    {
        lock (_gate)
        {
            _active = true;
            _stepStart = hostMs;
            _count = 0;
            _duplicates = 0;
            _stored = 0;
            _stride = 1;
            _sensorCount = 0;
            _firstSensor = NoTimestamp;
            _lastSensor = NoTimestamp;
            Array.Clear(_mean);
            Array.Clear(_m2);
            Array.Clear(_valid);
            Array.Fill(_min, double.PositiveInfinity);
            Array.Fill(_max, double.NegativeInfinity);
        }
    }

    /// <summary>Adds one reading; ignored outside a step.</summary>
    /// <param name="hostMs">Host receipt time in milliseconds.</param>
    /// <param name="sensorTicks">Sensor timestamp in 100 ns ticks, or <see cref="long.MinValue" /> when none.</param>
    /// <param name="values">Field values; NaN where a value is missing.</param>
    public void Add(double hostMs, long sensorTicks, ReadOnlySpan<double> values)
    {
        lock (_gate)
        {
            if (!_active)
            {
                return;
            }

            if (_count == 0)
            {
                _firstHost = hostMs;
            }

            _lastHost = hostMs;
            if (sensorTicks != NoTimestamp)
            {
                if (_firstSensor == NoTimestamp)
                {
                    _firstSensor = sensorTicks;
                }

                _lastSensor = sensorTicks;
                _sensorCount++;
            }

            var fields = Math.Min(values.Length, _names.Length);
            for (var i = 0; i < fields; i++)
            {
                var value = values[i];
                if (!double.IsFinite(value))
                {
                    continue;
                }

                var n = ++_valid[i];
                var delta = value - _mean[i];
                _mean[i] += delta / n;
                _m2[i] += delta * (value - _mean[i]);
                _min[i] = Math.Min(_min[i], value);
                _max[i] = Math.Max(_max[i], value);
            }

            if (_count % _stride == 0)
            {
                if (_stored == MaximumSamples)
                {
                    Compact();
                }

                // Compacting doubles the stride, so this reading may no longer be on it.
                if (_count % _stride == 0)
                {
                    var row = _stored * _width;
                    _rows[row] = hostMs - _stepStart;
                    _rows[row + 1] = sensorTicks == NoTimestamp || _firstSensor == NoTimestamp
                        ? double.NaN
                        : (sensorTicks - _firstSensor) / (double)TimeSpan.TicksPerMillisecond;
                    for (var i = 0; i < _names.Length; i++)
                    {
                        _rows[row + 2 + i] = i < values.Length ? values[i] : double.NaN;
                    }

                    _stored++;
                }
            }

            for (var i = 0; i < _names.Length; i++)
            {
                _latest[i] = i < values.Length ? values[i] : double.NaN;
            }

            _count++;
        }
    }

    /// <summary>Copies the latest reading of the current step, for live movement detection.</summary>
    /// <param name="values">Receives <see cref="FieldCount" /> values.</param>
    /// <returns>False when the step has no reading yet.</returns>
    public bool TryLatest(Span<double> values)
    {
        lock (_gate)
        {
            if (!_active || _count == 0)
            {
                return false;
            }

            _latest.AsSpan(0, Math.Min(values.Length, _latest.Length)).CopyTo(values);
            return true;
        }
    }

    /// <summary>Counts a poll that returned the previous reading; ignored outside a step.</summary>
    public void CountDuplicate()
    {
        lock (_gate)
        {
            if (_active)
            {
                _duplicates++;
            }
        }
    }

    /// <summary>Stops recording and returns what the step recorded.</summary>
    /// <returns>The record.</returns>
    public LabMotionChannelRecord End()
    {
        lock (_gate)
        {
            _active = false;
            List<LabMotionFieldStats> fields = new(_names.Length);
            for (var i = 0; i < _names.Length; i++)
            {
                var n = _valid[i];
                fields.Add(new LabMotionFieldStats(
                    _names[i],
                    n,
                    n > 0 ? Round(_mean[i]) : null,
                    n > 1 ? Round(Math.Sqrt(_m2[i] / (n - 1))) : null,
                    n > 0 ? Round(_min[i]) : null,
                    n > 0 ? Round(_max[i]) : null));
            }

            List<double?[]> samples = new(_stored);
            for (var row = 0; row < _stored; row++)
            {
                var values = new double?[_width];
                for (var i = 0; i < _width; i++)
                {
                    var value = _rows[row * _width + i];
                    values[i] = double.IsFinite(value) ? Round(value) : null;
                }

                samples.Add(values);
            }

            var duration = _count > 1 ? _lastHost - _firstHost : 0;
            var sensorSpan = _sensorCount > 1
                ? (_lastSensor - _firstSensor) / (double)TimeSpan.TicksPerSecond
                : 0;
            return new LabMotionChannelRecord
            {
                Count = _count,
                Duplicates = _duplicates,
                DurationMs = _count > 1 ? Math.Round(duration, 2) : null,
                HostRateHz = duration > 0 ? Math.Round((_count - 1) * 1000 / duration, 2) : null,
                SensorRateHz = sensorSpan > 0 ? Math.Round((_sensorCount - 1) / sensorSpan, 2) : null,
                SampleStride = _stride,
                Fields = fields,
                Samples = samples
            };
        }
    }

    private void Compact()
    {
        var kept = MaximumSamples / 2;
        for (var row = 1; row < kept; row++)
        {
            Array.Copy(_rows, row * 2 * _width, _rows, row * _width, _width);
        }

        _stored = kept;
        _stride *= 2;
    }

    private static double Round(double value)
    {
        return Math.Round(value, 6);
    }
}
