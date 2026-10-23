using System;
using Avalonia;
using Avalonia.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>Avalonia owns clipping and placement; the session owns the child process and its page.</summary>
internal sealed class BrowserViewport : OverlayNativeWebViewHost
{
    private BrowserSession? _session;
    private TopLevel? _topLevel;
    private int _lastWidth;
    private int _lastHeight;

    internal BrowserViewport()
    {
        Focusable = false;
        SizeChanged += (_, _) => Resize();
        LayoutUpdated += OnLayoutUpdated;
    }

    protected override bool AcceptsInput => true;

    internal void Attach(BrowserSession? session)
    {
        if (ReferenceEquals(_session, session))
        {
            return;
        }

        if (NativeHandle != 0)
        {
            _session?.DetachViewport(NativeHandle);
        }

        _session = session;
        if (NativeHandle != 0)
        {
            _session?.AttachViewport(NativeHandle);
            Resize();
        }
    }

    protected override void OnNativeCreated(nint handle)
    {
        _session?.AttachViewport(handle);
        Resize();
    }

    protected override void OnNativeDestroying(nint handle) => _session?.DetachViewport(handle);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
        {
            _topLevel.PropertyChanged += OnTopLevelChanged;
            if (_topLevel is Window window)
            {
                window.PositionChanged += OnPositionChanged;
            }
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_topLevel is not null)
        {
            _topLevel.PropertyChanged -= OnTopLevelChanged;
            if (_topLevel is Window window)
            {
                window.PositionChanged -= OnPositionChanged;
            }

            _topLevel = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnTopLevelChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name is "RenderScaling" or "ClientSize")
        {
            Resize();
        }
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        Resize();
        _session?.NotifyParentPositionChanged();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => Resize();

    private void Resize()
    {
        // The native anchor already has the final physical size, including the Overlay's
        // content transform. Reading it avoids multiplying only by monitor DPI and missing
        // RootScale at the 980x640 layout floor.
        if (NativeHandle != 0 && Interop.BrowserNative.GetClientRect(NativeHandle, out var rect))
        {
            var width = Math.Max(1, rect.Right - rect.Left);
            var height = Math.Max(1, rect.Bottom - rect.Top);
            if (width != _lastWidth || height != _lastHeight)
            {
                _lastWidth = width;
                _lastHeight = height;
                _session?.Resize(width, height);
            }
        }
    }
}
