namespace WSGM.Core;

/// <summary>Steam's own startup movie choice as WSGM keeps it while its own movie plays.</summary>
/// <remarks>
///     Steam lets the choice on Settings &gt; Customization replace the override file, so WSGM puts
///     Steam on its default movie while one of its own is chosen and keeps what Steam held here.
/// </remarks>
public sealed class SteamStartupMovieSetAside
{
    /// <summary>Steam's <c>startup_movie_id</c>: the Points Shop item, or empty.</summary>
    public string MovieId { get; set; } = string.Empty;

    /// <summary>Steam's <c>startup_movie_local_path</c>: the movie it played, or empty.</summary>
    public string LocalPath { get; set; } = string.Empty;

    /// <summary>Steam's <c>startup_movie_shuffle</c>.</summary>
    public bool Shuffle { get; set; }

    /// <summary>A copy.</summary>
    /// <returns>The copy.</returns>
    public SteamStartupMovieSetAside Clone()
    {
        return new SteamStartupMovieSetAside { MovieId = MovieId, LocalPath = LocalPath, Shuffle = Shuffle };
    }
}
