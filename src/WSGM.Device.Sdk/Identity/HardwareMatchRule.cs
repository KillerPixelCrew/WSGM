using System;
using System.Collections.Generic;

namespace WSGM.Device.Sdk.Identity;

/// <summary>
///     One way to recognise a machine from SMBIOS and processor identity, declared as data so that a
///     host can match hardware without loading plugin code.
/// </summary>
/// <remarks>
///     Every field that is set must match, compared case-insensitively after trimming. A fallback rule
///     is a vendor-wide default and ranks below every exact rule. The same rule shape is used by a
///     package manifest's <c>hardware</c> list, by WSGM setup and by Device Lab's knowledge base, so
///     there is one identity language. A match is a proposal: the plugin's
///     <c>DetectAsync</c> still confirms the machine once the plugin is loaded.
/// </remarks>
public sealed record HardwareMatchRule
{
    /// <summary>Largest number of rules one manifest may declare.</summary>
    public const int MaxRules = 32;

    /// <summary>Longest accepted value of any rule field.</summary>
    public const int MaxFieldLength = 128;

    /// <summary>Baseboard manufacturer (SMBIOS type 2).</summary>
    public string? BaseboardManufacturer { get; init; }

    /// <summary>Baseboard product (SMBIOS type 2).</summary>
    public string? BaseboardProduct { get; init; }

    /// <summary>System product name, for example what Win32_ComputerSystem reports as Model.</summary>
    public string? SystemModel { get; init; }

    /// <summary>System SKU number.</summary>
    public string? SystemSku { get; init; }

    /// <summary>Processor brand string, trimmed.</summary>
    public string? ProcessorName { get; init; }

    /// <summary>A substring the processor brand string must contain.</summary>
    public string? ProcessorNameContains { get; init; }

    /// <summary>Baseboard version.</summary>
    public string? BaseboardVersion { get; init; }

    /// <summary>Whether the rule is a vendor-wide default rather than an exact model.</summary>
    public bool Fallback { get; init; }

    internal IEnumerable<(string Name, string? Value)> Fields()
    {
        yield return ("baseboardManufacturer", BaseboardManufacturer);
        yield return ("baseboardProduct", BaseboardProduct);
        yield return ("systemModel", SystemModel);
        yield return ("systemSku", SystemSku);
        yield return ("processorName", ProcessorName);
        yield return ("processorNameContains", ProcessorNameContains);
        yield return ("baseboardVersion", BaseboardVersion);
    }
}

/// <summary>The rule that matched, and why.</summary>
/// <param name="Rule">The matching rule.</param>
/// <param name="Index">The rule's position in the list it came from.</param>
/// <param name="Explanations">One line per compared field.</param>
public sealed record HardwareMatch(HardwareMatchRule Rule, int Index, IReadOnlyList<string> Explanations)
{
    /// <summary>Whether only a vendor-wide default matched.</summary>
    public bool Fallback => Rule.Fallback;
}

/// <summary>Deterministic matching of <see cref="HardwareMatchRule" /> lists against a machine.</summary>
public static class HardwareMatcher
{
    /// <summary>Returns the best matching rule: the first exact rule, else the first fallback.</summary>
    /// <param name="rules">Rules in declaration order.</param>
    /// <param name="identity">The machine's observed identity.</param>
    /// <returns>The match, or null when no rule matches. A rule with no field set never matches.</returns>
    public static HardwareMatch? Match(IReadOnlyList<HardwareMatchRule> rules, DeviceIdentitySnapshot identity)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(identity);
        HardwareMatch? fallback = null;
        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules[index];
            List<string> explanations = [];
            if (!Matches(rule, identity, explanations))
            {
                continue;
            }

            HardwareMatch match = new(rule, index, explanations);
            if (!rule.Fallback)
            {
                return match;
            }

            fallback ??= match;
        }

        return fallback;
    }

    /// <summary>Whether every field the rule sets matches the identity.</summary>
    /// <param name="rule">Rule to test.</param>
    /// <param name="identity">Observed identity.</param>
    /// <param name="explanations">Receives one line per matched field.</param>
    /// <returns>True when the rule sets at least one field and all of them match.</returns>
    public static bool Matches(HardwareMatchRule rule, DeviceIdentitySnapshot identity, List<string> explanations)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(explanations);
        var any = false;
        foreach (var (name, value) in rule.Fields())
        {
            any |= value is not null;
        }

        return any
               && Field("baseboard manufacturer", rule.BaseboardManufacturer, identity.BaseboardManufacturer,
                   explanations)
               && Field("baseboard product", rule.BaseboardProduct, identity.BaseboardProduct, explanations)
               && Field("system model", rule.SystemModel, identity.SystemProduct, explanations)
               && Field("system SKU", rule.SystemSku, identity.SystemSku, explanations)
               && Field("processor", rule.ProcessorName, identity.ProcessorName, explanations)
               && Contains("processor", rule.ProcessorNameContains, identity.ProcessorName, explanations)
               && Field("baseboard version", rule.BaseboardVersion, identity.BaseboardVersion, explanations);
    }

    private static bool Contains(string label, string? expected, string? observed, List<string> explanations)
    {
        if (expected is null)
        {
            return true;
        }

        if (observed?.Contains(expected.Trim(), StringComparison.OrdinalIgnoreCase) != true)
        {
            return false;
        }

        explanations.Add($"{label} contains '{expected}'.");
        return true;
    }

    private static bool Field(string label, string? expected, string? observed, List<string> explanations)
    {
        if (expected is null)
        {
            return true;
        }

        if (!string.Equals(expected.Trim(), observed?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        explanations.Add($"{label} matched '{expected}'.");
        return true;
    }
}
