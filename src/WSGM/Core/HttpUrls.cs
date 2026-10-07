using System;

namespace WSGM.Core;

internal static class HttpUrls
{
    internal static bool IsHttps(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
    }
}
