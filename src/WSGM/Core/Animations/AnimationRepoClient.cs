using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>The repository could not be read, or refused.</summary>
public sealed class AnimationRepoException(string message) : Exception(message);

/// <summary>SteamDeckRepo, the animation repository Animation Changer browses.</summary>
/// <remarks>
///     One request lists everything: <c>/api/posts/all</c> answers every post, and the boot and
///     suspend movies are the ones whose type is <c>boot_video</c> or <c>suspend_video</c>. A movie
///     is downloaded from <c>/post/download/{id}</c>. Filtering, sorting and searching happen on the
///     list, as the plugin does them, because the repository offers no query. The answer is a third
///     party's, so every field is read defensively and bounded.
/// </remarks>
public sealed class AnimationRepoClient
{
    /// <summary>Where SteamDeckRepo answers.</summary>
    public const string DefaultSiteUrl = "https://steamdeckrepo.com";

    /// <summary>The largest movie accepted.</summary>
    public const int MaximumMovieBytes = 64 * 1024 * 1024;

    private const int MaximumJsonBytes = 16 * 1024 * 1024;
    private const int MaximumListings = 4096;
    private readonly HttpClient _http;

    /// <summary>Creates the client.</summary>
    /// <param name="handler">The HTTP handler, or null for the shared default.</param>
    /// <param name="siteUrl">The repository's address, or null for SteamDeckRepo.</param>
    public AnimationRepoClient(HttpMessageHandler? handler = null, string? siteUrl = null)
    {
        SiteUrl = (siteUrl ?? DefaultSiteUrl).TrimEnd('/');
        _http = handler is null ? new HttpClient() : new HttpClient(handler, false);
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.MaxResponseContentBufferSize = MaximumMovieBytes;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WSGM");
    }

    /// <summary>The repository's address.</summary>
    public string SiteUrl { get; }

    /// <summary>Every boot and suspend movie the repository lists.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The listings, in the repository's order.</returns>
    /// <exception cref="AnimationRepoException">The repository did not answer, or answered something else.</exception>
    public async Task<IReadOnlyList<AnimationListing>> ListAsync(CancellationToken cancellationToken)
    {
        var url = SiteUrl + "/api/posts/all";
        string text;
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if ((int)response.StatusCode == 429)
            {
                throw new AnimationRepoException("The repository is rate limiting; try again in a minute.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AnimationRepoException($"The repository answered {(int)response.StatusCode}.");
            }

            using var body = await BoundedHttp.ReadAsync(response.Content, MaximumJsonBytes,
                () => new AnimationRepoException("The repository's list is larger than expected."),
                cancellationToken).ConfigureAwait(false);
            text = Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length);
        }
        catch (AnimationRepoException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                   && !cancellationToken.IsCancellationRequested)
        {
            throw new AnimationRepoException($"The repository could not be reached: {ex.Message}");
        }

        return Parse(text, SiteUrl);
    }

    /// <summary>Reads a list answer.</summary>
    /// <param name="json">The answer.</param>
    /// <param name="siteUrl">The repository's address, which the download link is built on.</param>
    /// <returns>The boot and suspend movies in it.</returns>
    /// <exception cref="AnimationRepoException">The answer is not the repository's shape.</exception>
    public static IReadOnlyList<AnimationListing> Parse(string json, string siteUrl)
    {
        List<AnimationListing> listings = [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("posts", out var posts)
                || posts.ValueKind != JsonValueKind.Array)
            {
                throw new AnimationRepoException("The repository's list has no posts.");
            }

            foreach (var post in posts.EnumerateArray())
            {
                if (post.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var type = ThemeJson.OptionalString(post, "type");
                var target = type switch
                {
                    "boot_video" => AnimationTargets.Boot,
                    "suspend_video" => AnimationTargets.Suspend,
                    _ => null
                };
                var id = ThemeJson.OptionalString(post, "id");
                if (target is null || string.IsNullOrWhiteSpace(id) || !ValidId(id))
                {
                    continue;
                }

                var author = post.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
                    ? ThemeJson.OptionalString(user, "steam_name") ?? string.Empty
                    : string.Empty;
                listings.Add(new AnimationListing(
                    id,
                    ThemeJson.OptionalString(post, "title") ?? id,
                    author,
                    ThemeJson.OptionalString(post, "content") ?? string.Empty,
                    Url(ThemeJson.OptionalString(post, "thumbnail")),
                    Url(ThemeJson.OptionalString(post, "video")),
                    siteUrl.TrimEnd('/') + "/post/download/" + id,
                    Count(post, "likes"),
                    Count(post, "downloads"),
                    ThemeJson.OptionalString(post, "updated_at") ?? string.Empty,
                    target));
                if (listings.Count >= MaximumListings)
                {
                    break;
                }
            }
        }
        catch (JsonException ex)
        {
            throw new AnimationRepoException($"The repository's list could not be read: {ex.Message}");
        }

        return listings;
    }

    /// <summary>Downloads one movie.</summary>
    /// <param name="listing">The movie.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The movie's bytes, positioned at the start.</returns>
    /// <exception cref="AnimationRepoException">The download failed or is too large.</exception>
    public async Task<MemoryStream> DownloadAsync(AnimationListing listing, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http
                .GetAsync(listing.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new AnimationRepoException($"The download answered {(int)response.StatusCode}.");
            }

            return await BoundedHttp.ReadAsync(response.Content, MaximumMovieBytes,
                () => new AnimationRepoException("The movie is larger than the 64 MB safety limit."),
                cancellationToken).ConfigureAwait(false);
        }
        catch (AnimationRepoException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                   && !cancellationToken.IsCancellationRequested)
        {
            throw new AnimationRepoException($"The movie could not be downloaded: {ex.Message}");
        }
    }

    /// <summary>Whether an id is one the library can name a file by.</summary>
    /// <param name="id">The candidate.</param>
    internal static bool ValidId(string id)
    {
        if (id.Length is 0 or > 64)
        {
            return false;
        }

        foreach (var character in id)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static string Url(string? value)
    {
        return value is not null && value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                                 && value.Length <= 2048
            ? value
            : string.Empty;
    }

    private static int Count(JsonElement post, string property)
    {
        return post.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
                                                            && value.TryGetInt32(out var count)
            ? Math.Max(0, count)
            : 0;
    }
}
