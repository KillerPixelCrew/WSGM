using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using WSGM.DeviceLab.Windows;
using WSGM.Interop;
using static WSGM.DeviceLab.Capture.Live.LabSensorInterop;

namespace WSGM.DeviceLab.Capture.Live;

// HID: every collection is looked at once. Sensor-page (0x20) collections are listed for the inventory;
// their readings reach the stage through the sensor APIs. Collections of controllers HC decodes an IMU
// from are opened for reading only and decoded with LabMotionDecoders. Every raw report from every
// device also reaches the stage through the shared input capture, for the generic correlation.
internal sealed partial class LabMotionRecorder
{
    private void OpenHid()
    {
        try
        {
            foreach (var collection in HidDevices.EnumerateAll((_, _) => _hidUnopened++))
            {
                InspectHid(collection);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or Win32Exception)
        {
            _unavailable.Add($"hid: {ex.Message}");
        }
    }

    private void InspectHid(HidCollection collection)
    {
        if (collection.UsagePage == 0x20)
        {
            _hid.Add(new LabHidSensorCollection
            {
                Path = collection.DevicePath,
                VendorId = collection.VendorId.ToString("X4"),
                ProductId = collection.ProductId.ToString("X4"),
                Usage = collection.Usage,
                InputReportBytes = collection.InputLength,
                FeatureReportBytes = collection.FeatureLength,
                Values = SensorValues(collection)
            });
            return;
        }

        var layouts = LabMotionDecoders
            .LayoutsFor(collection.VendorId, collection.ProductId, collection.InputLength)
            .ToList();
        if (layouts.Count > 0)
        {
            OpenController(collection, layouts);
        }
    }

    // The declared input and feature values of a sensor collection.
    private IReadOnlyList<LabHidValueCap> SensorValues(HidCollection collection)
    {
        try
        {
            return
            [
                .. HidDevices.Inspect(collection).Capabilities
                    .Where(cap => !cap.IsButton && cap.ReportType is HidReportType.Input or HidReportType.Feature)
                    .Select(cap => new LabHidValueCap(cap.ReportType == HidReportType.Input ? "input" : "feature",
                        cap.UsagePage, cap.UsageMin, cap.UsageMax != cap.UsageMin ? cap.UsageMax : null,
                        cap.ReportId, cap.BitSize, cap.ReportCount, cap.LogicalMin, cap.LogicalMax,
                        cap.UnitsExponent, cap.Units))
            ];
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            _unavailable.Add($"hid sensor values: {ex.Message}");
            return [];
        }
    }

    private void OpenController(HidCollection collection, IReadOnlyList<LabControllerImuLayout> layouts)
    {
        var path = collection.DevicePath;
        var index = _listed.Count(item => item.Source == "hid") / 2;
        var device = $"{collection.VendorId:X4}:{collection.ProductId:X4}";
        List<(LabControllerImuLayout Layout, LabMotionChannel Accelerometer, LabMotionChannel Gyrometer)> targets = [];
        List<LabMotionSensorInfo> infos = [];
        foreach (var layout in layouts)
        {
            var id = $"hid-{layout.Name}{index}";
            LabMotionSensorInfo accelerometer = new()
            {
                Id = id + "-accelerometer",
                Source = "hid",
                Kind = LabMotionSensorKind.Accelerometer,
                Name = $"{layout.Name} {device} accelerometer",
                Path = path,
                Fields = ["a0", "a1", "a2"],
                AxisFields = [0, 1, 2],
                AxisSource = "hc-layout",
                Units = "g",
                Note = $"{layout.Reference}. {layout.Note}".Trim()
            };
            var gyrometer = accelerometer with
            {
                Id = id + "-gyrometer",
                Kind = LabMotionSensorKind.Gyrometer,
                Name = $"{layout.Name} {device} gyro",
                Fields = ["g0", "g1", "g2"],
                Units = "deg/s"
            };
            infos.Add(accelerometer);
            infos.Add(gyrometer);
            targets.Add((layout, new LabMotionChannel(accelerometer.Fields), new LabMotionChannel(gyrometer.Fields)));
        }

        var handle = Kernel32.CreateFileW(path, Kernel32.GenericRead,
            Kernel32.FileShareRead | Kernel32.FileShareWrite, 0, Kernel32.OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            _listed.AddRange(infos.Select(info =>
                info with { Problem = $"could not be opened for reading (error {error})" }));
            return;
        }

        HidReader reader = new(handle, collection.InputLength, [.. targets], () => Now);
        for (var i = 0; i < targets.Count; i++)
        {
            var accelerometer = infos[2 * i] with { Sampled = true };
            var gyrometer = infos[2 * i + 1] with { Sampled = true };
            _listed.Add(accelerometer);
            _listed.Add(gyrometer);
            _channels.Add((accelerometer, targets[i].Accelerometer, reader));
            _channels.Add((gyrometer, targets[i].Gyrometer, reader));
        }

        reader.Start($"hid-{index}");
    }

    // Reads one controller collection on its own thread. Every open handle receives its own copy of
    // each input report, so reading takes nothing from the game or the driver. Nothing is written.
    private sealed class HidReader : IMotionSource
    {
        private readonly byte[] _buffer;
        private readonly SafeFileHandle _handle;
        private readonly Func<double> _now;

        private readonly (LabControllerImuLayout Layout, LabMotionChannel Accelerometer, LabMotionChannel Gyrometer)[]
            _targets;

        private long _failures;
        private int _lastError;
        private volatile bool _stop;
        private Thread? _thread;

        public HidReader(
            SafeFileHandle handle,
            int reportLength,
            (LabControllerImuLayout Layout, LabMotionChannel Accelerometer, LabMotionChannel Gyrometer)[] targets,
            Func<double> now)
        {
            _handle = handle;
            _buffer = new byte[Math.Max(reportLength, 1)];
            _targets = targets;
            _now = now;
        }

        public long Failures => Interlocked.Read(ref _failures);

        public string? LastError => _lastError == 0 ? null : $"ReadFile failed (error {_lastError})";

        public IReadOnlyList<int>? FieldTypes => null;

        public string Freshness => "input report";

        public void ResetCounters()
        {
            Interlocked.Exchange(ref _failures, 0);
            _lastError = 0;
        }

        public void RequestStop()
        {
            _stop = true;
            CancelIoEx(_handle, 0);
        }

        public void StopAndRelease()
        {
            RequestStop();
            if (_thread is null || _thread.Join(TimeSpan.FromSeconds(2)))
            {
                _handle.Dispose();
            }
        }

        public void Start(string id)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = $"Device Lab motion {id}" };
            _thread.Start();
        }

        private unsafe void Run()
        {
            Span<double> accelerometer = stackalloc double[3];
            Span<double> gyrometer = stackalloc double[3];
            while (!_stop)
            {
                uint read;
                bool ok;
                fixed (byte* pointer = _buffer)
                {
                    ok = ReadFile(_handle, pointer, (uint)_buffer.Length, out read, 0);
                }

                if (!ok)
                {
                    if (_stop)
                    {
                        return;
                    }

                    Interlocked.Increment(ref _failures);
                    _lastError = Marshal.GetLastPInvokeError();
                    Thread.Sleep(50);
                    continue;
                }

                var report = _buffer.AsSpan(0, (int)read);
                foreach (var (layout, accelerometerChannel, gyrometerChannel) in _targets)
                {
                    if (!accelerometerChannel.Active
                        || !LabMotionDecoders.TryDecode(layout, report, accelerometer, gyrometer))
                    {
                        continue;
                    }

                    var now = _now();
                    accelerometerChannel.Add(now, long.MinValue, accelerometer);
                    gyrometerChannel.Add(now, long.MinValue, gyrometer);
                }
            }
        }
    }
}
