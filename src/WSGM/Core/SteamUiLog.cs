namespace WSGM.Core;

/// <summary>Where the Steam UI machinery writes its diagnostics.</summary>
/// <remarks>
/// The host supplies this. Without it the machinery would either depend on one application's
/// logger or say nothing at all, and saying nothing is not an option here: remote diagnosis of a
/// CEF surface is a pasted log, and the lines below are frequently the only evidence that a control
/// the user operated did nothing.
/// </remarks>
public interface ISteamUiLog
{
    /// <summary>Records something that happened.</summary>
    /// <param name="message">The line to write.</param>
    void Info(string message);

    /// <summary>Records a failure the caller recovered from.</summary>
    /// <param name="message">The line to write.</param>
    void Warn(string message);

    /// <summary>Records a line only when it differs from the last one written under this key.</summary>
    /// <param name="key">Identifies the state being reported, so unrelated lines do not suppress
    /// each other.</param>
    /// <param name="message">The line to write.</param>
    /// <param name="warning">Whether this is a failure rather than an observation.</param>
    /// <remarks>
    /// For anything on a poll or a repeating gate. A patch can repeat a refused write on its own
    /// schedule, and writing every repeat buries the transition that matters in thousands of
    /// identical lines. Suppressed repeats must be counted rather than dropped: a stalled timer and
    /// a steady state have to stay distinguishable in the log.
    /// </remarks>
    void Change(string key, string message, bool warning = false);
}

/// <summary>The sink the Steam UI machinery writes to.</summary>
/// <remarks>
/// A settable static rather than a constructor parameter on a dozen types. The alternative threads
/// a logger through the transport, the bridge, the patch manager and every patch context, which is
/// churn that buys nothing: there is one sink per process and it is set before anything starts.
/// <para>
/// It defaults to discarding, so a consumer that never sets one still runs — and a test that never
/// sets one writes nothing, which is what this repository's tests require.
/// </para>
/// </remarks>
public static class SteamUiLog
{
    private sealed class Discard : ISteamUiLog
    {
        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }

        public void Change(string key, string message, bool warning = false)
        {
        }
    }

    private static ISteamUiLog _sink = new Discard();

    /// <summary>Directs the machinery's diagnostics at the host's logger.</summary>
    /// <param name="sink">The host's sink, or <see langword="null"/> to discard.</param>
    public static void Use(ISteamUiLog? sink) => _sink = sink ?? new Discard();

    /// <summary>Records something that happened.</summary>
    /// <param name="message">The line to write.</param>
    public static void Info(string message) => _sink.Info(message);

    /// <summary>Records a failure the caller recovered from.</summary>
    /// <param name="message">The line to write.</param>
    public static void Warn(string message) => _sink.Warn(message);

    /// <summary>Records a line only when it differs from the last one under this key.</summary>
    /// <param name="key">Identifies the state being reported.</param>
    /// <param name="message">The line to write.</param>
    /// <param name="warning">Whether this is a failure rather than an observation.</param>
    public static void Change(string key, string message, bool warning = false) =>
        _sink.Change(key, message, warning);
}
