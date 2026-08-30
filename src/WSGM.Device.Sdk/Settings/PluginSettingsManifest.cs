using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Device.Sdk.Settings;

/// <summary>
/// One declared plugin setting: a preference WSGM stores and hands back, not a hardware control.
/// </summary>
/// <remarks>
/// The distinction from <see cref="CapabilityDescriptor"/> is what decides where a control lives and
/// is not a judgement call. Changing a setting configures how the plugin behaves and WSGM keeps the
/// value; changing a capability writes hardware state and the device keeps it. A control that writes
/// to the device when the user moves it is a capability, however much it reads like a preference.
/// </remarks>
public sealed record PluginSettingDescriptor
{
    /// <summary>Longest accepted <see cref="SettingId"/>.</summary>
    public const int MaxSettingIdLength = 64;

    /// <summary>Ceiling a text setting's own maximum length may declare.</summary>
    public const int MaxTextLength = 256;

    /// <summary>Most options a choice setting may offer.</summary>
    public const int MaxChoices = 64;

    /// <summary>Stable identifier, for example <c>ec.poll-interval</c>.</summary>
    public required string SettingId { get; init; }

    /// <summary>The shape of the value, which decides the control WSGM draws.</summary>
    public required CapabilityValueKind ValueKind { get; init; }

    /// <summary>How WSGM labels it.</summary>
    public required CapabilityDisplay Display { get; init; }

    /// <summary>The value used before the user chooses one, and after an invalid stored value.</summary>
    public required CapabilityValue Default { get; init; }

    /// <summary>
    /// Which declared section this belongs to. An unknown or absent section places the setting in a
    /// WSGM-owned fallback rather than dropping it.
    /// </summary>
    public string? SectionId { get; init; }

    /// <summary>Placement within its section. Ties break on declaration order.</summary>
    public int SortOrder { get; init; }

    /// <summary>Inclusive minimum for an integer setting.</summary>
    public int? Minimum { get; init; }

    /// <summary>Inclusive maximum for an integer setting.</summary>
    public int? Maximum { get; init; }

    /// <summary>Step between legal integer values.</summary>
    public int? Step { get; init; }

    /// <summary>Unit of a numeric value.</summary>
    public CapabilityUnit Unit { get; init; } = CapabilityUnit.None;

    /// <summary>Legal options for a choice setting.</summary>
    public IReadOnlyList<CapabilityChoice> Choices { get; init; } = [];

    /// <summary>Longest accepted value for a text setting. Required for text, rejected otherwise.</summary>
    public int? MaximumLength { get; init; }

    /// <summary>
    /// Whether this setting is usable.
    /// </summary>
    /// <param name="error">The reason it is not, when the result is <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the setting is safe to render and store.</returns>
    public bool TryValidate(out string? error)
    {
        if (!PlainText.IsIdentifier(SettingId, MaxSettingIdLength))
        {
            error = $"settingId '{SettingId}' is not a legal identifier.";
            return false;
        }

        if (SectionId is not null
            && !PlainText.IsIdentifier(SectionId, PluginSettingSection.MaxSectionIdLength))
        {
            error = $"setting '{SettingId}' names a sectionId that is not a legal identifier.";
            return false;
        }

        if (!Display.TryValidate(out error))
        {
            return false;
        }

        // A setting is something the user changes, so a value shape that cannot be changed or has no
        // value at all has nothing to draw.
        if (ValueKind is CapabilityValueKind.None)
        {
            error = $"setting '{SettingId}' must carry a value; use a capability for an action.";
            return false;
        }

        if (ValueKind is CapabilityValueKind.Integer
            && (Minimum is null || Maximum is null || Minimum > Maximum || Step is <= 0))
        {
            error = $"setting '{SettingId}' needs a minimum, a maximum, and a positive step.";
            return false;
        }

        if (ValueKind is CapabilityValueKind.Choice
            && (Choices.Count is 0 or > MaxChoices
                || Choices.Any(choice => !PlainText.IsIdentifier(choice.Value, 64))
                || Choices.Select(choice => choice.Value).Distinct(System.StringComparer.Ordinal)
                    .Count() != Choices.Count))
        {
            error = $"setting '{SettingId}' has empty, invalid, oversized, or duplicated choices.";
            return false;
        }

        if (ValueKind is not CapabilityValueKind.Choice && Choices.Count != 0)
        {
            error = $"setting '{SettingId}' may not carry choices.";
            return false;
        }

        if (ValueKind is CapabilityValueKind.Text && MaximumLength is not (> 0 and <= MaxTextLength))
        {
            error = $"setting '{SettingId}' needs a maximumLength between 1 and {MaxTextLength}.";
            return false;
        }

        if (ValueKind is not CapabilityValueKind.Text && MaximumLength is not null)
        {
            error = $"setting '{SettingId}' may not carry a maximumLength.";
            return false;
        }

        if (Default.Kind != ValueKind)
        {
            error = $"setting '{SettingId}' has a default of the wrong value kind.";
            return false;
        }

        error = null;
        return true;
    }
}

/// <summary>
/// Everything a plugin declares for its own settings page: the sections, and the settings placed in
/// them. The plugin ships no UI — WSGM draws, validates, stores, and localizes all of it.
/// </summary>
public sealed record PluginSettingsManifest
{
    /// <summary>Most sections one plugin may declare.</summary>
    /// <remarks>
    /// Bounded because an unbounded page cannot be navigated with a gamepad, and a plugin declaring
    /// two hundred rows produces a surface nobody can use.
    /// </remarks>
    public const int MaxSections = 12;

    /// <summary>Most settings one plugin may declare.</summary>
    public const int MaxSettings = 96;

    /// <summary>The declared sections, in the order they were written.</summary>
    public IReadOnlyList<PluginSettingSection> Sections { get; init; } = [];

    /// <summary>The declared settings, in the order they were written.</summary>
    public IReadOnlyList<PluginSettingDescriptor> Settings { get; init; } = [];

    /// <summary>
    /// Whether the whole manifest is usable.
    /// </summary>
    /// <param name="error">The reason it is not, when the result is <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when every section and setting is safe.</returns>
    /// <remarks>
    /// A setting naming an unknown section is deliberately <em>not</em> an error: it renders in a
    /// WSGM-owned fallback section and the placement is logged. Dropping it silently is the one
    /// outcome that leaves the user with a missing control and no way to find out why.
    /// </remarks>
    public bool TryValidate(out string? error)
    {
        if (Sections.Count > MaxSections)
        {
            error = $"manifest declares {Sections.Count} sections; the limit is {MaxSections}.";
            return false;
        }

        if (Settings.Count > MaxSettings)
        {
            error = $"manifest declares {Settings.Count} settings; the limit is {MaxSettings}.";
            return false;
        }

        var sectionIds = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (PluginSettingSection section in Sections)
        {
            if (!section.TryValidate(out error))
            {
                return false;
            }

            if (!sectionIds.Add(section.SectionId))
            {
                error = $"manifest declares section '{section.SectionId}' more than once.";
                return false;
            }
        }

        var settingIds = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (PluginSettingDescriptor setting in Settings)
        {
            if (!setting.TryValidate(out error))
            {
                return false;
            }

            if (!settingIds.Add(setting.SettingId))
            {
                error = $"manifest declares setting '{setting.SettingId}' more than once.";
                return false;
            }
        }

        error = null;
        return true;
    }
}
