using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Draws one projected capability row with Steam's settings controls and reads its edits back.</summary>
/// <remarks>
///     Quick Access, Steam's settings pages and the graphics pages draw the same <see cref="DeviceOverlayCapability" />
///     through this one builder and parse what the renderer sends back through this one reader, so a row's
///     kind, value, disabled state and accepted input agree on every Steam surface. Callers supply the key.
/// </remarks>
internal static class SteamCapabilityRows
{
    /// <summary>One capability row as Steam draws it.</summary>
    /// <param name="capability">The projected row.</param>
    /// <param name="key">The caller's command key for the row.</param>
    /// <returns>The Steam row; a row the running game overrides is drawn with Steam's accent.</returns>
    /// <remarks>
    ///     A value the running game overrides is marked as Steam's Quick Access rows mark one: its description
    ///     leads with "Game override" and is drawn in Steam's accent blue. There is no Use global row: Steam's
    ///     surfaces return to Global through Steam's Reset button, and the overlay keeps its own.
    /// </remarks>
    internal static SteamSettingsRow Row(DeviceOverlayCapability capability, string key)
    {
        ArgumentNullException.ThrowIfNull(capability);
        var description = capability.Description is { Length: > 0 } text ? text : null;
        var disabled = !capability.CanInvoke;
        var current = capability.CurrentValue;
        var row = capability switch
        {
            { Writable: true, ValueKind: CapabilityValueKind.Boolean } => new SteamSettingsRow(key,
                SteamSettingsRowKind.Boolean, capability.Title, description, current?.BooleanValue ?? false,
                Disabled: disabled),
            { Writable: true, ValueKind: CapabilityValueKind.Integer, Minimum: { } minimum, Maximum: { } maximum }
                when maximum >= minimum => new SteamSettingsRow(key, SteamSettingsRowKind.Range, capability.Title,
                    // No value is invented: a range nothing has read or written shows no position, as in the overlay.
                    description, Number: current?.IntegerValue, Minimum: minimum, Maximum: maximum,
                    Step: capability.Step ?? 1,
                    Suffix: CapabilityRowProjection.UnitSuffix(capability.Unit) is { Length: > 0 } suffix
                        ? suffix
                        : null,
                    Disabled: disabled),
            { Writable: true, ValueKind: CapabilityValueKind.Choice, Choices.Count: > 0 } => new SteamSettingsRow(key,
                SteamSettingsRowKind.Choice, capability.Title, description, Text: current?.ChoiceValue ?? string.Empty,
                Choices:
                [
                    .. capability.Choices.Select(choice =>
                        new SteamSettingsChoice(choice.Value,
                            CapabilityDisplayLabels.For(choice.Display, choice.Value)))
                ],
                Disabled: disabled),
            { Writable: true, ValueKind: CapabilityValueKind.Text } => new SteamSettingsRow(key,
                SteamSettingsRowKind.Text, capability.Title, description, Text: current?.TextValue ?? string.Empty,
                MaximumLength: capability.MaximumLength, Disabled: disabled),
            // A colour that was never read back still opens its editor, starting from white.
            { Writable: true, ValueKind: CapabilityValueKind.Color } => new SteamSettingsRow(key,
                SteamSettingsRowKind.Color, capability.Title, description,
                Text: $"#{current?.ColorValue ?? 0xFFFFFF:X6}", Disabled: disabled, ColorAlpha: false),
            { Writable: true, ValueKind: CapabilityValueKind.Curve } => CurveRow(capability, key, description,
                disabled),
            { SupportsAction: true } => new SteamSettingsRow(key, SteamSettingsRowKind.Action, capability.Title,
                description, Disabled: disabled, ButtonLabel: "Run"),
            _ => new SteamSettingsRow(key, SteamSettingsRowKind.Note, capability.Title, description,
                Text: capability.TrailingText)
        };
        return capability.OverrideId is null
            ? row
            : row with { Accent = true, Description = NativeQamLayout.AccentDescription(row.Description) };
    }

    /// <summary>Puts the renderer's value into the capability's own shape, within what the row accepts.</summary>
    /// <param name="capability">The row being changed, as it was offered.</param>
    /// <param name="value">The renderer's value.</param>
    /// <param name="candidate">The capability value, or null for an action.</param>
    /// <returns>Whether the value fits the row; the coordinator still validates it against the live descriptor.</returns>
    internal static bool TryValue(DeviceOverlayCapability capability, JsonElement value,
        out CapabilityValue? candidate)
    {
        ArgumentNullException.ThrowIfNull(capability);
        candidate = null;
        switch (capability)
        {
            case { Writable: true, ValueKind: CapabilityValueKind.Boolean }
                when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                candidate = CapabilityValue.Boolean(value.GetBoolean());
                return true;
            case { Writable: true, ValueKind: CapabilityValueKind.Integer, Minimum: { } minimum, Maximum: { } maximum }
                when value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
                                                             && double.IsFinite(number):
                // Steam's slider reports a double; it lands on the row's own steps.
                if (CapabilityProjection.ValidInteger(CapabilityValue.Integer((int)Math.Round(number)), minimum,
                        maximum, Math.Max(1, capability.Step ?? 1)) is not { } integer)
                {
                    return false;
                }

                candidate = CapabilityValue.Integer(integer);
                return true;
            case { Writable: true, ValueKind: CapabilityValueKind.Choice } when value.ValueKind == JsonValueKind.String
                && capability.Choices.Any(choice => choice.Value == value.GetString()):
                candidate = CapabilityValue.Choice(value.GetString()!);
                return true;
            case { Writable: true, ValueKind: CapabilityValueKind.Text } when value.ValueKind == JsonValueKind.String
                && (capability.MaximumLength is null || value.GetString()!.Length <= capability.MaximumLength):
                candidate = CapabilityValue.Text(value.GetString()!);
                return true;
            case { Writable: true, ValueKind: CapabilityValueKind.Color } when TryColor(value, out var color):
                candidate = CapabilityValue.Color(color);
                return true;
            case { Writable: true, ValueKind: CapabilityValueKind.Curve } when TryCurve(capability, value,
                out var curve):
                candidate = CapabilityValue.Curve(curve);
                return true;
            case { SupportsAction: true }:
                return true;
            default:
                return false;
        }
    }

    /// <summary>Parses the native editor's opaque RGB color vocabulary.</summary>
    /// <param name="value">A #RRGGBB string or hsla(h, s%, l%, 1); finite hue is wrapped into one turn.</param>
    /// <param name="color">Packed 24-bit RGB on success; zero on failure.</param>
    /// <returns>True for a complete supported color with valid saturation/lightness and full opacity.</returns>
    internal static bool TryColor(JsonElement value, out int color)
    {
        color = 0;
        if (value.ValueKind is not JsonValueKind.String || value.GetString() is not { } text)
        {
            return false;
        }

        if (text.Length == 7 && text[0] == '#')
        {
            return int.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
                out color);
        }

        if (!text.StartsWith("hsla(", StringComparison.Ordinal) || !text.EndsWith(')'))
        {
            return false;
        }

        var parts = text[5..^1].Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4 || !parts[1].EndsWith('%') || !parts[2].EndsWith('%')
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var hue)
            || !double.TryParse(parts[1][..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var saturation)
            || !double.TryParse(parts[2][..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lightness)
            || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha)
            || !double.IsFinite(hue) || saturation is < 0 or > 100 || !double.IsFinite(saturation)
            || lightness is < 0 or > 100 || !double.IsFinite(lightness) || alpha != 1)
        {
            return false;
        }

        var normalizedHue = (hue % 360 + 360) % 360 / 60;
        var light = lightness / 100;
        var chroma = (1 - Math.Abs(2 * light - 1)) * saturation / 100;
        var secondary = chroma * (1 - Math.Abs(normalizedHue % 2 - 1));
        var offset = light - chroma / 2;
        var (red, green, blue) = normalizedHue switch
        {
            < 1 => (chroma, secondary, 0d),
            < 2 => (secondary, chroma, 0d),
            < 3 => (0d, chroma, secondary),
            < 4 => (0d, secondary, chroma),
            < 5 => (secondary, 0d, chroma),
            _ => (chroma, 0d, secondary)
        };
        color = (Channel(red) << 16) | (Channel(green) << 8) | Channel(blue);
        return true;

        int Channel(double channel)
        {
            return (int)Math.Round((channel + offset) * 255);
        }
    }

    private static SteamSettingsRow CurveRow(DeviceOverlayCapability capability, string key, string? description,
        bool disabled)
    {
        var curve = capability.CurrentValue?.CurveValue ?? [];
        return new SteamSettingsRow(key, SteamSettingsRowKind.Curve, capability.Title, description,
            Minimum: capability.Minimum, Maximum: capability.Maximum, Step: capability.Step,
            Suffix: CapabilityRowProjection.UnitSuffix(capability.Unit) is { Length: > 0 } suffix ? suffix : null,
            Disabled: disabled,
            Curve: [.. curve.Select(point => new SteamSettingsCurvePoint(point.Input, point.Output))],
            CurvePresets: capability.Presets is { } presets
                ?
                [
                    .. Enum.GetValues<FanCurvePreset>().Select(preset => new SteamSettingsCurvePreset(
                        FanCurvePresets.Label(preset),
                        [
                            .. FanCurvePresets.SampleOnto(presets, preset, curve)
                                .Select(point => new SteamSettingsCurvePoint(point.Input, point.Output))
                        ]))
                ]
                : null);
    }

    // The editor keeps the curve's inputs; only its outputs change, within the row's duty range.
    private static bool TryCurve(DeviceOverlayCapability capability, JsonElement value, out List<CurvePoint> curve)
    {
        curve = [];
        var inputs = capability.CurrentValue?.CurveValue ?? [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != inputs.Count || inputs.Count == 0)
        {
            return false;
        }

        var index = 0;
        foreach (var point in value.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Object || !point.TryGetProperty("input", out var input)
                || input.ValueKind != JsonValueKind.Number || !input.TryGetInt32(out var temperature)
                || !point.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Number
                || !output.TryGetInt32(out var duty) || point.EnumerateObject().Count() != 2
                || temperature != inputs[index].Input
                || duty < (capability.Minimum ?? 0) || duty > (capability.Maximum ?? 100))
            {
                return false;
            }

            curve.Add(new CurvePoint(temperature, duty));
            index++;
        }

        return true;
    }
}
