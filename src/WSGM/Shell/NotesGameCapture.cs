using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>
///     Captures the game's own window for a Notes screenshot through Windows Graphics Capture.
/// </summary>
/// <remarks>
///     <para>
///         Why this path: Steam exposes no screenshot it can be asked to take for an arbitrary running game. Its
///         client <c>TakeScreenshot</c> belongs to Game Recording's timeline and needs background recording, and
///         its overlay hotkey would be synthetic input. WSGM had no capture path of its own. A window capture is
///         the least invasive one left, and the only one that cannot include another window.
///     </para>
///     <para>
///         Only the window in front is captured, and only when the game process owns it. Anything else fails
///         with a reason, so the note never receives the desktop, WSGM's overlay or another application.
///         Steam's in-game overlay is drawn into the game's own frames, so a capture is refused while Steam
///         reports it open over the game. A frame that comes back black is exclusive fullscreen or protected
///         content and is reported as such rather than saved.
///     </para>
/// </remarks>
internal static class NotesGameCapture
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Captures the game's foreground window as a JPEG.</summary>
    /// <param name="target">The game.</param>
    /// <param name="steamOverlayOpen">Reports whether Steam's overlay is open over a game process; null without Steam.</param>
    /// <param name="cancellationToken">Cancels waiting for the frame.</param>
    /// <returns>The image, or the reason there is none.</returns>
    internal static Task<NotesCaptureFrame> CaptureAsync(NotesCaptureTarget target,
        Func<uint, CancellationToken, Task<bool>>? steamOverlayOpen, CancellationToken cancellationToken)
    {
        return Task.Run(() => CaptureCoreAsync(target, steamOverlayOpen, cancellationToken), cancellationToken);
    }

    /// <summary>Whether a process still runs.</summary>
    /// <param name="processId">The process.</param>
    /// <returns>False once it has exited or cannot be found.</returns>
    internal static bool IsAlive(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            // HasExited can be unreadable across integrity levels; the process exists, so it runs.
            return ex is System.ComponentModel.Win32Exception;
        }
    }

    /// <summary>Describes the application owning a window, if it is one a screenshot may target.</summary>
    /// <param name="window">The window that was in front before a Notes surface opened.</param>
    /// <returns>The target, or null for WSGM, Steam, the shell and other non-game processes.</returns>
    internal static NotesCaptureTarget? DescribeTarget(nint window)
    {
        if (window == 0 || !NativeMethods.IsWindow(window))
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId == (uint)Environment.ProcessId)
        {
            return null;
        }

        string name;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            name = process.ProcessName + ".exe";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }

        return ForegroundApplicationFilter.Classify(name) is ForegroundApplicationKind.Application
            ? new NotesCaptureTarget(processId, window, 0, name)
            : null;
    }

    private static async Task<NotesCaptureFrame> CaptureCoreAsync(NotesCaptureTarget target,
        Func<uint, CancellationToken, Task<bool>>? steamOverlayOpen, CancellationToken cancellationToken)
    {
        var window = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(window, out var owner);
        if (window == 0 || owner != target.ProcessId)
        {
            Log.Info($"Notes capture refused: foreground pid {owner} is not the game's {target.ProcessId}.");
            return NotesCaptureFrame.Failed(
                $"{target.Description} was not in front when the countdown ended, so nothing was captured.");
        }

        if (NativeMethods.IsIconic(window))
        {
            return NotesCaptureFrame.Failed($"{target.Description} was minimized when the countdown ended.");
        }

        if (steamOverlayOpen is not null && await steamOverlayOpen(target.ProcessId, cancellationToken)
                .ConfigureAwait(false))
        {
            return NotesCaptureFrame.Failed(
                "Steam's overlay was open over the game, so nothing was captured. Close it during the countdown.");
        }

        NotesCaptureInterop.EnsureMultithreadedApartment();
        if (!GraphicsCaptureSession.IsSupported())
        {
            return NotesCaptureFrame.Failed("Windows cannot capture game windows on this PC.");
        }

        try
        {
            using var device = NotesCaptureInterop.CreateDevice();
            var item = NotesCaptureInterop.CreateItem(window);
            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
            TaskCompletionSource<Direct3D11CaptureFrame> arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pool.FrameArrived += (sender, _) =>
            {
                var frame = sender.TryGetNextFrame();
                if (frame is not null && !arrived.TrySetResult(frame))
                {
                    frame.Dispose();
                }
            };
            using var session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            var borderless = NotesCaptureInterop.TryHideBorder(session);
            session.StartCapture();
            using var captured = await arrived.Task.WaitAsync(FrameTimeout, cancellationToken).ConfigureAwait(false);
            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(captured.Surface, BitmapAlphaMode.Ignore);
            if (IsBlank(bitmap))
            {
                Log.Info("Notes capture: the frame was blank (exclusive fullscreen or protected content).");
                return NotesCaptureFrame.Failed(
                    "The game's picture could not be read. It may use exclusive fullscreen or protected content; "
                    + "borderless or windowed mode can be captured.");
            }

            var bytes = await EncodeAsync(bitmap).ConfigureAwait(false);
            Log.Info($"Notes capture: {bitmap.PixelWidth}x{bitmap.PixelHeight} from pid {target.ProcessId}, "
                     + $"{bytes.Length} bytes, border hidden={borderless}.");
            return new NotesCaptureFrame(bytes, bitmap.PixelWidth, bitmap.PixelHeight, null);
        }
        catch (TimeoutException)
        {
            return NotesCaptureFrame.Failed("The game did not draw a frame to capture.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"Notes capture failed: {ex.GetType().Name}: {ex.Message}");
            return NotesCaptureFrame.Failed("The game window could not be captured.");
        }
    }

    private static bool IsBlank(SoftwareBitmap bitmap)
    {
        var length = (uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4);
        Windows.Storage.Streams.Buffer buffer = new(length);
        bitmap.CopyToBuffer(buffer);
        using var reader = DataReader.FromBuffer(buffer);
        var pixels = new byte[buffer.Length];
        reader.ReadBytes(pixels);
        // Sampled: a real frame differs from black somewhere in every few hundred pixels.
        for (var index = 0; index + 2 < pixels.Length; index += 4 * 7)
        {
            if (pixels[index] > 8 || pixels[index + 1] > 8 || pixels[index + 2] > 8)
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<byte[]> EncodeAsync(SoftwareBitmap bitmap)
    {
        using InMemoryRandomAccessStream stream = new();
        BitmapPropertySet options = new() { { "ImageQuality", new BitmapTypedValue(0.9f, Windows.Foundation.PropertyType.Single) } };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, options);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
