using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Identity;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Device.Msi.Claw;

internal static class ClawHardwareFacts
{
    public const string PackageId = "wsgm.device.msi.claw";

    /// <summary>The package id before the plugin covered the whole family; its state is adopted once.</summary>
    public const string RetiredPackageId = "wsgm.device.msi.claw-8-a2vm";

    public const string Manufacturer = "MICRO-STAR INTERNATIONAL CO., LTD.";
    public const string UsbVendorId = "0DB0";
    public const string XInputProductId = "1901";
    public const string DirectInputProductId = "1902";

    /// <summary>The MCU's testing mode, which HC's <c>DClawController</c> still reads as a DirectInput pad.</summary>
    public const string TestingProductId = "1903";

    public const byte PowerSustainedAddress = 0x50;
    public const byte PowerBoostAddress = 0x51;

    /// <summary>HC's ClawBZ2EM writes its boost limit here as well; see <see cref="ClawModel.WritesFastLimit" />.</summary>
    public const byte PowerFastAddress = 0x52;

    public const byte ScenarioAddress = 0xD2;
    public const byte FanCustomAddress = 0xD4;
    public const byte FanFullSpeedAddress = 0x98;
    public const byte ChargeLimitAddress = 0xD7;

    /// <summary>The RGB profile on the reference unit; <see cref="ClawModels.LightingProfileAddress" /> picks per MCU.</summary>
    public const ushort DefaultLightingProfileAddress = 0x024A;

    public const int McuReportLength = 64;
    public const int WmiPackageLength = 32;
}

internal enum ClawControllerMode : byte
{
    Offline = 0,
    XInput = 1,
    DirectInput = 2
}

internal sealed record ClawIdentityState
{
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public required DeviceIdentitySnapshot Snapshot { get; init; }

    public required bool ExactMachineMatch { get; init; }

    /// <summary>The matched model; null exactly when <see cref="ExactMachineMatch" /> is false.</summary>
    public ClawModel? Model { get; init; }

    /// <summary>
    ///     The MSI_ACPI provider is present, which is all HC requires. Derived from the binding, which
    ///     exists whenever the provider does, so a WMI capability admitted here always has one to journal
    ///     against.
    /// </summary>
    public bool WmiAvailable => WmiFirmwareIdentity is not null;

    /// <summary>
    ///     The firmware the power and fan journal entries bind to: the EC version, or the BIOS version
    ///     where the EC's cannot be decoded, and the MSI_ACPI interface, for example
    ///     <c>ec:1T52EMS1.109;msi-acpi:8.0</c>. Null when the provider is unavailable.
    /// </summary>
    public string? WmiFirmwareIdentity { get; init; }

    public required bool OnAcPower { get; init; }
}

/// <summary>The EC power limits and the SHIFT scenario byte, read together.</summary>
/// <param name="SustainedWatts">PL1, EC 0x50.</param>
/// <param name="BoostWatts">PL2, EC 0x51.</param>
/// <param name="Scenario">The SHIFT scenario byte, EC 0xD2.</param>
/// <param name="FastWatts">
///     EC 0x52 on a model that writes it (the BZ2EM), when it could be read. HC never reads it, so an
///     unreadable register is null rather than a failure.
/// </param>
internal sealed record PowerPair(int SustainedWatts, int BoostWatts, byte Scenario, int? FastWatts = null);

internal sealed record ChargeLimitState(int Percent, byte RawValue);

internal sealed record FanTable(byte[] DutyBuffer, byte[] TemperatureBuffer);

internal sealed record FanSnapshot(
    FanTable Left,
    FanTable Right,
    byte CustomFlag,
    byte FullSpeedFlag);

internal sealed record FanTelemetry(int LeftRpm, int RightRpm, int TemperatureCelsius);

internal sealed record LightingState(
    int Brightness,
    int RightRingColor,
    int LeftRingColor,
    int ButtonsColor);

/// <summary>The controller interfaces observed at one moment, and what they looked like.</summary>
/// <param name="Mode">The mode the MCU reports.</param>
/// <param name="ProductId">The USB product id the MCU enumerated as.</param>
/// <param name="PhysicalLocation">Composite USB location shared by the interfaces.</param>
/// <param name="PhysicalDevices">Interfaces WSGM may hide, which is the set the handoff needs.</param>
/// <param name="ObservedEndpoints">
///     Every candidate endpoint seen at this location, as "productId/usagePage:usage in/out". Carried so
///     a failed handoff can say what it actually found rather than only that it found nothing — a plugin
///     has no logging channel of its own, so a reason string is the only way this reaches a log.
/// </param>
internal sealed record ControllerTopology(
    ClawControllerMode Mode,
    string ProductId,
    string PhysicalLocation,
    IReadOnlyList<PhysicalDeviceIdentity> PhysicalDevices,
    string ObservedEndpoints = "");

internal interface IClawIdentityReader
{
    ValueTask<ClawIdentityState> ReadAsync(CancellationToken cancellationToken);
}

internal interface IMsiWmiTransport : IAsyncDisposable
{
    ValueTask<bool> IsProviderAvailableAsync(CancellationToken cancellationToken);

    ValueTask<byte[]> InvokeGetterAsync(
        string methodName,
        byte selector,
        CancellationToken cancellationToken);

    ValueTask InvokeSetterAsync(
        string methodName,
        byte[] package,
        CancellationToken cancellationToken);
}

internal interface IMsiOemEventSource : IAsyncDisposable
{
    ValueTask<bool> StartAsync(
        Func<byte, DateTimeOffset, ValueTask> callback,
        CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

internal interface IClawMcuTransport : IAsyncDisposable
{
    ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken);

    ValueTask<byte[]> ReadProfileAsync(
        ushort address,
        byte length,
        CancellationToken cancellationToken);

    ValueTask WriteProfileAsync(
        ushort address,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken);

    /// <summary>HC's <c>SyncToROM</c> (<c>0F 00 00 3C 22</c>): commits written profiles to the MCU's ROM.</summary>
    ValueTask SyncToRomAsync(CancellationToken cancellationToken);

    ValueTask<ControllerTopology> SwitchModeAsync(
        ClawControllerMode mode,
        string physicalLocation,
        Deadline deadline,
        CancellationToken cancellationToken);
}

internal interface IClawControllerSource : IAsyncDisposable
{
    ValueTask<ControllerTopology?> DiscoverAsync(CancellationToken cancellationToken);

    ValueTask StartAsync(
        ClawModel model,
        Func<CanonicalControllerSample, CancellationToken, ValueTask> publish,
        Action<Exception> fault,
        CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);

    ValueTask WriteRumbleAsync(byte weak, byte strong, CancellationToken cancellationToken);
}

internal interface IClawMotionSource : IAsyncDisposable
{
    /// <param name="model">The model, for its sensors and axis signs.</param>
    /// <param name="publish">Takes each sample on the sensor's own thread; it must return quickly.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    ValueTask<bool> StartAsync(
        ClawModel model,
        Action<MotionSample> publish,
        CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

internal interface IFirmwareChordSuppressor : IAsyncDisposable
{
    ValueTask<bool> StartAsync(
        Action<Exception> fault,
        Action<FirmwareChord> chord,
        CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

internal sealed record ClawHardwareServices(
    IClawIdentityReader Identity,
    IMsiWmiTransport Wmi,
    IMsiOemEventSource OemEvents,
    IClawMcuTransport Mcu,
    IClawControllerSource Controller,
    IClawMotionSource Motion,
    IFirmwareChordSuppressor ChordSuppressor,
    OemButtonLatch OemButtons);

/// <summary>Applies the one minimum budget required before any Claw hardware write.</summary>
/// <remarks>
///     Two seconds covers the slowest journal flush plus one bounded firmware exchange. Keeping this
///     threshold here prevents lifecycle, command, lighting, and mode-switch paths from drifting apart.
/// </remarks>
internal static class ClawWriteBudget
{
    private static readonly TimeSpan Minimum = TimeSpan.FromSeconds(2);

    internal static bool IsAvailable(Deadline deadline)
    {
        return deadline.Remaining >= Minimum;
    }

    internal static void Require(Deadline deadline, string operation)
    {
        if (!IsAvailable(deadline))
        {
            throw new OperationCanceledException($"Insufficient budget for {operation}.");
        }
    }
}
