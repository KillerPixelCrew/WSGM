using System;
using System.Threading;
using System.Threading.Tasks;
using LibHandheld;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Hides the handheld's built-in pad as the session's first step, before anything reads controllers.</summary>
/// <remarks>
///     Steam keeps a pad it opened before the cloak: on an Atlas whose Steam started four seconds before the
///     controller start hid the pad, Steam drove the physical XInput pad beside the virtual Deck for the whole
///     session, and every Quick Access press arrived twice (2026-11-02). LibHandheld names the exact pad nodes;
///     the hide goes through the same ownership ledger the controller start uses, so every exit removes it.
/// </remarks>
internal static class SessionPadHide
{
    /// <summary>Hides and confirms the pad when the session manages it.</summary>
    /// <param name="config">The session configuration.</param>
    /// <param name="root">The per-user data root holding the HidHide ownership ledger.</param>
    /// <param name="cancellationToken">Cancels the hide.</param>
    /// <returns>Completion; a failure is logged and the controller start tries again.</returns>
    internal static async Task HideAsync(AppConfig config, string root, CancellationToken cancellationToken)
    {
        if (!config.DeviceIntegration.Enabled || !config.DeviceIntegration.ControllerManagementEnabled)
        {
            return;
        }

        try
        {
            var pads = HandheldDevice.GetPadsToHide();
            if (pads.Count == 0)
            {
                Log.Info("Session pad hide: this handheld has no pad to hide.");
                return;
            }

            var result = await HidHideOwnership.ForUser(root)
                .HideAsync(ControllerManager.ReaderApplicationPath(), pads, cancellationToken)
                .ConfigureAwait(false);
            if (result.Succeeded)
            {
                Log.Info($"Session pad hide: {result.Detail}");
            }
            else
            {
                Log.Warn($"Session pad hide failed before Steam starts: {result.Detail}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Log.Error("Session pad hide failed before Steam starts", ex);
        }
    }
}
