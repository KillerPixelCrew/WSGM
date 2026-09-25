using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Windows.Gaming.Input;
using WSGM.DeviceLab.Knowledge;

namespace WSGM.DeviceLab.Capture.Live;

/// <summary>One way of driving the motors.</summary>
/// <param name="Id">Stable ID within one discovery, for example <c>xinput:0</c>.</param>
/// <param name="Kind"><c>hid-output</c>, <c>xinput</c> or <c>windows-gaming-input</c>.</param>
/// <param name="Name">Short name for the tester and the summary, for example "XInput".</param>
/// <param name="Detail">What the route points at.</param>
internal sealed record LabRumbleRoute(string Id, string Kind, string Name, string Detail)
{
    /// <summary>What the route opens: a device path, slot or controller ID. Kept out of the evidence.</summary>
    [JsonIgnore]
    public string Target { get; init; } = string.Empty;

    /// <summary>The collection a HID route writes to.</summary>
    [JsonIgnore]
    public LabRumbleHidEndpoint? Endpoint { get; init; }

    /// <summary>The recorded report a HID route writes.</summary>
    [JsonIgnore]
    public LabRumbleHidLayout? Layout { get; init; }

    /// <summary>The recorded report layout, for the evidence.</summary>
    public string? Report => Layout?.Text;
}

/// <summary>One motor write and what Windows answered.</summary>
/// <param name="At">When the write was made.</param>
/// <param name="Route">Route ID.</param>
/// <param name="Purpose">Why it was made, for example <c>probe</c> or <c>stop</c>.</param>
/// <param name="Frame">Strengths written.</param>
/// <param name="Code">Result code: 0 is success.</param>
/// <param name="Error">What went wrong, when something did.</param>
/// <param name="Report">The report bytes, for a HID route.</param>
internal sealed record LabRumbleWrite(
    DateTimeOffset At,
    string Route,
    string Purpose,
    LabRumbleFrame Frame,
    long Code,
    string? Error,
    string? Report);

/// <summary>Every motor write of one stage run, safe to add to from any thread.</summary>
internal sealed class LabRumbleLog
{
    private readonly List<LabRumbleWrite> _writes = [];

    /// <summary>Adds a write.</summary>
    /// <param name="write">The write.</param>
    public void Add(LabRumbleWrite write)
    {
        lock (_writes)
        {
            _writes.Add(write);
        }
    }

    /// <summary>A copy of every write so far.</summary>
    public IReadOnlyList<LabRumbleWrite> Snapshot()
    {
        lock (_writes)
        {
            return [.. _writes];
        }
    }
}

/// <summary>A motor write Windows refused, or whose effect is unknown. It is never retried.</summary>
internal sealed class LabRumbleWriteException(string message) : InvalidOperationException(message);

/// <summary>An opened route. Every write is bounded, logged and serialized.</summary>
internal interface ILabRumbleOutput : IDisposable
{
    /// <summary>The route.</summary>
    LabRumbleRoute Route { get; }

    /// <summary>Writes one frame.</summary>
    /// <param name="frame">Strengths, each 0 to 100 percent.</param>
    /// <param name="purpose">Why, for the log.</param>
    /// <exception cref="LabRumbleWriteException">The write failed; it is logged and not retried.</exception>
    void Write(LabRumbleFrame frame, string purpose);
}

/// <summary>What route discovery found.</summary>
/// <param name="Routes">Routes that can be tried, HID first.</param>
/// <param name="Notes">Why a recorded route was not offered, in plain words.</param>
/// <param name="HidEndpoints">The recorded vendor's HID collections that were present.</param>
internal sealed record LabRumbleDiscovery(
    IReadOnlyList<LabRumbleRoute> Routes,
    IReadOnlyList<string> Notes,
    IReadOnlyList<LabRumbleHidEndpoint> HidEndpoints);

/// <summary>Finds and opens rumble routes, and plays bounded pulses on them.</summary>
/// <remarks>
///     XInput and Windows.Gaming.Input work on any controller Windows knows. A HID output report is used
///     only when the confirmed knowledge record is curated and gives the report layout and the exact
///     collection, and that collection is present; an output report is never guessed.
/// </remarks>
internal static class LabRumbleRoutes
{
    /// <summary>Longest pulse the stage plays, in milliseconds.</summary>
    public const int LongestPulseMilliseconds = 1000;

    /// <summary>Kind of a recorded HID output report route.</summary>
    public const string HidKind = "hid-output";

    /// <summary>Kind of an XInput slot route.</summary>
    public const string XInputKind = "xinput";

    /// <summary>Kind of a Windows.Gaming.Input gamepad route.</summary>
    public const string GamingInputKind = "windows-gaming-input";

    /// <summary>Lists every route this machine offers now.</summary>
    /// <param name="record">The confirmed knowledge record, if any.</param>
    public static LabRumbleDiscovery Discover(DeviceKnowledgeRecord? record)
    {
        var hid = DiscoverHid(record, LabRumbleNative.HidEndpoints);
        List<LabRumbleRoute> routes = [.. hid.Routes];

        for (uint slot = 0; slot < 4; slot++)
        {
            if (LabRumbleNative.XInputConnected(slot))
            {
                routes.Add(new LabRumbleRoute($"xinput:{slot}", XInputKind, "XInput",
                    $"XInput controller {slot + 1}") { Target = slot.ToString(CultureInfo.InvariantCulture) });
            }
        }

        // Gamepad.Gamepads renumbers when a pad disconnects, so a route names its pad by device ID. The
        // list fills in shortly after its first use in a process, so an empty first read is read again.
        var gamepads = Gamepad.Gamepads;
        if (gamepads.Count == 0)
        {
            Thread.Sleep(500);
            gamepads = Gamepad.Gamepads;
        }

        for (var index = 0; index < gamepads.Count; index++)
        {
            var raw = RawGameController.FromGameController(gamepads[index]);
            if (raw?.NonRoamableId is { Length: > 0 } id)
            {
                routes.Add(new LabRumbleRoute($"wgi:{index}", GamingInputKind, "Windows.Gaming.Input",
                    $"Windows.Gaming.Input gamepad {index + 1}: {raw.DisplayName} ({raw.HardwareVendorId:X4}:{raw.HardwareProductId:X4})")
                {
                    Target = id
                });
            }
        }

        return hid with { Routes = routes };
    }

    /// <summary>Opens a route for writing.</summary>
    /// <param name="route">A route from <see cref="Discover" />.</param>
    /// <param name="log">Where every write is recorded.</param>
    /// <returns>The output; the caller zeroes and disposes it.</returns>
    public static ILabRumbleOutput Open(LabRumbleRoute route, LabRumbleLog log)
    {
        switch (route.Kind)
        {
            case HidKind when route is { Endpoint: { } endpoint, Layout: { } layout }:
                return new HidOutput(route, LabRumbleNative.OpenForWrite(endpoint), endpoint, layout, log);
            case XInputKind when uint.TryParse(route.Target, CultureInfo.InvariantCulture, out var slot) && slot < 4:
                return LabRumbleNative.XInputConnected(slot)
                    ? new XInputOutput(route, slot, log)
                    : throw new InvalidOperationException("That controller is no longer connected.");
            case GamingInputKind:
                var matches = Gamepad.Gamepads
                    .Where(pad => RawGameController.FromGameController(pad)?.NonRoamableId == route.Target)
                    .ToArray();
                return matches.Length == 1
                    ? new GamingInputOutput(route, matches[0], log)
                    : throw new InvalidOperationException("That controller is no longer connected.");
            default:
                throw new InvalidOperationException("Unknown rumble route.");
        }
    }

    /// <summary>Plays one pulse and always follows it with an explicit zero.</summary>
    /// <param name="output">The route.</param>
    /// <param name="frame">Strengths, each 0 to 100 percent.</param>
    /// <param name="milliseconds">Pulse length, 1 to <see cref="LongestPulseMilliseconds" />.</param>
    /// <param name="purpose">Why, for the log.</param>
    /// <param name="cancel">Stops the pulse early; the zero is still written.</param>
    /// <returns>How long the motors were on between the two writes, in milliseconds.</returns>
    public static double Pulse(ILabRumbleOutput output, LabRumbleFrame frame, int milliseconds, string purpose,
        CancellationToken cancel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(milliseconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(milliseconds, LongestPulseMilliseconds);
        frame.Checked();
        cancel.ThrowIfCancellationRequested();
        var failed = true;
        double held;
        try
        {
            output.Write(frame, purpose);
            var clock = Stopwatch.StartNew();
            Hold(milliseconds, clock, cancel);
            held = clock.Elapsed.TotalMilliseconds;
            failed = false;
        }
        finally
        {
            try
            {
                output.Write(LabRumbleFrame.Zero, "stop");
            }
            catch (LabRumbleWriteException) when (failed)
            {
                // The zero is logged; the first failure is the one to report.
            }
        }

        return held;
    }

    /// <summary>Writes a zero to every output, without letting one failure skip the others.</summary>
    /// <param name="outputs">Open outputs.</param>
    /// <param name="purpose">Why, for the log.</param>
    /// <returns>Routes whose zero failed.</returns>
    public static IReadOnlyList<string> ZeroAll(IEnumerable<ILabRumbleOutput> outputs, string purpose = "stage-end")
    {
        List<string> failed = [];
        foreach (var output in outputs)
        {
            try
            {
                output.Write(LabRumbleFrame.Zero, purpose);
            }
            catch (LabRumbleWriteException)
            {
                failed.Add(output.Route.Id);
            }
        }

        return failed;
    }

    // Waits coarsely, then spins the last stretch, because the system timer alone cannot time a 5 ms
    // pulse.
    private static void Hold(int milliseconds, Stopwatch clock, CancellationToken cancel)
    {
        var coarse = milliseconds - 20;
        if (coarse > 0 && cancel.WaitHandle.WaitOne(coarse))
        {
            cancel.ThrowIfCancellationRequested();
        }

        while (clock.Elapsed.TotalMilliseconds < milliseconds)
        {
            cancel.ThrowIfCancellationRequested();
            Thread.SpinWait(64);
        }
    }

    /// <summary>The HID routes a knowledge record allows among the given collections.</summary>
    /// <param name="record">The confirmed knowledge record, if any.</param>
    /// <param name="endpoints">Lists the present HID collections of one vendor.</param>
    /// <returns>HID routes only, with the notes and the vendor's collections.</returns>
    /// <remarks>
    ///     A collection qualifies only when its vendor, product, usage page and usage are exactly the ones
    ///     the curated record names (its main collection or one of its <c>alsoCollections</c>) and its
    ///     output report is long enough for the recorded layout. Any other collection of the same device,
    ///     such as a vendor control collection, is never written.
    /// </remarks>
    internal static LabRumbleDiscovery DiscoverHid(DeviceKnowledgeRecord? record,
        Func<ushort, IReadOnlyList<LabRumbleHidEndpoint>> endpoints)
    {
        List<LabRumbleRoute> routes = [];
        List<string> notes = [];
        List<LabRumbleHidEndpoint> present = [];
        if (record is null)
        {
            notes.Add("No device was confirmed, so only the standard Windows routes are tried.");
            return new LabRumbleDiscovery(routes, notes, present);
        }

        var mechanisms = record.Mechanisms.Where(item => item is { Feature: "rumble", Transport: "hid-output" })
            .ToArray();
        if (mechanisms.Length == 0)
        {
            notes.Add($"The record for {record.DisplayName} gives no rumble report.");
            return new LabRumbleDiscovery(routes, notes, present);
        }

        if (record.Status != DeviceKnowledgeStatus.Curated)
        {
            notes.Add($"The record for {record.DisplayName} is not reviewed, so its rumble report is not used.");
            return new LabRumbleDiscovery(routes, notes, present);
        }

        foreach (var mechanism in mechanisms)
        {
            var parameters = mechanism.Parameters;
            var vendor = Hex(parameters, "vendorId");
            var product = Hex(parameters, "productId");
            var page = Hex(parameters, "usagePage");
            var usage = Hex(parameters, "usage");
            if (vendor is null)
            {
                notes.Add("The recorded rumble report names no vendor ID.");
                continue;
            }

            if (page is null || usage is null)
            {
                var named = record.HidEndpoints.FirstOrDefault(item =>
                    item is { Role: "rumble", UsagePage: not null, Usage: not null }
                    && Hex(item.VendorId) == vendor);
                page = (ushort?)named?.UsagePage;
                usage = (ushort?)named?.Usage;
                product ??= named?.ProductIds.Select(Hex).FirstOrDefault(id => id is not null);
            }

            if (product is null || page is null || usage is null)
            {
                notes.Add(
                    "The recorded rumble report does not name the exact controller collection, so it is not used.");
                continue;
            }

            parameters.TryGetValue("report", out var text);
            if (LabRumbleHidLayout.TryParse(text, out var problem) is not { } layout)
            {
                notes.Add(problem ?? "The recorded rumble report cannot be used.");
                continue;
            }

            List<(ushort Page, ushort Usage)> collections = [(page.Value, usage.Value)];
            if (parameters.TryGetValue("alsoCollections", out var also)
                && !TryCollections(also, collections))
            {
                notes.Add($"The recorded extra rumble collections cannot be read: {also}.");
            }

            var listed = endpoints(vendor.Value);
            foreach (var endpoint in listed.Where(item => !present.Contains(item)))
            {
                present.Add(endpoint);
            }

            foreach (var (collectionPage, collectionUsage) in collections.Distinct())
            {
                var matching = listed.Where(item => item.VendorId == vendor && item.ProductId == product
                                                    && item.UsagePage == collectionPage
                                                    && item.Usage == collectionUsage
                                                    && item.OutputLength >= layout.Length
                                                    && item.OutputLength <= 1024)
                    .ToArray();
                if (matching.Length == 0)
                {
                    notes.Add(
                        $"The recorded controller collection {vendor:X4}:{product:X4} {collectionPage:X4}:{collectionUsage:X4} is not present with a long enough output report.");
                    continue;
                }

                for (var i = 0; i < matching.Length; i++)
                {
                    var endpoint = matching[i];
                    routes.Add(new LabRumbleRoute(
                        $"hid:{vendor:X4}:{product:X4}:{collectionPage:X4}:{collectionUsage:X4}:{i}", HidKind,
                        "Device report",
                        $"Recorded output report on {vendor:X4}:{product:X4}, collection {collectionPage:X4}:{collectionUsage:X4}")
                    {
                        Target = endpoint.Path,
                        Endpoint = endpoint,
                        Layout = layout
                    });
                }
            }
        }

        return new LabRumbleDiscovery(routes, notes, present);
    }

    // Reads "0x000F:0x0002, 0x000F:0x0021" into the list; false when any part is not a page:usage pair.
    private static bool TryCollections(string text, List<(ushort Page, ushort Usage)> collections)
    {
        List<(ushort, ushort)> parsed = [];
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split(':');
            if (pair.Length != 2 || Hex(pair[0]) is not { } page || Hex(pair[1]) is not { } usage)
            {
                return false;
            }

            parsed.Add((page, usage));
        }

        collections.AddRange(parsed);
        return true;
    }

    private static ushort? Hex(IReadOnlyDictionary<string, string> parameters, string name)
    {
        return parameters.TryGetValue(name, out var value) ? Hex(value) : null;
    }

    private static ushort? Hex(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var text = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return ushort.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static ushort Wide(int percent)
    {
        return (ushort)(percent * ushort.MaxValue / 100);
    }

    private sealed class HidOutput(
        LabRumbleRoute route,
        SafeFileHandle handle,
        LabRumbleHidEndpoint endpoint,
        LabRumbleHidLayout layout,
        LabRumbleLog log) : ILabRumbleOutput
    {
        private readonly Lock _gate = new();

        public LabRumbleRoute Route => route;

        public void Write(LabRumbleFrame frame, string purpose)
        {
            var report = layout.Encode(frame.Checked(), endpoint.OutputLength);
            lock (_gate)
            {
                var code = LabRumbleNative.WriteReport(handle, report);
                var error = code switch
                {
                    0 => null,
                    -1 => "The report was only partly written; its effect is unknown.",
                    _ => $"Windows refused the report (error {code}); its effect is unknown."
                };
                log.Add(new LabRumbleWrite(DateTimeOffset.UtcNow, route.Id, purpose, frame, code, error,
                    Convert.ToHexString(report)));
                if (error is not null)
                {
                    throw new LabRumbleWriteException(error);
                }
            }
        }

        public void Dispose()
        {
            handle.Dispose();
        }
    }

    private sealed class XInputOutput(LabRumbleRoute route, uint slot, LabRumbleLog log) : ILabRumbleOutput
    {
        private readonly Lock _gate = new();

        public LabRumbleRoute Route => route;

        public void Write(LabRumbleFrame frame, string purpose)
        {
            frame.Checked();
            lock (_gate)
            {
                var code = LabRumbleNative.XInputVibrate(slot, Wide(frame.Left), Wide(frame.Right));
                var error = code switch
                {
                    0 => null,
                    LabRumbleNative.ErrorDeviceNotConnected => "The controller is no longer connected.",
                    _ => $"XInput refused the write (error {code}); its effect is unknown."
                };
                log.Add(new LabRumbleWrite(DateTimeOffset.UtcNow, route.Id, purpose, frame, code, error, null));
                if (error is not null)
                {
                    throw new LabRumbleWriteException(error);
                }
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class GamingInputOutput(LabRumbleRoute route, Gamepad gamepad, LabRumbleLog log)
        : ILabRumbleOutput
    {
        private readonly Lock _gate = new();

        public LabRumbleRoute Route => route;

        public void Write(LabRumbleFrame frame, string purpose)
        {
            frame.Checked();
            lock (_gate)
            {
                long code = 0;
                string? error = null;
                try
                {
                    gamepad.Vibration = new GamepadVibration
                    {
                        LeftMotor = frame.Left / 100d,
                        RightMotor = frame.Right / 100d,
                        LeftTrigger = 0,
                        RightTrigger = 0
                    };
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    code = ex.HResult;
                    error = $"Windows.Gaming.Input refused the write ({ex.Message}); its effect is unknown.";
                }

                log.Add(new LabRumbleWrite(DateTimeOffset.UtcNow, route.Id, purpose, frame, code, error, null));
                if (error is not null)
                {
                    throw new LabRumbleWriteException(error);
                }
            }
        }

        public void Dispose()
        {
        }
    }
}
