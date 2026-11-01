using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using LibHandheld.Contracts;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     The extended VRR range's Windows side: active displays and their reported EDID, the
///     <c>EDID_OVERRIDE</c> key through WindowsDeviceControl, and the standalone Atlas8Tweaks VRR Fix.
/// </summary>
/// <remarks>
///     The override lives under <c>HKLM\SYSTEM\CurrentControlSet\Enum</c>, which the elevated resident WSGM and the
///     uninstaller's elevated one-shot can write; a refused write surfaces as the row's failure. The standalone tool
///     is found by its own <c>%ProgramData%\AcerAtlasVrr\config.json</c> and removed, task and folder, only when the
///     user turns the range off in WSGM.
/// </remarks>
internal sealed class WindowsExtendedVrrMachine : IExtendedVrrMachine
{
    /// <summary>The standalone tool's sign-in task, as <c>AcerAtlasVrr.ps1</c> registers it.</summary>
    internal const string StandaloneTask = "Acer Atlas 8 VRR Repair";

    private static readonly string StandaloneDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AcerAtlasVrr");

    /// <inheritdoc />
    public IReadOnlyList<ExtendedVrrDisplay> FindPanels(ExtendedVrrPanel panel)
    {
        List<ExtendedVrrDisplay> found = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (var path in DisplayTopology.CaptureActive().Paths)
        {
            var target = path.Target;
            if (seen.Add(target.DevicePath) && DisplayEdid.ReadReported(target.DevicePath) is { } reported
                                            && panel.Identifies(reported))
            {
                found.Add(new ExtendedVrrDisplay(target, reported));
            }
        }

        return found;
    }

    /// <inheritdoc />
    public DisplayEdidOverride ReadOverride(DisplayTargetIdentity target)
    {
        return DisplayEdid.ReadOverride(target.DevicePath);
    }

    /// <inheritdoc />
    public void WriteOverride(DisplayTargetIdentity target, byte[] edid)
    {
        DisplayEdid.WriteOverride(target.DevicePath, edid);
    }

    /// <inheritdoc />
    public bool RemoveOverride(DisplayTargetIdentity target)
    {
        return DisplayEdid.RemoveOverride(target.DevicePath);
    }

    /// <inheritdoc />
    public ExtendedVrrStandalone? ReadStandalone()
    {
        var config = Path.Combine(StandaloneDirectory, "config.json");
        if (!File.Exists(config))
        {
            return null;
        }

        return new ExtendedVrrStandalone(ReadFloor(config),
            ReadOriginal(Path.Combine(StandaloneDirectory, "original-state.json")));
    }

    /// <inheritdoc />
    public string? RemoveStandalone()
    {
        List<string> problems = [];
        var task = ConsoleTool.RunAsync(ConsoleTool.System32("schtasks.exe"), $"/Delete /TN \"{StandaloneTask}\" /F")
            .GetAwaiter().GetResult();
        if (task.Outcome != ConsoleToolRunOutcome.Succeeded && TaskExists())
        {
            problems.Add($"Its sign-in task '{StandaloneTask}' could not be removed.");
        }

        try
        {
            if (Directory.Exists(StandaloneDirectory))
            {
                Directory.Delete(StandaloneDirectory, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"Its folder {StandaloneDirectory} could not be removed: {ex.Message}");
        }

        return problems.Count == 0 ? null : string.Join(" ", problems);
    }

    private static bool TaskExists()
    {
        return ConsoleTool.RunAsync(ConsoleTool.System32("schtasks.exe"), $"/Query /TN \"{StandaloneTask}\"")
            .GetAwaiter().GetResult().Outcome == ConsoleToolRunOutcome.Succeeded;
    }

    private static int? ReadFloor(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("MinimumHz", out var floor) && floor.TryGetInt32(out var hz)
                ? hz
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads the script's <c>original-state.json</c>; its field names are <see cref="ExtendedVrrIntelState" />'s.</summary>
    internal static ExtendedVrrIntelState? ReadOriginal(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return new ExtendedVrrIntelState
            {
                Source = root.GetProperty("Source").GetString() ?? "",
                ProfileId = root.GetProperty("ProfileId").GetInt32(),
                MinimumHz = root.GetProperty("MinimumHz").GetDouble(),
                MaximumHz = root.GetProperty("MaximumHz").GetDouble(),
                MaxIncreaseUs = root.GetProperty("MaxIncreaseUs").GetUInt32(),
                MaxDecreaseUs = root.GetProperty("MaxDecreaseUs").GetUInt32(),
                LowFpsSolutionEnabled = root.GetProperty("LowFpsSolutionEnabled").GetBoolean(),
                HighFpsSolutionEnabled = root.GetProperty("HighFpsSolutionEnabled").GetBoolean()
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            Log.Warn($"Extended VRR range: the standalone tool's saved Intel state is unreadable: {ex.Message}");
            return null;
        }
    }
}
