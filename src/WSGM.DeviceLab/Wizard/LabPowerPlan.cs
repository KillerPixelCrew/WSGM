using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using WSGM.Device.Sdk.Identity;
using WSGM.DeviceLab.Knowledge;
using WSGM.DeviceLab.Transports;

namespace WSGM.DeviceLab.Wizard;

/// <summary>The ATKACPI IDs a curated ASUS record declares, parsed and bounded.</summary>
internal sealed record LabAsusLayout
{
    /// <summary>Performance mode ID, when the record has a power-profile mechanism.</summary>
    public uint? Mode { get; init; }

    /// <summary>Performance mode values the record lists.</summary>
    public IReadOnlyList<int> ModeValues { get; init; } = [];

    /// <summary>Mode names by value, for the page.</summary>
    public IReadOnlyDictionary<int, string> ModeNames { get; init; } = new Dictionary<int, string>();

    /// <summary>Sustained power limit (SPL) ID.</summary>
    public uint? Spl { get; init; }

    /// <summary>Slow boost limit (SPPT) ID.</summary>
    public uint? Sppt { get; init; }

    /// <summary>Fast boost limit (FPPT) ID.</summary>
    public uint? Fppt { get; init; }

    /// <summary>Lowest watts the record allows for a test.</summary>
    public int MinimumWatts { get; init; }

    /// <summary>Highest watts the record allows for a test.</summary>
    public int MaximumWatts { get; init; }

    /// <summary>CPU fan curve ID.</summary>
    public uint? CpuCurve { get; init; }

    /// <summary>GPU fan curve ID.</summary>
    public uint? GpuCurve { get; init; }

    /// <summary>CPU fan speed ID (read-only).</summary>
    public uint? CpuSpeed { get; init; }

    /// <summary>GPU fan speed ID (read-only).</summary>
    public uint? GpuSpeed { get; init; }

    /// <summary>Charge limit ID.</summary>
    public uint? Charge { get; init; }

    /// <summary>Whether the record has a TDP mechanism.</summary>
    public bool HasTdp => Spl is not null && Sppt is not null && Fppt is not null;

    /// <summary>Whether the record has a performance mode mechanism.</summary>
    public bool HasProfile => Mode is not null && ModeValues.Count > 0;

    /// <summary>Whether the record has fan curves.</summary>
    public bool HasFanCurves => CpuCurve is not null && GpuCurve is not null;

    /// <summary>
    ///     Whether the whole power state (mode, three limits, both curves) can be captured and put back.
    ///     The mode selects which curves are read, and switching it changes the limits, so no power,
    ///     mode or fan write is made without all of them.
    /// </summary>
    public bool CanSnapshot => HasTdp && HasProfile && HasFanCurves;

    /// <summary>Every ID the record declares; the transport refuses any other.</summary>
    public IReadOnlyList<uint> AllIds =>
    [
        .. new[] { Mode, Spl, Sppt, Fppt, CpuCurve, GpuCurve, CpuSpeed, GpuSpeed, Charge }
            .Where(id => id is not null)
            .Select(id => id!.Value)
    ];
}

/// <summary>The MSI_ACPI accessors a curated MSI record declares.</summary>
internal sealed record LabMsiLayout
{
    /// <summary>Getter method, <c>Get_Data</c>.</summary>
    public required string Get { get; init; }

    /// <summary>Setter method, <c>Set_Data</c>.</summary>
    public required string Set { get; init; }

    /// <summary>PL1 address, when the record has a TDP mechanism.</summary>
    public byte? Sustained { get; init; }

    /// <summary>PL2 address.</summary>
    public byte? Boost { get; init; }

    /// <summary>Lowest watts the record allows.</summary>
    public int MinimumWatts { get; init; }

    /// <summary>Highest watts the record allows.</summary>
    public int MaximumWatts { get; init; }

    /// <summary>Charge limit address, when the record has one.</summary>
    public byte? Charge { get; init; }

    /// <summary>Lowest charge limit percentage.</summary>
    public int ChargeMinimum { get; init; }

    /// <summary>Highest charge limit percentage.</summary>
    public int ChargeMaximum { get; init; }

    /// <summary>Fan table getter, when the record has a fan mechanism; its channel 0 carries RPM.</summary>
    public string? FanGetter { get; init; }

    /// <summary>Custom fan mode flag address.</summary>
    public byte? FanCustom { get; init; }

    /// <summary>Full-speed fan mode flag address.</summary>
    public byte? FanFullSpeed { get; init; }

    /// <summary>Whether the record has a TDP mechanism.</summary>
    public bool HasTdp => Sustained is not null && Boost is not null;
}

/// <summary>The exact HID endpoint a curated Aura lighting mechanism names.</summary>
/// <param name="VendorId">USB vendor ID.</param>
/// <param name="ProductId">USB product ID.</param>
/// <param name="UsagePage">Top-level usage page.</param>
/// <param name="Usage">Top-level usage.</param>
internal sealed record LabAuraLayout(ushort VendorId, ushort ProductId, ushort UsagePage, ushort Usage);

/// <summary>What the power stage may test on the confirmed device, derived from its knowledge record.</summary>
internal sealed record LabPowerPlan
{
    /// <summary>Percentage points AllyXLab raised each fan duty by for its fan test.</summary>
    public const int FanDutyIncrease = 15;

    /// <summary>The record, or null for an unknown device.</summary>
    public DeviceKnowledgeRecord? Record { get; init; }

    /// <summary>Whether the record is curated; only curated records drive writes.</summary>
    public bool Curated => Record?.Status is DeviceKnowledgeStatus.Curated;

    /// <summary>ASUS ATKACPI mechanisms.</summary>
    public LabAsusLayout? Asus { get; init; }

    /// <summary>MSI WMI mechanisms.</summary>
    public LabMsiLayout? Msi { get; init; }

    /// <summary>ASUS Aura lighting.</summary>
    public LabAuraLayout? Aura { get; init; }

    /// <summary>Embedded controller mechanisms, which this build only records.</summary>
    public IReadOnlyList<DeviceMechanismKnowledge> EmbeddedController { get; init; } = [];

    /// <summary>Power, fan, charge and lighting mechanisms this build has no test for.</summary>
    public IReadOnlyList<DeviceMechanismKnowledge> Untested { get; init; } = [];

    /// <summary>Whether any device write test applies.</summary>
    public bool HasDeviceTests => Asus is not null || Msi is not null;

    /// <summary>The features this stage covers.</summary>
    public static IReadOnlyList<string> Features { get; } = ["tdp", "power-profile", "fan", "charge-limit", "lighting"];

    /// <summary>Builds the plan for a record.</summary>
    /// <param name="record">The confirmed record, or null.</param>
    /// <returns>The plan.</returns>
    public static LabPowerPlan For(DeviceKnowledgeRecord? record)
    {
        if (record is null)
        {
            return new LabPowerPlan();
        }

        var relevant = record.Mechanisms.Where(mechanism => Features.Contains(mechanism.Feature)).ToArray();
        var ec = relevant.Where(mechanism => mechanism.Transport == "superio-ec").ToArray();
        if (record.Status is not DeviceKnowledgeStatus.Curated)
        {
            return new LabPowerPlan
            {
                Record = record,
                EmbeddedController = ec,
                Untested = [.. relevant.Where(mechanism => mechanism.Transport != "superio-ec")]
            };
        }

        var asus = AsusLayout(relevant);
        var msi = MsiLayout(relevant);
        var aura = AuraLayout(relevant);
        List<DeviceMechanismKnowledge> untested = [];
        foreach (var mechanism in relevant.Where(mechanism => mechanism.Transport != "superio-ec"))
        {
            var covered = mechanism switch
            {
                { Transport: "atkacpi" } => asus is not null && AtkAcpiParameters(mechanism),
                { Transport: "wmi-method", Feature: "tdp" } => msi?.HasTdp == true,
                { Transport: "wmi-method", Feature: "charge-limit" } => msi?.Charge is not null,
                { Transport: "hid-output", Feature: "lighting" } => aura is not null && IsAura(mechanism),
                _ => false
            };
            if (!covered)
            {
                untested.Add(mechanism);
            }
        }

        return new LabPowerPlan
        {
            Record = record,
            Asus = asus,
            Msi = msi,
            Aura = aura,
            EmbeddedController = ec,
            Untested = untested
        };
    }

    /// <summary>The low test limit: a quarter of the way up the record's range.</summary>
    /// <param name="minimum">Lowest watts.</param>
    /// <param name="maximum">Highest watts.</param>
    /// <returns>Watts.</returns>
    public static int TestWatts(int minimum, int maximum)
    {
        return minimum + (maximum - minimum) / 4;
    }

    /// <summary>
    ///     Several test limits inside the record's range, low to high, as AllyXLab swept 13, 17 and 25 W.
    ///     Values outside the range are dropped and duplicates removed, so a narrow range still yields at
    ///     least the low limit.
    /// </summary>
    /// <param name="minimum">Lowest watts.</param>
    /// <param name="maximum">Highest watts.</param>
    /// <returns>Distinct watt values in range, ascending.</returns>
    public static IReadOnlyList<int> TestWattsList(int minimum, int maximum)
    {
        var candidates = new[] { 13, 17, 25 }
            .Where(watts => watts >= minimum && watts <= maximum)
            .ToArray();
        return candidates.Length > 0
            ? [.. candidates.Distinct().Order()]
            : [TestWatts(minimum, maximum)];
    }

    /// <summary>The charge limit to test: 80 %, or 90 % when it already is 80 %.</summary>
    /// <param name="original">The current limit.</param>
    /// <returns>Percent.</returns>
    public static int TestChargeLimit(int original)
    {
        return original == 80 ? 90 : 80;
    }

    /// <summary>
    ///     The fan test curve: the original temperatures with every duty raised by
    ///     <see cref="FanDutyIncrease" /> percentage points, capped at 99, as AllyXLab's fan test does. No
    ///     point is ever lowered.
    /// </summary>
    /// <param name="original">An eight-point curve: eight temperatures, then eight duties.</param>
    /// <returns>The test curve, or null when the original is not a valid curve or cannot be raised.</returns>
    public static byte[]? FanTestCurve(byte[] original)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (!LabAtkAcpi.ValidCurve(original))
        {
            return null;
        }

        var curve = (byte[])original.Clone();
        for (var i = 8; i < 16; i++)
        {
            curve[i] = (byte)Math.Min(99, curve[i] + FanDutyIncrease);
        }

        // Never reduce a captured duty, and the raised curve must still be writable.
        return LabAtkAcpi.ValidCurve(curve)
               && Enumerable.Range(8, 8).All(i => curve[i] >= original[i])
            ? curve
            : null;
    }

    /// <summary>Parses a <c>0x</c> hex or decimal parameter.</summary>
    /// <param name="parameters">Mechanism parameters.</param>
    /// <param name="key">Parameter name.</param>
    /// <returns>The value, or null when it is absent or not a number.</returns>
    public static uint? Number(IReadOnlyDictionary<string, string> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var text))
        {
            return null;
        }

        text = text.Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)
                ? hex
                : null
            : uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
    }

    /// <summary>Parses a list such as <c>0 balanced, 1 performance, 2 silent</c>.</summary>
    /// <param name="text">The list.</param>
    /// <returns>Values and names in order.</returns>
    public static IReadOnlyList<(int Value, string Name)> ValueList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        List<(int, string)> values = [];
        foreach (var item in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Split(' ', 2, StringSplitOptions.TrimEntries);
            if (int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                values.Add((value, parts.Length > 1 ? parts[1] : parts[0]));
            }
        }

        return values;
    }

    private static bool AtkAcpiParameters(DeviceMechanismKnowledge mechanism)
    {
        return mechanism.Parameters.TryGetValue("device", out var device)
               && string.Equals(device, LabAtkAcpi.DevicePath, StringComparison.OrdinalIgnoreCase)
               && Number(mechanism.Parameters, "ioctl") == LabAtkAcpi.IoControlCode;
    }

    private static LabAsusLayout? AsusLayout(IReadOnlyList<DeviceMechanismKnowledge> mechanisms)
    {
        var atk = mechanisms.Where(mechanism => mechanism.Transport == "atkacpi" && AtkAcpiParameters(mechanism))
            .ToArray();
        if (atk.Length == 0)
        {
            return null;
        }

        var layout = new LabAsusLayout();
        foreach (var mechanism in atk)
        {
            var p = mechanism.Parameters;
            switch (mechanism.Feature)
            {
                case "tdp":
                    var minimum = (int?)Number(p, "minimumWatts") ?? 0;
                    var maximum = (int?)Number(p, "maximumWatts") ?? 0;

                    // AllyXLab bounded its tests to 5-25 W; a record cannot widen that.
                    if (minimum >= 5 && maximum <= 25 && minimum < maximum)
                    {
                        layout = layout with
                        {
                            Spl = Number(p, "sustainedId"),
                            Sppt = Number(p, "slowId"),
                            Fppt = Number(p, "fastId"),
                            MinimumWatts = minimum,
                            MaximumWatts = maximum
                        };
                    }

                    break;
                case "power-profile":
                    var values = ValueList(p.GetValueOrDefault("values"))
                        .Where(item => item.Value is >= 0 and <= 2)
                        .ToArray();
                    layout = layout with
                    {
                        Mode = Number(p, "id"),
                        ModeValues = [.. values.Select(item => item.Value)],
                        ModeNames = values.ToDictionary(item => item.Value, item => item.Name)
                    };
                    break;
                case "fan":
                    layout = layout with
                    {
                        CpuCurve = Number(p, "cpuCurveId"),
                        GpuCurve = Number(p, "gpuCurveId"),
                        CpuSpeed = Number(p, "cpuSpeedId"),
                        GpuSpeed = Number(p, "gpuSpeedId")
                    };
                    break;
                case "charge-limit":
                    layout = layout with { Charge = Number(p, "id") };
                    break;
            }
        }

        return layout.AllIds.Count == 0 ? null : layout;
    }

    private static LabMsiLayout? MsiLayout(IReadOnlyList<DeviceMechanismKnowledge> mechanisms)
    {
        var wmi = mechanisms.Where(mechanism => mechanism.Transport == "wmi-method"
                                                && mechanism.Parameters.GetValueOrDefault("namespace") ==
                                                LabMsiWmi.Namespace
                                                && mechanism.Parameters.GetValueOrDefault("class") ==
                                                LabMsiWmi.ClassName)
            .ToArray();
        if (wmi.Length == 0)
        {
            return null;
        }

        LabMsiLayout layout = new() { Get = "Get_Data", Set = "Set_Data" };
        foreach (var mechanism in wmi)
        {
            var p = mechanism.Parameters;
            if (mechanism.Feature is "tdp" or "charge-limit"
                && (p.GetValueOrDefault("get") != layout.Get || p.GetValueOrDefault("set") != layout.Set))
            {
                continue;
            }

            switch (mechanism.Feature)
            {
                case "tdp":
                    var minimum = (int?)Number(p, "minimumWatts") ?? 0;
                    var maximum = (int?)Number(p, "maximumWatts") ?? 0;

                    // The Claw plugin accepts 8-37 W; a record cannot widen that.
                    if (minimum >= 8 && maximum <= 37 && minimum < maximum
                        && Number(p, "sustainedAddress") is <= 0xFF and var sustained
                        && Number(p, "boostAddress") is <= 0xFF and var boost)
                    {
                        layout = layout with
                        {
                            Sustained = (byte)sustained,
                            Boost = (byte)boost,
                            MinimumWatts = minimum,
                            MaximumWatts = maximum
                        };
                    }

                    break;
                case "charge-limit":
                    var low = (int?)Number(p, "minimumPercent") ?? 0;
                    var high = (int?)Number(p, "maximumPercent") ?? 0;
                    if (low >= 20 && high <= 100 && low < high && Number(p, "address") is <= 0xFF and var address)
                    {
                        layout = layout with
                        {
                            Charge = (byte)address,
                            ChargeMinimum = low,
                            ChargeMaximum = high
                        };
                    }

                    break;
                case "fan":
                    if (p.GetValueOrDefault("getTable") is { } getter &&
                        getter.StartsWith("Get_", StringComparison.Ordinal))
                    {
                        layout = layout with
                        {
                            FanGetter = getter,
                            FanCustom = Number(p, "customAddress") is <= 255 and var custom ? (byte)custom : null,
                            FanFullSpeed = Number(p, "fullSpeedAddress") is <= 255 and var full ? (byte)full : null
                        };
                    }

                    break;
            }
        }

        return layout;
    }

    private static bool IsAura(DeviceMechanismKnowledge mechanism)
    {
        // The Aura sequence ported from AllyXLab belongs to exactly this endpoint.
        var p = mechanism.Parameters;
        return string.Equals(p.GetValueOrDefault("vendorId"), "0B05", StringComparison.OrdinalIgnoreCase)
               && string.Equals(p.GetValueOrDefault("productId"), "1B4C", StringComparison.OrdinalIgnoreCase)
               && Number(p, "usagePage") == 0xFF31
               && Number(p, "usage") == 0x0080;
    }

    private static LabAuraLayout? AuraLayout(IReadOnlyList<DeviceMechanismKnowledge> mechanisms)
    {
        return mechanisms.Any(mechanism => mechanism is { Feature: "lighting", Transport: "hid-output" }
                                           && IsAura(mechanism))
            ? new LabAuraLayout(0x0B05, 0x1B4C, 0xFF31, 0x0080)
            : null;
    }
}

/// <summary>The firmware and vendor-endpoint facts captured when the power stage starts.</summary>
/// <param name="RecordId">The confirmed record.</param>
/// <param name="BiosVersion">SMBIOS BIOS version.</param>
/// <param name="EcVersion">Embedded controller version as SMBIOS reports it.</param>
/// <param name="Endpoints">The record's vendor USB endpoints present, as <c>VID:PID REV</c>, sorted.</param>
internal sealed record LabPowerFingerprint(
    string RecordId,
    string? BiosVersion,
    string? EcVersion,
    IReadOnlyList<string> Endpoints);

/// <summary>Rechecks, just before any write, that the machine is the device the record describes.</summary>
/// <remarks>
///     <see cref="Begin" /> captures the BIOS, EC and vendor endpoint facts at the start of the stage;
///     <see cref="Matches" /> then requires the exact identity rule to match and those facts to be
///     unchanged, so a firmware update, a swapped controller or a re-enumerated endpoint stops the next write.
/// </remarks>
internal static class LabPowerIdentity
{
    private static readonly Lock Gate = new();
    private static LabPowerFingerprint? _baseline;

    /// <summary>Captures the fingerprint later checks compare against.</summary>
    /// <param name="record">The confirmed record.</param>
    /// <returns>The fingerprint, or null when it could not be read (later checks then fail closed).</returns>
    public static LabPowerFingerprint? Begin(DeviceKnowledgeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var fingerprint = Fingerprint(record)
                          ?? new LabPowerFingerprint(record.Id, "(unreadable)", "(unreadable)", ["(unreadable)"]);
        lock (Gate)
        {
            _baseline = fingerprint;
        }

        return fingerprint;
    }

    /// <summary>
    ///     Whether an exact (not vendor-wide) rule of the record matches the live machine and, when a
    ///     baseline was captured for this record, the BIOS version, EC version and vendor endpoint IDs
    ///     and USB releases are unchanged.
    /// </summary>
    /// <param name="record">The confirmed record.</param>
    /// <returns>True for an exact, unchanged match.</returns>
    public static bool Matches(DeviceKnowledgeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var identity = Read();
        if (identity is null || HardwareMatcher.Match(record.Identity, identity) is not { Fallback: false })
        {
            return false;
        }

        LabPowerFingerprint? baseline;
        lock (Gate)
        {
            baseline = _baseline;
        }

        if (baseline is null || baseline.RecordId != record.Id)
        {
            return true;
        }

        var now = Fingerprint(record);
        return now is not null
               && string.Equals(now.BiosVersion, baseline.BiosVersion, StringComparison.Ordinal)
               && string.Equals(now.EcVersion, baseline.EcVersion, StringComparison.Ordinal)
               && now.Endpoints.SequenceEqual(baseline.Endpoints, StringComparer.OrdinalIgnoreCase);
    }

    private static LabPowerFingerprint? Fingerprint(DeviceKnowledgeRecord record)
    {
        try
        {
            var bios = First(
                "SELECT SMBIOSBIOSVersion, EmbeddedControllerMajorVersion, EmbeddedControllerMinorVersion FROM Win32_BIOS");
            var major = Text(bios, "EmbeddedControllerMajorVersion");
            var minor = Text(bios, "EmbeddedControllerMinorVersion");
            var ec = major is null or "255" ? null : $"{major}.{minor}";
            List<string> endpoints = [];
            foreach (var endpoint in record.HidEndpoints)
            {
                foreach (var product in endpoint.ProductIds)
                {
                    if (!IsHex4(endpoint.VendorId) || !IsHex4(product))
                    {
                        continue;
                    }

                    using ManagementObjectSearcher searcher = new("root\\CIMV2",
                        $"SELECT HardwareID FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\\\VID_{endpoint.VendorId}&PID_{product}%'");
                    using var results = searcher.Get();
                    foreach (var item in results)
                    {
                        using (item)
                        {
                            var release = (item["HardwareID"] as string[])?
                                .Select(Release)
                                .FirstOrDefault(value => value is not null);
                            endpoints.Add($"{endpoint.VendorId}:{product} {release ?? "?"}".ToUpperInvariant());
                        }
                    }
                }
            }

            return new LabPowerFingerprint(record.Id, Text(bios, "SMBIOSBIOSVersion"), ec,
                [.. endpoints.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)]);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException
                                       or COMException)
        {
            return null;
        }
    }

    private static bool IsHex4(string value)
    {
        return value.Length == 4 && value.All(Uri.IsHexDigit);
    }

    private static string? Release(string hardwareId)
    {
        var start = hardwareId.IndexOf("REV_", StringComparison.OrdinalIgnoreCase);
        return start < 0 || hardwareId.Length < start + 8 ? null : hardwareId.Substring(start + 4, 4);
    }

    private static DeviceIdentitySnapshot? Read()
    {
        try
        {
            var system = First("SELECT Manufacturer, Model, SystemSKUNumber FROM Win32_ComputerSystem");
            var board = First("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard");
            var processor = First("SELECT Name FROM Win32_Processor");
            return new DeviceIdentitySnapshot
            {
                SystemManufacturer = Text(system, "Manufacturer"),
                SystemProduct = Text(system, "Model"),
                SystemSku = Text(system, "SystemSKUNumber"),
                BaseboardManufacturer = Text(board, "Manufacturer"),
                BaseboardProduct = Text(board, "Product"),
                BaseboardVersion = Text(board, "Version"),
                ProcessorName = Text(processor, "Name")
            };
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException
                                       or COMException)
        {
            return null;
        }
    }

    private static Dictionary<string, object?> First(string query)
    {
        using ManagementObjectSearcher searcher = new("root\\CIMV2", query);
        using var results = searcher.Get();
        foreach (var item in results)
        {
            using (item)
            {
                return item.Properties.Cast<PropertyData>().ToDictionary(property => property.Name,
                    property => (object?)property.Value, StringComparer.OrdinalIgnoreCase);
            }
        }

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    private static string? Text(Dictionary<string, object?> row, string name)
    {
        var text = Convert.ToString(row.GetValueOrDefault(name), CultureInfo.InvariantCulture)?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
