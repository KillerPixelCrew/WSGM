using System;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Device.Msi.Claw8A2Vm;

// Own the sensor until every worker has stopped using it. A caller can abandon its bounded
// shutdown wait without releasing a live sensor or starting a second session beside it.
internal sealed class MotionWorkerSession(
    IDisposable sensors,
    CancellationTokenSource cancellation,
    Task producer,
    Task pump)
{
    internal async Task DrainAsync()
    {
        try
        {
            await Task.WhenAll(cancellation.CancelAsync(), producer, pump).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                sensors.Dispose();
            }
            finally
            {
                cancellation.Dispose();
            }
        }
    }
}
