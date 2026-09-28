using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace WSGM.Core;

/// <summary>One of a theme's patches: a named choice between sets of blocks.</summary>
/// <remarks>
///     Mirrors <c>ThemePatch</c> in <c>css_themepatch.py</c> (b1bc683): an option's blocks are
///     enabled while the option is chosen, a component's block while its option is chosen, and the
///     rules a value has to satisfy (<c>check_value</c>) are the same, including a checkbox needing
///     exactly Yes and No.
/// </remarks>
public sealed class ThemePatch
{
    private readonly List<string> _optionOrder = [];
    private readonly Dictionary<string, List<ThemeInject>> _options = new(StringComparer.Ordinal);

    /// <summary>Creates the patch from its manifest.</summary>
    /// <param name="manifest">The manifest entry.</param>
    /// <param name="theme">The theme the patch belongs to.</param>
    public ThemePatch(ThemePatchManifest manifest, InstalledTheme theme)
    {
        Name = manifest.Name;
        Default = manifest.Default;
        Type = manifest.Type;
        Value = manifest.Default;
        foreach (var (option, injects) in manifest.Options)
        {
            var blocks = injects.Select(inject => theme.CreateInject(inject, $"{Name}|{option}")).ToList();
            _options[option] = blocks;
            _optionOrder.Add(option);
            Injects.AddRange(blocks);
        }

        foreach (var component in manifest.Components)
        {
            var built = new ThemePatchComponent(component,
                ThemeTargets.Expand(component.Tabs, theme.TabMappings), theme.ThemesRoot, $"{theme.Name}|{Name}");
            Components.Add(built);
            Injects.Add(built.Inject);
            _options[built.On].Add(built.Inject);
        }

        CheckValue();
    }

    /// <summary>The patch's label.</summary>
    public string Name { get; }

    /// <summary>The option chosen until the user picks another.</summary>
    public string Default { get; }

    /// <summary><c>dropdown</c>, <c>checkbox</c>, <c>slider</c> or <c>none</c>.</summary>
    public string Type { get; private set; }

    /// <summary>The chosen option.</summary>
    public string Value { get; private set; }

    /// <summary>The options in manifest order.</summary>
    public IReadOnlyList<string> Options => _optionOrder;

    /// <summary>Every block the patch owns, in order.</summary>
    public List<ThemeInject> Injects { get; } = [];

    /// <summary>The colours and images picked under the patch.</summary>
    public List<ThemePatchComponent> Components { get; } = [];

    /// <summary>Chooses an option, when it is one of the patch's.</summary>
    /// <param name="value">The option.</param>
    /// <returns>Whether it was one.</returns>
    public bool TrySetValue(string value)
    {
        if (!_options.ContainsKey(value))
        {
            return false;
        }

        Value = value;
        return true;
    }

    /// <summary>Reads a saved value: the option, or the option with its components' values.</summary>
    /// <param name="saved">The saved value.</param>
    public void SetSavedValue(JsonNode? saved)
    {
        if (saved is JsonValue text && text.TryGetValue(out string? option))
        {
            Value = option ?? Value;
            return;
        }

        if (saved is not JsonObject savedObject)
        {
            return;
        }

        if (savedObject["value"] is JsonValue value && value.TryGetValue(out string? chosen))
        {
            Value = chosen ?? Value;
        }

        if (savedObject["components"] is not JsonObject components)
        {
            return;
        }

        foreach (var component in Components)
        {
            if (components[component.Name] is JsonValue componentValue
                && componentValue.TryGetValue(out string? picked))
            {
                component.Value = picked ?? component.Value;
            }
        }
    }

    /// <summary>The value to save: the option, or the option with its components' values.</summary>
    public JsonNode SavedValue()
    {
        if (Components.Count == 0)
        {
            return JsonValue.Create(Value);
        }

        JsonObject components = [];
        foreach (var component in Components)
        {
            components[component.Name] = component.Value;
        }

        return new JsonObject { ["value"] = Value, ["components"] = components };
    }

    /// <summary>Brings the value and the type back within what the patch offers.</summary>
    public void CheckValue()
    {
        if (!_options.ContainsKey(Value))
        {
            Value = Default;
        }

        if (Type is not ("dropdown" or "checkbox" or "slider" or "none"))
        {
            Type = "dropdown";
        }

        if (Type == "checkbox" && !(_options.ContainsKey("No") && _options.ContainsKey("Yes")))
        {
            Type = "dropdown";
        }
    }

    /// <summary>Marks the chosen option's blocks enabled, and every other block disabled.</summary>
    public void Apply()
    {
        CheckValue();
        foreach (var inject in Injects)
        {
            inject.Enabled = false;
        }

        foreach (var inject in _options[Value])
        {
            inject.Enabled = true;
        }
    }

    /// <summary>The snapshot the UI reads.</summary>
    public ThemePatchSnapshot Snapshot()
    {
        return new ThemePatchSnapshot(
            Name, Default, Value, [.. _optionOrder], Type, [.. Components.Select(component => component.Snapshot())]);
    }
}

/// <summary>What the UI shows of a patch.</summary>
/// <param name="Name">Its label.</param>
/// <param name="Default">The default option.</param>
/// <param name="Value">The chosen option.</param>
/// <param name="Options">The options in order.</param>
/// <param name="Type"><c>dropdown</c>, <c>checkbox</c>, <c>slider</c> or <c>none</c>.</param>
/// <param name="Components">The colours and images under it.</param>
public sealed record ThemePatchSnapshot(
    string Name,
    string Default,
    string Value,
    IReadOnlyList<string> Options,
    string Type,
    IReadOnlyList<ThemeComponentSnapshot> Components);
