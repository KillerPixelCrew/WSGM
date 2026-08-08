using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>The game-mode Wi-Fi and Bluetooth panel.
///
/// A real window rather than a taskbar flyout for two reasons that both matter
/// on a handheld: a 240 px flyout cannot hold a network list, and
/// <see cref="Input.GamepadNavigation"/> has no popup awareness, so a list
/// inside a flyout would not be reachable with a controller at all.</summary>
public partial class RadioWindow : Window
{
    private readonly RadioManager _radios;
    private bool _applyingSwitch;

    /// <summary>What the prompt is currently collecting, so one input box can
    /// serve both a Wi-Fi password and a Bluetooth PIN.</summary>
    private enum PromptMode
    {
        None,
        WifiPassword,
        PairingPin,
        PairingConfirm,
    }

    private PromptMode _prompt;
    private string _promptSsid = "";
    private uint _promptToken;

    /// <summary>Design-time constructor. Avalonia's XAML loader needs it.</summary>
    public RadioWindow()
        : this(new RadioManager(), bluetooth: false)
    {
    }

    /// <summary>Creates the panel.</summary>
    /// <param name="radios">The manager backing both tabs. Not owned: the taskbar's
    /// status object outlives this window.</param>
    /// <param name="bluetooth">True to open on the Bluetooth tab.</param>
    public RadioWindow(RadioManager radios, bool bluetooth)
    {
        _radios = radios;
        InitializeComponent();
        DataContext = radios;

        Tabs.Tabs = new List<TabStripItem>
        {
            new("Wi-Fi", Icons.WiFi),
            new("Bluetooth", Icons.Bluetooth),
        };
        Tabs.SelectionChanged += (_, e) => ShowTab(e.NewIndex);
        Tabs.SelectedIndex = bluetooth ? 1 : 0;
        ShowTab(Tabs.SelectedIndex);

        _radios.PairingRequested += OnPairingRequested;
        _radios.PropertyChanged += OnRadiosPropertyChanged;
        Opened += (_, _) => _radios.StartScanning();
        Closed += (_, _) =>
        {
            _radios.StopScanning();
            _radios.PairingRequested -= OnPairingRequested;
            _radios.PropertyChanged -= OnRadiosPropertyChanged;
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                Close();
            }
        };
    }

    /// <summary>Places the panel just above the taskbar, at the right-hand end
    /// where its tiles are.
    ///
    /// Without this the window opens wherever Windows decides, which is the
    /// top-left corner — nowhere near the button that opened it. The bar's own
    /// height is measured rather than assumed, because it is content-sized and
    /// DPI-scaled.</summary>
    /// <param name="taskbarHeight">Height of the bar in physical pixels, or 0
    /// when it could not be measured.</param>
    internal void DockAboveTaskbar(int taskbarHeight = 0)
    {
        var screen = Screens.Primary ?? (Screens.ScreenCount > 0 ? Screens.All[0] : null);
        if (screen is null)
        {
            return;
        }
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var width = (int)Math.Round(Width * scale);
        var height = (int)Math.Round(Height * scale);
        const int Gap = 8;
        var margin = (int)Math.Round(Gap * scale);

        // Right-aligned above the bar, mirroring where the tiles sit and where
        // Windows puts its own quick-settings panel.
        var x = area.X + area.Width - width - margin;
        var y = area.Y + area.Height - height - taskbarHeight - margin;
        // Never let it run off the top of a short display.
        if (y < area.Y)
        {
            y = area.Y;
        }
        Position = new PixelPoint(x, y);
    }

    /// <summary>Moves to the previous tab (left shoulder).</summary>
    public void SelectPreviousTab() => Tabs.SelectPrevious();

    /// <summary>Moves to the next tab (right shoulder).</summary>
    public void SelectNextTab() => Tabs.SelectNext();

    private bool OnBluetoothTab => Tabs.SelectedIndex == 1;

    private void ShowTab(int index)
    {
        var bluetooth = index == 1;
        PanelWifi.IsVisible = !bluetooth && _prompt == PromptMode.None;
        PanelBluetooth.IsVisible = bluetooth && _prompt == PromptMode.None;
        PanelTitle.Text = bluetooth ? "Bluetooth" : "Wi-Fi";
        SyncSwitch();
    }

    private void OnRadiosPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RadioManager.WifiPower) or nameof(RadioManager.BluetoothPower))
        {
            SyncSwitch();
        }
    }

    /// <summary>Mirrors the radio's real state onto the switch without letting
    /// that write look like a user toggle.</summary>
    private void SyncSwitch()
    {
        _applyingSwitch = true;
        var power = OnBluetoothTab ? _radios.BluetoothPower : _radios.WifiPower;
        RadioSwitch.IsChecked = power == RadioPower.On;
        // A radio the machine does not have, or that policy has blocked, is not
        // something a switch can fix.
        RadioSwitch.IsEnabled = power is RadioPower.On or RadioPower.Off;
        _applyingSwitch = false;
    }

    private async void OnRadioSwitchToggled(object? sender, RoutedEventArgs e)
    {
        if (_applyingSwitch)
        {
            return;
        }
        var on = RadioSwitch.IsChecked == true;
        await _radios.SetRadioAsync(OnBluetoothTab, on);
    }

    /// <summary>Selecting a network reveals its actions. It never connects or
    /// disconnects on its own: a stray tap must not drop the connection the user
    /// is currently browsing on.</summary>
    private void OnNetworkClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not WifiNetworkEntry entry)
        {
            return;
        }
        foreach (var other in _radios.Networks)
        {
            other.Expanded = ReferenceEquals(other, entry) && !entry.Expanded;
        }
    }

    private async void OnNetworkAction(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not WifiNetworkEntry entry)
        {
            return;
        }
        if (entry.Connected)
        {
            await _radios.DisconnectAsync();
            return;
        }
        if (entry.Security == WifiSecurity.Enterprise)
        {
            // 802.1X needs an EAP profile and a credential flow this panel has no
            // business guessing at; say so rather than failing obscurely later.
            Log.Info($"Wi-Fi connect: {entry.Ssid} skipped, enterprise networks are not supported.");
            return;
        }
        if (entry.NeedsPassword)
        {
            _promptSsid = entry.Ssid;
            ShowPrompt(PromptMode.WifiPassword, $"Connect to {entry.Ssid}", "Enter the network password.");
            return;
        }
        await _radios.ConnectAsync(entry.Ssid, null);
    }

    private async void OnNetworkForget(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is WifiNetworkEntry entry)
        {
            await _radios.ForgetAsync(entry.Ssid);
        }
    }

    private void OnDeviceClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not BluetoothDeviceEntry entry)
        {
            return;
        }
        foreach (var other in _radios.BluetoothDevices)
        {
            other.Expanded = ReferenceEquals(other, entry) && !entry.Expanded;
        }
    }

    private async void OnDeviceAction(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not BluetoothDeviceEntry entry || entry.Busy)
        {
            return;
        }
        if (entry.Paired)
        {
            await _radios.UnpairAsync(entry);
            return;
        }
        _radios.BeginPairing(entry);
    }

    private void OnRescanClicked(object? sender, RoutedEventArgs e) => _radios.Rescan();

    /// <summary>Raises the touch keyboard when the field takes focus, so a
    /// keyboard-less handheld does not need the button at all.</summary>
    private void OnPromptInputFocused(object? sender, RoutedEventArgs e) => TouchKeyboard.Show();

    private void OnPairingRequested(RadioManager.PairingPrompt prompt)
    {
        _promptToken = prompt.Token;
        switch (prompt.Kind)
        {
            case 2: // provide-pin: the device shows a code, the user types it here
                ShowPrompt(
                    PromptMode.PairingPin,
                    $"Pair with {prompt.DeviceName}",
                    "Enter the PIN shown on the device.");
                break;
            case 1: // display-pin: we show it, the user types it on the device
            case 3: // confirm-pin-match: both sides show it, the user confirms
                ShowPrompt(
                    PromptMode.PairingConfirm,
                    $"Pair with {prompt.DeviceName}",
                    prompt.Kind == 1
                        ? $"Enter this PIN on the device: {prompt.Pin}"
                        : $"Does the device show {prompt.Pin}?");
                break;
            default: // confirm-only
                ShowPrompt(
                    PromptMode.PairingConfirm,
                    $"Pair with {prompt.DeviceName}",
                    "Confirm to pair with this device.");
                break;
        }
    }

    private void ShowPrompt(PromptMode mode, string title, string detail)
    {
        _prompt = mode;
        PromptTitle.Text = title;
        PromptDetail.Text = detail;
        PromptInput.Text = "";
        // Only the two "type something" ceremonies get an input box; showing an
        // empty one for a confirmation would invite the user to type into it.
        var needsInput = mode is PromptMode.WifiPassword or PromptMode.PairingPin;
        PromptInput.IsVisible = needsInput;
        PromptKeyboard.IsVisible = needsInput;
        PromptAccept.Content = mode == PromptMode.WifiPassword ? "Connect" : "Pair";
        PromptPanel.IsVisible = true;
        PanelWifi.IsVisible = false;
        PanelBluetooth.IsVisible = false;
        if (needsInput)
        {
            PromptInput.Focus();
        }
        else
        {
            PromptAccept.Focus();
        }
    }

    private void HidePrompt()
    {
        _prompt = PromptMode.None;
        PromptPanel.IsVisible = false;
        PromptInput.Text = "";
        ShowTab(Tabs.SelectedIndex);
    }

    private async void OnPromptAccept(object? sender, RoutedEventArgs e)
    {
        var mode = _prompt;
        var text = PromptInput.Text ?? "";
        var ssid = _promptSsid;
        var token = _promptToken;
        HidePrompt();
        switch (mode)
        {
            case PromptMode.WifiPassword:
                await _radios.ConnectAsync(ssid, text);
                break;
            case PromptMode.PairingPin:
                _radios.RespondToPairing(token, accept: true, text);
                break;
            case PromptMode.PairingConfirm:
                _radios.RespondToPairing(token, accept: true, null);
                break;
        }
    }

    private void OnPromptCancel(object? sender, RoutedEventArgs e)
    {
        var mode = _prompt;
        var token = _promptToken;
        HidePrompt();
        // A pairing ceremony that is simply abandoned stalls until Windows times
        // it out, so a cancel has to be reported rather than just dismissed.
        if (mode is PromptMode.PairingPin or PromptMode.PairingConfirm)
        {
            _radios.RespondToPairing(token, accept: false, null);
        }
    }

    private void OnPromptKeyboard(object? sender, RoutedEventArgs e) => TouchKeyboard.Show();

    /// <summary>Collapses every row, so reopening a tab starts clean.</summary>
    private void CollapseRows()
    {
        foreach (var network in _radios.Networks)
        {
            network.Expanded = false;
        }
        foreach (var device in _radios.BluetoothDevices)
        {
            device.Expanded = false;
        }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
