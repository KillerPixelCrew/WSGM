using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Web.WebView2.Core;
using SkiaSharp;
using WSGM.Controls;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>A single explicit movie or animated-image preview. Controls remain native Overlay controls.</summary>
internal sealed class OverlayMediaPreview : StackPanel, IOverlayRefreshable
{
    private readonly TextBlock _status = new() { Text = "Preview is stopped", TextWrapping = TextWrapping.Wrap };
    private readonly MediaViewport _viewport;

    /// <summary>Creates stopped media controls; WebView2/media initialization waits for explicit Play.</summary>
    /// <param name="context">Borrowed user-data root for the WebView2 profile and temporary preview media.</param>
    /// <param name="source">Preview source consumed by this instance; immutable for its lifetime.</param>
    /// <param name="image">True for animated-image playback without video-specific seek/volume controls.</param>
    internal OverlayMediaPreview(UserDataContext context, string source, bool image = false)
    {
        Spacing = 8;
        _viewport = new MediaViewport(context, source, image) { Height = 240, Focusable = false };
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

    /// <inheritdoc />
    /// <remarks>No-op to preserve playback. The parent must replace this instance when media identity changes.</remarks>
    public void RefreshFrom(Control replacement)
    {
    }

    /// <summary>Where each preview keeps its own folder of downloaded media, beside the WebView2 profile.</summary>
    /// <param name="root">The user-data root.</param>
    private static string FilesRoot(string root)
    {
        return Path.Combine(root, "MediaPreview", "Files");
    }

    /// <summary>
    ///     Deletes the preview files a crash left behind. Called once at session start, before any
    ///     sheet can show a preview; a file that cannot be deleted is logged and left for the next start.
    /// </summary>
    /// <param name="root">The user-data root.</param>
    internal static void DeleteStaleFiles(string root)
    {
        var files = FilesRoot(root);
        try
        {
            if (Directory.Exists(files))
            {
                Directory.Delete(files, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not delete leftover media previews in {files}: {ex.Message}");
        }
    }

    internal sealed class MediaViewport : OverlayNativeWebViewHost
    {
        private const string Host = "wsgm-preview.local";
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
        private readonly UserDataContext _context;
        private readonly bool _image;
        private readonly string _source;
        private CoreWebView2Controller? _controller;
        private bool _covered;
        private string? _download;
        private IntPtr _handle;
        private CancellationTokenSource? _load;
        private bool _mediaFailed;
        private bool _ready;
        private double? _seek;
        private double? _volume;
        private bool _setting;

        internal MediaViewport(UserDataContext context, string source, bool image)
        {
            _source = source;
            _context = context;
            _image = image;
            SizeChanged += (_, _) => Resize();
        }

        internal event Action<string>? Status;

        /// <inheritdoc />
        protected override void OnNativeCreated(nint handle)
        {
            _handle = handle;
        }

        /// <inheritdoc />
        protected override void OnNativeDestroying(nint handle)
        {
            _load?.Cancel();
            _load?.Dispose();
            _load = null;
            _controller?.Close();
            _controller = null;
            _ready = false;
            _seek = null;
            _volume = null;
            _handle = IntPtr.Zero;
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

            if (command == "seek")
            {
                _seek = value;
            }
            else if (command == "volume")
            {
                _volume = value;
            }
            else
            {
                _ = ExecuteAsync(command);
                return;
            }

            if (!_setting)
            {
                _ = SetValuesAsync();
            }
        }

        private async Task ExecuteAsync(string command)
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

        /// <summary>
        ///     Sends the latest seek and volume values with one script in flight, so a slider drag
        ///     sends what it last reached rather than one call per step.
        /// </summary>
        private async Task SetValuesAsync()
        {
            _setting = true;
            try
            {
                while (_controller is { } controller)
                {
                    string script;
                    if (_seek is { } seek)
                    {
                        _seek = null;
                        script =
                            "(()=>{const v=document.querySelector('video');if(v&&Number.isFinite(v.duration))v.currentTime=v.duration*" +
                            Fraction(seek / 1000) + ";})()";
                    }
                    else if (_volume is { } volume)
                    {
                        _volume = null;
                        script = "(()=>{const v=document.querySelector('video');if(v)v.volume=" + Fraction(volume / 100) +
                                 ";})()";
                    }
                    else
                    {
                        return;
                    }

                    await controller.CoreWebView2.ExecuteScriptAsync(script);
                }
            }
            catch (Exception ex)
            {
                Status?.Invoke("Playback failed: " + ex.Message);
            }
            finally
            {
                _setting = false;
            }
        }

        private static string Fraction(double value)
        {
            return Math.Clamp(value, 0, 1).ToString(CultureInfo.InvariantCulture);
        }

        private async Task InitializeAsync(CancellationToken token)
        {
            var handle = _handle;
            string? download = null;
            try
            {
                Status?.Invoke("Loading preview…");
                var media = LocalPath(_source);
                if (media is null)
                {
                    download = Path.Combine(FilesRoot(_context.Root), Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(download);
                    media = Path.Combine(download, _image ? "preview.image" : "preview.webm");
                    await DownloadAsync(media, token);
                }
                else if (!File.Exists(media))
                {
                    throw new FileNotFoundException("The preview file no longer exists.", media);
                }

                if (_image)
                {
                    var extension = ImageExtension(media);
                    if (download is not null)
                    {
                        var named = Path.ChangeExtension(media, extension);
                        File.Move(media, named);
                        media = named;
                    }
                }

                token.ThrowIfCancellationRequested();
                var profile = Path.Combine(_context.Root, "MediaPreview");
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
                token.ThrowIfCancellationRequested();
                var controller = await environment.CreateCoreWebView2ControllerAsync(handle);
                if (token.IsCancellationRequested || _handle != handle)
                {
                    controller.Close();
                    DeleteDownload(download);
                    return;
                }

                _controller = controller;
                _download = download;
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
                // The page reads the file in place: the virtual host maps the folder that holds it and
                // the generated page references only that file name. Every other request is refused.
                var src = "https://" + Host + "/" + Uri.EscapeDataString(Path.GetFileName(media));
                web.SetVirtualHostNameToFolderMapping(Host, Path.GetDirectoryName(Path.GetFullPath(media))!,
                    CoreWebView2HostResourceAccessKind.DenyCors);
                web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                web.WebResourceRequested += (_, e) =>
                {
                    if (e.Request.Uri != src)
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
                web.NavigateToString(
                    "<!doctype html><html><head><style>html,body{margin:0;height:100%;background:#171b20}video,img{width:100%;height:100%;object-fit:contain;pointer-events:none}</style></head><body>" +
                    (_image
                        ? "<img onerror=\"window.chrome.webview.postMessage('error')\" src=\"" + src + "\">"
                        : "<video onerror=\"window.chrome.webview.postMessage('error')\" onplaying=\"window.chrome.webview.postMessage('playing')\" onended=\"window.chrome.webview.postMessage('ended')\" playsinline preload=\"metadata\" src=\"" +
                          src + "\"></video>") + "</body></html>");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                DeleteDownload(download);
            }
            catch (Exception ex)
            {
                DeleteDownload(download);
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

        /// <summary>The user's own file, read in place, or null when the source must be downloaded.</summary>
        private static string? LocalPath(string source)
        {
            if (HttpUrls.IsHttps(source) || source.StartsWith("data:image/", StringComparison.Ordinal))
            {
                return null;
            }

            return Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : source;
        }

        /// <summary>Streams a remote source, or decodes an image data URI, into the preview's own folder.</summary>
        private async Task DownloadAsync(string path, CancellationToken token)
        {
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, true);
            if (!HttpUrls.IsHttps(_source))
            {
                await using var data = await OverlayPreviewImage.OpenAsync(_source, token);
                await data.CopyToAsync(output, token);
                return;
            }

            using var response = await Http.GetAsync(_source, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await BoundedHttp.CopyAsync(response.Content, output,
                _image ? ArtworkDownload.MaximumBytes : AnimationRepoClient.MaximumMovieBytes,
                () => new InvalidDataException(_image
                    ? "The animated image is larger than 16 MB."
                    : "The movie is larger than 64 MB."), token);
        }

        /// <summary>Checks an animated image's format and dimensions and returns its file extension.</summary>
        private static string ImageExtension(string path)
        {
            using var input = File.OpenRead(path);
            using var codec = SKCodec.Create(input) ??
                              throw new InvalidDataException("Unsupported animated artwork format.");
            if ((long)codec.Info.Width * codec.Info.Height > 64 * 1024 * 1024)
            {
                throw new InvalidDataException("The image dimensions are too large.");
            }

            return codec.EncodedFormat switch
            {
                SKEncodedImageFormat.Gif => ".gif",
                SKEncodedImageFormat.Webp => ".webp",
                SKEncodedImageFormat.Png => ".png",
                SKEncodedImageFormat.Jpeg => ".jpg",
                _ => throw new InvalidDataException("Unsupported animated artwork format.")
            };
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
            DeleteDownload(_download);
            _download = null;
        }

        /// <summary>Deletes a downloaded source's own folder; the user's file read in place is never touched.</summary>
        private static void DeleteDownload(string? folder)
        {
            try
            {
                if (folder is not null && Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
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
