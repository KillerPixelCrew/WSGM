using System.Text.Json.Serialization;

namespace WSGM.Device.Sdk.Capabilities;

/// <summary>
/// How a capability is labelled in WSGM's own surfaces.
/// </summary>
/// <remarks>
/// Presentation is WSGM-owned. A plugin selects a display key from a WSGM-defined set, which WSGM
/// then localizes; it does not supply the words. That is what keeps every device speaking the same
/// language in the overlay and the native QAM, and it is why a plugin cannot ship markup,
/// formatting, localization resources, or anything executable through this path.
/// <para>
/// <see cref="CustomLabel"/> is the escape hatch for a genuinely device-specific control that no key
/// covers — an unusual vendor toggle. It is untrusted plain text: length-bounded, control characters
/// rejected, and escaped at every sink. It is never a format string, never markup, and never a
/// localization key.
/// </para>
/// </remarks>
public sealed record CapabilityDisplay
{
    /// <summary>Longest accepted <see cref="CustomLabel"/>.</summary>
    public const int MaxCustomLabelLength = 48;

    /// <summary>The WSGM-owned display key, or <see cref="DisplayKey.Custom"/>.</summary>
    public required DisplayKey Key { get; init; }

    /// <summary>
    /// Untrusted plain-text label, used only when <see cref="Key"/> is
    /// <see cref="DisplayKey.Custom"/>. Not localized: WSGM cannot translate text it did not author.
    /// </summary>
    public string? CustomLabel { get; init; }

    /// <summary>
    /// Whether this display metadata is usable.
    /// </summary>
    /// <param name="error">The reason it is not, when the result is <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the label is safe to render.</returns>
    public bool TryValidate(out string? error)
    {
        if (Key is not DisplayKey.Custom)
        {
            // A label alongside a real key would be dead weight that some surface eventually renders
            // instead of the localized string.
            if (CustomLabel is not null)
            {
                error = "customLabel is only permitted when key is Custom.";
                return false;
            }

            error = null;
            return true;
        }

        if (string.IsNullOrWhiteSpace(CustomLabel))
        {
            error = "key Custom requires a customLabel.";
            return false;
        }

        if (CustomLabel.Length > MaxCustomLabelLength)
        {
            error = $"customLabel exceeds {MaxCustomLabelLength} characters.";
            return false;
        }

        foreach (char c in CustomLabel)
        {
            // Control characters corrupt log lines and can hide the rest of a label from a reviewer;
            // bidirectional overrides can make a label render as something other than what it says.
            if (char.IsControl(c) || c is '‮' or '‭' or '‏' or '‎')
            {
                error = "customLabel contains a control or bidirectional-override character.";
                return false;
            }
        }

        error = null;
        return true;
    }
}

/// <summary>
/// The WSGM-owned vocabulary of capability labels.
/// </summary>
/// <remarks>
/// Adding a key is a WSGM change with a localized string behind it, not something a package can do.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<DisplayKey>))]
public enum DisplayKey
{
    /// <summary>Use <see cref="CapabilityDisplay.CustomLabel"/> as untrusted plain text.</summary>
    Custom,

    /// <summary>"TDP".</summary>
    Tdp,

    /// <summary>"Sustained power limit".</summary>
    SustainedPowerLimit,

    /// <summary>"Boost power limit".</summary>
    BoostPowerLimit,

    /// <summary>"Performance profile".</summary>
    PerformanceProfile,

    /// <summary>"Fan mode".</summary>
    FanMode,

    /// <summary>"Fan speed".</summary>
    FanSpeed,

    /// <summary>"Fan curve".</summary>
    FanCurve,

    /// <summary>"Left fan".</summary>
    FanLeft,

    /// <summary>"Right fan".</summary>
    FanRight,

    /// <summary>"Charge limit".</summary>
    ChargeLimit,

    /// <summary>"Bypass charging".</summary>
    BypassCharging,

    /// <summary>"Lighting".</summary>
    Lighting,

    /// <summary>"Brightness".</summary>
    Brightness,

    /// <summary>"Effect".</summary>
    LightingEffect,

    /// <summary>"Effect speed".</summary>
    LightingEffectSpeed,

    /// <summary>"CPU temperature".</summary>
    CpuTemperature,

    /// <summary>"Battery".</summary>
    Battery,

    /// <summary>"Controller".</summary>
    Controller,

    /// <summary>"Motion".</summary>
    Motion,

    /// <summary>"Rumble".</summary>
    Rumble,
}
