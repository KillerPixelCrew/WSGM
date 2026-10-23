using System;

namespace WSGM.Browser;

/// <summary>Shared URL policy for trusted chrome and the isolated browser host.</summary>
internal static class BrowserNavigation
{
    internal const string Home = "about:blank";
    internal const string SearchPrefix = "https://www.google.com/search?q=";
    internal const int MaximumAddressLength = 4096;

    internal static bool IsAllowed(string? address)
    {
        return address is { Length: > 0 and <= MaximumAddressLength }
               && (address == Home || Uri.TryCreate(address, UriKind.Absolute, out var uri)
                   && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                   && uri.UserInfo.Length == 0 && uri.Host.Length > 0);
    }

    internal static string Resolve(string text, string? game = null)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return Home;
        }

        if (text.Length > MaximumAddressLength)
        {
            throw new ArgumentException("The address or search is too long.");
        }

        if (game is null)
        {
            if (IsAllowed(text))
            {
                return text;
            }

            if (text.Contains("://", StringComparison.Ordinal))
            {
                throw new ArgumentException("Only HTTP and HTTPS addresses can be opened.");
            }

            if (!text.Contains(' ') && Uri.TryCreate("https://" + text, UriKind.Absolute, out var candidate)
                && (candidate.Host.Contains('.') || candidate.IsLoopback) && IsAllowed(candidate.AbsoluteUri))
            {
                return candidate.AbsoluteUri;
            }

            if (Uri.TryCreate(text, UriKind.Absolute, out _))
            {
                throw new ArgumentException("Only HTTP and HTTPS addresses can be opened.");
            }
        }

        var query = game is null ? text : game + " " + text;
        var address = SearchPrefix + Uri.EscapeDataString(query);
        return IsAllowed(address) ? address : throw new ArgumentException("The encoded search address is too long.");
    }
}
