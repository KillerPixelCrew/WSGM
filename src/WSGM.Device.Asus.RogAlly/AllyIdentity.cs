// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using WSGM.Device.Sdk.Identity;

namespace WSGM.Device.Asus.RogAlly;

/// <summary>What the plugin revalidates before every acquisition and hardware command.</summary>
internal sealed record AllyIdentityState
{
    public required DeviceIdentitySnapshot Snapshot { get; init; }

    /// <summary>The model the live SMBIOS identity resolves to, or null when it no longer matches.</summary>
    public AllyModel? Model { get; init; }

    public required bool OnAcPower { get; init; }

    public bool ExactMachineMatch => Model is not null;

    /// <summary>Firmware identity recovery journal entries are bound to.</summary>
    /// <remarks>
    ///     The BIOS version, because it carries the ACPI methods ATKACPI dispatches to. An entry
    ///     written under one BIOS is not replayed under another.
    /// </remarks>
    public string FirmwareIdentity => $"bios:{Snapshot.BiosVersion?.Trim() ?? "unknown"}";
}

internal interface IAllyIdentityReader
{
    ValueTask<AllyIdentityState> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Reads SMBIOS identity from the registry copy Windows keeps, and the power source.</summary>
/// <remarks>
///     <c>HKLM\HARDWARE\DESCRIPTION\System\BIOS</c> is populated from SMBIOS at boot, so reading it
///     touches no device and needs no WMI provider. HC matches the same baseboard fields through
///     <c>MotherboardInfo</c> (<c>HandheldCompanion.Devices/IDevice.cs:710-716</c>).
/// </remarks>
internal sealed partial class WindowsAllyIdentityReader : IAllyIdentityReader
{
    private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";

    public ValueTask<AllyIdentityState> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var key = Registry.LocalMachine.OpenSubKey(BiosKey);
        DeviceIdentitySnapshot snapshot = new()
        {
            BaseboardManufacturer = Read(key, "BaseBoardManufacturer"),
            BaseboardProduct = Read(key, "BaseBoardProduct"),
            BaseboardVersion = Read(key, "BaseBoardVersion"),
            SystemManufacturer = Read(key, "SystemManufacturer"),
            SystemProduct = Read(key, "SystemProductName"),
            SystemSku = Read(key, "SystemSKU"),
            SystemFamily = Read(key, "SystemFamily"),
            BiosVersion = Read(key, "BIOSVersion")
        };
        return ValueTask.FromResult(new AllyIdentityState
        {
            Snapshot = snapshot,
            Model = AllyModels.Match(snapshot),
            OnAcPower = IsOnAcPower()
        });
    }

    private static string? Read(RegistryKey? key, string name)
    {
        return key?.GetValue(name) is string value ? IdentityText.Normalize(value) : null;
    }

    private static bool IsOnAcPower()
    {
        // An unknown line status is treated as AC: no capability here is restricted to battery.
        return !GetSystemPowerStatus(out var status) || status.ACLineStatus != 0;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }
}
