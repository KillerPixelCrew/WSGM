using System.Diagnostics;

namespace WSGM.Plugin.Artwork;

internal static class ArtworkLog
{
    internal static void Warn(string message)
    {
        Trace.TraceWarning("Artwork plugin: {0}", message);
    }
}
