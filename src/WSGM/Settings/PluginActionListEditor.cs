using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using WSGM.Core;
using WSGM.Plugin.Sdk;

namespace WSGM.Settings;

/// <summary>One argument of one configured step, typed the way the plugin declared it.</summary>
public sealed class PluginArgumentRow : INotifyPropertyChanged
{
    private readonly PluginActionStep _step;
    private readonly PluginSetting _field;
    private readonly Action _changed;

    internal PluginArgumentRow(PluginActionStep step, PluginSetting field, Action changed)
    {
        _step = step;
        _field = field;
        _changed = changed;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the argument's label.</summary>
    public string Label => _field.Label;

    /// <summary>Gets whether this argument is a switch.</summary>
    public bool IsBoolean => _field.Kind == PluginSettingKind.Boolean;

    /// <summary>Gets whether this argument is chosen from a fixed list.</summary>
    public bool IsChoice => !IsBoolean && _field.Choices is { Count: > 0 };

    /// <summary>Gets whether this argument is typed as free text or a number.</summary>
    public bool IsText => !IsBoolean && !IsChoice;

    /// <summary>Gets the values this argument accepts, when it is a choice.</summary>
    public IReadOnlyList<string> Choices => _field.Choices ?? [];

    /// <summary>Gets or sets the boolean value.</summary>
    public bool BooleanValue
    {
        get => Current.Boolean ?? false;
        set => Write(new(Boolean: value), nameof(BooleanValue));
    }

    /// <summary>Gets or sets the text or chosen value.</summary>
    public string TextValue
    {
        get => Current.Text ?? Current.Number?.ToString(CultureInfo.InvariantCulture) ?? "";
        set => Write(
            _field.Kind == PluginSettingKind.Number
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                ? new PluginValue(Number: number)
                : new PluginValue(Text: value),
            nameof(TextValue));
    }

    /// <summary>Gets why this value is not acceptable, or an empty string when it is.</summary>
    public string ValidationText =>
        PluginConfigurationRules.Accepts(_field, Current)
            ? ""
            : $"{_field.Label} is outside the range or choices the plugin declared.";

    /// <summary>Gets whether this value would be refused.</summary>
    public bool HasValidationError => ValidationText.Length > 0;

    private PluginValue Current => _step.Arguments.TryGetValue(_field.Key, out PluginValue value)
        ? value
        : _field.Default;

    private void Write(PluginValue value, string name)
    {
        _step.Arguments[_field.Key] = value;
        PropertyChanged?.Invoke(this, new(name));
        PropertyChanged?.Invoke(this, new(nameof(ValidationText)));
        PropertyChanged?.Invoke(this, new(nameof(HasValidationError)));
        _changed();
    }
}

/// <summary>One configured step, with its arguments when the plugin that declares them is
/// running.</summary>
public sealed class PluginActionStepEditorRow : INotifyPropertyChanged
{
    private readonly Action _changed;

    internal PluginActionStepEditorRow(
        PluginActionStep step, SettingsViewModel.PluginActionOption? option, Action changed)
    {
        Step = step;
        _changed = changed;
        Arguments = [.. (option?.Action.Arguments ?? []).Select(
            field => new PluginArgumentRow(step, field, changed))];
        Available = option is not null;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    internal PluginActionStep Step { get; }

    /// <summary>Gets which plugin instance and action this step names.</summary>
    public string Title => $"{Step.Plugin?.PluginId} / {Step.Plugin?.InstanceId}: {Step.ActionId}";

    /// <summary>Gets whether the named plugin instance is running, so its arguments are known.</summary>
    public bool Available { get; }

    /// <summary>Gets the note shown when the plugin is not running.</summary>
    public string UnavailableText => Available
        ? ""
        : "This plugin is not running, so its values are kept as saved and cannot be edited here.";

    /// <summary>Gets the argument editors, empty when the plugin is not running.</summary>
    public IReadOnlyList<PluginArgumentRow> Arguments { get; }

    /// <summary>Gets a plain rendering of the saved arguments, for a step that cannot be edited.</summary>
    public string SavedArguments => Step.Arguments.Count == 0
        ? "no arguments"
        : string.Join(", ", Step.Arguments.Select(pair => $"{pair.Key}={Describe(pair.Value)}"));

    /// <summary>Gets or sets this step's own deadline in seconds.</summary>
    public int TimeoutSeconds
    {
        get => Step.TimeoutSeconds;
        set
        {
            if (Step.TimeoutSeconds == value) { return; }
            Step.TimeoutSeconds = Math.Clamp(value, 1, 120);
            PropertyChanged?.Invoke(this, new(nameof(TimeoutSeconds)));
            _changed();
        }
    }

    /// <summary>Gets whether any argument would be refused.</summary>
    public bool HasValidationError => Arguments.Any(argument => argument.HasValidationError);

    private static string Describe(PluginValue value) =>
        value.Text is { Length: > 0 } text ? text
        : value.Number is { } number ? number.ToString(CultureInfo.InvariantCulture)
        : value.Boolean is { } flag ? (flag ? "on" : "off")
        : "";
}

/// <summary>Edits one ordered list of plugin action steps.
///
/// Order is the whole point of a list here: the HDMI switch has to select this PC before the
/// television is told to turn on, or the television turns on showing the wrong input. So steps move
/// up and down rather than being a set.
///
/// A step whose plugin is not running keeps its saved values and cannot be edited. Its argument
/// schema comes from the plugin, and the host captures that schema before the plugin starts, so
/// there is nothing truthful to render for one that is not there. Deleting it is still allowed:
/// removing a step you no longer want must not require starting a plugin.</summary>
public sealed class PluginActionListEditor : INotifyPropertyChanged
{
    private readonly Action _changed;
    private IReadOnlyList<SettingsViewModel.PluginActionOption> _options = [];

    internal PluginActionListEditor(string title, Action changed)
    {
        Title = title;
        _changed = changed;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the session event this list runs at.</summary>
    public string Title { get; }

    /// <summary>Gets the configured steps, in the order they run.</summary>
    public ObservableCollection<PluginActionStepEditorRow> Rows { get; } = [];

    /// <summary>Gets the actions that can be added, from the running plugins.</summary>
    public ObservableCollection<SettingsViewModel.PluginActionOption> Choices { get; } = [];

    /// <summary>Gets whether anything can be added right now.</summary>
    public bool CanAdd => Choices.Count > 0;

    /// <summary>Gets the note shown when no plugin is running to add an action from.</summary>
    public string AddHintText => CanAdd
        ? ""
        : "No running plugin declares an action. Open Settings from the WSGM tray icon with the plugin enabled to add one.";

    /// <summary>Gets or sets which action the Add button would append.</summary>
    public int ChoiceIndex
    {
        get => _choiceIndex;
        set { _choiceIndex = value; PropertyChanged?.Invoke(this, new(nameof(ChoiceIndex))); }
    }

    private int _choiceIndex = -1;

    /// <summary>Gets whether any step's arguments would be refused.</summary>
    public bool HasValidationError => Rows.Any(row => row.HasValidationError);

    /// <summary>Rebuilds the rows from a saved list and the currently running plugins.</summary>
    /// <param name="steps">The saved steps.</param>
    /// <param name="options">Actions the running plugins declare.</param>
    internal void Load(
        IReadOnlyList<PluginActionStep> steps,
        IReadOnlyList<SettingsViewModel.PluginActionOption> options)
    {
        _options = options;
        Choices.Clear();
        foreach (SettingsViewModel.PluginActionOption option in options) { Choices.Add(option); }
        ChoiceIndex = Choices.Count > 0 ? 0 : -1;

        Rows.Clear();
        foreach (PluginActionStep step in steps) { Rows.Add(Build(step)); }
        RaiseState();
    }

    /// <summary>The steps this list currently describes.</summary>
    /// <returns>A fresh list in run order.</returns>
    internal List<PluginActionStep> Build() => [.. Rows.Select(row => row.Step)];

    /// <summary>Appends the selected action, with every argument at its declared default.</summary>
    internal void Add()
    {
        if (ChoiceIndex < 0 || ChoiceIndex >= Choices.Count) { return; }
        SettingsViewModel.PluginActionOption option = Choices[ChoiceIndex];
        PluginActionStep step = new()
        {
            Plugin = option.Identity,
            ActionId = option.Action.Id,
            Arguments = option.Action.Arguments.ToDictionary(
                field => field.Key, field => field.Default),
        };
        Rows.Add(Build(step));
        RaiseState();
        _changed();
    }

    /// <summary>Removes one step.</summary>
    /// <param name="row">The step to remove.</param>
    internal void Remove(PluginActionStepEditorRow row)
    {
        if (!Rows.Remove(row)) { return; }
        RaiseState();
        _changed();
    }

    /// <summary>Moves one step earlier or later in the run order.</summary>
    /// <param name="row">The step to move.</param>
    /// <param name="delta">-1 for earlier, +1 for later.</param>
    internal void Move(PluginActionStepEditorRow row, int delta)
    {
        int index = Rows.IndexOf(row);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= Rows.Count) { return; }
        Rows.Move(index, target);
        _changed();
    }

    private PluginActionStepEditorRow Build(PluginActionStep step) =>
        new(step,
            _options.FirstOrDefault(option =>
                step.Plugin is { } plugin && option.Identity == plugin && option.Action.Id == step.ActionId),
            OnRowChanged);

    private void OnRowChanged()
    {
        PropertyChanged?.Invoke(this, new(nameof(HasValidationError)));
        _changed();
    }

    private void RaiseState()
    {
        PropertyChanged?.Invoke(this, new(nameof(CanAdd)));
        PropertyChanged?.Invoke(this, new(nameof(AddHintText)));
        PropertyChanged?.Invoke(this, new(nameof(HasValidationError)));
    }
}
