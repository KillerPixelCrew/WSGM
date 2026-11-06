using System;
using System.Linq;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Overlay;

public sealed partial class OverlayController
{
    /// <summary>
    ///     Launches a program or focuses its window once the sheet or taskbar has closed; a program that can do
    ///     neither opens its editor on the sheet's Programs page instead, which says why.
    /// </summary>
    /// <param name="id">The program chosen on the Programs page, a Quick access pin or the taskbar's Start.</param>
    private void ActivateProgram(string id)
    {
        if (_disposed || OpenSurface() is not { } window || _services.Sources.Programs is not { } programs)
        {
            return;
        }

        if (programs.ReadState().FirstOrDefault(item => item.Definition.Id == id) is
            { CanLaunch: false, Running: false })
        {
            OpenProgramsPage(id);
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
