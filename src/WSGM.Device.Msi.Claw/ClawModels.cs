using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Identity;

namespace WSGM.Device.Msi.Claw;

/// <summary>How a model's IMU reaches Windows.</summary>
internal enum ClawMotionPath
{
    /// <summary>
    ///     The Intel IO driver's "Physical Gyrometer" and "Physical Accelerometer", custom legacy
    ///     Sensor API sensors that WinRT does not project. HC declares them through
    ///     <c>WindowsGyrometerFields</c> for ClawA2VM and ClawCG3EM only.
    /// </summary>
    PhysicalSensorApi,

    /// <summary>The standard WinRT Gyrometer and Accelerometer, HC's default when no fields are declared.</summary>
    WinRt
}

/// <summary>
///     One Claw model. Every per-model fact lives in this record; correct a model by changing its row,
///     never by adding a model check elsewhere. Rows other than MS-1T52 are taken from Handheld
///     Companion 1.3.1.6 (<c>_ref/HandheldCompanion</c>) and have no hardware pass; PROVENANCE.md cites each.
/// </summary>
internal sealed record ClawModel
{
    /// <summary>The definition id WSGM keys glyphs and diagnostics on.</summary>
    public required string DefinitionId { get; init; }

    /// <summary>The SMBIOS baseboard product HC's <c>IDevice.GetCurrent</c> switches on.</summary>
    public required string BoardProduct { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>The HC device class the row was read from.</summary>
    public required string HcClass { get; init; }

    /// <summary>True only for a model with an attended Device Lab pass.</summary>
    public required bool HardwareVerified { get; init; }

    /// <summary>The lower bound of both power limits, from HC's <c>cTDP</c>.</summary>
    public required int MinimumWatts { get; init; }

    /// <summary>The upper bound of both power limits, from HC's <c>cTDP</c>.</summary>
    public required int MaximumWatts { get; init; }

    public required IReadOnlyList<DevicePowerPreset> PowerPresets { get; init; }

    /// <summary>The low bits HC's <c>GetShiftModeValue(User)</c> writes: 3, or 6 on ClawCG3EM.</summary>
    public required byte UserScenario { get; init; }

    /// <summary>
    ///     True where HC's <c>set_short_limit</c> also writes EC 0x52 (ClawBZ2EM, the AMD model, whose
    ///     boost limit is a fast/slow PPT pair).
    /// </summary>
    public required bool WritesFastLimit { get; init; }

    public required ClawMotionPath MotionPath { get; init; }

    /// <summary>
    ///     Signs applied after HC's shared axis swap (X, Z, Y), from the model's <c>GyroMatrix.Axis</c>.
    /// </summary>
    public required Vector3 GyroSigns { get; init; }

    /// <summary>
    ///     Signs applied after the same swap, from the model's <c>AcceleroMatrix.Axis</c>.
    /// </summary>
    public required Vector3 AccelerometerSigns { get; init; }

    /// <summary>
    ///     True where HC's <c>DClawController</c> drives rumble as on/off at 193 (exactly ClawA1M);
    ///     every later model takes the proportional motor values.
    /// </summary>
    public required bool BinaryRumble { get; init; }
}

internal static class ClawModels
{
    /// <summary>HC's ClawA1M rumble level, written for any nonzero motor value on that model.</summary>
    public const byte BinaryRumbleLevel = 193;

    private static readonly Vector3 Positive = new(1, 1, 1);
    private static readonly Vector3 FlippedZ = new(1, 1, -1);

    public static ClawModel A1M { get; } = new()
    {
        DefinitionId = "ms-1t41",
        BoardProduct = "MS-1T41",
        DisplayName = "MSI Claw A1M",
        HcClass = "ClawA1M",
        HardwareVerified = false,
        MinimumWatts = 20,
        MaximumWatts = 45,
        PowerPresets = Presets((20, 20), (30, 30), (35, 35), (45, 45)),
        UserScenario = 3,
        WritesFastLimit = false,
        MotionPath = ClawMotionPath.WinRt,
        GyroSigns = FlippedZ,
        AccelerometerSigns = new Vector3(-1, -1, 1),
        BinaryRumble = true
    };

    public static ClawModel Claw7A2Vm { get; } = new()
    {
        DefinitionId = "ms-1t42",
        BoardProduct = "MS-1T42",
        DisplayName = "MSI Claw 7 AI+ A2VM",
        HcClass = "ClawA2VM",
        HardwareVerified = false,
        MinimumWatts = 8,
        MaximumWatts = 37,
        PowerPresets = Presets((8, 9), (17, 18), (30, 31), (37, 37)),
        UserScenario = 3,
        WritesFastLimit = false,
        MotionPath = ClawMotionPath.PhysicalSensorApi,
        GyroSigns = FlippedZ,
        AccelerometerSigns = FlippedZ,
        BinaryRumble = false
    };

    /// <summary>The reference unit. Its power range and presets are the ones measured and reviewed there.</summary>
    public static ClawModel Claw8A2Vm { get; } = Claw7A2Vm with
    {
        DefinitionId = "ms-1t52",
        BoardProduct = "MS-1T52",
        DisplayName = "MSI Claw 8 AI+ A2VM",
        HardwareVerified = true
    };

    public static ClawModel A8Bz2Em { get; } = new()
    {
        DefinitionId = "ms-1t8k",
        BoardProduct = "MS-1T8K",
        DisplayName = "MSI Claw A8 BZ2EM",
        HcClass = "ClawBZ2EM",
        HardwareVerified = false,
        MinimumWatts = 15,
        MaximumWatts = 35,
        PowerPresets = Presets((15, 15), (20, 20), (28, 28), (35, 35)),
        UserScenario = 3,
        WritesFastLimit = true,
        MotionPath = ClawMotionPath.WinRt,
        GyroSigns = FlippedZ,
        AccelerometerSigns = Positive,
        BinaryRumble = false
    };

    public static ClawModel Claw8ExCg3Em { get; } = new()
    {
        DefinitionId = "ms-1t91",
        BoardProduct = "MS-1T91",
        DisplayName = "MSI Claw 8 EX AI+ CG3EM",
        HcClass = "ClawCG3EM",
        HardwareVerified = false,
        // HC's cTDP floor is 20 W, but its own Better Battery override writes 15 W. The range admits
        // the value HC writes.
        MinimumWatts = 15,
        MaximumWatts = 37,
        PowerPresets = Presets((15, 20), (25, 37), (30, 37), (37, 37)),
        UserScenario = 6,
        WritesFastLimit = false,
        MotionPath = ClawMotionPath.PhysicalSensorApi,
        GyroSigns = FlippedZ,
        AccelerometerSigns = FlippedZ,
        BinaryRumble = false
    };

    public static IReadOnlyList<ClawModel> All { get; } = [A1M, Claw7A2Vm, Claw8A2Vm, A8Bz2Em, Claw8ExCg3Em];

    /// <summary>
    ///     Matches HC's identity switch: MSI as the manufacturer and the exact baseboard product. The SKU
    ///     is recorded, never matched, because HC does not read it and it is unknown for every model but
    ///     the reference unit.
    /// </summary>
    public static ClawModel? Find(DeviceIdentitySnapshot identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!string.Equals(identity.SystemManufacturer?.Trim(), ClawHardwareFacts.Manufacturer,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var board = identity.BaseboardProduct?.Trim();
        return All.FirstOrDefault(model =>
            string.Equals(model.BoardProduct, board, StringComparison.OrdinalIgnoreCase));
    }

    public static ClawModel? FindByDefinition(string? definitionId)
    {
        return All.FirstOrDefault(model => string.Equals(model.DefinitionId, definitionId, StringComparison.Ordinal));
    }

    /// <summary>
    ///     HC's <c>deviceVersions</c> table: the RGB profile address for the MCU revision nearest the one
    ///     reported (<c>FirmwareDevice</c> is a MinBy on the absolute difference). Revisions 0x0163 and
    ///     0x0211 keep the profile at 0x01FA; every later one at 0x024A, where the reference unit
    ///     measured it on 0229 and 0230.
    /// </summary>
    private static readonly (int Revision, ushort Address)[] LightingProfileAddresses =
    [
        (0x0163, 0x01FA), (0x0166, 0x024A), (0x0167, 0x024A),
        (0x0211, 0x01FA), (0x0217, 0x024A), (0x0219, 0x024A),
        (0x0308, 0x024A), (0x0411, 0x024A), (0x0414, 0x024A)
    ];

    /// <summary>
    ///     Picks the RGB profile address for an MCU revision (USB bcdDevice as hex, "0230"). A revision
    ///     that cannot be read takes the measured 0x024A; HC would take its nearest-to-zero row instead.
    ///     The lighting service still checks the profile's shape before offering any write.
    /// </summary>
    public static ushort LightingProfileAddress(string? mcuRevision)
    {
        if (!int.TryParse(mcuRevision, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var revision))
        {
            return ClawHardwareFacts.DefaultLightingProfileAddress;
        }

        return LightingProfileAddresses.MinBy(row => Math.Abs(row.Revision - revision)).Address;
    }

    /// <summary>HC's three device power profiles, in order, and WSGM's Full Power at the model ceiling.</summary>
    /// <remarks>
    ///     HC's TDP override arrays are {PL1, PL1, PL2}; the sustained/slow pair takes the first and last.
    ///     The inherited ClawA1M profile handler selects Eco/Green/Sport on AC and Comfort on DC.
    /// </remarks>
    private static IReadOnlyList<DevicePowerPreset> Presets(
        (int Sustained, int Slow) battery,
        (int Sustained, int Slow) balanced,
        (int Sustained, int Slow) performance,
        (int Sustained, int Slow) full)
    {
        return
        [
            new DevicePowerPreset("super-battery", "Super Battery", battery.Sustained, battery.Slow,
                DevicePowerMode.BetterBattery) { ScenarioOnAc = "eco", ScenarioOnDc = "comfort" },
            new DevicePowerPreset("balanced", "Balanced", balanced.Sustained, balanced.Slow,
                DevicePowerMode.Balanced) { ScenarioOnAc = "green", ScenarioOnDc = "comfort" },
            new DevicePowerPreset("extreme-performance", "Extreme Performance", performance.Sustained,
                performance.Slow, DevicePowerMode.BestPerformance) { ScenarioOnAc = "sport", ScenarioOnDc = "comfort" },
            new DevicePowerPreset("full-power", "Full Power", full.Sustained, full.Slow,
                DevicePowerMode.BestPerformance) { ScenarioOnAc = "sport", ScenarioOnDc = "comfort" }
        ];
    }
}
