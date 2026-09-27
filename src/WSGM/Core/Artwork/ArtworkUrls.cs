using System;
using System.Linq;

namespace WSGM.Core;

/// <summary>Keeps credentials out of artwork URLs that are shown, stored or logged.</summary>
/// <remarks>
///     Screenscraper answers with media URLs that repeat the request's own credentials in their query,
///     the user's account password among them. Those URLs are shown to Steam's page as thumbnails,
///     kept in the Game Library's saved picks and written to the log when a download fails, so the
///     user's account is taken out of them before any of that, and put back only by the provider for
///     the one download that needs it.
/// </remarks>
internal static class ArtworkUrls
{
    /// <summary>The user's own account: taken out of every URL that leaves the provider.</summary>
    private static readonly string[] AccountParameters = ["ssid", "sspassword"];

    /// <summary>Every credential a URL can carry, hidden when one is logged.</summary>
    private static readonly string[] CredentialParameters =
        ["ssid", "sspassword", "devid", "devpassword", "devdebugpassword"];

    /// <summary>Removes the user's account from a URL, keeping everything else as it was.</summary>
    /// <param name="url">The URL.</param>
    /// <returns>The URL without the account's parameters.</returns>
    internal static string WithoutAccount(string url)
    {
        return Rewrite(url, name => AccountParameters.Contains(name, StringComparer.OrdinalIgnoreCase)
            ? null
            : string.Empty);
    }

    /// <summary>A URL fit for the log: every credential's value replaced.</summary>
    /// <param name="url">The URL.</param>
    /// <returns>The URL with credential values shown as <c>***</c>.</returns>
    internal static string Redact(string url)
    {
        return Rewrite(url, name => CredentialParameters.Contains(name, StringComparer.OrdinalIgnoreCase)
            ? "***"
            : string.Empty);
    }

    /// <summary>Rewrites a URL's query parameter by parameter.</summary>
    /// <param name="url">The URL.</param>
    /// <param name="replacement">
    ///     For a parameter's name: null to drop it, empty to keep it as it is, or the value to show
    ///     instead.
    /// </param>
    private static string Rewrite(string url, Func<string, string?> replacement)
    {
        var start = url.IndexOf('?', StringComparison.Ordinal);
        if (start < 0)
        {
            return url;
        }

        var end = url.IndexOf('#', start);
        var query = end < 0 ? url[(start + 1)..] : url[(start + 1)..end];
        var kept = query.Split('&')
            .Select(pair =>
            {
                var equals = pair.IndexOf('=', StringComparison.Ordinal);
                var name = Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals]);
                return replacement(name) switch
                {
                    null => null,
                    { Length: 0 } => pair,
                    var shown => (equals < 0 ? pair : pair[..equals]) + "=" + shown
                };
            })
            .Where(pair => pair is not null);
        var rebuilt = string.Join('&', kept);
        return url[..start] + (rebuilt.Length > 0 ? "?" + rebuilt : string.Empty) +
               (end < 0 ? string.Empty : url[end..]);
    }
}
