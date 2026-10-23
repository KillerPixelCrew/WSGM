using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using WSGM.Core;

namespace WSGM.ScreensaverHost;

/// <summary>An unelevated, disposable browser with no application/native scripting bridge.</summary>
internal sealed class SceneWindow : Form, IMessageFilter
{
    private const string Origin = "https://wsgm-screensaver.invalid/";
    private const string ImageDocument = "__wsgm_images.html";
    private const string Policy = "default-src 'none'; script-src 'self' 'unsafe-inline'; "
                                  + "style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; "
                                  + "font-src 'self'; connect-src 'self'; media-src 'none'; frame-src 'none'; "
                                  + "worker-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";
    private readonly string _pipeName;
    private readonly Process _parent;
    private readonly string _profile;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly SemaphoreSlim _render = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private CancellationTokenSource? _load;
    private Task _connection = Task.CompletedTask;
    private Task _rendering = Task.CompletedTask;
    private long _generation;
    private bool _closing;

    internal SceneWindow(string pipeName, Process parent, string profile)
    {
        _pipeName = pipeName;
        _parent = parent;
        _profile = profile;
        Text = "WSGM Screensaver";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
        BackColor = Color.Black;
        TopMost = true;
        Application.AddMessageFilter(this);
        Shown += ConnectWhenShown;
        FormClosed += (_, _) =>
        {
            _closing = true;
            _load?.Cancel();
            _lifetime.Cancel();
            _controller?.Close();
            _controller = null;
            _pipe?.Dispose();
            Application.RemoveMessageFilter(this);
        };
        Resize += (_, _) => ResizeController();
    }

    protected override bool ShowWithoutActivation => true;

    private void ConnectWhenShown(object? sender, EventArgs arguments)
    {
        Shown -= ConnectWhenShown;
        Hide();
        _connection = ConnectAsync();
    }

    public bool PreFilterMessage(ref Message message)
    {
        if (!_closing && message.Msg is 0x100 or 0x104 or 0x201 or 0x204 or 0x207 or 0x20a or 0x246)
        {
            // A user gesture dismisses the saver before it reaches arbitrary scene content.
            Finish("closed", "input");
            return true;
        }

        return false;
    }

    protected override void WndProc(ref Message message)
    {
        if (!_closing && (message.Msg == 0x7e || message.Msg == 0x218 && message.WParam is 4 or 7 or 0x12))
        {
            Finish("closed", "Display or sleep state changed; stock screensaver fallback.");
        }

        base.WndProc(ref message);
    }

    private async Task ConnectAsync()
    {
        try
        {
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            connection.CancelAfter(TimeSpan.FromSeconds(15));
            _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await _pipe.ConnectAsync(connection.Token);
            await SendAsync(new ScreensaverPlayback { Kind = "hello", ProcessId = Environment.ProcessId });
            while (!_closing && !_lifetime.IsCancellationRequested && !_parent.HasExited)
            {
                var packet = await ScreensaverWire.ReadAsync(_pipe, _lifetime.Token);
                if (packet.Kind == "close")
                {
                    Close();
                    return;
                }

                if (packet.Kind != "play")
                {
                    throw new InvalidDataException("Unknown screensaver host command.");
                }

                _load?.Cancel();
                var load = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _load = load;
                var generation = ++_generation;
                _rendering = RenderAsync(packet, generation, load);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidDataException)
        {
            if (!_closing)
            {
                Finish("error", "Screensaver host connection closed: " + error.Message);
            }
        }
    }

    private async Task RenderAsync(ScreensaverPlayback packet, long generation, CancellationTokenSource load)
    {
        var entered = false;
        CoreWebView2Controller? candidate = null;
        try
        {
            await _render.WaitAsync(load.Token);
            entered = true;
            if (packet.Type is not "images" and not "scene" || !Path.IsPathFullyQualified(packet.Root)
                || packet.Assets.Count > ScreensaverContent.MaximumImages || packet.Images.Count > ScreensaverContent.MaximumImages
                || packet.IntervalSeconds is < 2 or > 3600 || packet.FramesPerSecond is < 1 or > 60)
            {
                throw new InvalidDataException("Invalid screensaver playback definition.");
            }

            var assets = new HashSet<string>(packet.Assets.Select(ScreensaverContent.NormalizeRelative),
                StringComparer.OrdinalIgnoreCase);
            foreach (var asset in assets)
            {
                ScreensaverContent.CheckFile(ScreensaverContent.AssetPath(packet.Root, asset));
            }

            if (packet.Type == "scene" && (!assets.Contains(packet.Entry)
                                           || Path.GetExtension(packet.Entry).ToLowerInvariant() is not ".html" and not ".htm"))
            {
                throw new InvalidDataException("The scene entry is not a declared HTML asset.");
            }

            if (packet.Type == "images" && (packet.Images.Count == 0
                                           || packet.Images.Any(image => !assets.Contains(image) || !ScreensaverContent.IsImage(image))))
            {
                throw new InvalidDataException("The image playlist is invalid.");
            }

            Hide();
            Bounds = Screen.FromHandle((nint)packet.WindowHandle).Bounds;
            _controller?.Close();
            _controller = null;
            // Browser creation starts only after the verified parent has placed this helper in
            // its kill-on-close job and sent the first allowlisted playback definition.
            _environment ??= await CoreWebView2Environment.CreateAsync(userDataFolder: _profile);
            load.Token.ThrowIfCancellationRequested();
            var browserOptions = _environment.CreateCoreWebView2ControllerOptions();
            browserOptions.IsInPrivateModeEnabled = true;
            candidate = await _environment.CreateCoreWebView2ControllerAsync(Handle, browserOptions);
            if (_closing || generation != _generation)
            {
                candidate.Close();
                candidate = null;
                return;
            }

            _controller = candidate;
            candidate = null;
            var web = _controller.CoreWebView2;
            web.Settings.AreHostObjectsAllowed = false;
            web.Settings.IsWebMessageEnabled = false;
            web.Settings.AreDevToolsEnabled = false;
            web.Settings.AreDefaultContextMenusEnabled = false;
            web.Settings.AreBrowserAcceleratorKeysEnabled = false;
            web.Settings.AreDefaultScriptDialogsEnabled = false;
            web.Settings.IsStatusBarEnabled = false;
            web.Settings.IsGeneralAutofillEnabled = false;
            web.Settings.IsPasswordAutosaveEnabled = false;
            web.IsMuted = true;
            _controller.AllowExternalDrop = false;
            var entry = packet.Type == "images" ? ImageDocument : packet.Entry;
            var entryUrl = Url(entry);
            var controller = _controller;
            void FailCurrent(string reason)
            {
                if (!_closing && generation == _generation && controller == _controller)
                {
                    Finish("error", reason);
                }
            }
            web.PermissionRequested += (_, arguments) => arguments.State = CoreWebView2PermissionState.Deny;
            web.NewWindowRequested += (_, arguments) => arguments.Handled = true;
            web.DownloadStarting += (_, arguments) => arguments.Cancel = true;
            web.LaunchingExternalUriScheme += (_, arguments) => arguments.Cancel = true;
            web.NavigationStarting += (_, arguments) => arguments.Cancel = arguments.Uri != entryUrl;
            web.FrameNavigationStarting += (_, arguments) => arguments.Cancel = true;
            web.WindowCloseRequested += (_, _) => FailCurrent("The scene closed its window. Stock fallback is active.");
            web.ProcessFailed += (_, arguments) => FailCurrent("Scene renderer stopped: " + arguments.ProcessFailedKind);
            web.DocumentTitleChanged += (_, _) =>
            {
                if (packet.Type == "images" && web.DocumentTitle.StartsWith("WSGM_IMAGE_ERROR:", StringComparison.Ordinal))
                {
                    FailCurrent("No image in the source could be decoded. Stock fallback is active.");
                }
            };
            web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            web.WebResourceRequested += (_, arguments) =>
            {
                try
                {
                    if (!Uri.TryCreate(arguments.Request.Uri, UriKind.Absolute, out var request)
                        || request.Scheme != "https" || request.Host != "wsgm-screensaver.invalid"
                        || request.Port != 443 || request.Query.Length != 0 || request.Fragment.Length != 0)
                    {
                        arguments.Response = _environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
                        return;
                    }

                    var relative = Uri.UnescapeDataString(request.AbsolutePath.TrimStart('/'));
                    byte[] bytes;
                    string mime;
                    if (packet.Type == "images" && relative == ImageDocument)
                    {
                        bytes = System.Text.Encoding.UTF8.GetBytes(ImageHtml(packet));
                        mime = "text/html; charset=utf-8";
                    }
                    else
                    {
                        if (!assets.Contains(relative))
                        {
                            arguments.Response = _environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
                            return;
                        }

                        var path = ScreensaverContent.AssetPath(packet.Root, relative);
                        ScreensaverContent.CheckFile(path);
                        bytes = File.ReadAllBytes(path);
                        if (bytes.LongLength > ScreensaverContent.MaximumAssetBytes)
                        {
                            throw new InvalidDataException("Asset changed beyond the size limit.");
                        }

                        mime = Mime(relative);
                    }

                    arguments.Response = _environment.CreateWebResourceResponse(new MemoryStream(bytes, writable: false),
                        200, "OK", "Content-Type: " + mime + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n"
                                   + "Referrer-Policy: no-referrer\r\nContent-Security-Policy: " + Policy + "\r\n");
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
                {
                    arguments.Response = _environment.CreateWebResourceResponse(null, 404, "Missing asset", "");
                    FailCurrent("A source asset became unavailable: " + error.Message);
                }
            };
            web.NavigationCompleted += (_, arguments) =>
            {
                if (_closing || generation != _generation || controller != _controller)
                {
                    return;
                }

                if (!arguments.IsSuccess)
                {
                    FailCurrent("Scene navigation failed: " + arguments.WebErrorStatus);
                    return;
                }

                controller.IsVisible = true;
                if (packet.WindowHandle != 0)
                {
                    Show(new SteamWindow((nint)packet.WindowHandle));
                }
                else
                {
                    Show();
                }

                controller.NotifyParentWindowPositionChanged();
                _ = SendAsync(new ScreensaverPlayback { Kind = "playing", Revision = packet.Revision });
            };
            var exceptions = web.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown");
            exceptions.DevToolsProtocolEventReceived += (_, _) => FailCurrent("The scene raised an uncaught JavaScript error.");
            await web.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
            await web.AddScriptToExecuteOnDocumentCreatedAsync(AnimationBootstrap(packet.FramesPerSecond));
            load.Token.ThrowIfCancellationRequested();
            ResizeController();
            web.Navigate(entryUrl);
        }
        catch (OperationCanceledException)
        {
            candidate?.Close();
        }
        catch (Exception error)
        {
            candidate?.Close();
            if (!_closing && generation == _generation)
            {
                Finish("error", "Custom screensaver could not start: " + error.Message);
            }
        }
        finally
        {
            if (entered)
            {
                _render.Release();
            }

            if (ReferenceEquals(_load, load))
            {
                _load = null;
            }

            load.Dispose();
        }
    }

    private void ResizeController()
    {
        if (_controller is not null)
        {
            _controller.Bounds = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
        }
    }

    private async Task SendAsync(ScreensaverPlayback packet)
    {
        try
        {
            await _send.WaitAsync(_lifetime.Token);
            try
            {
                if (_pipe?.IsConnected == true)
                {
                    await ScreensaverWire.WriteAsync(_pipe, packet, _lifetime.Token);
                }
            }
            finally
            {
                _send.Release();
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
        }
    }

    private async void Finish(string kind, string detail)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        Hide();
        await SendAsync(new ScreensaverPlayback { Kind = kind, Error = detail });
        Close();
    }

    private static string Url(string relative)
    {
        return Origin + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
    }

    private static string Mime(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream"
        };
    }

    private static string ImageHtml(ScreensaverPlayback packet)
    {
        var sources = string.Join(',', packet.Images.Select(image => "\"" + Url(image) + "\""));
        var shuffle = packet.Shuffle ? "true" : "false";
        return "<!doctype html><html><head><meta charset=utf-8><style>html,body{margin:0;width:100%;height:100%;background:#000;overflow:hidden}img{width:100%;height:100%;object-fit:"
               + (packet.Fill ? "cover" : "contain") + "}</style></head><body><img id=image><script>"
               + "const items=[" + sources + "];let order=[],at=0,failed=0;const image=document.getElementById('image');"
               + "function cycle(){order=items.map((_,i)=>i);if(" + shuffle
               + "){for(let i=order.length-1;i>0;i--){const j=Math.floor(Math.random()*(i+1));[order[i],order[j]]=[order[j],order[i]];}}at=0;}"
               + "function next(){if(at>=order.length)cycle();image.src=items[order[at++]];}"
               + "image.onerror=()=>{if(++failed>=items.length){document.title='WSGM_IMAGE_ERROR:decode';return;}next();};"
               + "image.onload=()=>{failed=0;};cycle();next();setInterval(next," + packet.IntervalSeconds * 1000 + ");"
               + "</script></body></html>";
    }

    private static string AnimationBootstrap(int framesPerSecond)
    {
        return "(()=>{const raf=window.requestAnimationFrame.bind(window),caf=window.cancelAnimationFrame.bind(window),pending=new Map();let next=0,handle=0,last=0;const step="
               + (1000.0 / framesPerSecond).ToString(System.Globalization.CultureInfo.InvariantCulture)
               + ";function tick(t){handle=0;if(t-last<step){handle=raf(tick);return;}last=t;const batch=[...pending];pending.clear();for(const [id,fn] of batch){try{fn(t);}catch(e){setTimeout(()=>{throw e;},0);}}if(pending.size&&!handle)handle=raf(tick);}"
               + "Object.defineProperty(window,'requestAnimationFrame',{value:fn=>{if(typeof fn!=='function'||pending.size>=64)return 0;const id=++next;pending.set(id,fn);if(!handle)handle=raf(tick);return id;},writable:false,configurable:false});"
               + "Object.defineProperty(window,'cancelAnimationFrame',{value:id=>{pending.delete(id);if(!pending.size&&handle){caf(handle);handle=0;}},writable:false,configurable:false});"
               + "for(const key of ['showOpenFilePicker','showSaveFilePicker','showDirectoryPicker','requestFileSystem','webkitRequestFileSystem','resolveLocalFileSystemURL','webkitResolveLocalFileSystemURL','RTCPeerConnection','webkitRTCPeerConnection']){try{Object.defineProperty(window,key,{value:undefined,writable:false,configurable:false});}catch{}}"
               + "const storage=Object.getPrototypeOf(navigator.storage??{});if(storage&&'getDirectory' in storage){Object.defineProperty(storage,'getDirectory',{value:undefined,writable:false,configurable:false});}})();";
    }

    private sealed class SteamWindow(nint handle) : IWin32Window
    {
        public nint Handle => handle;
    }
}
