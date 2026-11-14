using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     The Power destination's value editors: the manual Keep awake hold on the Wake page and the
///     idle timeouts on their own page, both following the sheet's view model.
/// </summary>
/// <remarks>
///     The editors report the user's choice and never write Windows themselves. A readback that
///     arrives while a dropdown is open leaves that popup's draft and items alone.
/// </remarks>
internal sealed class OverlayPowerEditors
{
    private readonly Dictionary<PowerTimeoutKind, DeviceSettingRow> _hibernateRows = [];
    private readonly TextBlock _keepAwakeDetail;
    private readonly Dictionary<PowerTimeoutKind, ComboBox> _timeoutEditors = [];
    private readonly OverlayViewModel? _viewModel;
    private readonly ComboBox _wakeEditor;
    private readonly ComboBox _powerButtonEditor;
    private readonly Panel _powerButtonSection;
    private bool _refreshing;

    /// <summary>Builds the editors into their pages and follows the view model.</summary>
    /// <param name="viewModel">The sheet's state, or null on a surface without one.</param>
    /// <param name="keepAwakeHost">The Wake page panel that receives the Keep awake row.</param>
    /// <param name="keepAwakeDetail">The Wake page caption that describes the current hold.</param>
    /// <param name="timeoutHost">The Idle timeouts page panel that receives one row per timeout.</param>
    /// <param name="powerButtonSection">The Power page section shown only while the power button is handled.</param>
    /// <param name="powerButtonHost">The panel in that section that receives the short-press row.</param>
    internal OverlayPowerEditors(OverlayViewModel? viewModel, Panel keepAwakeHost, TextBlock keepAwakeDetail,
        Panel timeoutHost, Panel powerButtonSection, Panel powerButtonHost)
    {
        _powerButtonSection = powerButtonSection;
        _powerButtonEditor = new ComboBox { MinWidth = 180, HorizontalAlignment = HorizontalAlignment.Stretch };
        _powerButtonEditor.ItemsSource = PowerButtonSetting.Choices
            .Select(value => new PowerButtonChoice(value, PowerButtonSetting.Label(value))).ToArray();
        Observe<PowerButtonChoice>(_powerButtonEditor, selected => PowerButtonSelected?.Invoke(selected.Value));
        powerButtonHost.Children.Add(new DeviceSettingRow("power.button.short-press", "Desktop short press",
            "Game Mode always uses Steam suspend. Hold to open the power menu.", _powerButtonEditor, _ => { }));

        _viewModel = viewModel;
        _keepAwakeDetail = keepAwakeDetail;
        _wakeEditor = new ComboBox { MinWidth = 180, HorizontalAlignment = HorizontalAlignment.Stretch };
        _wakeEditor.ItemsSource = new[]
        {
            new WakeChoice(ManualWakeMode.Off, "Automatic"),
            new WakeChoice(ManualWakeMode.Standby, "Prevent standby"),
            new WakeChoice(ManualWakeMode.StandbyAndDisplay, "Keep screen on")
        };
        Observe<WakeChoice>(_wakeEditor, selected => KeepAwakeSelected?.Invoke(selected.Mode));
        keepAwakeHost.Children.Add(new DeviceSettingRow("power.keep-awake", "Keep awake",
            "Choose the manual wake hold", _wakeEditor, _ => { }));

        foreach (var kind in Enum.GetValues<PowerTimeoutKind>())
        {
            var title = PowerTimeouts.Label(kind);
            var hibernate = kind is PowerTimeoutKind.HibernateDc or PowerTimeoutKind.HibernateAc;
            var editor = new ComboBox { MinWidth = 150, HorizontalAlignment = HorizontalAlignment.Stretch };
            _timeoutEditors.Add(kind, editor);
            Observe<TimeoutChoice>(editor, selected => PowerTimeoutSelected?.Invoke(kind, selected.Seconds));
            DeviceSettingRow row = new("power.timeout." + kind, title,
                hibernate ? "Time in standby before the device hibernates" : string.Empty, editor, _ => { })
            {
                // Shown once a reading says the machine can hibernate.
                IsVisible = !hibernate
            };
            if (hibernate)
            {
                _hibernateRows.Add(kind, row);
            }

            timeoutHost.Children.Add(row);
        }

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnStateChanged;
            Refresh();
        }
    }

    /// <summary>Whether the Keep Awake detail, the indicator's one consumer, is on screen.</summary>
    internal bool KeepAwakeShowing => _keepAwakeDetail.IsEffectivelyVisible;

    /// <summary>Raised with the manual wake hold the user chose.</summary>
    internal event Action<ManualWakeMode>? KeepAwakeSelected;

    /// <summary>Raised with the short-press action the user picked for the power button.</summary>
    internal event Action<PowerButtonShortPress>? PowerButtonSelected;

    /// <summary>Raised with the timeout and its new value in seconds when the user picks one.</summary>
    internal event Action<PowerTimeoutKind, int>? PowerTimeoutSelected;

    /// <summary>
    ///     Paints the Keep Awake row's status dot in the WakeWatch color vocabulary: green free, yellow
    ///     standby-blocked, red display-pinned, grey unknown. Brushes come from the palette tokens.
    /// </summary>
    /// <param name="state">The system-wide wake-lock state.</param>
    internal void SetKeepAwakeStatus(WakeLockState state)
    {
        _keepAwakeDetail.Foreground = _keepAwakeDetail.FindResource(state switch
        {
            WakeLockState.DisplayHeld => "HcDangerBrush",
            WakeLockState.SystemHeld => "HcWarningBrush",
            WakeLockState.Free => "HcSuccessBrush",
            _ => "HcTextMutedBrush"
        }) as IBrush;
    }

    /// <summary>Shows the view model's current hold and timeouts in the editors.</summary>
    internal void Refresh()
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        _refreshing = true;
        try
        {
            if (_wakeEditor is { IsDropDownOpen: false })
            {
                _wakeEditor.SelectedItem = _wakeEditor.Items.OfType<WakeChoice>()
                    .FirstOrDefault(choice => choice.Mode == vm.KeepAwakeManualMode);
            }

            _keepAwakeDetail.Text = vm.KeepAwakeDescription;
            _powerButtonSection.IsVisible = vm.PowerButtonAvailable;
            if (_powerButtonEditor is { IsDropDownOpen: false })
            {
                _powerButtonEditor.SelectedItem = _powerButtonEditor.Items.OfType<PowerButtonChoice>()
                    .FirstOrDefault(choice => choice.Value == vm.PowerButtonShortPress);
            }

            foreach (var (kind, row) in _hibernateRows)
            {
                // A machine that cannot hibernate reads no value; its timeout would do nothing.
                row.IsVisible = vm.PowerTimeoutValues.GetValueOrDefault(kind) is not null;
            }

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
                editor.IsEnabled = current is not null && vm.PowerTimeoutsEditable;
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Stops following the view model as the window closes.</summary>
    internal void Detach()
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnStateChanged;
        }
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OverlayViewModel.KeepAwakeManualMode)
            or nameof(OverlayViewModel.KeepAwakeDescription) or nameof(OverlayViewModel.PowerTimeoutValues)
            or nameof(OverlayViewModel.PowerButtonAvailable) or nameof(OverlayViewModel.PowerButtonShortPress))
        {
            Refresh();
        }
    }

    private void Observe<T>(ComboBox editor, Action<T> selected) where T : class
    {
        ComboCommit.Attach(editor, () => _refreshing, selected);
        // Added after the commit rule so a closing dropdown commits before the editors re-read.
        editor.DropDownClosed += (_, _) => Refresh();
    }

    private sealed record WakeChoice(ManualWakeMode Mode, string Label)
    {
        public override string ToString()
        {
            return Label;
        }
    }

    private sealed record PowerButtonChoice(PowerButtonShortPress Value, string Label)
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
