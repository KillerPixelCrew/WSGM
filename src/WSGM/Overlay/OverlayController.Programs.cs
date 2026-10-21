using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Overlay;

public sealed partial class OverlayController
{
    private void ActivateProgram(string id)
    {
        if (_disposed || _overlay is not { } window || _sources.Programs is not { } programs)
        {
            return;
        }

        _windowReturnCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _windowReturnCancellation = cancellation;
        _suppressFocusRestore = true;
        Log.Observe(ActivateProgramAfterCloseAsync(), "Programs activation");
        CloseOverlay(preserveWindowReturn: true);
        return;

        async Task ActivateProgramAfterCloseAsync()
        {
            TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += OnClosed;
            try
            {
                await closed.Task.WaitAsync(cancellation.Token);
                await _leaseRelease.WaitAsync(cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (!_disposed)
                {
                    var result = await Task.Run(() => programs.ActivateAsync(id, cancellation.Token), cancellation.Token);
                    programs.ReportResult(result);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                programs.ReportResult(ex.Message);
                Log.Error("Programs activation failed.", ex);
            }
            finally
            {
                window.Closed -= OnClosed;
                if (ReferenceEquals(_windowReturnCancellation, cancellation))
                {
                    _windowReturnCancellation = null;
                }

                cancellation.Dispose();
            }

            return;

            void OnClosed(object? sender, EventArgs args) => closed.TrySetResult();
        }
    }
}
