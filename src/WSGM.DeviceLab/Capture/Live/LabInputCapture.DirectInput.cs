using System;
using System.Collections.Generic;
using System.Linq;
using Vortice.DirectInput;
using WSGM.DeviceLab.Application;

namespace WSGM.DeviceLab.Capture.Live;

// DirectInput sees game controllers that neither XInput nor Windows.Gaming.Input exposes. A single
// poll thread owns the COM objects; all devices are acquired nonexclusively in the background.
internal sealed partial class LabInputCapture
{
    private readonly nint _directInputWindow;

    private sealed class DirectInputReader : IDisposable
    {
        private readonly LabInputCapture _capture;
        private readonly HashSet<Guid> _failed = [];
        private readonly IDirectInput8 _input;
        private readonly Dictionary<Guid, Pad> _pads = [];
        private readonly nint _window;
        private long _lastScan = long.MinValue;

        public DirectInputReader(LabInputCapture capture, nint window)
        {
            _capture = capture;
            _window = window;
            _input = DInput.DirectInput8Create();
        }

        public void Dispose()
        {
            foreach (var pad in _pads.Values)
            {
                pad.Controller.Unacquire();
                pad.Controller.Dispose();
            }

            _pads.Clear();
            _input.Dispose();
        }

        public void Poll()
        {
            var now = (long)_capture.Now;
            if (now - _lastScan >= 2000 || _lastScan == long.MinValue)
            {
                _lastScan = now;
                Scan();
            }

            foreach (var pad in _pads.Values)
            {
                try
                {
                    if (pad.Controller.Poll().Failure)
                    {
                        pad.Controller.Acquire();
                        continue;
                    }

                    pad.Controller.GetCurrentJoystickState(ref pad.State);
                    pad.Faulted = false;
                    if (!pad.Changed())
                    {
                        continue;
                    }

                    pad.Remember();
                    var pressed = string.Join(',', pad.State.Buttons.Select((down, index) => (down, index))
                        .Where(item => item.down).Select(item => item.index));
                    _capture.Record(new LabInputEvent(Math.Round(_capture.Now, 2), "directinput", pad.Device.Id,
                        $"buttons [{pressed}] pov [{string.Join(',', pad.State.PointOfViewControllers)}] " +
                        $"axes [{pad.State.X},{pad.State.Y},{pad.State.Z},{pad.State.RotationX}," +
                        $"{pad.State.RotationY},{pad.State.RotationZ},{string.Join(',', pad.State.Sliders)}]"), true);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (!pad.Faulted)
                    {
                        _capture.MarkUnavailable($"directinput {pad.Device.Name}", ex.Message);
                        pad.Faulted = true;
                    }

                    try
                    {
                        pad.Controller.Unacquire();
                    }
                    catch (Exception unacquireError) when (unacquireError is not OutOfMemoryException)
                    {
                        // The device may already be disconnected.
                    }
                }
            }
        }

        private void Scan()
        {
            var instances = _input.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly);
            var attached = instances.Select(instance => instance.InstanceGuid).ToHashSet();
            _failed.RemoveWhere(id => !attached.Contains(id));
            foreach (var (id, pad) in _pads.ToArray())
            {
                if (attached.Contains(id))
                {
                    continue;
                }

                pad.Controller.Unacquire();
                pad.Controller.Dispose();
                _pads.Remove(id);
                _capture.Record(new LabInputEvent(Math.Round(_capture.Now, 2), "directinput", pad.Device.Id,
                    "controller disconnected"), false);
            }

            foreach (var instance in instances)
            {
                if (_pads.ContainsKey(instance.InstanceGuid))
                {
                    continue;
                }

                IDirectInputDevice8? controller = null;
                try
                {
                    LabTrace.Write($"capture directinput {instance.ProductName}: create and acquire");
                    controller = _input.CreateDevice(instance.InstanceGuid);
                    if (controller.SetDataFormat<RawJoystickState>().Failure
                        || controller.SetCooperativeLevel(_window,
                            CooperativeLevel.NonExclusive | CooperativeLevel.Background).Failure
                        || controller.Acquire().Failure)
                    {
                        throw new InvalidOperationException("The controller could not be acquired.");
                    }

                    JoystickState state = new();
                    controller.GetCurrentJoystickState(ref state);
                    LabTrace.Write($"capture directinput {instance.ProductName}: acquired");

                    var product = BitConverter.ToUInt32(instance.ProductGuid.ToByteArray());
                    var device = _capture.AddDevice(new LabInputDevice(_capture.NextId("dinput"), "directinput",
                        ((ushort)product).ToString("X4"), ((ushort)(product >> 16)).ToString("X4"),
                        (int)instance.UsagePage, (int)instance.Usage, null, false, instance.ProductName));
                    Pad pad = new(controller, device) { State = state };
                    pad.Remember();
                    _pads.Add(instance.InstanceGuid, pad);
                    _failed.Remove(instance.InstanceGuid);
                    _capture.Record(new LabInputEvent(Math.Round(_capture.Now, 2), "directinput", device.Id,
                        $"controller present: {instance.ProductName}"), false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    controller?.Dispose();
                    if (_failed.Add(instance.InstanceGuid))
                    {
                        _capture.MarkUnavailable($"directinput {instance.ProductName}", ex.Message);
                    }
                }
            }
        }

        private sealed class Pad(IDirectInputDevice8 controller, LabInputDevice device)
        {
            private readonly bool[] _buttons = new bool[128];
            private readonly int[] _values = new int[12];

            public JoystickState State = new();

            public IDirectInputDevice8 Controller { get; } = controller;

            public LabInputDevice Device { get; } = device;

            public bool Faulted { get; set; }

            public bool Changed()
            {
                if (!State.Buttons.AsSpan().SequenceEqual(_buttons))
                {
                    return true;
                }

                Span<int> values = stackalloc int[12];
                Values(values);
                return !values.SequenceEqual(_values);
            }

            public void Remember()
            {
                State.Buttons.CopyTo(_buttons, 0);
                Values(_values);
            }

            private void Values(Span<int> values)
            {
                values[0] = State.X;
                values[1] = State.Y;
                values[2] = State.Z;
                values[3] = State.RotationX;
                values[4] = State.RotationY;
                values[5] = State.RotationZ;
                values[6] = State.Sliders[0];
                values[7] = State.Sliders[1];
                State.PointOfViewControllers.AsSpan().CopyTo(values[8..]);
            }
        }
    }
}
