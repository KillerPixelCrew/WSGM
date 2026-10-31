using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LibGPUDriverInteract;
using SteamUiToolkit;

namespace WSGM.Shell;

internal sealed partial class SteamNativeSettingsService
{
    private string? _scaledDisplay;
    private string _scaledStatus = "";
    private bool _scaledDriverOptions;

    private async Task AddScaledResolutionSectionAsync(List<SteamSettingsSection> display, OfferedRows offer)
    {
        if (scaledResolutions is null)
        {
            return;
        }

        List<SteamSettingsRow> rows = [];
        ScaledResolutionState state;
        try
        {
            state = await scaledResolutions.ReadAsync(_scaledDisplay).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            rows.Add(new SteamSettingsRow("scaled.error", SteamSettingsRowKind.Note, "Resolutions unavailable",
                Text: error.Message));
            Section(display, "GPU-scaled resolutions", "scaled-resolutions", rows);
            return;
        }

        _scaledDisplay ??= state.SelectedPath;
        var targetKey = state.Catalog is { } selectedCatalog
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0',
                selectedCatalog.AdapterIdentity, selectedCatalog.Display.DevicePath,
                selectedCatalog.Display.AdapterLowPart?.ToString(CultureInfo.InvariantCulture),
                selectedCatalog.Display.AdapterHighPart?.ToString(CultureInfo.InvariantCulture),
                selectedCatalog.Display.TargetId?.ToString(CultureInfo.InvariantCulture))))) : "unavailable";
        offer.Add(rows, new SteamSettingsRow("scaled.display", SteamSettingsRowKind.Choice, "Display",
            state.Catalog is { } gpu ? gpu.AdapterName : null, Text: _scaledDisplay ?? "",
            Choices: state.Displays.Select(item => new SteamSettingsChoice(item.Path, item.Name)).ToArray()),
            (value, _) =>
            {
                _scaledDisplay = value.GetString();
                _scaledStatus = "";
                return Task.FromResult(SteamUiCommandResult.Applied);
            });
        if (state.Catalog is { Supported: true } catalog)
        {
            if (state.RequiresRestart)
            {
                rows.Add(new SteamSettingsRow("scaled.activation", SteamSettingsRowKind.Note, "Restart required",
                    Text: state.ActivationNotice));
            }

            var choices = state.Resolutions.Select(mode => new SteamSettingsChoice(
                $"{mode.Width}x{mode.Height}", state.ResolutionLabel(mode))).ToArray();
            offer.Add(rows, new SteamSettingsRow("scaled.editor/target/" + targetKey,
                SteamSettingsRowKind.ResolutionList, "Resolutions",
                "Your active resolution stays the same. The native resolution is protected.", Choices: choices,
                ResolutionEditor: new SteamSettingsResolutionEditor(
                    state.Resolutions.Where(mode => mode != state.Native).Select(mode => $"{mode.Width}x{mode.Height}").ToArray(),
                    state.Native?.Width, state.Native?.Height, state.Original is not null)), async (value, token) =>
            {
                var operation = value.GetProperty("operation").GetString();
                if (operation == "refresh")
                {
                    await scaledResolutions.RefreshTopologyAsync(token).ConfigureAwait(false);
                    _scaledStatus = "";
                    return SteamUiCommandResult.Applied;
                }

                if (operation == "restore")
                {
                    return await Apply(() => scaledResolutions.RestoreOriginalAsync(catalog, token)).ConfigureAwait(false);
                }

                var mode = new GpuScaledResolution(value.GetProperty("width").GetInt32(), value.GetProperty("height").GetInt32());
                return await Apply(operation switch
                {
                    "add" => () => scaledResolutions.AddAsync(catalog, [mode], token),
                    "remove" => () => scaledResolutions.RemoveAsync(catalog, mode, token),
                    "steps" => () => scaledResolutions.AddAsync(catalog, ScaledResolutionService.PercentagePreview(mode), token),
                    _ => throw new InvalidOperationException("Unknown resolution edit.")
                }).ConfigureAwait(false);
            });

            if (state.Missing.Count > 0 || !catalog.OutputSpecific)
            {
                offer.Add(rows, new SteamSettingsRow("scaled.driver-options", SteamSettingsRowKind.Action,
                    "Driver options",
                    catalog.OutputSpecific ? null : "Restart the driver to activate changes.",
                    ButtonLabel: _scaledDriverOptions ? "Hide" : "Show"), (_, _) =>
                    {
                        _scaledDriverOptions = !_scaledDriverOptions;
                        return Task.FromResult(SteamUiCommandResult.Applied);
                    });
                if (_scaledDriverOptions)
                {
                    if (state.Missing.Count > 0)
                    {
                        Action("scaled.reapply", "Restore missing resolutions",
                            $"{state.Missing.Count} resolutions are missing, for example after a driver update.",
                            token => scaledResolutions.ReapplyAsync(catalog, token), "Restore");
                    }

                    if (!catalog.OutputSpecific)
                    {
                        Action("scaled.restart", "Restart graphics driver",
                            "Activates changes. Displays may go blank and games may be interrupted.",
                            token => scaledResolutions.RestartAsync(catalog, token), "Restart");
                    }
                }
            }
        }
        else
        {
            rows.Add(new SteamSettingsRow("scaled.support", SteamSettingsRowKind.Note, "Resolutions unavailable",
                Text: state.Detail));
            offer.Add(rows, new SteamSettingsRow("scaled.refresh", SteamSettingsRowKind.Action,
                "Refresh displays and resolutions",
                ButtonLabel: "Refresh"), async (_, token) =>
            {
                await scaledResolutions.RefreshTopologyAsync(token).ConfigureAwait(false);
                return SteamUiCommandResult.Applied;
            });
        }

        if (_scaledStatus.Length > 0)
        {
            rows.Add(new SteamSettingsRow("scaled.status", SteamSettingsRowKind.Note, "Status",
                Text: _scaledStatus));
        }

        Section(display, "GPU-scaled resolutions", "scaled-resolutions", rows);

        void Action(string key, string label, string? description,
            Func<CancellationToken, Task<GpuScaledResolutionResult>> write, string buttonLabel, bool disabled = false)
        {
            offer.Add(rows, new SteamSettingsRow(key + "/target/" + targetKey, SteamSettingsRowKind.Action, label,
                    description,
                    Disabled: disabled, ButtonLabel: buttonLabel),
                (_, token) => Apply(() => write(token)));
        }

        async Task<SteamUiCommandResult> Apply(Func<Task<GpuScaledResolutionResult>> write)
        {
            try
            {
                var result = await write().ConfigureAwait(false);
                _scaledStatus = result.Detail;
                return new SteamUiCommandResult(result.Accepted, result.Detail);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                _scaledStatus = error.Message + " Refresh before another explicit action.";
                return new SteamUiCommandResult(false, _scaledStatus);
            }
        }
    }

    internal static bool ValidResolutionEdit(SteamSettingsRow row, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || row.ResolutionEditor is not { } editor
            || !value.TryGetProperty("operation", out var operation) || operation.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        if (operation.GetString() == "refresh")
        {
            return true;
        }

        if (operation.GetString() == "restore")
        {
            return editor.RestoreAvailable;
        }

        if (!value.TryGetProperty("width", out var widthValue) || widthValue.ValueKind != JsonValueKind.Number
            || !widthValue.TryGetInt32(out var width)
            || !value.TryGetProperty("height", out var heightValue) || heightValue.ValueKind != JsonValueKind.Number
            || !heightValue.TryGetInt32(out var height)
            || !new GpuScaledResolution(width, height).Valid)
        {
            return false;
        }

        return operation.GetString() switch
        {
            "add" => true,
            "remove" => editor.Removable.Contains($"{width}x{height}"),
            "steps" => width == editor.NativeWidth && height == editor.NativeHeight,
            _ => false
        };
    }
}
