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

    /// <summary>The window's design size in DIPs, before the touch scale.</summary>
    private const double BaseWidth = 500;
    private const double BaseHeight = 600;

    private readonly double _uiScale;

    /// <summary>Creates the panel.</summary>
    /// <param name="radios">The manager backing both tabs. Not owned: the taskbar's
    /// status object outlives this window.</param>
    /// <param name="bluetooth">True to open on the Bluetooth tab.</param>
    /// <param name="uiScale">The desktop-DPI scale factor for WSGM UI (e.g. 1.5
    /// for a 150% desktop; see DisplayScale.GetUiScalePercent).</param>
    public RadioWindow(RadioManager radios, bool bluetooth, double uiScale = 1.0)
    {
        _radios = radios;
        _uiScale = uiScale;
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

        Keyboard.Accepted += (_, _) => OnPromptAccept(this, new RoutedEventArgs());
        _radios.PairingRequested += OnPairingRequested;
        _radios.PropertyChanged += OnRadiosPropertyChanged;
        Opened += (_, _) => _radios.StartScanning();
        Closed += (_, _) =>
        {
            // A prompt on screen when the panel goes means Windows is still
            // waiting on an answer. Unsubscribing alone left the deferral
            // pending until its 90 s timeout, with the row stuck on Working and
            // no way to start another pairing until it expired — so an
            // abandoned ceremony is declined, not just forgotten.
            if (_prompt is PromptMode.PairingPin or PromptMode.PairingConfirm)
            {
                Log.Info("Radio panel closed with a pairing question open — declining it.");
                _radios.RespondToPairing(_promptToken, accept: false, null);
                _prompt = PromptMode.None;
            }
            _radios.StopScanning();
            _radios.PairingRequested -= OnPairingRequested;
            _radios.PropertyChanged -= OnRadiosPropertyChanged;
        };
        // Same touch-promotion defense as the overlay and taskbar (invariant 3):
        // Avalonia never marks touch handled, so DefWindowProc synthesizes a
        // late mouse click that would press whatever sits under the panel.
        Win32Properties.AddWndProcHookCallback(this, WndProcHook);
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                Close();
            }
        };
    }

    /// <summary>Renders the panel at the user's desktop DPI. Game mode forces
    /// every display to 100% scaling, which shrinks a DIP-sized panel — and the
    /// on-screen keyboard inside it — to millimeters on dense handheld panels.
    /// Same mechanism as the taskbar: a layout transform by the desktop factor,
    /// with the window itself grown to hold the scaled content and clamped to
    /// the display so it can never outgrow a short screen (the list scrolls).</summary>
    /// <param name="taskbarTop">The bar's top edge in physical screen pixels, or
    /// 0 when it is not on screen.</param>
    private void ApplyTouchScale(int taskbarTop)
    {
        // Window scaling, not screen.Scaling — the screens cache is stale after
        // a runtime display-scale flip (see OverlayWindow.DockToRightEdge).
        var factor = Math.Clamp(_uiScale / DesktopScaling, 1.0, 3.0);
        if (Math.Abs(factor - 1.0) >= 0.01)
        {
            Log.Info($"Radio panel UI scale {factor:0.##}x (desktop DPI over current {DesktopScaling:0.##}).");
            RootScale.LayoutTransform = new Avalonia.Media.ScaleTransform(factor, factor);
        }

        var screen = Screens.Primary ?? (Screens.ScreenCount > 0 ? Screens.All[0] : null);
        if (screen is not null)
        {
            // Clamp against the space above the bar, in DIPs. The content
            // scroll viewer absorbs a shortened panel.
            var bottom = taskbarTop > 0 ? taskbarTop : screen.Bounds.Y + screen.Bounds.Height;
            var availableWidth = (screen.Bounds.Width / DesktopScaling) - 12;
            var availableHeight = ((bottom - screen.Bounds.Y) / DesktopScaling) - 8;
            Width = Math.Min(BaseWidth * factor, availableWidth);
            Height = Math.Min(BaseHeight * factor, availableHeight);
        }
        // Sizes must be final before the dock computes the position.
        UpdateLayout();
    }

    /// <summary>Places the panel just above the taskbar, at the right-hand end
    /// where its tiles are.
    ///
    /// Without this the window opens wherever Windows decides, which is the
    /// top-left corner — nowhere near the button that opened it. The bar's own
    /// height is measured rather than assumed, because it is content-sized and
    /// DPI-scaled.</summary>
    /// <param name="taskbarTop">The bar's top edge in physical screen pixels, or
    /// 0 when it is not on screen.</param>
    internal void DockAboveTaskbar(int taskbarTop = 0)
    {
        ApplyTouchScale(taskbarTop);
        var screen = Screens.Primary ?? (Screens.ScreenCount > 0 ? Screens.All[0] : null);
        if (screen is null)
        {
            return;
        }
        var area = screen.Bounds;
        // Window scaling, not screen.Scaling — the screens cache reports the
        // pre-game-mode factor after the runtime display-scale flip, which is
        // exactly when this window positions itself, and a wrong factor here
        // parked the panel far from the bar (device-reported).
        var scale = DesktopScaling;
        var width = (int)Math.Round(Width * scale);
        var height = (int)Math.Round(Height * scale);
        // Small and deliberate: the panel should look attached to the bar, not
        // floating above it.
        var gap = (int)Math.Round(2 * scale);
        var margin = (int)Math.Round(6 * scale);

        // Measured against the bar's ACTUAL top edge rather than derived from the
        // working area. The bar is a topmost window, not a registered appbar, so
        // the working area does not account for it — deriving the position from
        // screen height and bar height double-counted and left a visible gap.
        var bottom = taskbarTop > 0 ? taskbarTop : area.Y + area.Height;

        // Right-aligned, mirroring where the tiles are and where Windows puts
        // its own quick settings.
        var x = area.X + area.Width - width - margin;
        var y = bottom - height - gap;
        // Never let it run off the top of a short display.
        if (y < area.Y)
        {
            y = area.Y;
        }
        Position = new PixelPoint(x, y);
    }

    private static IntPtr WndProcHook(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is Interop.NativeMethods.WmMouseMove
                or Interop.NativeMethods.WmLButtonDown
                or Interop.NativeMethods.WmLButtonUp)
        {
            var extra = (uint)Interop.NativeMethods.GetMessageExtraInfo();
            if ((extra & Interop.NativeMethods.MiWpSignatureMask)
                == Interop.NativeMethods.MiWpSignature)
            {
                handled = true;
                return IntPtr.Zero;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>Shows the Wi-Fi or Bluetooth tab. Lets an already-open panel
    /// honour the tile that was tapped instead of staying on whichever tab it
    /// happened to open on.</summary>
    /// <param name="bluetooth">True for the Bluetooth tab.</param>
    internal void SelectTab(bool bluetooth) => Tabs.SelectedIndex = bluetooth ? 1 : 0;

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

    /// <summary>The primary action: pair a stranger, or soft-connect/disconnect
    /// a paired audio device. Never unpairs — that is the Remove button's job,
    /// so a tap meant as "disconnect" can never destroy the pairing.</summary>
    private async void OnDeviceAction(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not BluetoothDeviceEntry entry || entry.Busy)
        {
            return;
        }
        if (!entry.Paired)
        {
            _radios.BeginPairing(entry);
            return;
        }
        if (entry.AudioConnectable)
        {
            await _radios.SetAudioConnectionAsync(entry, connect: !entry.AudioActive);
        }
    }

    private async void OnDeviceRemove(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is BluetoothDeviceEntry { Busy: false } entry)
        {
            await _radios.UnpairAsync(entry);
        }
    }

    private void OnRescanClicked(object? sender, RoutedEventArgs e) => _radios.Rescan();

    /// <summary>Reveals what has been typed. A password field a user cannot read
    /// back is unusable on a keyboard they are tapping one character at a time.</summary>
    private void OnPromptReveal(object? sender, RoutedEventArgs e)
    {
        var hidden = PromptInput.PasswordChar != '\0';
        PromptInput.PasswordChar = hidden ? '\0' : '●';
        PromptReveal.Content = hidden ? "Hide" : "Show";
    }

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
        PromptReveal.IsVisible = needsInput;
        Keyboard.IsVisible = needsInput;
        Keyboard.Target = PromptInput;
        Keyboard.Reset();
        // Passwords start hidden; the reveal button is there for when a
        // one-character-at-a-time entry has gone wrong.
        PromptInput.PasswordChar = '●';
        PromptReveal.Content = "Show";
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
        // An empty PIN cannot answer the provide-pin ceremony: the helper reads
        // it as the no-PIN Accept overload, so the pairing fails for a reason
        // the user never sees. Keep the prompt open instead.
        if (mode == PromptMode.PairingPin && text.Length == 0)
        {
            PromptDetail.Text = "Enter the PIN shown on the device to continue.";
            PromptInput.Focus();
            return;
        }
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
