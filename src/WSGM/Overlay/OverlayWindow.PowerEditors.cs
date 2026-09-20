using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using FluentAvalonia.UI.Controls;
using WSGM.Core;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    private readonly Dictionary<PowerTimeoutKind, ComboBox> _timeoutEditors = [];
    private bool _refreshingPowerEditors;
    private ComboBox? _wakeEditor;

    internal event Action<ManualWakeMode>? KeepAwakeSelected;
    internal event Action<PowerTimeoutKind, int>? PowerTimeoutSelected;

    private void InitializePowerEditors()
    {
        _wakeEditor = new ComboBox { MinWidth = 180, HorizontalAlignment = HorizontalAlignment.Stretch };
        _wakeEditor.ItemsSource = new[]
        {
            new WakeChoice(ManualWakeMode.Off, "Automatic"),
            new WakeChoice(ManualWakeMode.Standby, "Prevent standby"),
            new WakeChoice(ManualWakeMode.StandbyAndDisplay, "Keep screen on")
        };
        ObservePowerChoice<WakeChoice>(_wakeEditor, selected => KeepAwakeSelected?.Invoke(selected.Mode));
        AutomationProperties.SetName(_wakeEditor, "Keep awake");
        KeepAwakeHost.Children.Add(new FASettingsExpanderItem
        {
            Content = "Keep awake", Description = "Choose the manual wake hold", Footer = _wakeEditor,
            HorizontalAlignment = HorizontalAlignment.Stretch, Focusable = false
        });

        foreach (var kind in Enum.GetValues<PowerTimeoutKind>())
        {
            var title = kind switch
            {
                PowerTimeoutKind.DisplayDc => "Screen off · battery",
                PowerTimeoutKind.DisplayAc => "Screen off · plugged in",
                PowerTimeoutKind.SleepDc => "Standby · battery",
                _ => "Standby · plugged in"
            };
            var editor = new ComboBox { MinWidth = 150, HorizontalAlignment = HorizontalAlignment.Stretch };
            _timeoutEditors.Add(kind, editor);
            AutomationProperties.SetName(editor, title);
            ObservePowerChoice<TimeoutChoice>(editor, selected => PowerTimeoutSelected?.Invoke(kind, selected.Seconds));
            PowerTimeoutEditors.Children.Add(new FASettingsExpanderItem
            {
                Content = title, Footer = editor, HorizontalAlignment = HorizontalAlignment.Stretch,
                Focusable = false
            });
        }

        if (DataContext is OverlayViewModel vm)
        {
            vm.PropertyChanged += OnPowerEditorStateChanged;
            RefreshPowerEditors(vm);
        }
    }

    private void OnPowerEditorStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is OverlayViewModel vm && e.PropertyName is nameof(OverlayViewModel.KeepAwakeManualMode)
                or nameof(OverlayViewModel.KeepAwakeDescription) or nameof(OverlayViewModel.PowerTimeoutValues)
                or nameof(OverlayViewModel.PowerTimeoutMinimums))
        {
            RefreshPowerEditors(vm);
        }
    }

    private void ObservePowerChoice<T>(ComboBox editor, Action<T> selected) where T : class
    {
        object? committed = null;
        var open = false;
        editor.DropDownOpened += (_, _) => open = true;
        editor.DropDownClosed += (_, _) =>
        {
            open = false;
            CommitChoice();
            if (DataContext is OverlayViewModel vm)
            {
                RefreshPowerEditors(vm);
            }
        };
        editor.SelectionChanged += (_, _) =>
        {
            if (_refreshingPowerEditors)
            {
                committed = editor.SelectedItem;
            }
            else if (!open && !editor.IsDropDownOpen)
            {
                CommitChoice();
            }
        };

        void CommitChoice()
        {
            if (_refreshingPowerEditors || !editor.IsEnabled || editor.SelectedItem is not T choice
                || Equals(committed, choice))
            {
                return;
            }

            committed = choice;
            selected(choice);
        }
    }

    internal void RefreshPowerEditors(OverlayViewModel vm)
    {
        _refreshingPowerEditors = true;
        try
        {
            if (_wakeEditor is { IsDropDownOpen: false })
            {
                _wakeEditor.SelectedItem = _wakeEditor.Items.OfType<WakeChoice>()
                    .FirstOrDefault(choice => choice.Mode == vm.KeepAwakeManualMode);
            }

            KeepAwakeDetail.Text = vm.KeepAwakeDescription;
            foreach (var (kind, editor) in _timeoutEditors)
            {
                // A readback must not replace the popup's draft or its item objects.
                if (editor.IsDropDownOpen)
                {
                    continue;
                }

                var current = vm.PowerTimeoutValues.GetValueOrDefault(kind);
                var minimum = vm.PowerTimeoutMinimums.GetValueOrDefault(kind);
                var values = current is null
                    ? []
                    : DisplayTimeoutPolicy.Choices(current.Value, minimum)
                        .Select(seconds => new TimeoutChoice(seconds)).ToArray();
                if (!editor.Items.OfType<TimeoutChoice>().SequenceEqual(values))
                {
                    editor.ItemsSource = values;
                }

                editor.SelectedItem = values.FirstOrDefault(choice => choice.Seconds == current);
                editor.IsEnabled = current is not null;
            }
        }
        finally
        {
            _refreshingPowerEditors = false;
        }
    }

    private sealed record WakeChoice(ManualWakeMode Mode, string Label)
    {
        public override string ToString()
        {
            return Label;
        }
    }

    private sealed record TimeoutChoice(int Seconds)
    {
        public override string ToString()
        {
            return PowerTimeouts.Describe(Seconds);
        }
    }
}
