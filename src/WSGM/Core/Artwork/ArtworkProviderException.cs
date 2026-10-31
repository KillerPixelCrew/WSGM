using System;

namespace WSGM.Core;

/// <summary>An artwork provider could not answer: a failure the user should see, never "no images".</summary>
/// <param name="message">What went wrong, in words the page shows.</param>
/// <param name="providerId">The provider that failed, when known.</param>
/// <param name="paused">Whether further requests must wait for credentials or an allowance reset.</param>
internal class ArtworkProviderException(string message, string providerId = "", bool paused = false)
    : Exception(message)
{
    internal string ProviderId { get; } = providerId;
    internal bool Paused { get; } = paused;
}
