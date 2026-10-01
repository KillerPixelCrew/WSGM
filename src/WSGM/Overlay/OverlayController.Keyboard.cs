using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Overlay;

public sealed partial class OverlayController
{
    private async Task RequestOnScreenKeyboardAsync()
    {
        if (_keyboardRequestPending || _disposed || _overlay is not { } window)
        {
            return;
        }

        _keyboardRequestPending = true;
        using var cancellation = new CancellationTokenSource();
        _keyboardRequestCancellation = cancellation;
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnClosed(object? sender, EventArgs args)
        {
            closed.TrySetResult();
        }

        window.Closed += OnClosed;
        try
        {
            CloseOverlay();
            await closed.Task.WaitAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed)
            {
                return;
            }

            var shown = ShowOnScreenKeyboard is { } show
                        && await show(cancellation.Token);
            if (!shown && !_disposed && !cancellation.IsCancellationRequested)
            {
                WarnOrReopen("On-screen keyboard unavailable. Check Steam or Windows touch keyboard.");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"On-screen keyboard request failed: {ex.Message}");
            if (!_disposed && !cancellation.IsCancellationRequested)
            {
                WarnOrReopen("On-screen keyboard could not be opened.");
            }
        }
        finally
        {
            window.Closed -= OnClosed;
            if (ReferenceEquals(_keyboardRequestCancellation, cancellation))
            {
                _keyboardRequestCancellation = null;
                _keyboardRequestPending = false;
            }
        }
    }

    private bool OpenKeyboard(string prompt, string initial, int maxLength, Action<string> onAccept)
    {
        if (_overlay is not { } overlay)
        {
            return false;
        }

        var keyboard = new KeyboardPanel(prompt, initial, maxLength);
        keyboard.Accepted += onAccept;
        overlay.ShowKeyboardSurface(keyboard);
        return true;
    }

    private void CloseKeyboardNow()
    {
        _overlay?.CloseAllSurfaces();
    }
}
