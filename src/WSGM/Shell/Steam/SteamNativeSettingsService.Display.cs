using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

internal sealed partial class SteamNativeSettingsService
{
    /// <summary>Builds the Display page: frame pacing, idle behavior, graphics and scaled resolutions.</summary>
    /// <param name="display">The page's sections, appended in order.</param>
    /// <param name="offer">The read's offered command set.</param>
    /// <param name="lightRead">The panel-brightness read, started with the other backends.</param>
    private async Task AddDisplayPageAsync(List<SteamSettingsSection> display, OfferedRows offer,
        ValueTask<SteamBrightnessState?> lightRead)
    {
        List<SteamSettingsRow> displayRows = [];
        if (await lightRead.ConfigureAwait(false) is { } light)
        {
            offer.Add(displayRows, new SteamSettingsRow("display.brightness", SteamSettingsRowKind.Range, "Brightness",
                    "Current panel backlight.", Number: light.Percent, Minimum: 0, Maximum: 100, Step: 1,
                    Suffix: "%"),
                (value, token) => brightness.SetBrightnessAsync(Integer(value), token));
        }

        if (resolution?.Current is { Available: true } modes)
        {
            offer.Add(displayRows, new SteamSettingsRow("display.resolution", SteamSettingsRowKind.Choice, "Resolution",
                    modes.StatusText, Text: modes.Current,
                    Choices: [.. modes.Options.Select(mode => new SteamSettingsChoice(mode, mode))]),
                (value, token) => resolution.SetResolutionAsync(value.GetString()!, token));
        }

        var frame = performance.FrameLimit;
        var support = performance.PerfSupport?.Invoke();
        if (frame.RefreshRates is { Count: > 1 } rates && performance.ApplyRefreshRate is not null)
        {
            var selectable = !frame.LimitEnabled || support?.RefreshRatesSelectable == true;
            offer.Add(displayRows, new SteamSettingsRow("display.refresh", SteamSettingsRowKind.Choice, "Refresh rate",
                    selectable
                        ? "Current display mode."
                        : "The frame limit currently owns the paired refresh rate.",
                    Text: frame.CurrentRefreshHz?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    Choices:
                    [
                        .. rates.Select(rate => new SteamSettingsChoice(rate.ToString(CultureInfo.InvariantCulture),
                            $"{rate} Hz"))
                    ], Disabled: !selectable),
                (value, token) => performance.SetRefreshRateAsync(
                    int.Parse(value.GetString()!, CultureInfo.InvariantCulture),
                    token), true);
        }

        if (frame.Available && frame.MinimumFps is { } minFps && frame.MaximumFps is { } maxFps)
        {
            offer.Add(displayRows, new SteamSettingsRow("display.limit-enabled", SteamSettingsRowKind.Boolean,
                    "Frame limit", offer.Scope, frame.LimitEnabled, Accent: frame.Accent),
                (value, token) => performance.ApplyPerfChangeAsync(new SteamPerformanceChange(
                        SteamPerformanceSetting.FrameLimitEnabled, value.GetBoolean() ? 1 : 0), "native-settings",
                    token),
                true);
            offer.Add(displayRows, new SteamSettingsRow("display.frame-limit", SteamSettingsRowKind.Range,
                    "Maximum frame rate", frame.StatusText,
                    Number: frame.DesiredFps is > 0 ? frame.DesiredFps : maxFps,
                    Minimum: minFps, Maximum: maxFps, Step: 1, Suffix: " FPS", Disabled: !frame.LimitEnabled,
                    Accent: frame.Accent),
                (value, token) => performance.SetFrameLimitAsync(Integer(value), "native-settings", token),
                true);
        }

        var vrr = performance.Vrr;
        if (vrr.Available)
        {
            offer.Add(displayRows, new SteamSettingsRow("display.vrr", SteamSettingsRowKind.Boolean,
                    "Variable refresh rate", vrr.StatusText, vrr.Enabled, Accent: vrr.Accent),
                (value, token) => performance.SetVariableRefreshRateAsync(value.GetBoolean(), token),
                true);
        }

        Section(display, "Display and frame pacing", "display", displayRows);
        if (displayTimeouts is not null)
        {
            List<SteamSettingsRow> timeouts = [];
            foreach (var timeout in displayTimeouts.ReadState().Rows)
            {
                var id = timeout.Id;
                offer.Add(timeouts, new SteamSettingsRow("display.timeout-" + id, SteamSettingsRowKind.Choice,
                        timeout.Label, timeout.Description,
                        Text: timeout.Seconds.ToString(CultureInfo.InvariantCulture),
                        Choices:
                        [
                            .. timeout.Options.Select(option => new SteamSettingsChoice(
                                option.Seconds.ToString(CultureInfo.InvariantCulture), option.Label))
                        ],
                        Disabled: !timeout.Available),
                    (value, token) => displayTimeouts.SetTimeoutAsync(id,
                        int.Parse(value.GetString()!, CultureInfo.InvariantCulture), token));
            }

            Section(display, "Display idle behavior", "display-timeouts", timeouts);
        }

        foreach (var page in graphics?.ReadState().Pages ?? [])
        {
            foreach (var section in page.Sections)
            {
                List<SteamSettingsRow> rows = [];
                foreach (var row in section.Rows)
                {
                    var key = row.Key;
                    offer.Add(rows, row with { Key = "gpu/" + key },
                        (value, token) => graphics!.SetAsync(key, value, token), true);
                }

                Section(display, section.Title is null ? page.Title : page.Title + ": " + section.Title,
                    "gpu/" + page.Id + "/" + section.Id, rows);
            }
        }

        await AddScaledResolutionSectionAsync(display, offer).ConfigureAwait(false);
    }
}
