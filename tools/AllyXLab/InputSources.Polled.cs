using System.Runtime.InteropServices;
using Windows.Gaming.Input;

namespace WSGM.AllyXLab;

/// <summary>The polled controller APIs: XInput, including the guide button that the public entry
/// point hides, and Windows.Gaming.Input, which is where GameInput-era devices appear.</summary>
internal sealed partial class InputSources
{
    private System.Windows.Forms.Timer? _poll;
    private readonly uint[] _packets = new uint[4];
    private readonly uint[] _results = [uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue];
    private readonly XGamepad[] _lastPads = new XGamepad[4];
    private readonly Dictionary<string, (bool[] Buttons, double[] Axes, int[] Switches)> _rawControllers = [];
    private readonly HashSet<string> _knownControllers = [];
    private bool _guideUnavailable;

    private void StartPolledSources()
    {
        _poll = new System.Windows.Forms.Timer { Interval = 10 };
        _poll.Tick += (_, _) =>
        {
            try { PollXInput(); } catch (Exception e) { _log.Add("source-error", new { Source = "xinput", e.Message }); }
            try { PollGameControllers(); } catch (Exception e) { _log.Add("source-error", new { Source = "windows-gaming-input", e.Message }); }
        };
        _poll.Start();
    }

    private void StopPolledSources()
    {
        _poll?.Stop();
        _poll?.Dispose();
        _poll = null;
    }

    private void PollXInput()
    {
        for (uint slot = 0; slot < 4; slot++)
        {
            uint result = ReadState(slot, out XState state);
            if (result != _results[slot])
            {
                _results[slot] = result;
                _log.Add("xinput-slot", new { Slot = slot, Result = result, Connected = result == 0 });
                if (result == 0)
                {
                    LogCapabilities(slot);
                    Report("xinput", $"controller connected in slot {slot}");
                }
            }

            if (result != 0 || _packets[slot] == state.Packet)
            {
                continue;
            }

            _packets[slot] = state.Packet;
            XGamepad pad = state.Gamepad, previous = _lastPads[slot];
            _lastPads[slot] = pad;
            _log.Add("xinput", new
            {
                Slot = slot,
                state.Packet,
                pad.Buttons,
                Guide = (pad.Buttons & 0x0400) != 0,
                pad.LeftTrigger,
                pad.RightTrigger,
                pad.LX,
                pad.LY,
                pad.RX,
                pad.RY,
            });
            if (pad.Buttons != previous.Buttons || Math.Abs(pad.LeftTrigger - previous.LeftTrigger) > 40 || Math.Abs(pad.RightTrigger - previous.RightTrigger) > 40
                || Far(pad.LX, previous.LX) || Far(pad.LY, previous.LY) || Far(pad.RX, previous.RX) || Far(pad.RY, previous.RY))
            {
                Report("xinput", $"slot {slot} buttons 0x{pad.Buttons:X4}");
            }
        }
    }

    private static bool Far(short now, short before) => Math.Abs(now - before) > 7000;

    private uint ReadState(uint slot, out XState state)
    {
        if (!_guideUnavailable)
        {
            try { return XInputGetStateEx(slot, out state); }
            catch (EntryPointNotFoundException)
            {
                _guideUnavailable = true;
                _log.Add("source-unavailable", new { Source = "xinput-guide", Detail = "Ordinal 100 is absent; the guide button cannot be read." });
            }
        }

        return XInputGetState(slot, out state);
    }

    private void LogCapabilities(uint slot)
    {
        try
        {
            if (XInputGetCapabilitiesEx(1, slot, 0, out XCapabilitiesEx caps) == 0)
            {
                _log.Add("xinput-capabilities", new { Slot = slot, caps.Type, caps.SubType, caps.Flags, Vid = caps.VendorId, Pid = caps.ProductId, caps.ProductVersion });
                return;
            }
        }
        catch (EntryPointNotFoundException) { }
        _log.Add("xinput-capabilities", new { Slot = slot, Detail = "Extended capabilities are unavailable; no vendor identity from XInput." });
    }

    private void PollGameControllers()
    {
        foreach (RawGameController controller in RawGameController.RawGameControllers)
        {
            string id = "wgi-" + SessionLog.Token($"{controller.HardwareVendorId:X4}:{controller.HardwareProductId:X4}:{controller.DisplayName}:{controller.ButtonCount}:{controller.AxisCount}");
            if (_knownControllers.Add(id))
            {
                _log.Add("game-controller", new
                {
                    Id = id,
                    Vid = controller.HardwareVendorId,
                    Pid = controller.HardwareProductId,
                    controller.ButtonCount,
                    controller.AxisCount,
                    controller.SwitchCount,
                    IsGamepad = Gamepad.FromGameController(controller) is not null,
                });
                Report("windows-gaming-input", $"controller {controller.HardwareVendorId:X4}:{controller.HardwareProductId:X4} present");
            }

            var buttons = new bool[controller.ButtonCount];
            var switches = new GameControllerSwitchPosition[controller.SwitchCount];
            var axes = new double[controller.AxisCount];
            controller.GetCurrentReading(buttons, switches, axes);
            int[] switchValues = switches.Select(s => (int)s).ToArray();
            if (!_rawControllers.TryGetValue(id, out var previous))
            {
                _rawControllers[id] = (buttons, axes, switchValues);
                continue;
            }

            bool buttonChanged = !buttons.SequenceEqual(previous.Buttons);
            bool switchChanged = !switchValues.SequenceEqual(previous.Switches);
            bool axisMoved = axes.Length == previous.Axes.Length && axes.Where((value, i) => Math.Abs(value - previous.Axes[i]) > 0.2).Any();
            if (!buttonChanged && !switchChanged && !axisMoved)
            {
                continue;
            }

            _rawControllers[id] = (buttons, axes, switchValues);
            _log.Add("game-controller-reading", new
            {
                Id = id,
                Buttons = string.Concat(buttons.Select(b => b ? '1' : '0')),
                Switches = switchValues,
                Axes = axes.Select(a => Math.Round(a, 3)).ToArray(),
            });
            Report("windows-gaming-input", buttonChanged ? "controller button" : switchChanged ? "controller switch" : "controller axis");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XGamepad
    {
        internal ushort Buttons;
        internal byte LeftTrigger, RightTrigger;
        internal short LX, LY, RX, RY;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XState
    {
        internal uint Packet;
        internal XGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XCapabilitiesEx
    {
        internal byte Type, SubType;
        internal ushort Flags;
        internal XGamepad Gamepad;
        internal ushort LeftMotor, RightMotor;
        internal ushort VendorId, ProductId, ProductVersion, Unknown;
    }

    [DllImport("xinput1_4.dll")] internal static extern uint XInputGetState(uint index, out XState state);
    [DllImport("xinput1_4.dll", EntryPoint = "#100")] private static extern uint XInputGetStateEx(uint index, out XState state);
    [DllImport("xinput1_4.dll", EntryPoint = "#108")] private static extern uint XInputGetCapabilitiesEx(uint one, uint index, uint flags, out XCapabilitiesEx capabilities);
}
