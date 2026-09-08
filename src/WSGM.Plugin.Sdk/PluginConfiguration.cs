using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Plugin.Sdk;

/// <summary>The primitive accepted by a declared plugin setting.</summary>
public enum PluginSettingKind
{
    /// <summary>A boolean preference.</summary>
    Boolean,
    /// <summary>A finite numeric preference.</summary>
    Number,
    /// <summary>A bounded plain-text preference.</summary>
    Text,
}

/// <summary>Declarative plugin behavior configuration, separate from external-state actions.</summary>
/// <param name="Key">Stable setting identity.</param>
/// <param name="Label">Plain display label.</param>
/// <param name="Kind">Accepted primitive.</param>
/// <param name="Default">Initial fallback, never implicitly saved as user intent.</param>
/// <param name="Minimum">Inclusive numeric lower bound, or null.</param>
/// <param name="Maximum">Inclusive numeric upper bound, or null.</param>
/// <param name="Choices">Optional text choices; null permits arbitrary bounded text.</param>
public sealed record PluginSetting(string Key, string Label, PluginSettingKind Kind, PluginValue Default,
    double? Minimum = null, double? Maximum = null, IReadOnlyList<string>? Choices = null);

/// <summary>Origin of a complete requested configuration.</summary>
public enum PluginConfigurationOrigin
{
    /// <summary>A person explicitly changed plugin preferences.</summary>
    User,
    /// <summary>The host restores saved preferences and unsaved declaration defaults.</summary>
    Restore,
}

/// <summary>Host-owned desired revision delivered as a complete immutable snapshot.</summary>
/// <param name="Revision">Persisted user-intent revision; zero when no preference has been saved.</param>
/// <param name="Origin">Explicit user change or host restoration.</param>
/// <param name="Values">Validated settings, including fallback values for undeclared user preferences.</param>
public sealed record PluginConfiguration(long Revision, PluginConfigurationOrigin Origin, IReadOnlyDictionary<string, PluginValue> Values);

/// <summary>Result of applying plugin behavior configuration.</summary>
public enum PluginConfigurationOutcome
{
    /// <summary>The plugin confirms it adopted the requested configuration.</summary>
    Applied,
    /// <summary>The plugin rejected the request before adopting it.</summary>
    Rejected,
    /// <summary>The final outcome is unknown and must not be retried automatically.</summary>
    Unconfirmed,
}

/// <summary>Confirmation for one requested revision. It cannot modify desired preferences.</summary>
/// <param name="Revision">Exact requested revision.</param>
/// <param name="Outcome">Confirmed or uncertain application status.</param>
/// <param name="Detail">Plain explanation.</param>
public sealed record PluginConfigurationResult(long Revision, PluginConfigurationOutcome Outcome, string? Detail = null);

/// <summary>Optional declaration and delivery contract for plugin behavior preferences.</summary>
public interface IConfigurablePlugin
{
    /// <summary>Bounded static settings declaration, captured before startup.</summary>
    IReadOnlyList<PluginSetting> Settings { get; }
    /// <summary>Adopts a complete configuration after startup, without treating readback as desired state.</summary>
    /// <param name="configuration">Host revision and values.</param>
    /// <param name="context">Current lifecycle scope.</param>
    /// <param name="cancellationToken">Cooperative cancellation, not proof that application was undone.</param>
    /// <returns>Confirmation for the exact supplied revision.</returns>
    ValueTask<PluginConfigurationResult> ConfigureAsync(PluginConfiguration configuration, PluginContext context, CancellationToken cancellationToken);
}

/// <summary>Shared validation for declarations, authoring tools and host delivery.</summary>
public static class PluginConfigurationRules
{
    /// <summary>Checks bounded declarations, unique identities, defaults, choices and numeric ranges.</summary>
    /// <param name="settings">Declared plugin preferences.</param>
    /// <returns>Whether the complete declaration is valid.</returns>
    public static bool IsValid(IReadOnlyList<PluginSetting>? settings)
    {
        if (settings is null || settings.Count > 128) { return false; }
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (var setting in settings)
        {
            if (setting is null || !ValidKey(setting.Key) || !keys.Add(setting.Key) || !Enum.IsDefined(setting.Kind)
                || string.IsNullOrWhiteSpace(setting.Label) || setting.Label.Length > 128 || setting.Label.Any(char.IsControl)
                || (setting.Minimum is { } minimum && !double.IsFinite(minimum))
                || (setting.Maximum is { } maximum && !double.IsFinite(maximum)) || setting.Minimum > setting.Maximum
                || (setting.Kind != PluginSettingKind.Number && (setting.Minimum.HasValue || setting.Maximum.HasValue))
                || (setting.Choices is { } choices && (setting.Kind != PluginSettingKind.Text || choices.Count is 0 or > 64
                    || choices.Any(choice => choice is null || choice.Length > 4096) || choices.Distinct(StringComparer.Ordinal).Count() != choices.Count))
                || !Accepts(setting, setting.Default)) { return false; }
        }
        return true;
    }

    /// <summary>Checks one value against an already validated declaration.</summary>
    /// <param name="setting">Validated declaration.</param>
    /// <param name="value">Candidate preference.</param>
    /// <returns>Whether the value matches kind, bounds and choices.</returns>
    public static bool Accepts(PluginSetting setting, PluginValue value) => value.IsValid && setting.Kind switch
    {
        PluginSettingKind.Boolean => value.Boolean.HasValue,
        PluginSettingKind.Number => value.Number.HasValue && !(value.Number < setting.Minimum) && !(value.Number > setting.Maximum),
        PluginSettingKind.Text => value.Text is not null && (setting.Choices is null || setting.Choices.Contains(value.Text, StringComparer.Ordinal)),
        _ => false,
    };

    /// <summary>Checks a bounded stable preference identity.</summary>
    /// <param name="key">Candidate identity.</param>
    /// <returns>Whether it is suitable for keyed state and preferences.</returns>
    public static bool ValidKey(string? key) => key is { Length: > 0 and <= 128 }
        && key.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_');
}
