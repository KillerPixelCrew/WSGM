using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Sdk;

namespace WSGM.Settings;

/// <summary>One driver-declared display preference in a layout draft; editing never writes the driver.</summary>
public sealed class DisplayGpuEditorRow : ObservableObject
{
    private readonly Action _changed;
    private bool _useSetting;
    private PluginValue _value;

    internal DisplayGpuEditorRow(DisplayGpuCapability capability, DisplayGpuPreference? saved, Action changed)
    {
        Capability = capability;
        _changed = changed;
        _value = saved?.Value ?? capability.ObservedValue ?? (Descriptor.SupportsWrite ? DefaultValue : default);
        _useSetting = saved is not null;
        Choices =
        [
            .. Descriptor.Choices.Select(choice => new DisplayGpuChoice(choice.Value,
                CapabilityDisplayLabels.For(choice.Display, choice.Value)))
        ];
    }

    internal DisplayGpuCapability Capability { get; }
    private CapabilityDescriptor Descriptor => Capability.Descriptor!;

    /// <summary>Gets the label declared by the admitted driver.</summary>
    public string Title => CapabilityDisplayLabels.For(Descriptor.Display, Descriptor.CapabilityId);

    /// <summary>Gets the exact supported choices declared by the driver.</summary>
    public IReadOnlyList<DisplayGpuChoice> Choices { get; }

    /// <summary>Gets whether the driver publishes a writable shape this draft can edit.</summary>
    public bool CanEdit => Descriptor.SupportsWrite && (IsBoolean || IsChoice || IsInteger);

    /// <summary>Gets whether this setting is a boolean.</summary>
    public bool IsBoolean => Descriptor.ValueKind == CapabilityValueKind.Boolean;

    /// <summary>Gets whether this setting has driver-declared named choices.</summary>
    public bool IsChoice => Descriptor.ValueKind == CapabilityValueKind.Choice && Choices.Count > 0;

    /// <summary>Gets whether this setting has a driver-declared numeric range.</summary>
    public bool IsInteger => Descriptor.ValueKind == CapabilityValueKind.Integer
                             && Descriptor.Minimum is not null && Descriptor.Maximum >= Descriptor.Minimum;

    /// <summary>Gets whether this driver capability is read-only.</summary>
    public bool IsReadOnly => !CanEdit;

    /// <summary>Gets the driver's numeric minimum.</summary>
    public int Minimum => Descriptor.Minimum ?? 0;

    /// <summary>Gets the driver's numeric maximum.</summary>
    public int Maximum => Descriptor.Maximum ?? 0;

    /// <summary>Gets the driver's numeric step.</summary>
    public int Step => Math.Max(1, Descriptor.Step ?? 1);

    /// <summary>Gets or sets whether this layout applies an explicit value for this control.</summary>
    public bool UseSetting
    {
        get => _useSetting;
        set
        {
            if (SetFieldIfChanged(ref _useSetting, value, nameof(UseSetting)))
            {
                _changed();
            }
        }
    }

    /// <summary>Gets or sets the boolean draft.</summary>
    public bool BooleanValue
    {
        get => _value.Boolean ?? false;
        set => SetValue(new PluginValue(value));
    }

    /// <summary>Gets or sets the integer draft within the driver's range.</summary>
    public int IntegerValue
    {
        get => _value.Number is { } number ? (int)number : Minimum;
        set => SetValue(new PluginValue(Number: value));
    }

    /// <summary>Gets or sets one of the driver's named choices; null binding feedback is ignored.</summary>
    public DisplayGpuChoice? Choice
    {
        get => Choices.FirstOrDefault(choice => choice.Value == _value.Text);
        set
        {
            if (value is not null)
            {
                SetValue(new PluginValue(Text: value.Value));
            }
        }
    }

    /// <summary>Gets a supplied value or explains that this driver did not supply one.</summary>
    public string ValueText => _value.Boolean is { } boolean ? boolean ? "On" : "Off"
        : _value.Number is { } number ? number.ToString(CultureInfo.CurrentCulture)
        : _value.Text is { } text ? Choices.FirstOrDefault(choice => choice.Value == text)?.Label ?? text
        : "Value not reported";

    /// <summary>Gets whether a saved choice is currently outside the driver's supported set.</summary>
    public bool HasUnavailableChoice => Descriptor.ValueKind == CapabilityValueKind.Choice
                                        && _value.Text is not null && Choice is null;

    /// <summary>Gets the explanation for a retained unsupported saved choice.</summary>
    public string SavedChoiceHint => HasUnavailableChoice ? $"Saved value kept: {ValueText}" : "";

    private PluginValue DefaultValue => Descriptor.ValueKind switch
    {
        CapabilityValueKind.Boolean => new PluginValue(false),
        CapabilityValueKind.Integer when Descriptor.Minimum is { } minimum => new PluginValue(Number: minimum),
        CapabilityValueKind.Choice when Descriptor.Choices.FirstOrDefault() is { } choice =>
            new PluginValue(Text: choice.Value),
        _ => default
    };

    private void SetValue(PluginValue value)
    {
        if (_value == value)
        {
            return;
        }

        _value = value;
        foreach (var name in new[]
                 {
                     nameof(BooleanValue), nameof(IntegerValue), nameof(Choice), nameof(ValueText),
                     nameof(HasUnavailableChoice), nameof(SavedChoiceHint)
                 })
        {
            Raise(name);
        }

        _changed();
    }

    internal DisplayGpuPreference? Build()
    {
        return _useSetting && _value.IsValid
            ? new DisplayGpuPreference
            {
                Target = Capability.Target,
                PluginId = Capability.PluginId,
                CapabilityId = Descriptor.CapabilityId,
                InstanceId = Descriptor.InstanceId,
                Value = _value
            }
            : null;
    }
}

/// <summary>One exact driver-declared value with its display label.</summary>
/// <param name="Value">Machine value, unchanged from the descriptor.</param>
/// <param name="Label">Plain human-readable label from the same descriptor.</param>
public sealed record DisplayGpuChoice(string Value, string Label)
{
    /// <inheritdoc />
    public override string ToString()
    {
        return Label;
    }
}
