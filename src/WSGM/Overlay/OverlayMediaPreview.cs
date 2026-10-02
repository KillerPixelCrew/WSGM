using System;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;
using SkiaSharp;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Overlay;

/// <summary>A single explicit movie or animated-image preview. Controls remain native Overlay controls.</summary>
internal sealed class OverlayMediaPreview : StackPanel, IOverlayRefreshable
{
    private readonly TextBlock _status = new() { Text = "Preview is stopped", TextWrapping = TextWrapping.Wrap };
    private readonly MediaViewport _viewport;

    internal OverlayMediaPreview(string source, bool image = false)
    {
        Spacing = 8;
        _viewport = new MediaViewport(source, image) { Height = 240, Focusable = false };
        _viewport.Status += text => _status.Text = text;
        Children.Add(_viewport);
        Children.Add(_status);
        var commands = new WrapPanel { Orientation = Orientation.Horizontal };
        Add("Play", "play");
        if (!image)
        {
            Add("Pause", "pause");
            Add("Replay", "replay");
            Add("Back 10s", "back");
            Add("Forward 10s", "forward");
            var volume = new Slider { Minimum = 0, Maximum = 100, Value = 60, Width = 180, Tag = "preview.volume" };
            volume.ValueChanged += (_, _) => _viewport.Command("volume", volume.Value);
            commands.Children.Add(volume);
            var position = new Slider { Minimum = 0, Maximum = 1000, Width = 220, Tag = "preview.seek" };
            position.ValueChanged += (_, _) => _viewport.Command("seek", position.Value);
            ToolTip.SetTip(position, "Seek through the movie");
            commands.Children.Add(position);
        }

        Children.Add(commands);
        return;

        void Add(string title, string command)
        {
            var button = new ActionButton { Title = title, Margin = new Thickness(0, 0, 8, 0) };
            button.Click += (_, _) => _viewport.Command(command);
            commands.Children.Add(button);
        }
    }

    public void RefreshFrom(Control replacement)
    {
    }

    internal sealed class MediaViewport : NativeControlHost
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
        private readonly bool _image;
        private readonly string _source;
        private CoreWebView2Controller? _controller;
        private bool _covered;
        private string? _folder;
        private IntPtr _handle;
        private CancellationTokenSource? _load;
        private string? _media;
        private bool _mediaFailed;
        private bool _ready;

        internal MediaViewport(string source, bool image)
        {
            _source = source;
            _image = image;
            SizeChanged += (_, _) => Resize();
        }

        internal event Action<string>? Status;

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            if (parent.HandleDescriptor != "HWND")
            {
                return base.CreateNativeControlCore(parent);
            }

            _handle = OverlayMediaNative.CreateWindowEx(0, "STATIC", "WSGM media", 0x4e000000,
                0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("The media viewport could not be created.");
            }

            return new PlatformHandle(_handle, "HWND");
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            _load?.Cancel();
            _load?.Dispose();
            _load = null;
            _controller?.Close();
            _controller = null;
            _ready = false;
            _handle = IntPtr.Zero;
            if (control.HandleDescriptor == "HWND")
            {
                OverlayMediaNative.DestroyWindow(control.Handle);
            }
            else
            {
                base.DestroyNativeControlCore(control);
            }

            ClearMedia();
        }

        internal void Suspend(bool covered)
        {
            _covered = covered;
            if (_controller is not null)
            {
                _controller.IsVisible = !covered;
                if (covered)
                {
                    Command("pause");
                }
            }
        }

        internal void Command(string command, double value = 0)
        {
            if (_controller is null)
            {
                if (command == "play" && _load is null && _handle != IntPtr.Zero)
                {
                    _load = new CancellationTokenSource();
                    _ = InitializeAsync(_load.Token);
                }

                return;
            }

            if (!_ready)
            {
                return;
            }

            _ = ExecuteAsync(command, value);
        }

        private async Task ExecuteAsync(string command, double value)
        {
            try
            {
                var script = command switch
                {
                    "play" =>
                        "document.querySelector('video')?.play().catch(()=>window.chrome.webview.postMessage('playFailed'))",
                    "pause" => "document.querySelector('video')?.pause()",
                    "replay" => "(()=>{const v=document.querySelector('video');if(v){v.currentTime=0;v.play();}})()",
                    "back" =>
                        "(()=>{const v=document.querySelector('video');if(v)v.currentTime=Math.max(0,v.currentTime-10);})()",
                    "forward" =>
                        "(()=>{const v=document.querySelector('video');if(v)v.currentTime=Math.min(v.duration||0,v.currentTime+10);})()",
                    "seek" =>
                        "(()=>{const v=document.querySelector('video');if(v&&Number.isFinite(v.duration))v.currentTime=v.duration*" +
                        JsonSerializer.Serialize(Math.Clamp(value / 1000, 0, 1)) + ";})()",
                    "volume" => "(()=>{const v=document.querySelector('video');if(v)v.volume=" +
                                JsonSerializer.Serialize(Math.Clamp(value / 100, 0, 1)) + ";})()",
                    _ => null
                };
                if (script is not null && _controller is { } controller)
                {
                    await controller.CoreWebView2.ExecuteScriptAsync(script);
                }
            }
            catch (Exception ex)
            {
                Status?.Invoke("Playback failed: " + ex.Message);
            }
        }

        private async Task InitializeAsync(CancellationToken token)
        {
            var handle = _handle;
            var folder = Path.Combine(Path.GetTempPath(), "WSGM-media-" + Guid.NewGuid().ToString("N"));
            var media = Path.Combine(folder, _image ? "preview.image" : "preview.webm");
            try
            {
                Status?.Invoke("Loading preview…");
                Directory.CreateDirectory(folder);
                await DownloadAsync(media, token);
                if (_image)
                {
                    using var input = File.OpenRead(media);
                    using var codec = SKCodec.Create(input) ??
                                      throw new InvalidDataException("Unsupported animated artwork format.");
                    if ((long)codec.Info.Width * codec.Info.Height > 64 * 1024 * 1024)
                    {
                        throw new InvalidDataException("The image dimensions are too large.");
                    }

                    var extension = codec.EncodedFormat switch
                    {
                        SKEncodedImageFormat.Gif => ".gif",
                        SKEncodedImageFormat.Webp => ".webp",
                        SKEncodedImageFormat.Png => ".png",
                        SKEncodedImageFormat.Jpeg => ".jpg",
                        _ => throw new InvalidDataException("Unsupported animated artwork format.")
                    };
                    input.Close();
                    var named = Path.ChangeExtension(media, extension);
                    File.Move(media, named);
                    media = named;
                }

                token.ThrowIfCancellationRequested();
                var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WSGM", "MediaPreview");
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
                token.ThrowIfCancellationRequested();
                var controller = await environment.CreateCoreWebView2ControllerAsync(handle);
                if (token.IsCancellationRequested || _handle != handle)
                {
                    controller.Close();
                    ClearFiles(folder, media);
                    return;
                }

                _controller = controller;
                _folder = folder;
                _media = media;
                var web = controller.CoreWebView2;
                web.Settings.AreDevToolsEnabled = false;
                web.Settings.AreDefaultContextMenusEnabled = false;
                web.Settings.AreBrowserAcceleratorKeysEnabled = false;
                web.Settings.IsWebMessageEnabled = true;
                _mediaFailed = false;
                web.WebMessageReceived += (_, e) =>
                {
                    if (e.Source != "about:blank")
                    {
                        return;
                    }

                    try
                    {
                        var message = e.TryGetWebMessageAsString();
                        if (message == "error" || message == "playFailed")
                        {
                            _mediaFailed = true;
                            Status?.Invoke("The media could not be decoded or played.");
                        }
                        else if (message == "ended")
                        {
                            Status?.Invoke("Playback finished");
                        }
                        else if (message == "playing")
                        {
                            Status?.Invoke("Playing");
                        }
                    }
                    catch (ArgumentException)
                    {
                    }
                };
                web.NewWindowRequested += (_, e) => e.Handled = true;
                web.DownloadStarting += (_, e) => e.Cancel = true;
                web.NavigationStarting += (_, e) => e.Cancel = e.Uri != "about:blank";
                web.SetVirtualHostNameToFolderMapping("wsgm-preview.local", _folder,
                    CoreWebView2HostResourceAccessKind.DenyCors);
                web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                web.WebResourceRequested += (_, e) =>
                {
                    if (!e.Request.Uri.StartsWith("https://wsgm-preview.local/", StringComparison.Ordinal))
                    {
                        e.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
                    }
                };
                web.NavigationCompleted += (_, e) =>
                {
                    _ready = e.IsSuccess && !_mediaFailed;
                    Status?.Invoke(_ready ? "Preview ready" : "The preview could not be loaded.");
                    if (_ready && !_covered)
                    {
                        Command("play");
                    }
                };
                Resize();
                controller.IsVisible = !_covered;
                var src = "https://wsgm-preview.local/" + Path.GetFileName(_media);
                web.NavigateToString(
                    "<!doctype html><html><head><style>html,body{margin:0;height:100%;background:#171b20}video,img{width:100%;height:100%;object-fit:contain;pointer-events:none}</style></head><body>" +
                    (_image
                        ? "<img onerror=\"window.chrome.webview.postMessage('error')\" src=\"" + src + "\">"
                        : "<video onerror=\"window.chrome.webview.postMessage('error')\" onplaying=\"window.chrome.webview.postMessage('playing')\" onended=\"window.chrome.webview.postMessage('ended')\" playsinline preload=\"metadata\" src=\"" +
                          src + "\"></video>") + "</body></html>");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                ClearFiles(folder, media);
            }
            catch (Exception ex)
            {
                ClearFiles(folder, media);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                Status?.Invoke("Preview unavailable: " + ex.Message);
                _controller?.Close();
                _controller = null;
                _load?.Dispose();
                _load = null;
                ClearMedia();
            }
        }

        private async Task DownloadAsync(string path, CancellationToken token)
        {
            if (_image)
            {
                await File.WriteAllBytesAsync(path, await OverlayPreviewImage.ReadAsync(_source, token), token);
                return;
            }

            if (Uri.TryCreate(_source, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            {
                using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, true);
                await BoundedHttp.CopyAsync(response.Content, output, AnimationRepoClient.MaximumMovieBytes,
                    () => new InvalidDataException("The movie is larger than 64 MB."), token);
            }
            else
            {
                var local = Uri.TryCreate(_source, UriKind.Absolute, out uri) && uri.IsFile ? uri.LocalPath : _source;
                if (new FileInfo(local).Length > AnimationRepoClient.MaximumMovieBytes)
                {
                    throw new InvalidDataException("The movie is larger than 64 MB.");
                }

                await using var input =
                    new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, true);
                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    copied += read;
                    if (copied > AnimationRepoClient.MaximumMovieBytes)
                    {
                        throw new InvalidDataException("The movie is larger than 64 MB.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                }
            }
        }

        private void Resize()
        {
            if (_controller is null)
            {
                return;
            }

            var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            _controller.Bounds = new Rectangle(0, 0, Math.Max(1, (int)(Bounds.Width * scale)),
                Math.Max(1, (int)(Bounds.Height * scale)));
        }

        private void ClearMedia()
        {
            ClearFiles(_folder, _media);
            _folder = null;
            _media = null;
        }

        private static void ClearFiles(string? folder, string? media)
        {
            try
            {
                if (media is not null && File.Exists(media))
                {
                    File.Delete(media);
                }

                if (folder is not null && Directory.Exists(folder))
                {
                    Directory.Delete(folder);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
