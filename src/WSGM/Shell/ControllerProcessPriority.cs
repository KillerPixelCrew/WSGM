using System;
using System.Diagnostics;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Owns the temporary process priority used by asynchronous controller emulation.</summary>
/// <remarks>Calls are serialized by the controller manager's state gate.</remarks>
internal sealed class ControllerProcessPriority
{
    private readonly Func<ProcessPriorityClass> _read;
    private readonly Action<ProcessPriorityClass> _write;
    private readonly Action<string> _info;
    private readonly Action<string> _warn;
    private ProcessPriorityClass? _original;
    private bool _active;

    internal ControllerProcessPriority()
        : this(ReadCurrent, WriteCurrent, Log.Info, Log.Warn)
    {
    }

    internal ControllerProcessPriority(
        Func<ProcessPriorityClass> read,
        Action<ProcessPriorityClass> write,
        Action<string> info,
        Action<string> warn)
    {
        _read = read;
        _write = write;
        _info = info;
        _warn = warn;
    }

    internal void SetActive(bool active)
    {
        if (_active == active)
        {
            return;
        }

        _active = active;
        try
        {
            if (active)
            {
                ProcessPriorityClass current = _read();
                if (current is ProcessPriorityClass.High or ProcessPriorityClass.RealTime)
                {
                    return;
                }

                // Capture before the write so cleanup can reconcile even an uncertain failure.
                _original = current;
                _write(ProcessPriorityClass.High);
                _info($"Controller process priority: {current} -> High.");
            }
            else if (_original is { } original)
            {
                if (_read() is ProcessPriorityClass.High)
                {
                    _write(original);
                    _info($"Controller process priority: High -> {original}.");
                }

                // An external priority change supersedes our ownership.
                _original = null;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _warn($"Controller process priority {(active ? "boost" : "restore")} failed: {ex.Message}");
        }
    }

    private static ProcessPriorityClass ReadCurrent()
    {
        using Process process = Process.GetCurrentProcess();
        return process.PriorityClass;
    }

    private static void WriteCurrent(ProcessPriorityClass priority)
    {
        using Process process = Process.GetCurrentProcess();
        process.PriorityClass = priority;
    }
}
