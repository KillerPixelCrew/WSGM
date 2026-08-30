using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;

namespace WSGM.Settings;

/// <summary>One rendered row on the plugin settings page.</summary>
/// <param name="SettingId">The declared setting this row edits.</param>
/// <param name="Descriptor">Its declaration, which decides the control drawn.</param>
/// <param name="Value">The effective value after reconciliation against the manifest.</param>
internal readonly record struct PluginSettingsRow(
    string SettingId,
    PluginSettingDescriptor Descriptor,
    CapabilityValue Value);

/// <summary>One rendered section, which is also a focus group.</summary>
/// <param name="SectionId">
/// Stable key. The existing per-destination focus and scroll restoration keys off this, so it must
/// survive a refresh rather than being an index into a list that changed.
/// </param>
/// <param name="Key">The WSGM-owned title key, or <see cref="SettingSectionKey.Custom"/>.</param>
/// <param name="CustomTitle">The plugin's plain-text title when the key is custom.</param>
/// <param name="Rows">The rows in this section, in render order.</param>
internal readonly record struct PluginSettingsSection(
    string SectionId,
    SettingSectionKey Key,
    string? CustomTitle,
    IReadOnlyList<PluginSettingsRow> Rows);

/// <summary>The whole page, plus what was dropped getting there.</summary>
/// <param name="Sections">Sections in render order. Empty when the plugin declares no settings.</param>
/// <param name="Diagnostics">
/// What was moved or not drawn, and why. Never discarded silently: a section that vanished or a
/// setting that landed somewhere unexpected is otherwise impossible to diagnose from a user's log.
/// </param>
internal readonly record struct PluginSettingsPage(
    IReadOnlyList<PluginSettingsSection> Sections,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Turns a plugin's declared manifest and its reconciled values into the page WSGM draws.
/// </summary>
/// <remarks>
/// Pure, and deliberately separate from the control that renders it: the ordering, the fallback for
/// an unknown section, and the decision not to draw an empty one are rules with observable
/// consequences, and they are testable without a device or a UI thread.
/// <para>
/// The plugin chooses placement and order. It never supplies layout — no widths, no columns, no
/// nesting — which is what keeps one WSGM page consistent across devices that share nothing.
/// </para>
/// </remarks>
internal static class PluginSettingsPageLayout
{
    /// <summary>Section id for settings whose declared section does not exist.</summary>
    /// <remarks>
    /// A WSGM-owned identifier a plugin cannot declare, because it is not a legal plugin section id:
    /// the manifest requires an identifier and this carries a character that is not one. Without
    /// that, a plugin could declare this id itself and take over the fallback group.
    /// </remarks>
    internal const string FallbackSectionId = "wsgm:other";

    /// <summary>Builds the page.</summary>
    /// <param name="manifest">The plugin's declaration.</param>
    /// <param name="values">Reconciled values, one per declared setting.</param>
    /// <returns>The sections to draw, and what was moved or dropped.</returns>
    /// <remarks>
    /// A setting with no reconciled value is not drawn: the resolver produces one entry per declared
    /// setting, so a missing entry means the two disagree, and drawing a control with no value would
    /// show the user a state the plugin never reported.
    /// </remarks>
    internal static PluginSettingsPage Build(
        PluginSettingsManifest manifest,
        IReadOnlyList<EffectivePluginSetting> values)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(values);

        Dictionary<string, CapabilityValue> resolved = [];
        foreach (EffectivePluginSetting value in values)
        {
            resolved[value.SettingId] = value.Value;
        }

        List<string> diagnostics = [];
        Dictionary<string, List<(int Order, int Declared, PluginSettingsRow Row)>> grouped = [];
        HashSet<string> declaredSections = [.. manifest.Sections.Select(section => section.SectionId)];

        int declaredIndex = 0;
        foreach (PluginSettingDescriptor setting in manifest.Settings)
        {
            int index = declaredIndex++;
            if (!resolved.TryGetValue(setting.SettingId, out CapabilityValue? value))
            {
                diagnostics.Add(
                    $"Setting '{setting.SettingId}' has no reconciled value and was not drawn.");
                continue;
            }

            string sectionId = setting.SectionId is { } declared
                && declaredSections.Contains(declared)
                    ? declared
                    : FallbackSectionId;

            // Named rather than dropped. A plugin author reading their own log needs to see that the
            // control exists and simply landed somewhere else.
            if (setting.SectionId is { } named && sectionId == FallbackSectionId)
            {
                diagnostics.Add(
                    $"Setting '{setting.SettingId}' names undeclared section '{named}' and was "
                    + "placed in the fallback group.");
            }

            if (!grouped.TryGetValue(sectionId, out List<(int, int, PluginSettingsRow)>? rows))
            {
                rows = [];
                grouped[sectionId] = rows;
            }

            rows.Add((setting.SortOrder, index, new PluginSettingsRow(
                setting.SettingId,
                setting,
                value)));
        }

        List<PluginSettingsSection> sections = [];
        int declaredSectionIndex = 0;
        foreach (PluginSettingSection section in manifest.Sections
            .Select(section => (Section: section, Index: declaredSectionIndex++))
            // Ties break on declaration order, so a manifest that orders nothing still renders the
            // same way every time rather than in dictionary order.
            .OrderBy(entry => entry.Section.SortOrder)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Section))
        {
            if (!grouped.TryGetValue(section.SectionId, out List<(int, int, PluginSettingsRow)>? rows)
                || rows.Count == 0)
            {
                diagnostics.Add(
                    $"Section '{section.SectionId}' has no visible setting and was not drawn.");
                continue;
            }

            sections.Add(new PluginSettingsSection(
                section.SectionId,
                section.Key,
                section.CustomTitle,
                Order(rows)));
        }

        // Last, always. It holds what could not be placed, and a group of leftovers above the
        // plugin's own sections would read as the page's most important content.
        if (grouped.TryGetValue(FallbackSectionId, out List<(int, int, PluginSettingsRow)>? fallback)
            && fallback.Count > 0)
        {
            sections.Add(new PluginSettingsSection(
                FallbackSectionId,
                SettingSectionKey.General,
                null,
                Order(fallback)));
        }

        return new PluginSettingsPage(sections, diagnostics);
    }

    private static IReadOnlyList<PluginSettingsRow> Order(
        List<(int Order, int Declared, PluginSettingsRow Row)> rows) =>
        [.. rows.OrderBy(entry => entry.Order).ThenBy(entry => entry.Declared).Select(entry => entry.Row)];
}
