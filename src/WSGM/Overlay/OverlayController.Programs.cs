using System;
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

        RunAfterClose(window, "Programs activation", async token =>
        {
            try
            {
                var result = await Task.Run(() => programs.ActivateAsync(id, token), token);
                programs.ReportResult(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                programs.ReportResult(ex.Message);
                Log.Error("Programs activation failed.", ex);
            }
        });
    }
}
