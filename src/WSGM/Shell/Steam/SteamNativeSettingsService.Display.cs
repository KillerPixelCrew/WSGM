using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

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

        if (resolution is not null
            && ChoiceRow("display.resolution", "Display resolution", "aspect",
                ProjectResolution(resolution.Options(), PrimaryDisplayModes.ReadCurrentResolution())) is
            {
                Disabled: false
            } modes)
        {
            offer.Quick("resolution", offer.Add(displayRows, modes,
                (value, token) => SetResolutionAsync(resolution, value.GetString()!, token)));
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
            offer.Quick("vrr", offer.Add(displayRows, new SteamSettingsRow("display.vrr", SteamSettingsRowKind.Boolean,
                    "Variable refresh rate", Marked(vrr.StatusText, vrr.Accent), vrr.Enabled, Accent: vrr.Accent,
                    Icon: "pulse", LabelToken: "#QuickAccess_Tab_Perf_EnableVRR",
                    Summary: vrr.Enabled ? "VRR on" : "VRR off"),
                (value, token) => performance.SetVariableRefreshRateAsync(value.GetBoolean(), token),
                true));
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

        AddGraphicsQuickAccess(offer);
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
    /// <summary>The graphics owners' Quick Access groups: one titled section per publisher.</summary>
    /// <param name="offer">The read's offered command set and Quick Access sections.</param>
    /// <remarks>
    ///     No layout section names them, so each draws as its own folding section under its publisher's title and
    ///     glyph. Their keys are the Settings page's, so a row both surfaces draw is one offered command.
    /// </remarks>
    private void AddGraphicsQuickAccess(OfferedRows offer)
    {
        if (graphics is null)
        {
            return;
        }

        foreach (var page in graphics.ReadQuickAccessState().Pages)
        {
            List<SteamSettingsSection> groups = [];
            foreach (var section in page.Sections)
            {
                List<SteamSettingsRow> rows = [];
                foreach (var row in section.Rows)
                {
                    var key = row.Key;
                    offer.Add(rows, row with { Key = "gpu/" + key },
                        (value, token) => graphics.SetAsync(key, value, token), true);
                }

                groups.Add(section with { Rows = rows });
            }

            offer.QuickSection(new SteamQuickAccessRowSection("settings." + page.Id, [], page.Title, page.Glyph,
                groups));
        }
    }

    /// <summary>Builds the resolution choice from discovered options and the current mode.</summary>
    /// <param name="options">Resolutions the driver accepted.</param>
    /// <param name="current">The resolution in force, or null when unreadable.</param>
    /// <returns>The choice the row renders.</returns>
    /// <remarks>
    ///     A single option is still no choice, so the row hides: offering a picker that cannot change anything
    ///     reads as a broken control rather than an absent feature.
    /// </remarks>
    internal static ChoiceRowState ProjectResolution(IReadOnlyList<DisplayResolution> options,
        DisplayResolution? current)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Count < 2)
        {
            return new ChoiceRowState(false, [], current?.ToString() ?? string.Empty,
                options.Count == 0
                    ? "No display modes could be validated."
                    : "This display accepts only one resolution.");
        }

        return new ChoiceRowState(true,
            [.. options.Select(option => new ChoiceRowOption(option.ToString(), option.ToString()))],
            current?.ToString() ?? string.Empty, string.Empty);
    }

    /// <summary>Applies one offered resolution off the calling thread, because a mode change blocks.</summary>
    /// <param name="display">The owner of discovery, apply and restore.</param>
    /// <param name="option">The resolution as <c>WIDTHxHEIGHT</c>.</param>
    /// <param name="cancellationToken">Cancels before the mode change starts.</param>
    /// <returns>Applied, or why not.</returns>
    /// <remarks>Parsed rather than trusted, so an unparseable string never reaches the driver.</remarks>
    internal static async Task<SteamUiCommandResult> SetResolutionAsync(DisplayResolutionService display,
        string option, CancellationToken cancellationToken)
    {
        var parts = option.Split('x', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height)
            || width <= 0
            || height <= 0)
        {
            Log.Warn($"Native QAM resolution refused: '{option}' is not a resolution.");
            return new SteamUiCommandResult(false, "The resolution value is invalid.");
        }

        var resolution = new DisplayResolution(width, height);
        var applied = await Task.Run(() => display.Apply(resolution), cancellationToken).ConfigureAwait(false);
        return applied
            ? SteamUiCommandResult.Applied
            : new SteamUiCommandResult(false, $"The display refused {resolution}.");
    }
}
