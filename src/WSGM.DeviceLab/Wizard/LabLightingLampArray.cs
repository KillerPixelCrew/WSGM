using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.Lights;
using Windows.UI;

namespace WSGM.DeviceLab.Wizard;

/// <summary>What one Windows Dynamic Lighting lamp array reports about itself.</summary>
internal sealed record LabLampArrayInfo
{
    /// <summary>Device name.</summary>
    public required string Name { get; init; }

    /// <summary>Lamp array kind, for example <c>GameController</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Number of lamps.</summary>
    public int LampCount { get; init; }

    /// <summary>Whether Windows lets apps control it.</summary>
    public bool IsEnabled { get; init; }

    /// <summary>Whether it is connected.</summary>
    public bool IsConnected { get; init; }

    /// <summary>USB vendor ID.</summary>
    public string? VendorId { get; init; }

    /// <summary>USB product ID.</summary>
    public string? ProductId { get; init; }

    /// <summary>Hardware version.</summary>
    public int HardwareVersion { get; init; }

    /// <summary>Whether lamps map to keyboard keys.</summary>
    public bool SupportsVirtualKeys { get; init; }

    /// <summary>Brightness level Windows applies, 0 to 1.</summary>
    public double BrightnessLevel { get; init; }

    /// <summary>Minimum time between updates.</summary>
    public double MinUpdateIntervalMilliseconds { get; init; }

    /// <summary>
    ///     Distinct colour capabilities across lamps, as <c>red x green x blue levels, gain levels</c>;
    ///     a lamp with a fixed colour shows it instead.
    /// </summary>
    public IReadOnlyList<string> ColourLevels { get; init; } = [];

    /// <summary>Distinct lamp purposes, for example <c>Control, Accent</c>.</summary>
    public IReadOnlyList<string> Purposes { get; init; } = [];
}

/// <summary>
///     The Windows Dynamic Lighting lamp arrays on this machine. Windows takes control back when the app
///     releases them, so nothing needs writing back; disposing releases them.
/// </summary>
internal sealed class LabLampArrays : IDisposable
{
    private readonly List<LampArray> _arrays;
    private readonly List<string> _names;

    private LabLampArrays(List<LampArray> arrays, List<string> names, IReadOnlyList<string> problems)
    {
        _arrays = arrays;
        _names = names;
        Problems = problems;
    }

    /// <summary>Lamp arrays found.</summary>
    public int Count => _arrays.Count;

    /// <summary>Devices that were listed but could not be opened.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        // LampArray has no Close; dropping the last reference releases the device back to Windows.
        _arrays.Clear();
    }

    /// <summary>Lists and opens every lamp array.</summary>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The arrays.</returns>
    public static async Task<LabLampArrays> OpenAsync(CancellationToken cancellationToken)
    {
        var devices = await DeviceInformation.FindAllAsync(LampArray.GetDeviceSelector()).AsTask(cancellationToken);
        List<LampArray> arrays = [];
        List<string> names = [];
        List<string> problems = [];
        foreach (var device in devices.Take(8))
        {
            try
            {
                var array = await LampArray.FromIdAsync(device.Id).AsTask(cancellationToken);
                if (array is null)
                {
                    problems.Add($"{device.Name}: could not be opened");
                    continue;
                }

                arrays.Add(array);
                names.Add(device.Name);
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException
                                           or InvalidOperationException)
            {
                problems.Add($"{device.Name}: {ex.Message}");
            }
        }

        return new LabLampArrays(arrays, names, problems);
    }

    /// <summary>Reads what one array reports now.</summary>
    /// <param name="index">Array index.</param>
    /// <returns>The description.</returns>
    public LabLampArrayInfo Describe(int index)
    {
        var array = _arrays[index];
        HashSet<string> levels = [];
        HashSet<string> purposes = [];
        for (var lamp = 0; lamp < Math.Min(array.LampCount, 512); lamp++)
        {
            var info = array.GetLampInfo(lamp);
            levels.Add(info.FixedColor is { } fixedColour
                ? $"fixed #{fixedColour.R:X2}{fixedColour.G:X2}{fixedColour.B:X2}"
                : $"{info.RedLevelCount} x {info.GreenLevelCount} x {info.BlueLevelCount} levels, {info.GainLevelCount} gain levels");
            purposes.Add(info.Purposes.ToString());
        }

        return new LabLampArrayInfo
        {
            Name = _names[index],
            Kind = array.LampArrayKind.ToString(),
            LampCount = array.LampCount,
            IsEnabled = array.IsEnabled,
            IsConnected = array.IsConnected,
            VendorId = $"{array.HardwareVendorId:X4}",
            ProductId = $"{array.HardwareProductId:X4}",
            HardwareVersion = array.HardwareVersion,
            SupportsVirtualKeys = array.SupportsVirtualKeys,
            BrightnessLevel = array.BrightnessLevel,
            MinUpdateIntervalMilliseconds = array.MinUpdateInterval.TotalMilliseconds,
            ColourLevels = [.. levels.Order()],
            Purposes = [.. purposes.Order()]
        };
    }

    /// <summary>Sets every lamp of one array to a colour.</summary>
    /// <param name="index">Array index.</param>
    /// <param name="red">Red.</param>
    /// <param name="green">Green.</param>
    /// <param name="blue">Blue.</param>
    public void SetAll(int index, byte red, byte green, byte blue)
    {
        _arrays[index].SetColor(new Color { A = 255, R = red, G = green, B = blue });
    }
}
