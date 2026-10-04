using System;

namespace WSGM.PackagedLaunch;

/// <summary>Requests owner cancellation without retiring any owner-held resources.</summary>
/// <param name="cancel">The owner's cancellation request.</param>
public sealed class ShutdownRequest(Action cancel)
{
    /// <summary>Contains managed failures at a native shutdown callback boundary.</summary>
    public void Request()
    {
        try
        {
            cancel();
        }
        catch (Exception)
        {
            // Cancellation callbacks can throw, or the owner can already have disposed its source.
            // Neither may escape a native callback. The journal covers interrupted retirement.
        }
    }
}
