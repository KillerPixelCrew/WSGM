using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using WSGM.DeviceLab.Knowledge;
using WSGM.Interop;
using static WSGM.DeviceLab.Wizard.LabSensorInterop;

namespace WSGM.DeviceLab.Wizard;

// The CH340 serial IMU some handhelds carry (HC Sensors/SerialUSBIMU.cs): 115200 8N1 with RTS on,
// 23-byte frames. The lab only reads. HC writes a register command when frames look wrong and has an
// unreachable calibration write; neither is sent here. A CH340 the knowledge record assigns to device
// control (the OXP X1 drives its LEDs through one) is never opened.
internal sealed partial class LabMotionRecorder
{
    private const uint RtsControlEnable = 1u << 12;
    private const uint BinaryMode = 1;

    private void OpenSerial(DeviceKnowledgeRecord? record)
    {
        string[] ports;
        try
        {
            ports = ListInterfaces(ComPortInterface);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _unavailable.Add($"serial: {ex.Message}");
            return;
        }

        var index = 0;
        foreach (var path in ports)
        {
            if (!path.Contains($"VID_{LabMotionDecoders.SerialVendorId:X4}&PID_{LabMotionDecoders.SerialProductId:X4}",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = $"serial-imu{index++}";
            LabMotionSensorInfo accelerometer = new()
            {
                Id = id + "-accelerometer",
                Source = "serial",
                Kind = LabMotionSensorKind.Accelerometer,
                Name = "CH340 serial IMU accelerometer",
                Path = path,
                Fields = ["a0", "a1", "a2"],
                AxisFields = [0, 1, 2],
                AxisSource = "hc-layout",
                Units = "g",
                Note = "HandheldCompanion.Sensors/SerialUSBIMU.cs: values in frame order, which HC reads as X, Z, Y."
            };
            var gyrometer = accelerometer with
            {
                Id = id + "-gyrometer",
                Kind = LabMotionSensorKind.Gyrometer,
                Name = "CH340 serial IMU gyro",
                Fields = ["g0", "g1", "g2"],
                Units = "deg/s"
            };
            if (LabMotionDecoders.SerialAssignedToControl(record))
            {
                const string problem =
                    "not opened: the knowledge record assigns this CH340 to device control, not to motion";
                _listed.Add(accelerometer with { Problem = problem });
                _listed.Add(gyrometer with { Problem = problem });
                continue;
            }

            if (OpenSerialPort(path, out var handle, out var original) is { } failure)
            {
                _listed.Add(accelerometer with { Problem = failure });
                _listed.Add(gyrometer with { Problem = failure });
                continue;
            }

            LabMotionChannel accelerometerChannel = new(accelerometer.Fields);
            LabMotionChannel gyrometerChannel = new(gyrometer.Fields);
            SerialReader reader = new(handle, original, accelerometerChannel, gyrometerChannel, () => Now);
            accelerometer = accelerometer with { Sampled = true, IntervalMs = null };
            gyrometer = gyrometer with { Sampled = true };
            _listed.Add(accelerometer);
            _listed.Add(gyrometer);
            _channels.Add((accelerometer, accelerometerChannel, reader));
            _channels.Add((gyrometer, gyrometerChannel, reader));
            reader.Start(id);
        }
    }

    // Opens the port and sets HC's line settings; the old settings are put back when the reader stops.
    private static string? OpenSerialPort(string path, out SafeFileHandle handle, out Dcb original)
    {
        original = new Dcb { Length = Marshal.SizeOf<Dcb>() };
        handle = Kernel32.CreateFileW(path, Kernel32.GenericRead | Kernel32.GenericWrite, 0, 0,
            Kernel32.OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return error == 5
                ? "not opened: another program has the port open (HC, or the device's own software)"
                : $"could not be opened (error {error})";
        }

        if (!GetCommState(handle, ref original))
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return $"line settings could not be read (error {error})";
        }

        var settings = original;
        settings.BaudRate = 115200;
        settings.ByteSize = 8;
        settings.Parity = 0;
        settings.StopBits = 0;
        settings.Flags = BinaryMode | RtsControlEnable;
        CommTimeouts timeouts = new()
        {
            ReadIntervalTimeout = uint.MaxValue,
            ReadTotalTimeoutMultiplier = uint.MaxValue,
            ReadTotalTimeoutConstant = 100
        };
        if (!SetCommState(handle, in settings) || !SetCommTimeouts(handle, in timeouts))
        {
            var error = Marshal.GetLastPInvokeError();
            SetCommState(handle, in original);
            handle.Dispose();
            return $"line settings could not be applied (error {error})";
        }

        return null;
    }

    private sealed class SerialReader : IMotionSource
    {
        private readonly LabMotionChannel _accelerometer;
        private readonly byte[] _buffer = new byte[512];
        private readonly LabMotionChannel _gyrometer;
        private readonly SafeFileHandle _handle;
        private readonly Func<double> _now;
        private readonly Dcb _original;
        private long _badChecksums;
        private long _failures;
        private long _frames;
        private int _lastError;
        private volatile bool _stop;
        private Thread? _thread;

        public SerialReader(SafeFileHandle handle, Dcb original, LabMotionChannel accelerometer,
            LabMotionChannel gyrometer, Func<double> now)
        {
            _handle = handle;
            _original = original;
            _accelerometer = accelerometer;
            _gyrometer = gyrometer;
            _now = now;
        }

        public long Failures => Interlocked.Read(ref _failures);

        public string? LastError
        {
            get
            {
                var frames = Interlocked.Read(ref _frames);
                var bad = Interlocked.Read(ref _badChecksums);
                var parts = new List<string>();
                if (_lastError != 0)
                {
                    parts.Add($"ReadFile failed (error {_lastError})");
                }

                if (bad > 0)
                {
                    parts.Add($"{bad} of {frames} frames failed the checksum");
                }

                return parts.Count == 0 ? null : string.Join("; ", parts);
            }
        }

        public IReadOnlyList<int>? FieldTypes => null;

        public string Freshness => "serial frame";

        public void Start(string id)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = $"Device Lab motion {id}" };
            _thread.Start();
        }

        public void ResetCounters()
        {
            Interlocked.Exchange(ref _failures, 0);
            Interlocked.Exchange(ref _frames, 0);
            Interlocked.Exchange(ref _badChecksums, 0);
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
            if (_thread is not null && !_thread.Join(TimeSpan.FromSeconds(2)))
            {
                return;
            }

            SetCommState(_handle, in _original);
            _handle.Dispose();
        }

        private unsafe void Run()
        {
            Span<double> accelerometer = stackalloc double[3];
            Span<double> gyrometer = stackalloc double[3];
            var used = 0;
            while (!_stop)
            {
                uint read;
                bool ok;
                fixed (byte* pointer = &_buffer[used])
                {
                    ok = ReadFile(_handle, pointer, (uint)(_buffer.Length - used), out read, 0);
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

                used += (int)read;
                var consumed = 0;
                while (LabMotionDecoders.FindSerialFrame(_buffer.AsSpan(consumed, used - consumed), out var start))
                {
                    var frame = _buffer.AsSpan(consumed + start, LabMotionDecoders.SerialFrameLength);
                    var valid = LabMotionDecoders.DecodeSerialFrame(frame, accelerometer, gyrometer);
                    consumed += start + LabMotionDecoders.SerialFrameLength;
                    if (!_accelerometer.Active)
                    {
                        continue;
                    }

                    Interlocked.Increment(ref _frames);
                    if (!valid)
                    {
                        Interlocked.Increment(ref _badChecksums);
                    }

                    var now = _now();
                    _accelerometer.Add(now, long.MinValue, accelerometer);
                    _gyrometer.Add(now, long.MinValue, gyrometer);
                }

                // Keep only what may still start a frame.
                LabMotionDecoders.FindSerialFrame(_buffer.AsSpan(consumed, used - consumed), out var keep);
                consumed += keep;
                if (consumed > 0)
                {
                    Array.Copy(_buffer, consumed, _buffer, 0, used - consumed);
                    used -= consumed;
                }

                if (used == _buffer.Length)
                {
                    used = 0;
                }
            }
        }
    }
}
