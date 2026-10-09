using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WSGM.DeviceLab.Application;
using WSGM.DeviceLab.Windows;
using WSGM.Interop;

namespace WSGM.DeviceLab.Capture.Live;

// Open every readable HID input collection, including vendor pages and unknown controllers. Raw Input
// remains active; a direct HID read also covers collections that do not publish Raw Input reports.
internal sealed partial class LabInputCapture
{
    private readonly HashSet<string> _hidPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<HidCollectionReader> _hidReaders = [];
    private int _hidScanQueued;

    public void RescanHidCollections()
    {
        StartHidCollections();
    }

    private void StartHidCollections()
    {
        try
        {
            LabTrace.Write("capture hid: enumerate collections");
            var collections = HidDevices.EnumerateAll((path, problem) =>
                MarkUnavailable("hid collection descriptor", $"{ShortPath(path)}: {problem}"));
            foreach (var collection in collections)
            {
                try
                {
                    StartHidCollection(collection);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    ForgetHidPath(collection.DevicePath);
                    MarkUnavailable("hid collection", $"{ShortPath(collection.DevicePath)}: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MarkUnavailable("hid collection enumeration", ex.Message);
        }
    }

    private void StartHidCollection(HidCollection collection)
    {
        var path = collection.DevicePath;
        if (collection.InputLength == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || !_hidPaths.Add(path))
            {
                return;
            }
        }

        var name =
            $"{collection.VendorId:X4}:{collection.ProductId:X4} {collection.UsagePage:X4}:{collection.Usage:X4}";
        LabTrace.Write($"capture hid {name}: open for reading ({collection.InputLength} bytes)");
        var handle = Kernel32.CreateFileW(path, Kernel32.GenericRead,
            Kernel32.FileShareRead | Kernel32.FileShareWrite, 0, Kernel32.OpenExisting, 0x40000000, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            ForgetHidPath(path);
            MarkUnavailable("hid collection read", $"{name} could not be opened (error {error})");
            return;
        }

        var device = AddDevice(new LabInputDevice(NextId("hid-read"), "hid-read",
            collection.VendorId.ToString("X4"), collection.ProductId.ToString("X4"),
            collection.UsagePage, collection.Usage, path, false));
        HidCollectionReader reader = new(this, device,
            new FileStream(handle, FileAccess.Read, collection.InputLength, true), collection.InputLength);
        lock (_gate)
        {
            if (_disposed)
            {
                reader.Dispose();
                return;
            }

            _hidReaders.Add(reader);
            reader.Start();
        }

        LabTrace.Write($"capture hid {name}: reading");
    }

    private void QueueHidRescan()
    {
        if (Interlocked.CompareExchange(ref _hidScanQueued, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                StartHidCollections();
            }
            finally
            {
                Volatile.Write(ref _hidScanQueued, 0);
            }
        });
    }

    private static string ShortPath(string path)
    {
        var at = path.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        return at >= 0 && at + 17 <= path.Length ? path.Substring(at, 17) : "unknown collection";
    }

    private void ForgetHidPath(string path)
    {
        lock (_gate)
        {
            _hidPaths.Remove(path);
        }
    }

    private sealed class HidCollectionReader(
        LabInputCapture capture,
        LabInputDevice device,
        FileStream stream,
        int reportLength) : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private Task? _task;

        public void Dispose()
        {
            _stop.Cancel();
            stream.Dispose();
            try
            {
                _task?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // The read fault was recorded by RunAsync.
            }

            _stop.Dispose();
        }

        public void Start()
        {
            _task = RunAsync();
        }

        private async Task RunAsync()
        {
            var report = new byte[reportLength];
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var offset = 0;
                    while (offset < report.Length)
                    {
                        var read = await stream.ReadAsync(report.AsMemory(offset), _stop.Token);
                        if (read == 0)
                        {
                            throw new EndOfStreamException("The HID collection disconnected.");
                        }

                        offset += read;
                    }

                    capture.OnHidReport(device, report, "hid-read");
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                capture.MarkUnavailable($"hid collection {device.VendorId}:{device.ProductId}", ex.Message);
            }
        }
    }
}
