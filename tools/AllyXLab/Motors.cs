using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Gaming.Input;

namespace WSGM.AllyXLab;

/// <summary>A way to reach the motors, as one of the two references drives them.</summary>
/// <param name="Id">Stable identifier the wizard passes to the worker.</param>
/// <param name="Kind">"hid", "xinput" or "windows-gaming-input".</param>
/// <param name="Detail">What the route points at, for the report.</param>
internal sealed record MotorRoute(string Id, string Kind, string Detail);

/// <summary>Drives both motors at a bounded strength and can silence them.</summary>
internal interface IMotorOutput : IDisposable
{
    string Route { get; }

    void Set(int leftPercent, int rightPercent);

    void Zero();
}

internal static class Motors
{
    /// <summary>Lists every motor route this machine offers, in reference order.</summary>
    /// <param name="endpoints">The inventoried HID collections.</param>
    /// <returns>The available routes; empty when neither reference's path exists.</returns>
    /// <remarks>
    /// HHD writes an output report to the controller's gamepad collection; HC drives the same pad
    /// through XInput. Windows.Gaming.Input is the third, because a device that only appears to the
    /// newer stack has no XInput slot. Which one a device answers is a measurement, not an assumption.
    /// </remarks>
    internal static IReadOnlyList<MotorRoute> Discover(IEnumerable<HidEndpoint> endpoints)
    {
        List<MotorRoute> routes = [];
        foreach (HidEndpoint endpoint in endpoints.Where(e => e.Rumble && e.OutputBytes >= 9))
        {
            routes.Add(new("hid:" + endpoint.Id, "hid", $"HID output report on {endpoint.Page:X4}:{endpoint.Usage:X4}"));
        }

        for (uint slot = 0; slot < 4; slot++)
        {
            if (InputSources.XInputGetState(slot, out _) == 0)
            {
                routes.Add(new("xinput:" + slot, "xinput", $"XInput slot {slot}"));
            }
        }

        // A position in Gamepad.Gamepads shifts when an earlier pad disconnects, so the route names
        // the pad by its device id instead. XInput slots do not renumber on disconnect.
        IReadOnlyList<Gamepad> gamepads = Gamepad.Gamepads;
        for (int index = 0; index < gamepads.Count; index++)
        {
            if (DeviceId(gamepads[index]) is { } id)
            {
                routes.Add(new("wgi:" + id, "windows-gaming-input", $"Windows.Gaming.Input gamepad {index}"));
            }
        }

        return routes;
    }

    private static string? DeviceId(Gamepad gamepad) =>
        RawGameController.FromGameController(gamepad)?.NonRoamableId is { Length: > 0 } id ? id : null;

    /// <summary>Opens one route for writing.</summary>
    /// <param name="route">The route id from <see cref="Discover"/>.</param>
    /// <param name="endpoints">The inventoried HID collections.</param>
    /// <param name="log">The session log recording every write.</param>
    /// <returns>The opened output; the caller silences and disposes it.</returns>
    internal static IMotorOutput Open(string route, IReadOnlyList<HidEndpoint> endpoints, SessionLog log)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(endpoints);
        if (route.StartsWith("hid:", StringComparison.Ordinal))
        {
            HidEndpoint endpoint = endpoints.SingleOrDefault(e => e.Rumble && e.Id == route[4..])
                ?? throw new InvalidOperationException("The selected motor interface is not in this inventory.");
            return new HidMotors(Hid.Open(endpoint), endpoint, log);
        }

        if (route.StartsWith("xinput:", StringComparison.Ordinal) && uint.TryParse(route[7..], out uint slot) && slot < 4)
        {
            if (InputSources.XInputGetState(slot, out _) != 0)
            {
                throw new InvalidOperationException("That XInput slot is no longer connected.");
            }

            return new XInputMotors(slot, log);
        }

        if (route.StartsWith("wgi:", StringComparison.Ordinal) && route.Length > 4)
        {
            Gamepad[] matches = [.. Gamepad.Gamepads.Where(pad => DeviceId(pad) == route[4..])];
            return matches.Length == 1
                ? new GameControllerMotors(matches[0], route, log)
                : throw new InvalidOperationException("That Windows.Gaming.Input gamepad is no longer connected.");
        }

        throw new InvalidOperationException("Unknown motor route.");
    }

    private static int Bound(int percent) => percent is < 0 or > 100
        ? throw new InvalidOperationException("Motor strength must be 0-100 percent.")
        : percent;

    private sealed class HidMotors(SafeFileHandle handle, HidEndpoint endpoint, SessionLog log) : IMotorOutput
    {
        public string Route => "hid:" + endpoint.Id;

        public void Set(int leftPercent, int rightPercent) =>
            Hid.Output(handle, endpoint, [0x0D, 0x0F, 0, 0, (byte)Bound(leftPercent), (byte)Bound(rightPercent), 0xFF, 0, 0xEB], log);

        public void Zero() => Set(0, 0);

        public void Dispose() => handle.Dispose();
    }

    private sealed class XInputMotors(uint slot, SessionLog log) : IMotorOutput
    {
        public string Route => "xinput:" + slot;

        public void Set(int leftPercent, int rightPercent)
        {
            Vibration vibration = new()
            {
                Left = (ushort)(Bound(leftPercent) * ushort.MaxValue / 100),
                Right = (ushort)(Bound(rightPercent) * ushort.MaxValue / 100),
            };
            log.Add("motor-output-attempt", new { Route = Route, Left = leftPercent, Right = rightPercent });
            uint result = XInputSetState(slot, ref vibration);
            if (result != 0)
            {
                throw new IOException($"XInput refused the vibration write ({result}); effect is unknown. No retry.");
            }

            log.Add("motor-output-returned", new { Route = Route, Verified = false });
        }

        public void Zero() => Set(0, 0);

        public void Dispose() { }

        [StructLayout(LayoutKind.Sequential)]
        private struct Vibration
        {
            internal ushort Left, Right;
        }

        [DllImport("xinput1_4.dll")] private static extern uint XInputSetState(uint index, ref Vibration vibration);
    }

    private sealed class GameControllerMotors(Gamepad gamepad, string route, SessionLog log) : IMotorOutput
    {
        public string Route => route;

        public void Set(int leftPercent, int rightPercent)
        {
            log.Add("motor-output-attempt", new { Route = Route, Left = leftPercent, Right = rightPercent });
            gamepad.Vibration = new GamepadVibration
            {
                LeftMotor = Bound(leftPercent) / 100d,
                RightMotor = Bound(rightPercent) / 100d,
                LeftTrigger = 0,
                RightTrigger = 0,
            };
            log.Add("motor-output-returned", new { Route = Route, Verified = false });
        }

        public void Zero() => Set(0, 0);

        public void Dispose() { }
    }
}
