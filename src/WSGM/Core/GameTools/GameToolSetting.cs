using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace WSGM.Core;

/// <summary>A setting mapped onto an existing key of the tool's configuration file.</summary>
internal sealed record GameToolSetting
{
    /// <summary>The setting identity.</summary>
    public string Id { get; init; } = "";

    /// <summary>The label shown to the user.</summary>
    public string Label { get; init; } = "";

    /// <summary>A short explanation.</summary>
    public string Description { get; init; } = "";

    /// <summary>The INI section, matched case-insensitively.</summary>
    public string Section { get; init; } = "";

    /// <summary>The INI key inside that section.</summary>
    public string Key { get; init; } = "";

    /// <summary>The default option value.</summary>
    public string Default { get; init; } = "";

    public string Kind { get; init; } = "choice";
    public string Scope { get; init; } = "game";
    public string Group { get; init; } = "";
    public string ShortcutDefault { get; init; } = "";
    public bool CreateMissing { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public double Step { get; init; } = 1;
    public string Pattern { get; init; } = "";
    public string HotkeyFormat { get; init; } = "";
    public bool OmitDefault { get; init; }
    public bool Integer { get; init; }
    public string UnavailableReason { get; init; } = "";

    internal bool Accepts(string value)
    {
        if (value.Length > 1024 || value.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            return false;
        }

        return Kind switch
        {
            "number" => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                        && double.IsFinite(number) && (Minimum is null || number >= Minimum)
                        && (Maximum is null || number <= Maximum) && (!Integer || number == Math.Truncate(number)),
            "text" => Pattern.Length == 0 || Regex.IsMatch(value, Pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
            "hotkey" => GameToolHotkeys.TryParse(value, HotkeyFormat, out _),
            _ => Options.Any(option => option.Value == value)
        };
    }

    internal string Raw(string value) => Kind == "choice" ? Options.First(option => option.Value == value).Ini : value;

    internal string? FromRaw(string raw) => Kind == "choice"
        ? Options.FirstOrDefault(option => option.Ini.Equals(raw, StringComparison.OrdinalIgnoreCase))?.Value
        : Accepts(raw) ? raw : null;

    /// <summary>The selectable options.</summary>
    public GameToolSettingOption[] Options { get; init; } = [];

    /// <summary>Notes keyed by lowercase GPU vendor (<c>nvidia</c>, <c>amd</c>, <c>intel</c>).</summary>
    public Dictionary<string, string> VendorNotes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
