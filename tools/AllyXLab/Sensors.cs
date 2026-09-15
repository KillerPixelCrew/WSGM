using System.Runtime.InteropServices;

namespace WSGM.AllyXLab;

internal sealed class Sensors : IDisposable
{
    private readonly List<(ISensor Sensor, string Id, PROPERTYKEY[] Fields)> _sensors = [];
    private readonly Dictionary<string, string> _last = [];
    private readonly Dictionary<string, Stats> _stats = [];
    private readonly SessionLog _log;
    private int _duplicateReports;
    private sealed class Stats
    {
        internal int Count;
        internal double Mean, M2, Min = double.PositiveInfinity, Max = double.NegativeInfinity;
        internal void Add(double value)
        {
            Count++; double delta = value - Mean; Mean += delta / Count; M2 += delta * (value - Mean);
            Min = Math.Min(Min, value); Max = Math.Max(Max, value);
        }
    }
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT value);
    internal Sensors(SessionLog log)
    {
        _log = log;
        object? manager = null;
        ISensorCollection? collection = null;
        try
        {
            manager = new SensorManagerClass();
            Guid all = new("C317C286-C468-4288-9975-D4C4587C442C");
            Marshal.ThrowExceptionForHR(((ISensorManager)manager).GetSensorsByCategory(ref all, out collection));
            Marshal.ThrowExceptionForHR(collection.GetCount(out uint count));
            for (uint i = 0; i < Math.Min(count, 32); i++)
            {
                if (collection.GetAt(i, out var sensor) < 0)
                {
                    continue;
                }

                bool retained = false;
                IPortableDeviceKeyCollection? keys = null;
                try
                {
                    sensor.GetFriendlyName(out string name);
                    sensor.GetType(out Guid type);
                    sensor.GetState(out int state);
                    sensor.GetID(out Guid sensorId);
                    string id = "sensor-" + SessionLog.Token(sensorId.ToString());
                    _log.Add("sensor", new { Id = id, Name = name, Type = type, State = state });
                    bool motion = name.Contains("accel", StringComparison.OrdinalIgnoreCase) || name.Contains("gyro", StringComparison.OrdinalIgnoreCase)
                        || type == new Guid("C2FB0F5F-E2D2-4C78-BCD0-352A9582819D") || type == new Guid("09485F5A-759E-42C2-BD4B-A349B75C8643");
                    if (!motion || sensor.GetSupportedDataFields(out keys) < 0)
                    {
                        continue;
                    }

                    keys.GetCount(out uint fields);
                    List<PROPERTYKEY> selected = [];
                    for (uint k = 0; k < Math.Min(fields, 96); k++)
                    {
                        PROPERTYKEY key = new();
                        if (keys.GetAt(k, ref key) >= 0)
                        {
                            selected.Add(key);
                        }
                    }
                    _log.Add("sensor-fields", new { Id = id, Fields = selected.Select(k => $"{k.fmtid}:{k.pid}").ToArray() });
                    _sensors.Add((sensor, id, selected.ToArray())); retained = true;
                }
                finally
                {
                    if (keys is not null)
                    {
                        Marshal.ReleaseComObject(keys);
                    }

                    if (!retained)
                    {
                        Marshal.ReleaseComObject(sensor);
                    }
                }
            }
        }
        catch (Exception e) { _log.Add("sensor-unavailable", e.Message); }
        finally
        {
            if (collection is not null)
            {
                Marshal.ReleaseComObject(collection);
            }

            if (manager is not null)
            {
                Marshal.ReleaseComObject(manager);
            }
        }
    }
    internal void Poll()
    {
        foreach (var item in _sensors)
        {
            ISensorDataReport? report = null;
            try
            {
                int hr = item.Sensor.GetData(out report);
                if (hr < 0 || report is null)
                {
                    continue;
                }

                int timestampResult = report.GetTimestamp(out SYSTEMTIME timestamp);
                Dictionary<string, double> values = [];
                foreach (var field in item.Fields)
                {
                    PROPERTYKEY key = field;
                    if (report.GetSensorValue(ref key, out PROPVARIANT value) < 0)
                    {
                        continue;
                    }

                    try
                    {
                        double? number = value.vt switch
                        {
                            2 => value.i2,
                            3 => value.i4,
                            4 => value.r4,
                            5 => value.r8,
                            16 => value.i1,
                            17 => value.ui1,
                            18 => value.ui2,
                            19 => value.ui4,
                            20 => value.i8,
                            21 => value.ui8,
                            _ => null,
                        };
                        if (number is { } n && double.IsFinite(n))
                        {
                            values[$"{key.fmtid}:{key.pid}:vt{value.vt}"] = n;
                        }
                    }
                    finally { PropVariantClear(ref value); }
                }
                string stamp = $"{timestamp.Year}-{timestamp.Month}-{timestamp.Day}T{timestamp.Hour}:{timestamp.Minute}:{timestamp.Second}.{timestamp.Milliseconds}";
                // Retain data even when a driver has no usable timestamp/counter, but mark its
                // freshness unknown instead of treating host polls as independent measurements.
                bool timestampValid = timestampResult >= 0 && timestamp.Year >= 2000 && timestamp.Month is >= 1 and <= 12;
                var counters = values.Where(v => v.Key.Contains(":34:", StringComparison.Ordinal)).Select(v => v.Value).ToArray();
                bool hasIdentity = timestampValid || counters.Length > 0;
                string identity = (timestampValid ? stamp : "") + "|" + string.Join(',', counters);
                if (hasIdentity && _last.GetValueOrDefault(item.Id) == identity) { _duplicateReports++; continue; }
                if (hasIdentity)
                {
                    _last[item.Id] = identity;
                }

                _log.Add("motion", new
                {
                    Sensor = item.Id,
                    SensorTimestamp = timestampValid ? stamp : null,
                    Freshness = hasIdentity ? "timestamp-or-counter-changed" : "unknown; host poll only",
                    Values = values
                });
                foreach (var value in values)
                {
                    string key = item.Id + "/" + value.Key;
                    if (!_stats.TryGetValue(key, out var stats))
                    {
                        _stats[key] = stats = new();
                    }

                    stats.Add(value.Value);
                }
            }
            catch (Exception e) { _log.Add("sensor-read-error", e.Message); }
            finally
            {
                if (report is not null)
                {
                    Marshal.ReleaseComObject(report);
                }
            }
        }
    }
    internal void Summarize(string step = "")
    {
        _log.Add("motion-statistics", new
        {
            Step = step,
            DuplicateReports = _duplicateReports,
            Fields = _stats.Select(k => new { Field = k.Key, k.Value.Count, k.Value.Mean, StdDev = k.Value.Count > 1 ? Math.Sqrt(k.Value.M2 / (k.Value.Count - 1)) : 0, k.Value.Min, k.Value.Max }).ToArray(),
            Interpretation = "Stationary means are bias candidates only. Pose captures establish gravity/axis mapping. Units, freshness and stationary acceptance require review; no calibration is written to firmware.",
        });
    }
    public void Dispose()
    {
        foreach (var item in _sensors)
        {
            Marshal.ReleaseComObject(item.Sensor);
        }

        _sensors.Clear();
    }
}
