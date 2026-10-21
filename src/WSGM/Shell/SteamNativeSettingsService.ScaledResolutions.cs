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
    private string _scaledWidth = "1760";
    private string _scaledHeight = "1100";
    private string _scaledStatus = "";
    private GpuScaledResolutionCatalog? _scaledPreviewCatalog;
    private GpuScaledResolution? _scaledPreviewNative;
    private IReadOnlyList<GpuScaledResolution> _scaledPreview = [];

    private async Task AddScaledResolutionSectionAsync(List<SteamSettingsSection> display,
        Dictionary<string, Offered> offered)
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
            rows.Add(new SteamSettingsRow("scaled.error", SteamSettingsRowKind.Note, "Source-mode catalog",
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
        Add(new SteamSettingsRow("scaled.display", SteamSettingsRowKind.Choice, "Display",
            "Choose the display whose driving GPU owns the source-mode list.", Text: _scaledDisplay ?? "",
            Choices: state.Displays.Select(item => new SteamSettingsChoice(item.Path, item.Name)).ToArray()),
            (value, _) =>
            {
                _scaledDisplay = value.GetString();
                _scaledPreview = [];
                _scaledPreviewCatalog = null;
                _scaledStatus = "";
                return Task.FromResult(SteamUiCommandResult.Applied);
            });
        Add(new SteamSettingsRow("scaled.refresh", SteamSettingsRowKind.Action, "Refresh displays and modes",
                ButtonLabel: "Refresh"), async (_, token) =>
            {
                await scaledResolutions.RefreshTopologyAsync(token).ConfigureAwait(false);
                return SteamUiCommandResult.Applied;
            });
        rows.Add(new SteamSettingsRow("scaled.support", SteamSettingsRowKind.Note, "Source-mode catalog",
            Text: state.Detail + " This catalog is global, independent of game profiles."));
        if (state.Catalog is { } catalog)
        {
            rows.Add(new SteamSettingsRow("scaled.adapter", SteamSettingsRowKind.Note, "Driving GPU",
                Text: catalog.AdapterName + " · " + catalog.AdapterIdentity
                    + (catalog.OutputSpecific ? " · This output" : " · All outputs on this adapter")));
            rows.Add(new SteamSettingsRow("scaled.existing", SteamSettingsRowKind.Note, "Existing source sizes",
                Text: string.Join(", ", catalog.Modes.Select(mode => $"{mode.Width} × {mode.Height}"))));
            if (catalog.Supported)
            {
                Add(new SteamSettingsRow("scaled.width", SteamSettingsRowKind.Text, "Width in pixels",
                    Text: _scaledWidth, MaximumLength: 5), (value, _) =>
                {
                    _scaledWidth = value.GetString() ?? "";
                    return Task.FromResult(SteamUiCommandResult.Applied);
                });
                Add(new SteamSettingsRow("scaled.height", SteamSettingsRowKind.Text, "Height in pixels",
                    Text: _scaledHeight, MaximumLength: 5), (value, _) =>
                {
                    _scaledHeight = value.GetString() ?? "";
                    return Task.FromResult(SteamUiCommandResult.Applied);
                });
                Action("scaled.add", "Add resolution", "Makes this size available without switching the active mode.",
                    async token =>
                    {
                        if (!int.TryParse(_scaledWidth, NumberStyles.None, CultureInfo.InvariantCulture, out var width)
                            || !int.TryParse(_scaledHeight, NumberStyles.None, CultureInfo.InvariantCulture, out var height))
                        {
                            return new GpuScaledResolutionResult(false, false, GpuScaledResolutionActivation.Immediate,
                                "Enter pixel width and height as whole numbers.");
                        }

                        return await scaledResolutions.AddAsync(catalog, [new GpuScaledResolution(width, height)], token)
                            .ConfigureAwait(false);
                    });
                if (state.Native is { } native)
                {
                    Add(new SteamSettingsRow("scaled.preview", SteamSettingsRowKind.Action,
                        "Preview percentage steps", $"Native {native.Width} × {native.Height}. 100% down to 50% in 5% steps of width and height.",
                        ButtonLabel: "Preview"), (_, _) =>
                    {
                        _scaledPreview = ScaledResolutionService.PercentagePreview(native);
                        _scaledPreviewCatalog = catalog;
                        _scaledPreviewNative = native;
                        return Task.FromResult(SteamUiCommandResult.Applied);
                    });
                    if (_scaledPreviewCatalog is not null && ScaledResolutionService.Same(_scaledPreviewCatalog, catalog)
                        && _scaledPreviewNative == native && _scaledPreview.Count > 0)
                    {
                        rows.Add(new SteamSettingsRow("scaled.preview-list", SteamSettingsRowKind.Note, "Percentage preview",
                            Text: string.Join("; ", _scaledPreview.Select((mode, index) =>
                                $"{100 - index * 5}%: {mode.Width} × {mode.Height}"))));
                        var preview = _scaledPreview.ToArray();
                        Action("scaled.apply-preview", "Add previewed resolutions", "Existing entries are preserved. The active mode stays as it is.",
                            token => scaledResolutions.AddAsync(catalog, preview, token));
                    }
                }
                else
                {
                    rows.Add(new SteamSettingsRow("scaled.native-unavailable", SteamSettingsRowKind.Note, "Percentage generation",
                        Text: "The display did not report its native size. Enter a source size manually."));
                }

                foreach (var addition in state.Remembered)
                {
                    Action($"scaled.remove/{addition.Width}/{addition.Height}", $"Remove {addition.Width} × {addition.Height}",
                        "Removes this WSGM addition from the driver list and remembered additions.",
                        token => scaledResolutions.RemoveAsync(catalog, addition, token));
                }

                if (state.Missing.Count > 0)
                {
                    Action("scaled.reapply", "Reapply missing additions",
                        $"{state.Missing.Count} remembered source sizes are absent, for example after a driver update.",
                        token => scaledResolutions.ReapplyAsync(catalog, token));
                }

                if (!catalog.OutputSpecific)
                {
                    Action("scaled.restart", "Restart this graphics driver now",
                        "Explicit action: every display on this GPU can go blank and games can lose their graphics device. Restart only when ready.",
                        token => scaledResolutions.RestartAsync(catalog, token));
                }
            }
        }

        if (_scaledStatus.Length > 0)
        {
            rows.Add(new SteamSettingsRow("scaled.status", SteamSettingsRowKind.Note, "Last source-mode operation",
                Text: _scaledStatus));
        }

        Section(display, "GPU-scaled resolutions", "scaled-resolutions", rows);

        void Add(SteamSettingsRow row, Func<JsonElement, CancellationToken, Task<SteamUiCommandResult>> write)
        {
            rows.Add(row);
            offered.Add(row.Key, new Offered(row, write, null));
        }

        void Action(string key, string label, string description,
            Func<CancellationToken, Task<GpuScaledResolutionResult>> write)
        {
            Add(new SteamSettingsRow(key + "/target/" + targetKey, SteamSettingsRowKind.Action, label, description, ButtonLabel: label),
                async (_, token) =>
                {
                    try
                    {
                        var result = await write(token).ConfigureAwait(false);
                        _scaledStatus = result.Detail;
                        return new SteamUiCommandResult(result.Accepted, result.Detail);
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        _scaledStatus = error.Message + " Refresh before another explicit action.";
                        return new SteamUiCommandResult(false, _scaledStatus);
                    }
                });
        }
    }
}
