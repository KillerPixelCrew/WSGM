using WindowsDeviceControl;

namespace WSGM.Shell;

/// <summary>The wording WSGM shows and stores for an audio endpoint.</summary>
internal static class AudioEndpointText
{
    /// <summary>The endpoint's friendly name, or "Audio device" when Windows reports none.</summary>
    /// <param name="endpoint">The endpoint Core Audio listed.</param>
    /// <returns>The name to show, and to store in a captured profile.</returns>
    internal static string Name(CoreAudio.AudioEndpoint endpoint)
    {
        return string.IsNullOrEmpty(endpoint.Name) ? "Audio device" : endpoint.Name;
    }
}
