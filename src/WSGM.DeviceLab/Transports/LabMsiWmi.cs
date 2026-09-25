using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WSGM.DeviceLab.Wizard;
using WSGM.DeviceLab.Worker;

namespace WSGM.DeviceLab.Transports;

/// <summary>An MSI power state the lab captured before changing it.</summary>
/// <param name="Sustained">PL1 in watts.</param>
/// <param name="Boost">PL2 in watts.</param>
/// <param name="Scenario">The raw firmware scenario byte.</param>
internal sealed record LabMsiState(int Sustained, int Boost, int Scenario);

/// <summary>The MSI state a worker checkpoint captures before the first write.</summary>
/// <param name="Power">PL1, PL2 and the scenario, when the record has a TDP mechanism and they read stably.</param>
/// <param name="PowerUnavailable">Why <paramref name="Power" /> is missing, when the record declares it.</param>
/// <param name="ChargeRaw">The raw charge limit byte, when the record has one and it reads.</param>
/// <param name="ChargeUnavailable">Why <paramref name="ChargeRaw" /> is missing, when the record declares it.</param>
internal sealed record LabMsiOriginal(
    LabMsiState? Power,
    string? PowerUnavailable,
    int? ChargeRaw,
    string? ChargeUnavailable)
{
    /// <summary>Raw fan flags before testing.</summary>
    public LabMsiFanState? Fans { get; init; }
}

/// <summary>Raw custom and full-speed flags, including firmware-owned bits.</summary>
internal sealed record LabMsiFanState(byte Custom, byte FullSpeed);

/// <summary>
///     The MSI_ACPI service the wizard calls through the hardware worker (<c>msi-wmi</c>, opened with a
///     <see cref="LabMsiLayout" />). Writes are refused until the worker's checkpoint is acknowledged.
/// </summary>
internal interface ILabMsiWmi : IDisposable
{
    /// <summary>Reads PL1, PL2 and the scenario.</summary>
    /// <returns>The state.</returns>
    LabMsiState ReadPower();

    /// <summary>Reads the raw charge limit byte.</summary>
    /// <returns>The byte.</returns>
    int ReadChargeRaw();

    /// <summary>Reads both fans' RPM.</summary>
    /// <returns>Readings, or none.</returns>
    IReadOnlyList<LabFanReading> FanSpeeds();

    /// <summary>The checkpoint snapshot: the stable power state and the raw charge limit.</summary>
    /// <returns>What could be captured, and why the rest could not.</returns>
    [LabWorkerSnapshot]
    LabMsiOriginal Original();

    /// <summary>Writes PL1 and PL2 in the safe order.</summary>
    /// <param name="current">The state now.</param>
    /// <param name="sustained">New PL1.</param>
    /// <param name="boost">New PL2.</param>
    [LabWorkerWrite]
    void WritePair(LabMsiState current, int sustained, int boost);

    /// <summary>Puts a captured state back and reads it back.</summary>
    /// <param name="original">The captured state.</param>
    /// <returns>Whether the readback matches exactly.</returns>
    [LabWorkerWrite]
    bool RestorePower(LabMsiState original);

    /// <summary>Writes a raw charge limit byte.</summary>
    /// <param name="raw">The byte.</param>
    [LabWorkerWrite]
    void WriteChargeRaw(int raw);

    /// <summary>Reads both fan mode flags.</summary>
    LabMsiFanState ReadFans();

    /// <summary>Writes fan mode flags and reads them back.</summary>
    [LabWorkerWrite]
    bool WriteFans(LabMsiFanState state);
}

/// <summary>The raw MSI_ACPI method calls, so the logic above them can be tested without hardware.</summary>
internal interface ILabMsiWmiChannel : IDisposable
{
    /// <summary>Calls a <c>Get_*</c> method with a selector byte.</summary>
    /// <param name="method">Method name.</param>
    /// <param name="selector">First package byte.</param>
    /// <returns>The 32-byte response, status byte first.</returns>
    byte[] Get(string method, byte selector);

    /// <summary>Calls a <c>Set_*</c> method with a 32-byte package.</summary>
    /// <param name="method">Method name.</param>
    /// <param name="package">The package.</param>
    void Set(string method, byte[] package);
}

/// <summary>
///     MSI_ACPI power limit, charge limit and fan telemetry access, following the Claw 8 A2VM plugin
///     (<c>src/WSGM.Device.Msi.Claw8A2Vm/ClawCapabilities.cs</c>). Every call is logged before and after.
///     It runs only inside the hardware worker, behind <see cref="ILabMsiWmi" />.
/// </summary>
internal sealed class LabMsiWmi : ILabMsiWmi
{
    /// <summary>The WMI namespace.</summary>
    public const string Namespace = "root\\WMI";

    /// <summary>The WMI class.</summary>
    public const string ClassName = "MSI_ACPI";

    /// <summary>MSI_ACPI packages are always this long.</summary>
    public const int PackageLength = 32;

    /// <summary>
    ///     The firmware scenario register. A scenario switch can reset the limits, so it is captured and
    ///     put back first (<c>ClawHardwareFacts.ScenarioAddress</c> in the Claw plugin).
    /// </summary>
    public const byte ScenarioAddress = 0xD2;

    /// <summary>The bits of the charge register that hold the percentage; bit 7 is a firmware flag.</summary>
    public const byte ChargePercentMask = 0x7F;

    private readonly ILabMsiWmiChannel _channel;
    private readonly LabMsiLayout _layout;
    private readonly LabPowerLog _log;

    /// <summary>Creates the transport over a channel.</summary>
    /// <param name="channel">The WMI channel; owned and disposed by this instance.</param>
    /// <param name="layout">Accessors from the curated record.</param>
    /// <param name="log">Where every call is logged.</param>
    internal LabMsiWmi(ILabMsiWmiChannel channel, LabMsiLayout layout, LabPowerLog log)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>The worker service registration.</summary>
    public static LabWorkerService Service { get; } = new("msi-wmi", typeof(ILabMsiWmi),
        (args, log) => Open(LabWorkerService.Arg<LabMsiLayout>(args, 0), log));

    /// <inheritdoc />
    public void Dispose()
    {
        _channel.Dispose();
    }

    /// <summary>Reads PL1, PL2 and the scenario.</summary>
    /// <returns>The state.</returns>
    public LabMsiState ReadPower()
    {
        var sustained = Int32(Get(_layout.Sustained!.Value));
        var boost = Int32(Get(_layout.Boost!.Value));
        var scenario = Get(ScenarioAddress)[1];
        LabMsiState state = new(sustained, boost, scenario);
        _log.Add("snapshot", state);
        return state;
    }

    /// <inheritdoc />
    public LabMsiOriginal Original()
    {
        LabMsiState? power = null;
        string? powerUnavailable = null;
        if (_layout.HasTdp)
        {
            try
            {
                power = StablePower();
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                powerUnavailable = ex.Message;
            }
        }

        int? charge = null;
        string? chargeUnavailable = null;
        if (_layout.Charge is not null)
        {
            try
            {
                charge = ReadChargeRaw();
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                chargeUnavailable = ex.Message;
            }
        }

        return new LabMsiOriginal(power, powerUnavailable, charge, chargeUnavailable)
        {
            Fans = _layout.FanCustom is not null && _layout.FanFullSpeed is not null ? ReadFans() : null
        };
    }

    /// <inheritdoc />
    public LabMsiFanState ReadFans()
    {
        return new LabMsiFanState(Get(_layout.FanCustom!.Value)[1], Get(_layout.FanFullSpeed!.Value)[1]);
    }

    /// <inheritdoc />
    public bool WriteFans(LabMsiFanState state)
    {
        var current = ReadFans();
        if (current.Custom != state.Custom)
        {
            WriteByte(_layout.FanCustom!.Value, state.Custom);
        }

        if (current.FullSpeed != state.FullSpeed)
        {
            WriteByte(_layout.FanFullSpeed!.Value, state.FullSpeed);
        }

        return ReadFans() == state;
    }

    /// <summary>
    ///     Writes PL1 and PL2 in the plugin's order: PL2 first when PL1 rises above the current PL2,
    ///     otherwise PL1 first. The return is not a readback.
    /// </summary>
    /// <param name="current">The state now.</param>
    /// <param name="sustained">New PL1.</param>
    /// <param name="boost">New PL2.</param>
    public void WritePair(LabMsiState current, int sustained, int boost)
    {
        if (sustained < _layout.MinimumWatts || boost > _layout.MaximumWatts || sustained > boost)
        {
            throw new InvalidOperationException(
                $"Refused MSI limits {sustained}/{boost} W: outside the record's range.");
        }

        if (sustained > current.Boost)
        {
            WriteInt32(_layout.Boost!.Value, boost);
            WriteInt32(_layout.Sustained!.Value, sustained);
        }
        else
        {
            WriteInt32(_layout.Sustained!.Value, sustained);
            if (boost != current.Boost)
            {
                WriteInt32(_layout.Boost!.Value, boost);
            }
        }
    }

    /// <summary>Puts a captured state back: the scenario first, then the pair in order.</summary>
    /// <param name="original">The captured state.</param>
    /// <returns>Whether the readback matches exactly.</returns>
    public bool RestorePower(LabMsiState original)
    {
        try
        {
            var current = ReadPower();
            if (current.Scenario != original.Scenario)
            {
                WriteByte(ScenarioAddress, (byte)original.Scenario);
                current = ReadPower();
            }

            if (current.Sustained != original.Sustained || current.Boost != original.Boost)
            {
                // The captured pair was accepted by the firmware, so it is written back even when the
                // record's test range is narrower.
                if (original.Sustained > current.Boost)
                {
                    WriteInt32(_layout.Boost!.Value, original.Boost);
                    WriteInt32(_layout.Sustained!.Value, original.Sustained);
                }
                else
                {
                    WriteInt32(_layout.Sustained!.Value, original.Sustained);
                    if (original.Boost != current.Boost)
                    {
                        WriteInt32(_layout.Boost!.Value, original.Boost);
                    }
                }
            }

            var readback = ReadPower();
            var matches = readback == original;
            _log.Add("restore-readback", new { State = readback, Matches = matches });
            return matches;
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
            _log.Add("restore-error", ex.Message);
            return false;
        }
    }

    /// <summary>Reads the raw charge limit byte.</summary>
    /// <returns>The byte: bit 7 a firmware flag, the rest the percentage.</returns>
    public int ReadChargeRaw()
    {
        var raw = Get(_layout.Charge!.Value)[1];
        _log.Add("charge-limit", new { Raw = raw, Percent = raw & ChargePercentMask });
        return raw;
    }

    /// <summary>Writes a raw charge limit byte. The return is not a readback.</summary>
    /// <param name="raw">The byte.</param>
    public void WriteChargeRaw(int raw)
    {
        var percent = raw & ChargePercentMask;
        // BIOS can report 0x80 (no percentage configured). It must remain restorable verbatim.
        if (raw is < 0 or > 0xFF ||
            (percent != 0 && (percent < _layout.ChargeMinimum || percent > _layout.ChargeMaximum)))
        {
            throw new InvalidOperationException($"Refused MSI charge limit {percent} %: outside the record's range.");
        }

        WriteByte(_layout.Charge!.Value, (byte)raw);
    }

    /// <summary>Reads both fans' RPM from the fan table's channel 0, as the plugin does.</summary>
    /// <returns>Readings, or none when the record has no fan getter or the read fails.</returns>
    public IReadOnlyList<LabFanReading> FanSpeeds()
    {
        if (_layout.FanGetter is not { } getter)
        {
            return [];
        }

        try
        {
            var fan = _channel.Get(getter, 0);
            return
            [
                new LabFanReading("Left fan", Rpm(fan[1], fan[2]), "rpm"),
                new LabFanReading("Right fan", Rpm(fan[3], fan[4]), "rpm")
            ];
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
            _log.Add("fan-read-unavailable", ex.Message);
            return [];
        }
    }

    /// <summary>Finds the one active MSI_ACPI instance and checks it answers.</summary>
    /// <param name="layout">Accessors from the curated record.</param>
    /// <param name="log">Where every call is logged.</param>
    /// <returns>The transport.</returns>
    public static LabMsiWmi Open(LabMsiLayout layout, LabPowerLog log)
    {
        var channel = WmiChannel.Open();
        try
        {
            var version = channel.Get("Get_WMI", 0);
            log.Add("wmi-open", new { Class = ClassName, Version = Convert.ToHexString(version) });
            return new LabMsiWmi(channel, layout, log);
        }
        catch
        {
            channel.Dispose();
            throw;
        }
    }

    /// <summary>Two reads a moment apart; refuses a state another program is changing.</summary>
    /// <returns>The stable state.</returns>
    public LabMsiState StablePower()
    {
        var first = ReadPower();
        Thread.Sleep(150);
        return first == ReadPower()
            ? first
            : throw new InvalidOperationException(
                "The power settings changed by themselves. Another program may be controlling them.");
    }

    /// <summary>Puts the percentage into the register, carrying the flag bit through unchanged.</summary>
    /// <param name="currentRaw">The register now.</param>
    /// <param name="percent">The percentage.</param>
    /// <returns>The new register value.</returns>
    public static int EncodeCharge(int currentRaw, int percent)
    {
        return (currentRaw & ~ChargePercentMask & 0xFF) | (percent & ChargePercentMask);
    }

    /// <summary>Whether an exception is one a WMI call can raise.</summary>
    /// <param name="ex">The exception.</param>
    /// <returns>True for transport failures.</returns>
    public static bool IsTransportFailure(Exception ex)
    {
        return ex is ManagementException or IOException or InvalidDataException or UnauthorizedAccessException
            or TimeoutException or COMException or InvalidOperationException;
    }

    private static int Rpm(byte high, byte low)
    {
        var divisor = (high << 8) | low;
        return divisor == 0 ? 0 : 480_000 / divisor;
    }

    private static int Int32(byte[] response)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(1, sizeof(int)));
    }

    private byte[] Get(byte address)
    {
        _log.Add("wmi-read", new { Method = _layout.Get, Address = $"0x{address:X2}" });
        var response = _channel.Get(_layout.Get, address);
        _log.Add("wmi-response", new { Address = $"0x{address:X2}", Raw = Convert.ToHexString(response) });
        return response.Length == PackageLength
            ? response
            : throw new InvalidDataException("The MSI response has the wrong length.");
    }

    private void WriteInt32(byte address, int value)
    {
        var package = new byte[PackageLength];
        package[0] = address;
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(1, sizeof(int)), value);
        Write(package);
    }

    private void WriteByte(byte address, byte value)
    {
        var package = new byte[PackageLength];
        package[0] = address;
        package[1] = value;
        Write(package);
    }

    private void Write(byte[] package)
    {
        _log.Add("wmi-write", new { Method = _layout.Set, Package = Convert.ToHexString(package) });
        try
        {
            _channel.Set(_layout.Set, package);
            _log.Add("wmi-write-returned", new { Address = $"0x{package[0]:X2}" });
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
            _log.Add("wmi-write-failed", new { Address = $"0x{package[0]:X2}", ex.Message });
            throw new IOException("The MSI write failed; its effect is unknown and it was not tried again.", ex);
        }
    }

    private sealed class WmiChannel : ILabMsiWmiChannel
    {
        private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);
        private readonly ManagementObject _instance;
        private readonly ManagementClass _packageClass;
        private bool _stuck;

        private WmiChannel(ManagementObject instance, ManagementClass packageClass)
        {
            _instance = instance;
            _packageClass = packageClass;
        }

        public byte[] Get(string method, byte selector)
        {
            if (!method.StartsWith("Get_", StringComparison.Ordinal))
            {
                throw new ArgumentException("Only Get_* methods read.", nameof(method));
            }

            var package = new byte[PackageLength];
            package[0] = selector;
            return Invoke(method, package);
        }

        public void Set(string method, byte[] package)
        {
            if (!method.StartsWith("Set_", StringComparison.Ordinal) || package.Length != PackageLength)
            {
                throw new ArgumentException("Only 32-byte Set_* calls write.", nameof(method));
            }

            _ = Invoke(method, [.. package]);
        }

        public void Dispose()
        {
            if (_stuck)
            {
                // A call is still inside WMI; it keeps the objects alive and they are collected later.
                return;
            }

            _packageClass.Dispose();
            _instance.Dispose();
        }

        public static WmiChannel Open()
        {
            using (ManagementClass definition = new(Namespace, ClassName, null))
            {
                if (definition.Methods.Cast<MethodData>().All(method => method.Name != "Get_WMI"))
                {
                    throw new FileNotFoundException("MSI_ACPI has no Get_WMI method.");
                }
            }

            ManagementObject? found = null;
            using (ManagementObjectSearcher searcher = new(Namespace, $"SELECT * FROM {ClassName} WHERE Active = TRUE"))
            using (var candidates = searcher.Get())
            {
                foreach (var candidate in candidates)
                {
                    if (found is not null)
                    {
                        candidate.Dispose();
                        found.Dispose();
                        throw new InvalidDataException("More than one active MSI_ACPI instance.");
                    }

                    found = (ManagementObject)candidate;
                }
            }

            if (found is null)
            {
                throw new FileNotFoundException("No active MSI_ACPI instance.");
            }

            try
            {
                return new WmiChannel(found, new ManagementClass(Namespace, "Package_32", null));
            }
            catch
            {
                found.Dispose();
                throw;
            }
        }

        // A call that does not return in time may still reach the firmware, so the channel refuses
        // every later call rather than stacking another on top of an unknown one.
        private byte[] Invoke(string method, byte[] request)
        {
            if (_stuck)
            {
                throw new IOException("An earlier MSI call did not finish, so no further call is made.");
            }

            var call = Task.Run(() => InvokeCore(method, request));
            if (Task.WhenAny(call, Task.Delay(CallTimeout)).GetAwaiter().GetResult() != call)
            {
                _stuck = true;
                throw new TimeoutException($"{method} did not answer within three seconds; its effect is unknown.");
            }

            return call.GetAwaiter().GetResult();
        }

        private byte[] InvokeCore(string method, byte[] request)
        {
            // Get_WMI takes no input package; every addressed accessor does (see MsiWmiPlatform).
            using var input = _instance.GetMethodParameters(method);
            using var package = input is null ? null : _packageClass.CreateInstance();
            if (input is not null)
            {
                if (package is null)
                {
                    throw new IOException("The Package_32 request could not be created.");
                }

                package["Bytes"] = request;
                input["Data"] = package;
            }

            using var output = _instance.InvokeMethod(method, input, null)
                               ?? throw new IOException($"{method} returned no response.");
            if (output["Data"] is not ManagementBaseObject returned)
            {
                throw new InvalidDataException($"{method} returned no Package_32 response.");
            }

            using (returned)
            {
                if (returned["Bytes"] is not byte[] { Length: PackageLength } response)
                {
                    throw new InvalidDataException($"{method} returned an invalid response.");
                }

                return response[0] == 0x01
                    ? response
                    : throw new InvalidDataException($"{method} returned status 0x{response[0]:X2}.");
            }
        }
    }
}
