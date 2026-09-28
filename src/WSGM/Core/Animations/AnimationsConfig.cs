namespace WSGM.Core;

/// <summary>Which boot movie Big Picture starts with, and whether it is reshuffled at start.</summary>
/// <remarks>
///     The choice is a library id, or empty for Steam's own movie. The content is the library's;
///     this is only the choice, which is what lets a shuffle pick again and a return to Steam's own
///     leave the downloads in place.
/// </remarks>
public sealed class AnimationsConfig
{
    /// <summary>The library id playing at Big Picture's start, or empty for Steam's own.</summary>
    public string Boot { get; set; } = string.Empty;

    /// <summary>Whether the boot movie is picked anew from the library each time WSGM starts.</summary>
    public bool ShuffleOnStart { get; set; }

    /// <summary>
    ///     Steam's own startup movie choice, set aside while one of WSGM's plays, or null when WSGM set
    ///     nothing aside. A return to Steam's own gives it back.
    /// </summary>
    public SteamStartupMovieSetAside? SteamSetAside { get; set; }

    /// <summary>A copy, for a service that shows a change before the saved configuration reaches it again.</summary>
    /// <returns>The copy.</returns>
    public AnimationsConfig Clone()
    {
        return new AnimationsConfig
        {
            Boot = Boot,
            ShuffleOnStart = ShuffleOnStart,
            SteamSetAside = SteamSetAside?.Clone()
        };
    }
}

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
