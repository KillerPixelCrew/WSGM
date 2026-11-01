using System;
using System.Collections.Generic;
using System.Diagnostics;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>The live desktop a hotkey is sent through: the foreground window and <see cref="KeyboardInput" />.</summary>
internal sealed class HotkeyDesktop : IHotkeyDesktop
{
    /// <summary>The one live desktop.</summary>
    internal static readonly HotkeyDesktop Instance = new();

    private HotkeyDesktop()
    {
    }

    /// <inheritdoc />
    public nint Foreground()
    {
        return NativeMethods.GetForegroundWindow();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The same rule that decides whether a foreground window drives a per-game profile: WSGM, Steam and the
    ///     other launchers, Explorer and the shell's own surfaces never count as the game.
    /// </remarks>
    public HotkeyTarget? Describe(nint window)
    {
        if (window == 0 || !NativeMethods.IsWindow(window))
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId == (uint)Environment.ProcessId)
        {
            return null;
        }

        string name;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            name = process.ProcessName + ".exe";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }

        return ForegroundApplicationFilter.Classify(name) is ForegroundApplicationKind.Application
            ? new HotkeyTarget(window, processId, name)
            : null;
    }

    /// <inheritdoc />
    public void BringToFront(nint window)
    {
        WindowFinder.BringToForeground(window);
    }

    /// <inheritdoc />
    public KeyboardInput.SendResult Send(IReadOnlyList<uint> keys, int pressDelayMs)
    {
        return KeyboardInput.SendChord(keys, pressDelayMs);
    }
}
