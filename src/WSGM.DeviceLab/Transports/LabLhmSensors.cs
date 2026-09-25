using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;

namespace WSGM.DeviceLab.Transports;

/// <summary>One LibreHardwareMonitor sensor value.</summary>
/// <param name="Hardware">Hardware name, for example the CPU or GPU.</param>
/// <param name="HardwareIdentifier">LHM hardware identifier, for example <c>/amdcpu/0</c>.</param>
/// <param name="HardwareType">LHM hardware type.</param>
/// <param name="Name">Sensor name, for example <c>Core (Tctl/Tdie)</c>.</param>
/// <param name="Identifier">LHM sensor identifier, for example <c>/amdcpu/0/temperature/2</c>.</param>
/// <param name="Value">Value in the sensor type's unit (RPM, °C or percent), or null when not read.</param>
internal sealed record LabLhmSensor(
    string Hardware,
    string HardwareIdentifier,
    string HardwareType,
    string Name,
    string Identifier,
    double? Value);

/// <summary>The fan, temperature and fan-control sensors LibreHardwareMonitor saw at one moment.</summary>
internal sealed record LabLhmReading
{
    /// <summary>Fan tachometers in RPM.</summary>
    public IReadOnlyList<LabLhmSensor> Fans { get; init; } = [];

    /// <summary>Temperatures in °C.</summary>
    public IReadOnlyList<LabLhmSensor> Temperatures { get; init; } = [];

    /// <summary>Fan-control duty readings in percent. Read only; the lab never sets them.</summary>
    public IReadOnlyList<LabLhmSensor> Controls { get; init; } = [];

    /// <summary>Sensors left out because a list was full.</summary>
    public int Dropped { get; init; }

    /// <summary>Why nothing could be read, or null.</summary>
    public string? Problem { get; init; }
}

/// <summary>
///     A read-only LibreHardwareMonitor session for fan RPM and temperatures on any device.
/// </summary>
/// <remarks>
///     <para>
///         It opens LHM's <c>Computer</c> once with the CPU and GPU groups, the ones Handheld Companion
///         uses, updates it for each reading, and closes it on dispose. It only reads sensor values; it never
///         calls a control's <c>SetSoftware</c> or <c>SetDefault</c>, so LHM has nothing to restore.
///     </para>
///     <para>
///         The motherboard group stays off: its discovery writes Super I/O enter-configuration sequences to
///         LPC ports and reads EC tachometers through the 0x62/0x66 ports, which on a handheld reach the
///         embedded controller that Windows' EC driver and the firmware are using. It was on when an ROG Xbox
///         Ally X hard-reset twice after the system dump (2026-09-25). The controller and PSU groups stay off
///         too: their discovery sends commands to every FTDI serial port and to vendor USB HID devices (fan
///         hubs, pumps, PSUs), which is a write to hardware the lab did not identify. SMU access goes through
///         PawnIO; without it LHM reports fewer sensors.
///     </para>
///     <para>
///         LHM is not thread-safe, so every call on a session is serialized. Opening can take seconds and
///         runs off the UI thread with a deadline.
///     </para>
/// </remarks>
internal sealed class LabLhmSensors : IDisposable
{
    private const int MaxHardware = 64;
    private const int MaxDepth = 3;
    private const int MaxPerKind = 64;

    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(20);

    private readonly Lock _gate = new();
    private Computer? _computer;

    private LabLhmSensors(Computer? computer, string? problem)
    {
        _computer = computer;
        Problem = problem;
    }

    /// <summary>Why the session could not open, or null when it is open.</summary>
    public string? Problem { get; }

    /// <summary>Closes the LHM session.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            var computer = _computer;
            _computer = null;
            Close(computer);
        }
    }

    /// <summary>Opens a session off the calling thread, giving up after a deadline.</summary>
    /// <param name="cancellationToken">Stops waiting; a late open is closed when it finishes.</param>
    /// <returns>An open session, or one carrying only a <see cref="Problem" />.</returns>
    public static async Task<LabLhmSensors> OpenAsync(CancellationToken cancellationToken)
    {
        var opening = Task.Run(Open, CancellationToken.None);
        try
        {
            return await opening.WaitAsync(OpenTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // The open keeps running; close whatever it produces so no session is left behind.
            _ = opening.ContinueWith(task => task.Result.Dispose(), CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            if (ex is OperationCanceledException)
            {
                throw;
            }

            return new LabLhmSensors(null, "it did not finish opening in time");
        }
    }

    /// <summary>Opens, reads once and closes, for a one-off report. Call it from a worker thread.</summary>
    /// <param name="cancellationToken">Stops waiting for the open.</param>
    /// <returns>The reading.</returns>
    public static LabLhmReading ReadOnce(CancellationToken cancellationToken)
    {
        using var session = OpenAsync(cancellationToken).GetAwaiter().GetResult();
        return session.Read();
    }

    /// <summary>Updates every opened hardware item and collects fan, temperature and control sensors.</summary>
    /// <returns>The reading; a problem instead of values when the session is not open or LHM failed.</returns>
    public LabLhmReading Read()
    {
        lock (_gate)
        {
            if (_computer is null)
            {
                return new LabLhmReading { Problem = Problem ?? "the session is closed" };
            }

            List<LabLhmSensor> fans = [];
            List<LabLhmSensor> temperatures = [];
            List<LabLhmSensor> controls = [];
            var dropped = 0;
            try
            {
                var visited = 0;
                foreach (var hardware in _computer.Hardware)
                {
                    Collect(hardware, 0);
                }

                void Collect(IHardware hardware, int depth)
                {
                    if (depth > MaxDepth || ++visited > MaxHardware)
                    {
                        return;
                    }

                    hardware.Update();
                    foreach (var sensor in hardware.Sensors)
                    {
                        var target = sensor.SensorType switch
                        {
                            SensorType.Fan => fans,
                            SensorType.Temperature => temperatures,
                            SensorType.Control => controls,
                            _ => null
                        };
                        if (target is null)
                        {
                            continue;
                        }

                        if (target.Count >= MaxPerKind)
                        {
                            dropped++;
                            continue;
                        }

                        target.Add(Sensor(hardware, sensor));
                    }

                    foreach (var sub in hardware.SubHardware)
                    {
                        Collect(sub, depth + 1);
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return new LabLhmReading
                {
                    Fans = fans,
                    Temperatures = temperatures,
                    Controls = controls,
                    Dropped = dropped,
                    Problem = $"update failed: {ex.Message}"
                };
            }

            return new LabLhmReading
            {
                Fans = fans,
                Temperatures = temperatures,
                Controls = controls,
                Dropped = dropped
            };
        }
    }

    private static LabLhmSensors Open()
    {
        Computer computer = new()
        {
            IsMotherboardEnabled = false,
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsControllerEnabled = false,
            IsPsuEnabled = false
        };
        try
        {
            computer.Open();
            return new LabLhmSensors(computer, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Close(computer);
            return new LabLhmSensors(null, $"it did not open: {ex.Message}");
        }
    }

    private static void Close(Computer? computer)
    {
        if (computer is null)
        {
            return;
        }

        try
        {
            computer.Close();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Closing releases LHM's driver handles; a failure leaves nothing the lab changed.
        }
    }

    private static LabLhmSensor Sensor(IHardware hardware, ISensor sensor)
    {
        double? value = sensor.Value is { } raw && float.IsFinite(raw) ? Math.Round(raw, 1) : null;
        return new LabLhmSensor(
            hardware.Name,
            hardware.Identifier.ToString(),
            hardware.HardwareType.ToString(),
            sensor.Name,
            sensor.Identifier.ToString(),
            value);
    }
}
