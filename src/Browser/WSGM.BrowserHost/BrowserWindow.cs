using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using WSGM.Browser;

namespace WSGM.BrowserHost;

/// <summary>Unelevated, unprivileged WebView2 owner. The Form is embedded by the resident after verification.</summary>
internal sealed partial class BrowserWindow : Form
{
    private readonly string _pipeName;
    private readonly Process _parent;
    private readonly string _profile;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<BrowserPacket> _outbound = Channel.CreateBounded<BrowserPacket>(new BoundedChannelOptions(32)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true
    });
    private readonly System.Windows.Forms.Timer _parentWatch = new() { Interval = 500 };
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private NamedPipeClientStream? _pipe;
    private bool _closing;
    private bool _visible;
    private bool _loading;
    private long _visibilityGeneration;
    private string _status = "";
    private string _pageStatus = "";
    private double _zoom = 1;
    private ulong _navigationId;
    private bool _creating;

    internal BrowserWindow(string pipeName, Process parent, string profile)
    {
        _pipeName = pipeName;
        _parent = parent;
        _profile = profile;
        Text = "WSGM Browser";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Size = new Size(1, 1);
        BackColor = Color.FromArgb(23, 27, 32);
        AutoScaleMode = AutoScaleMode.None;
        _parentWatch.Tick += (_, _) =>
        {
            if (_parent.HasExited)
            {
                Close();
            }
        };
        _parentWatch.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x80; // Tool window: never an independent app-switcher destination.
            return parameters;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Hide();
        _ = RunPipeAsync();
    }

    private async Task RunPipeAsync()
    {
        try
        {
            _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await _pipe.ConnectAsync(10000, _lifetime.Token);
            _ = WritePacketsAsync(_pipe, _lifetime.Token);
            Send(new BrowserPacket { Kind = "hello", ProcessId = Environment.ProcessId, Handle = Handle.ToInt64() });
            while (!_closing)
            {
                var packet = await BrowserWire.ReadAsync(_pipe, _lifetime.Token);
                await HandlePacketAsync(packet);
            }
        }
        catch (Exception) when (_closing || _lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // Broken/invalid IPC ends the helper. Nothing supplied by content can become a host command.
            Close();
        }
    }

    private async Task WritePacketsAsync(Stream stream, CancellationToken token)
    {
        try
        {
            await foreach (var packet in _outbound.Reader.ReadAllAsync(token))
            {
                await BrowserWire.WriteAsync(stream, packet, token);
            }
        }
        catch (Exception) when (_closing || token.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            Close();
        }
    }

    private async Task HandlePacketAsync(BrowserPacket packet)
    {
        try
        {
            switch (packet.Kind)
            {
                case "open":
                    _zoom = double.IsFinite(packet.Zoom) ? Math.Clamp(packet.Zoom, 0.5, 3) : 1;
                    _visible = packet.Visible;
                    _ = OpenBrowserAsync();
                    break;
                case "visibility":
                    await SetVisibleAsync(packet.Visible);
                    break;
                case "position":
                    _controller?.NotifyParentWindowPositionChanged();
                    break;
                case "processes":
                    Send(new BrowserPacket
                    {
                        Kind = "processes",
                        Text = JsonSerializer.Serialize(_environment?.GetProcessInfos().Select(info => info.ProcessId).ToArray() ?? [])
                    });
                    break;
                case "capture" when _visible && _controller is { } captureController:
                    // Fixed, profile-local diagnostic output. No caller-supplied path, script or page bridge.
                    await using (var capture = File.Create(Path.Combine(_profile, "BrowserAcceptance.png")))
                    {
                        await captureController.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, capture);
                    }

                    Send(new BrowserPacket { Kind = "capture" });
                    break;
                case "navigate":
                    if (BrowserNavigation.IsAllowed(packet.Text))
                    {
                        _controller?.CoreWebView2.Navigate(packet.Text);
                    }
                    else
                    {
                        Publish("Blocked address. Only HTTP and HTTPS pages are allowed.");
                    }

                    break;
                case "external" when BrowserNavigation.IsAllowed(packet.Text) && packet.Text != BrowserNavigation.Home:
                    Process.Start(new ProcessStartInfo(packet.Text) { UseShellExecute = true });
                    break;
                case "back" when _controller?.CoreWebView2.CanGoBack == true:
                    _controller.CoreWebView2.GoBack();
                    break;
                case "forward" when _controller?.CoreWebView2.CanGoForward == true:
                    _controller.CoreWebView2.GoForward();
                    break;
                case "reload":
                    _controller?.CoreWebView2.Reload();
                    break;
                case "stop":
                    _controller?.CoreWebView2.Stop();
                    _loading = false;
                    Publish("Stopped");
                    break;
                case "zoom" when double.IsFinite(packet.Zoom):
                    _zoom = Math.Clamp(packet.Zoom, 0.5, 3);
                    if (_controller is { } zoomController)
                    {
                        zoomController.ZoomFactor = _zoom;
                    }

                    Publish();
                    break;
                case "focus" when _visible:
                    _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
                    break;
                case "keyboard" when _visible && _controller is { } inputController:
                    if (!await InvokeTextInputAsync(inputController.CoreWebView2, "wsgmRequestTextInput()"))
                    {
                        Publish("Select a text field on the website first.");
                    }

                    break;
                case "key" when _visible && _controller is { } keyController:
                    await SendPageKeyAsync(keyController.CoreWebView2, packet.Text).WaitAsync(TimeSpan.FromSeconds(2));
                    break;
                case "text" when packet.Text.Length <= 4096 && _visible && _controller is { } textController:
                    // CDP insertText affects only this WebView's current editable selection. No script or host
                    // object is injected; the page cannot use this one-way UI action to invoke the resident.
                    textController.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
                    if (packet.NavigationId != 0 && packet.NavigationId != _navigationId
                        || !await InvokeTextInputAsync(textController.CoreWebView2, "wsgmRestoreTextInput()"))
                    {
                        Publish("The selected web field changed. Select it again to enter text.");
                        break;
                    }

                    if (packet.Replace)
                    {
                        await SendPageKeyAsync(textController.CoreWebView2, "SelectAll");
                    }

                    await textController.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.insertText",
                        JsonSerializer.Serialize(new { text = packet.Text })).WaitAsync(TimeSpan.FromSeconds(2));
                    if (packet.Submit)
                    {
                        await SendPageKeyAsync(textController.CoreWebView2, "Enter").WaitAsync(TimeSpan.FromSeconds(2));
                    }

                    break;
                case "close":
                    Close();
                    break;
            }
        }
        catch (Exception ex) when (!_closing)
        {
            Publish("Browser unavailable: " + ex.Message);
            if (_controller is null)
            {
                Send(new BrowserPacket { Kind = "failed", Text = "WebView2 could not start. Repair the WebView2 runtime and retry." });
            }
        }
    }

    private async Task OpenBrowserAsync()
    {
        if (_creating || _controller is not null || _closing)
        {
            return;
        }

        _creating = true;
        try
        {
            await CreateBrowserAsync().WaitAsync(TimeSpan.FromSeconds(20), _lifetime.Token);
            if (!_closing)
            {
                await SetVisibleAsync(_visible);
            }
        }
        catch (Exception)
        {
            if (!_closing)
            {
                Send(new BrowserPacket { Kind = "failed", Text = "WebView2 could not start. Repair the WebView2 runtime and retry." });
            }
        }
        finally
        {
            _creating = false;
        }
    }

    private static async Task SendPageKeyAsync(CoreWebView2 web, string key)
    {
        var code = key switch
        {
            "ArrowUp" => 38,
            "ArrowDown" => 40,
            "ArrowLeft" => 37,
            "ArrowRight" => 39,
            "PageUp" => 33,
            "PageDown" => 34,
            "Tab" or "ShiftTab" => 9,
            "Enter" => 13,
            "Backspace" => 8,
            "Delete" => 46,
            "Home" => 36,
            "End" => 35,
            "Space" => 32,
            "SelectAll" => 65,
            _ => 0
        };
        if (code == 0)
        {
            return;
        }

        var nativeKey = key == "ShiftTab" ? "Tab" : key == "SelectAll" ? "a" : key;
        var modifiers = key == "ShiftTab" ? 8 : key == "SelectAll" ? 2 : 0;
        await web.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new
        {
            type = "keyDown", key = nativeKey, code = key == "SelectAll" ? "KeyA" : nativeKey, windowsVirtualKeyCode = code,
            modifiers, text = key == "Enter" ? "\r" : key == "Space" ? " " : ""
        }));
        await web.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new
        {
            type = "keyUp", key = nativeKey, code = nativeKey, windowsVirtualKeyCode = code, modifiers
        }));
    }

    private async Task CreateBrowserAsync()
    {
        if (_controller is not null || _closing)
        {
            return;
        }

        _environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _profile);
        if (_closing)
        {
            _environment = null;
            return;
        }

        var controller = await _environment.CreateCoreWebView2ControllerAsync(Handle);
        if (_closing)
        {
            controller.Close();
            _environment = null;
            return;
        }

        _controller = controller;
        controller.IsVisible = false;
        controller.ZoomFactor = _zoom;
        controller.Bounds = ClientRectangle;
        controller.AcceleratorKeyPressed += OnAccelerator;
        controller.MoveFocusRequested += OnMoveFocusRequested;
        controller.GotFocus += OnGotFocus;
        controller.LostFocus += OnLostFocus;
        var web = controller.CoreWebView2;
        web.Settings.AreHostObjectsAllowed = false;
        web.Settings.IsWebMessageEnabled = false;
        web.Settings.AreDevToolsEnabled = false;
        web.Settings.AreDefaultContextMenusEnabled = false;
        web.Settings.AreDefaultScriptDialogsEnabled = false;
        web.Settings.AreBrowserAcceleratorKeysEnabled = false;
        web.Settings.IsPasswordAutosaveEnabled = false;
        web.Settings.IsGeneralAutofillEnabled = false;
        web.NavigationStarting += OnNavigationStarting;
        web.FrameNavigationStarting += OnFrameNavigationStarting;
        web.NavigationCompleted += OnNavigationCompleted;
        web.SourceChanged += OnSourceChanged;
        web.HistoryChanged += OnHistoryChanged;
        web.DocumentTitleChanged += OnTitleChanged;
        web.NewWindowRequested += OnNewWindow;
        web.DownloadStarting += OnDownload;
        web.PermissionRequested += OnPermission;
        web.LaunchingExternalUriScheme += OnExternalScheme;
        web.ServerCertificateErrorDetected += OnCertificateError;
        web.ProcessFailed += OnProcessFailed;
        // All local file resources, including file: frames inside an otherwise allowed page, are refused.
        web.AddWebResourceRequestedFilter("file://*", CoreWebView2WebResourceContext.All);
        web.WebResourceRequested += OnLocalResource;
        await InitializeTextInputAsync(web);
        web.Navigate(BrowserNavigation.Home);
        Publish("Ready. Enter an address or search.");
    }

    private async Task SetVisibleAsync(bool visible)
    {
        _visible = visible;
        var generation = ++_visibilityGeneration;
        if (_controller is not { } controller)
        {
            return;
        }

        var web = controller.CoreWebView2;
        if (visible)
        {
            web.Resume();
            web.IsMuted = false;
            controller.Bounds = ClientRectangle;
            controller.NotifyParentWindowPositionChanged();
            controller.IsVisible = true;
            await web.CallDevToolsProtocolMethodAsync("Input.setIgnoreInputEvents", "{\"ignore\":false}")
                .WaitAsync(TimeSpan.FromSeconds(2));
            if (!_closing && generation == _visibilityGeneration)
            {
                Publish(_pageStatus);
            }
        }
        else
        {
            // Mute is unconditional, even when a live stream prevents Chromium suspension.
            web.IsMuted = true;
            controller.IsVisible = false;
            try
            {
                await web.CallDevToolsProtocolMethodAsync("Input.setIgnoreInputEvents", "{\"ignore\":true}")
                    .WaitAsync(TimeSpan.FromSeconds(2));
                if (_closing || generation != _visibilityGeneration)
                {
                    return;
                }

                // Pause ordinary top-level media. Muting also covers cross-origin frames; suspension
                // is best effort, with a bounded wait so a hung page cannot block reopening forever.
                await web.ExecuteScriptAsync("document.querySelectorAll('audio,video').forEach(m=>m.pause())")
                    .WaitAsync(TimeSpan.FromSeconds(2));
                if (_closing || generation != _visibilityGeneration)
                {
                    return;
                }

                var suspended = await SuspendAsync(web).WaitAsync(TimeSpan.FromSeconds(2));
                if (!_closing && generation == _visibilityGeneration)
                {
                    Publish(suspended ? "Retained, muted and suspended" : "Retained and muted; this page cannot suspend", lifecycle: true);
                }
            }
            catch (Exception) when (!_closing)
            {
                if (generation == _visibilityGeneration)
                {
                    Publish("Retained and muted", lifecycle: true);
                }
            }
        }
    }

    private async Task<bool> SuspendAsync(CoreWebView2 web)
    {
        try
        {
            return await web.TrySuspendAsync();
        }
        finally
        {
            // WaitAsync bounds the caller, not Chromium's operation. Even a late completion
            // must honor a page that reopened while suspension was pending.
            if (!_closing && _visible && ReferenceEquals(_controller?.CoreWebView2, web))
            {
                web.Resume();
            }
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_controller is { } controller && !_closing)
        {
            controller.Bounds = ClientRectangle;
            controller.NotifyParentWindowPositionChanged();
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (_controller is { } controller && !_closing)
        {
            controller.Bounds = ClientRectangle;
            controller.NotifyParentWindowPositionChanged();
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        e.Cancel = !BrowserNavigation.IsAllowed(e.Uri);
        if (e.Cancel)
        {
            Publish("Blocked address. Only HTTP and HTTPS pages are allowed.");
        }
        else
        {
            _navigationId = e.NavigationId;
            _loading = true;
            Publish("Loading…");
        }
    }

    private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        e.Cancel = !BrowserNavigation.IsAllowed(e.Uri) && e.Uri != "about:srcdoc";
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.NavigationId != _navigationId)
        {
            return;
        }

        _loading = false;
        Publish(e.IsSuccess ? "" : "Page could not load: " + e.WebErrorStatus + ". Reload to retry.");
        if (!_visible)
        {
            _ = SetVisibleAsync(false);
        }
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e) => Publish();
    private void OnHistoryChanged(object? sender, object e) => Publish();
    private void OnTitleChanged(object? sender, object e) => Publish();

    private void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (BrowserNavigation.IsAllowed(e.Uri))
        {
            _controller?.CoreWebView2.Navigate(e.Uri);
        }
        else
        {
            Publish("Popup blocked: only HTTP and HTTPS pages are allowed.");
        }
    }

    private void OnDownload(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
        e.Handled = true;
        Publish("Downloads are disabled. Open this page in your external browser to download.");
    }

    private static void OnPermission(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = CoreWebView2PermissionState.Deny;
        e.Handled = true;
    }

    private void OnExternalScheme(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e)
    {
        e.Cancel = true;
        Publish("External application links are disabled.");
    }

    private static void OnCertificateError(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
    }

    private void OnLocalResource(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        e.Response = _environment?.CreateWebResourceResponse(null, 403, "Forbidden", "");
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _loading = false;
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            Send(new BrowserPacket { Kind = "failed", Text = "The browser process stopped. Reopen Browser to retry." });
        }
        else
        {
            Publish("Browser process failed: " + e.ProcessFailedKind + ". Stop or reload to recover.");
        }
    }

    private void OnAccelerator(object? sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
    {
        if (e.KeyEventKind is CoreWebView2KeyEventKind.KeyDown or CoreWebView2KeyEventKind.SystemKeyDown
            && e.VirtualKey == 27)
        {
            e.Handled = true;
            Send(new BrowserPacket { Kind = "chrome" });
        }
    }

    private void OnMoveFocusRequested(object? sender, CoreWebView2MoveFocusRequestedEventArgs e)
    {
        e.Handled = true;
        Send(new BrowserPacket { Kind = "chrome" });
    }

    private void OnGotFocus(object? sender, object e) => Send(new BrowserPacket { Kind = "viewport" });
    private void OnLostFocus(object? sender, object e) => Send(new BrowserPacket { Kind = "blur" });

    private void Publish(string? status = null, bool lifecycle = false)
    {
        if (_closing)
        {
            return;
        }

        if (status is not null)
        {
            _status = status;
            if (!lifecycle)
            {
                _pageStatus = status;
            }
        }

        var web = _controller?.CoreWebView2;
        Send(new BrowserPacket
        {
            Kind = "state", Text = _status.Length > 2048 ? _status[..2048] : _status,
            Loading = _loading, CanGoBack = web?.CanGoBack == true, CanGoForward = web?.CanGoForward == true,
            Zoom = _zoom, NavigationId = _navigationId
        });
        var source = web?.Source;
        if (BrowserNavigation.IsAllowed(source))
        {
            Send(new BrowserPacket { Kind = "address", Text = source!, NavigationId = _navigationId });
        }
    }

    private void Send(BrowserPacket packet) => _outbound.Writer.TryWrite(packet);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        _visible = false;
        _visibilityGeneration++;
        _parentWatch.Stop();
        var controller = _controller;
        _controller = null;
        if (controller is not null)
        {
            try
            {
                var web = controller.CoreWebView2;
                DetachTextInputEvents();
                web.Stop();
                web.IsMuted = true;
                web.NavigationStarting -= OnNavigationStarting;
                web.FrameNavigationStarting -= OnFrameNavigationStarting;
                web.NavigationCompleted -= OnNavigationCompleted;
                web.SourceChanged -= OnSourceChanged;
                web.HistoryChanged -= OnHistoryChanged;
                web.DocumentTitleChanged -= OnTitleChanged;
                web.NewWindowRequested -= OnNewWindow;
                web.DownloadStarting -= OnDownload;
                web.PermissionRequested -= OnPermission;
                web.LaunchingExternalUriScheme -= OnExternalScheme;
                web.ServerCertificateErrorDetected -= OnCertificateError;
                web.ProcessFailed -= OnProcessFailed;
                web.WebResourceRequested -= OnLocalResource;
                controller.AcceleratorKeyPressed -= OnAccelerator;
                controller.MoveFocusRequested -= OnMoveFocusRequested;
                controller.GotFocus -= OnGotFocus;
                controller.LostFocus -= OnLostFocus;
            }
            catch (Exception)
            {
                // A crashed browser can already have disconnected its COM proxies.
            }
            finally
            {
                try
                {
                    controller.Close();
                }
                catch (Exception)
                {
                }
            }
        }

        _environment = null;
        _lifetime.Cancel();
        _pipe?.Dispose();
        _outbound.Writer.TryComplete();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _parentWatch.Dispose();
            _lifetime.Dispose();
        }

        base.Dispose(disposing);
    }
}
