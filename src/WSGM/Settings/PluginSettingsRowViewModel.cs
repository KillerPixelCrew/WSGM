using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;

namespace WSGM.Settings;

/// <summary>One editable plugin setting on the plugin settings page.</summary>
/// <remarks>
/// One row type for every value kind rather than a type per kind, because the page templates by
/// visibility: each control binds to the one property its kind uses and shows itself through the
/// matching <c>Is…</c> flag. A template selector would need runtime type inspection, which this
/// NativeAOT surface does not have.
/// <para>
/// The row never writes to the device or to configuration. It reports an edit and the owning page
/// decides what to do with it, which keeps the Settings/overlay boundary intact — a setting
/// configures how the plugin behaves and WSGM keeps the value.
/// </para>
/// </remarks>
public sealed class PluginSettingRowViewModel : INotifyPropertyChanged
{
    private readonly PluginSettingDescriptor _descriptor;
    private bool _booleanValue;
    private int _integerValue;
    private string _textValue = string.Empty;
    private CapabilityChoice? _selectedChoice;

    /// <summary>Creates a row for one declared setting.</summary>
    /// <param name="descriptor">The plugin's declaration.</param>
    /// <param name="value">The reconciled value to show.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public PluginSettingRowViewModel(PluginSettingDescriptor descriptor, CapabilityValue value)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(value);
        _descriptor = descriptor;
        Choices = descriptor.Choices;
        Adopt(value);
    }

    /// <summary>Raised after an edit, carrying the setting id and its new value.</summary>
    public event Action<string, CapabilityValue>? Edited;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The declared setting this row edits.</summary>
    public string SettingId => _descriptor.SettingId;

    /// <summary>Label text. A custom label is the plugin's own untrusted plain text.</summary>
    /// <remarks>
    /// Rendered as text and never as markup. <see cref="CapabilityDisplay"/> already bounds and
    /// validates it; this only chooses between the localized key and the custom string.
    /// </remarks>
    public string Label => _descriptor.Display.Key is DisplayKey.Custom
        ? _descriptor.Display.CustomLabel ?? _descriptor.SettingId
        : _descriptor.Display.Key.ToString();

    // No description property, deliberately: CapabilityDisplay carries a label and nothing else, and
    // adding a second free-text field to the SDK to fill a row would widen the untrusted-text
    // surface for presentation's sake. A setting that needs explaining needs a clearer label.

    /// <summary>Legal options for a choice setting.</summary>
    public IReadOnlyList<CapabilityChoice> Choices { get; }

    /// <summary>Whether this row draws a toggle.</summary>
    public bool IsToggle => _descriptor.ValueKind is CapabilityValueKind.Boolean;

    /// <summary>Whether this row draws a slider.</summary>
    public bool IsRange => _descriptor.ValueKind is CapabilityValueKind.Integer;

    /// <summary>Whether this row draws a choice list.</summary>
    public bool IsChoice => _descriptor.ValueKind is CapabilityValueKind.Choice;

    /// <summary>Whether this row draws a text box.</summary>
    public bool IsText => _descriptor.ValueKind is CapabilityValueKind.Text;

    /// <summary>Lowest legal value for a range setting.</summary>
    public int Minimum => _descriptor.Minimum ?? 0;

    /// <summary>Highest legal value for a range setting.</summary>
    public int Maximum => _descriptor.Maximum ?? 0;

    /// <summary>Step between legal values for a range setting.</summary>
    public int Step => _descriptor.Step ?? 1;

    /// <summary>Longest accepted text, for a text setting.</summary>
    public int MaximumLength => _descriptor.MaximumLength ?? PluginSettingDescriptor.MaxTextLength;

    /// <summary>Gets or sets the value of a boolean setting.</summary>
    public bool BooleanValue
    {
        get => _booleanValue;
        set
        {
            if (_booleanValue == value)
            {
                return;
            }

            _booleanValue = value;
            Raise(nameof(BooleanValue));
            Publish(new CapabilityValue
            {
                Kind = CapabilityValueKind.Boolean,
                BooleanValue = value,
            });
        }
    }

    /// <summary>Gets or sets the value of an integer setting.</summary>
    public int IntegerValue
    {
        get => _integerValue;
        set
        {
            // Clamped here as well as validated on commit. A slider bound to a stale range can
            // otherwise report a value the plugin already refuses, and the user sees a control that
            // moves and then springs back with no explanation.
            int clamped = _descriptor.Minimum is { } min && _descriptor.Maximum is { } max
                ? Math.Clamp(value, min, max)
                : value;
            if (_integerValue == clamped)
            {
                return;
            }

            _integerValue = clamped;
            Raise(nameof(IntegerValue));
            Publish(new CapabilityValue
            {
                Kind = CapabilityValueKind.Integer,
                IntegerValue = clamped,
            });
        }
    }

    /// <summary>Gets or sets the value of a text setting.</summary>
    public string TextValue
    {
        get => _textValue;
        set
        {
            string bounded = value ?? string.Empty;
            if (bounded.Length > MaximumLength)
            {
                bounded = bounded[..MaximumLength];
            }

            if (string.Equals(_textValue, bounded, StringComparison.Ordinal))
            {
                return;
            }

            _textValue = bounded;
            Raise(nameof(TextValue));
            Publish(new CapabilityValue
            {
                Kind = CapabilityValueKind.Text,
                TextValue = bounded,
            });
        }
    }

    /// <summary>Gets or sets the selected option of a choice setting.</summary>
    public CapabilityChoice? SelectedChoice
    {
        get => _selectedChoice;
        set
        {
            if (_selectedChoice == value)
            {
                return;
            }

            _selectedChoice = value;
            Raise(nameof(SelectedChoice));
            if (value is { } choice)
            {
                Publish(new CapabilityValue
                {
                    Kind = CapabilityValueKind.Choice,
                    TextValue = choice.Value,
                });
            }
        }
    }

    /// <summary>Replaces the shown value without reporting an edit.</summary>
    /// <param name="value">The value to adopt.</param>
    /// <remarks>
    /// Used when the page refreshes from configuration. It must not raise <see cref="Edited"/>, or a
    /// refresh would write the value it just read straight back and every reload would look like a
    /// user edit in the log.
    /// </remarks>
    public void Adopt(CapabilityValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _booleanValue = value.BooleanValue ?? false;
        _integerValue = value.IntegerValue ?? Minimum;
        _textValue = value.TextValue ?? string.Empty;
        _selectedChoice = Choices.FirstOrDefault(
            choice => string.Equals(choice.Value, value.TextValue, StringComparison.Ordinal));
        Raise(nameof(BooleanValue));
        Raise(nameof(IntegerValue));
        Raise(nameof(TextValue));
        Raise(nameof(SelectedChoice));
    }

    private void Publish(CapabilityValue value) => Edited?.Invoke(SettingId, value);

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One titled group of plugin setting rows, which is also a focus group.</summary>
public sealed class PluginSettingSectionViewModel
{
    /// <summary>Creates a section.</summary>
    /// <param name="sectionId">Stable key that focus and scroll restoration use.</param>
    /// <param name="title">The title to draw.</param>
    /// <param name="rows">The rows, already in render order.</param>
    public PluginSettingSectionViewModel(
        string sectionId,
        string title,
        IReadOnlyList<PluginSettingRowViewModel> rows)
    {
        SectionId = sectionId;
        Title = title;
        Rows = rows;
    }

    /// <summary>
    /// Stable key. Focus and scroll restoration key off this, so it survives a refresh rather than
    /// being an index into a list that changed.
    /// </summary>
    public string SectionId { get; }

    /// <summary>The title to draw.</summary>
    public string Title { get; }

    /// <summary>The rows, in render order.</summary>
    public IReadOnlyList<PluginSettingRowViewModel> Rows { get; }
}
