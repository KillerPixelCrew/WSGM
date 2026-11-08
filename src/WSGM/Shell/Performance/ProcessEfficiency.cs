using System;
using System.Collections.Generic;
using System.Threading;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Keeps the resident process on EcoQoS whenever none of its windows is on screen.</summary>
/// <remarks>Windows does not pass the setting to child processes, so Steam and games are unaffected.</remarks>
internal sealed class ProcessEfficiency
{
    private readonly Lock _gate = new();
    private readonly Action<string> _info;
    private readonly HashSet<string> _surfaces = new(StringComparer.Ordinal);
    private readonly Action<string> _warn;
    private readonly Action<bool> _write;
    private bool? _applied;
    private bool _started;

    internal ProcessEfficiency()
        : this(ProcessPowerThrottling.SetEfficient, Log.Info, Log.Warn)
    {
    }

    internal ProcessEfficiency(Action<bool> write, Action<string> info, Action<string> warn)
    {
        _write = write;
        _info = info;
        _warn = warn;
    }

    /// <summary>Begins applying the level; boot's cold work has run at full speed until now.</summary>
    internal void Start()
    {
        lock (_gate)
        {
            _started = true;
            Apply();
        }
    }

    /// <summary>Records a WSGM window coming on screen.</summary>
    /// <param name="surfaceId">The window's stable identity.</param>
    internal void SurfaceOpened(string surfaceId)
    {
        lock (_gate)
        {
            _surfaces.Add(surfaceId);
            Apply();
        }
    }

    /// <summary>Records a WSGM window leaving the screen.</summary>
    /// <param name="surfaceId">The window's stable identity.</param>
    internal void SurfaceClosed(string surfaceId)
    {
        lock (_gate)
        {
            _surfaces.Remove(surfaceId);
            Apply();
        }
    }

    private void Apply()
    {
        var efficient = _surfaces.Count == 0;
        if (!_started || _applied == efficient)
        {
            return;
        }

        _applied = efficient;
        try
        {
            _write(efficient);
            _info($"Process efficiency: {(efficient ? "EcoQoS" : "HighQoS")}.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _warn($"Process efficiency change failed: {ex.Message}");
        }
    }
}
