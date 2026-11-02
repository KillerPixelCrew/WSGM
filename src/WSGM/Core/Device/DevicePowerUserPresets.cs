using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WSGM.Core;

/// <summary>The rules for user-saved power presets: identity, names, normalization and edits.</summary>
/// <remarks>
///     Pure functions over the stored lists, so the session owner, normalization and tests share one rule set.
///     Presets are scoped by device family; another handheld never sees them.
/// </remarks>
internal static class DevicePowerUserPresets
{
    /// <summary>The prefix of every saved preset's identifier, which keeps it apart from device-declared ids.</summary>
    internal const string IdPrefix = "user.";

    /// <summary>Whether an identifier names a saved preset rather than a device-declared one.</summary>
    /// <param name="id">A preset identifier.</param>
    /// <returns>True for a saved preset's identifier.</returns>
    internal static bool IsUserId(string? id)
    {
        return id is not null && id.StartsWith(IdPrefix, StringComparison.Ordinal) && id.Length > IdPrefix.Length;
    }

    /// <summary>A new identifier no saved preset uses.</summary>
    /// <returns>The identifier.</returns>
    internal static string NewId()
    {
        return IdPrefix + Guid.NewGuid().ToString("N")[..12];
    }

    /// <summary>The saved presets of one device family, in saved order.</summary>
    /// <param name="presets">Every saved preset.</param>
    /// <param name="pluginId">The family, or null for none.</param>
    /// <returns>That family's presets; none without a family.</returns>
    internal static IReadOnlyList<DevicePowerUserPreset> For(IEnumerable<DevicePowerUserPreset>? presets,
        string? pluginId)
    {
        return pluginId is null || presets is null
            ? []
            : [.. presets.Where(preset => preset.PluginId == pluginId && preset.Values is not null)];
    }

    /// <summary>The first unused "Custom preset N" name for a family.</summary>
    /// <param name="presets">Every saved preset.</param>
    /// <param name="pluginId">The family.</param>
    /// <returns>A name no preset of that family uses.</returns>
    internal static string DefaultName(IEnumerable<DevicePowerUserPreset> presets, string? pluginId)
    {
        var taken = For(presets, pluginId).Select(preset => preset.Name)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        for (var number = 1;; number++)
        {
            var name = string.Create(CultureInfo.CurrentCulture, $"Custom preset {number}");
            if (!taken.Contains(name))
            {
                return name;
            }
        }
    }

    /// <summary>Checks a name for a family's preset.</summary>
    /// <param name="presets">Every saved preset.</param>
    /// <param name="pluginId">The family.</param>
    /// <param name="name">The requested name.</param>
    /// <param name="exceptId">The preset being renamed, which may keep its own name; null for a new one.</param>
    /// <param name="error">Why the name is refused, or null.</param>
    /// <returns>The trimmed name, or null when it is refused.</returns>
    /// <remarks>No length limit: a long name wraps or ellipsizes where it is drawn.</remarks>
    internal static string? ValidateName(IEnumerable<DevicePowerUserPreset> presets, string pluginId, string? name,
        string? exceptId, out string? error)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            error = "Enter a name for the preset.";
            return null;
        }

        if (trimmed.Any(char.IsControl))
        {
            error = "The preset name must be a single line of text.";
            return null;
        }

        if (For(presets, pluginId).Any(preset => preset.Id != exceptId
                                                 && string.Equals(preset.Name, trimmed,
                                                     StringComparison.CurrentCultureIgnoreCase)))
        {
            error = $"A preset named “{trimmed}” already exists.";
            return null;
        }

        error = null;
        return trimmed;
    }

    /// <summary>Applies one edit to the stored list.</summary>
    /// <param name="presets">The stored list, changed in place.</param>
    /// <param name="edit">The edit.</param>
    /// <returns>The preset as it was before the edit, or the new preset for a save.</returns>
    /// <exception cref="InvalidOperationException">The name, values or target preset is refused; nothing changed.</exception>
    internal static DevicePowerUserPreset Apply(List<DevicePowerUserPreset> presets, DevicePowerUserPresetEdit edit)
    {
        if (string.IsNullOrWhiteSpace(edit.PluginId))
        {
            throw new InvalidOperationException("No device is selected for power presets.");
        }

        if (edit.Kind == DevicePowerUserPresetEditKind.Save)
        {
            if (!IsUserId(edit.Id) || presets.Any(preset => preset.Id == edit.Id))
            {
                throw new InvalidOperationException("The preset could not be given an identifier.");
            }

            var name = ValidateName(presets, edit.PluginId, edit.Name, null, out var error)
                       ?? throw new InvalidOperationException(error);
            var created = new DevicePowerUserPreset
                { PluginId = edit.PluginId, Id = edit.Id, Name = name, Values = ValidValues(edit.Values) };
            presets.Add(created);
            return created;
        }

        var index = presets.FindIndex(preset => preset.PluginId == edit.PluginId && preset.Id == edit.Id);
        if (index < 0)
        {
            throw new InvalidOperationException("This preset no longer exists.");
        }

        var existing = presets[index];
        var before = new DevicePowerUserPreset
            { PluginId = existing.PluginId, Id = existing.Id, Name = existing.Name, Values = existing.Values };
        switch (edit.Kind)
        {
            case DevicePowerUserPresetEditKind.Rename:
                existing.Name = ValidateName(presets, edit.PluginId, edit.Name, edit.Id, out var error)
                                ?? throw new InvalidOperationException(error);
                break;
            case DevicePowerUserPresetEditKind.Update:
                existing.Values = ValidValues(edit.Values);
                break;
            case DevicePowerUserPresetEditKind.Delete:
                presets.RemoveAt(index);
                break;
            default:
                throw new InvalidOperationException("Unknown preset change.");
        }

        return before;
    }

    /// <summary>Turns every assignment of a deleted preset into Custom holding its values, in every layer.</summary>
    /// <param name="profiles">The profile store, changed in place.</param>
    /// <param name="deleted">The preset being deleted.</param>
    /// <returns>Whether any assignment changed.</returns>
    /// <remarks>The device keeps running what it runs; only the name of the assignment goes away.</remarks>
    internal static bool ReplaceReferences(ProfileConfig profiles, DevicePowerUserPreset deleted)
    {
        var changed = false;
        foreach (var values in new[] { profiles.Global }.Concat(profiles.Games.Select(game => game.Values)))
        {
            if (values is null)
            {
                continue;
            }

            values.AcPowerPreset = Replace(values.AcPowerPreset);
            values.BatteryPowerPreset = Replace(values.BatteryPowerPreset);
        }

        return changed;

        DevicePowerPresetReference? Replace(DevicePowerPresetReference? reference)
        {
            if (reference is null || reference.PluginId != deleted.PluginId || reference.PresetId != deleted.Id)
            {
                return reference;
            }

            changed = true;
            return new DevicePowerPresetReference
                { PluginId = deleted.PluginId, PresetId = "custom", CustomValues = deleted.Values };
        }
    }

    /// <summary>Repairs the stored list: trims, drops incomplete or duplicate entries.</summary>
    /// <param name="presets">The stored list, changed in place.</param>
    internal static void Normalize(List<DevicePowerUserPreset> presets)
    {
        presets.RemoveAll(static preset => preset is null);
        foreach (var preset in presets)
        {
            preset.PluginId = preset.PluginId?.Trim() ?? string.Empty;
            preset.Id = preset.Id?.Trim() ?? string.Empty;
            preset.Name = preset.Name?.Trim() ?? string.Empty;
        }

        presets.RemoveAll(static preset => preset.PluginId.Length == 0 || !IsUserId(preset.Id)
                                                                       || preset.Name.Length == 0
                                                                       || preset.Values?.IsValid() != true);
        HashSet<string> ids = new(StringComparer.Ordinal);
        HashSet<string> names = new(StringComparer.CurrentCultureIgnoreCase);
        presets.RemoveAll(preset => !ids.Add(preset.Id) || !names.Add(preset.PluginId + "/" + preset.Name));
    }

    private static DevicePowerCustomValues ValidValues(DevicePowerCustomValues? values)
    {
        return values?.IsValid() == true
            ? values
            : throw new InvalidOperationException("The current power values are incomplete and cannot be saved.");
    }
}
