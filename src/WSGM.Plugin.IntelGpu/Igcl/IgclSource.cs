namespace WSGM.Plugin.IntelGpu.Igcl;

/// <summary>One value read in one pass, with the driver result behind it.</summary>
/// <typeparam name="T">The value.</typeparam>
/// <remarks>
///     A pass is one observation, one command or one sync (<see cref="IgclSession.BeginPass" />). Several
///     controls publish fields of one driver structure, and within a pass they all share the one read.
///     Nothing is reused across passes, so a value is never older than the pass that publishes it.
/// </remarks>
internal struct PassCache<T>
    where T : struct
{
    private long _pass;
    private int _result;
    private T _value;

    /// <summary>The value read in this pass, if it was.</summary>
    /// <param name="pass">The current pass.</param>
    /// <param name="result">The driver result of that read.</param>
    /// <param name="value">The value it returned.</param>
    /// <returns><see langword="true" /> when this pass already read it.</returns>
    public readonly bool TryGet(long pass, out int result, out T value)
    {
        result = _result;
        value = _value;
        return _pass == pass;
    }

    /// <summary>Keeps a read for the rest of the pass.</summary>
    /// <param name="pass">The current pass.</param>
    /// <param name="result">The driver result.</param>
    /// <param name="value">The value.</param>
    public void Store(long pass, int result, T value)
    {
        _pass = pass;
        _result = result;
        _value = value;
    }

    /// <summary>Forgets the read, after a write changed what the driver holds.</summary>
    public void Invalidate()
    {
        _pass = 0;
    }
}

/// <summary>
///     One driver structure behind several controls: read once per pass through its get call, written
///     through its set call.
/// </summary>
/// <typeparam name="T">The IGCL structure; it starts with the <c>Size</c> field every IGCL structure has.</typeparam>
internal sealed unsafe class IgclSource<T>
    where T : unmanaged
{
    private readonly delegate* unmanaged[Cdecl]<nint, T*, int> _get;
    private readonly nint _handle;
    private readonly T _request;
    private readonly IgclSession _session;
    private readonly delegate* unmanaged[Cdecl]<nint, T*, int> _set;
    private PassCache<T> _cache;

    /// <summary>Binds a structure to its calls.</summary>
    /// <param name="session">The session.</param>
    /// <param name="handle">The adapter or output handle the calls take.</param>
    /// <param name="get">The get call.</param>
    /// <param name="set">The set call; the get call again for a get/set entry point.</param>
    /// <param name="request">What a get sends: version, operation and buffers, whatever the call needs.</param>
    public IgclSource(
        IgclSession session,
        nint handle,
        delegate* unmanaged[Cdecl]<nint, T*, int> get,
        delegate* unmanaged[Cdecl]<nint, T*, int> set,
        T request)
    {
        _session = session;
        _handle = handle;
        _get = get;
        _set = set;
        _request = request;
    }

    /// <summary>Reads the structure, once per pass.</summary>
    /// <param name="value">What the driver returned, whatever the result.</param>
    /// <returns>The driver result.</returns>
    public int Read(out T value)
    {
        if (_cache.TryGet(_session.Pass, out var result, out value))
        {
            return result;
        }

        value = _request;
        result = _session.Call(_get, _handle, ref value);
        _cache.Store(_session.Pass, result, value);
        return result;
    }

    /// <summary>Writes the structure and forgets the pass's read.</summary>
    /// <param name="request">The whole request.</param>
    /// <returns>The driver result.</returns>
    public int Write(T request)
    {
        _cache.Invalidate();
        return _session.Call(_set, _handle, ref request);
    }
}
