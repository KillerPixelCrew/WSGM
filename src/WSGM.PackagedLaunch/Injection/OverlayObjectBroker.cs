using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace WSGM.PackagedLaunch;

/// <summary>
///     Opens the renderer's named IPC objects in the desktop namespace and duplicates their handles
///     into one game.
/// </summary>
/// <remarks>
///     <para>
///         Inside an AppContainer, a name like <c>GameOverlay_InputEventStream_1234</c> resolves
///         under the package's own private object namespace, so the game and Steam create two
///         different objects with identical names and never meet. A probe running under the game's
///         token was also refused outright when it tried to open Steam's desktop objects.
///     </para>
///     <para>
///         So the game asks and this answers: it opens one allowlisted object as the desktop user
///         and duplicates the handle in. It does not rewrite Steam's ACLs, change the game's token,
///         create a substitute rendering window, or inject into ApplicationFrameHost. The transport
///         itself is unnamed, so nothing new appears in any namespace.
///     </para>
///     <para>
///         The allowlist is the whole security boundary, which is why it is a pure predicate with
///         tests rather than a condition buried in the worker.
///     </para>
/// </remarks>
internal sealed class OverlayObjectBroker : IDisposable
{
    private const int RequestSize = 552;

    /// <summary>A refusal storm is one line plus a count, not one line per refusal.</summary>
    private const int RefusalsLogged = 3;

    private readonly int pid;
    private readonly string gameId;
    private readonly OverlayObjectAllowList _allowed;
    private int _refusals;
    private readonly List<IntPtr> owned = [];
    private IntPtr process;
    private IntPtr view;
    private IntPtr requestEvent;
    private IntPtr responseEvent;
    private IntPtr stopEvent;
    private Thread? worker;

    private OverlayObjectBroker(int pid, string gameId)
    {
        this.pid = pid;
        this.gameId = gameId;
        _allowed = new OverlayObjectAllowList(pid, gameId);
    }

    internal static OverlayObjectBroker? Start(int pid, IReadOnlyList<string> environment, GameInjector injector)
    {
        var gameId = string.Empty;
        foreach (var variable in environment)
        {
            if (variable.StartsWith("SteamOverlayGameId=", StringComparison.OrdinalIgnoreCase))
            {
                gameId = variable["SteamOverlayGameId=".Length..];
            }
        }

        if (!ulong.TryParse(gameId, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            // The game id scopes the allowlist. Without it every name would have to be admitted on
            // shape alone, which is not a boundary worth having.
            PackagedLaunchLog.Error(
                "The overlay broker needs the SteamOverlayGameId this wrapper was launched with, "
                + "and this process does not have one.");
            return null;
        }

        var broker = new OverlayObjectBroker(pid, gameId);
        try
        {
            broker.process = broker.Own(NativeMethods.OpenProcess(NativeMethods.ProcessDupHandle | NativeMethods.Synchronize, false, (uint)pid));
            var mapping = broker.Own(Api.CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, 4, 0, RequestSize, null));
            broker.view = Api.MapViewOfFile(mapping, 6, 0, 0, (UIntPtr)RequestSize);
            if (broker.view == IntPtr.Zero) { throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
            Marshal.WriteInt32(broker.view, 0, 1);
            broker.requestEvent = broker.Own(Api.CreateEventW(IntPtr.Zero, false, false, null));
            broker.responseEvent = broker.Own(Api.CreateEventW(IntPtr.Zero, false, false, null));
            broker.stopEvent = broker.Own(Api.CreateEventW(IntPtr.Zero, true, false, null));
            var remoteMapping = broker.Duplicate(mapping);
            var remoteRequest = broker.Duplicate(broker.requestEvent);
            var remoteResponse = broker.Duplicate(broker.responseEvent);
            if (!injector.SetEnvironment(pid,
                [$"WSGM_UWP_BRIDGE_MAPPING={remoteMapping:X}", $"WSGM_UWP_BRIDGE_REQUEST={remoteRequest:X}", $"WSGM_UWP_BRIDGE_RESPONSE={remoteResponse:X}"]))
            {
                throw new InvalidOperationException("Could not pass the bridge's handles to the game.");
            }

            broker.worker = new Thread(broker.Run) { IsBackground = true, Name = "UWP overlay object broker" };
            broker.worker.Start();
            PackagedLaunchLog.Info($"IPC broker: ready for pid {pid}; transport handles duplicated without named transport objects.");
            return broker;
        }
        catch (Exception ex)
        {
            PackagedLaunchLog.Error($"IPC broker: {ex.Message}");
            broker.Dispose();
            return null;
        }
    }

    private IntPtr Own(IntPtr handle)
    {
        if (handle == IntPtr.Zero) { throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
        owned.Add(handle);
        return handle;
    }

    private long Duplicate(IntPtr handle)
    {
        if (!Api.DuplicateHandle(Api.GetCurrentProcess(), handle, process, out var remote, 0, false, 2))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        return remote.ToInt64();
    }

    private void Run()
    {
        var handles = new[] { stopEvent, process, requestEvent };
        while (Api.WaitForMultipleObjects((uint)handles.Length, handles, false, uint.MaxValue) == 2)
        {
            try { Respond(); }
            catch (Exception ex)
            {
                PackagedLaunchLog.Error($"IPC broker request failed: {ex.Message}");
                Marshal.WriteInt64(view, 24, 0);
                Marshal.WriteInt32(view, 32, 6);
            }

            Api.SetEvent(responseEvent);
        }
    }

    private void Respond()
    {
        var operation = Marshal.ReadInt32(view, 4);
        var access = unchecked((uint)Marshal.ReadInt32(view, 8));
        var flags = Marshal.ReadInt32(view, 12);
        var high = unchecked((uint)Marshal.ReadInt32(view, 16));
        var low = unchecked((uint)Marshal.ReadInt32(view, 20));
        var name = Marshal.PtrToStringUni(view + 40, 256)!.Split('\0', 2)[0];
        Marshal.WriteInt64(view, 24, 0);
        Marshal.WriteInt32(view, 32, 5);
        var rendererLog = operation == 7 && name == "RendererLog";
        if ((!rendererLog && !_allowed.Admits(name))
            || (operation == 1 && (high != 0 || low > 64 * 1024 * 1024 || access != 4)))
        {
            // Bounded: a game that asks repeatedly for something outside the allowlist would
            // otherwise fill the log with one line per frame.
            var refusals = ++_refusals;
            if (refusals <= RefusalsLogged)
            {
                PackagedLaunchLog.Warn($"The overlay broker refused operation {operation} for {name}.");
            }
            else if (refusals == RefusalsLogged + 1)
            {
                PackagedLaunchLog.Warn("Further overlay broker refusals are not logged individually.");
            }

            return;
        }

        Marshal.SetLastPInvokeError(0);
        var handle = operation switch
        {
            1 => Api.CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, access, high, low, name),
            2 => Api.OpenFileMappingW(access, false, name),
            3 => Api.CreateMutexW(IntPtr.Zero, false, name),
            4 => Api.OpenMutexW(access, false, name),
            5 => Api.CreateEventW(IntPtr.Zero, (flags & 1) != 0, (flags & 2) != 0, name),
            6 => Api.OpenEventW(access, false, name),
            7 when rendererLog => Api.CreateFileW(RendererLogPath(pid),
                access, high | 1, IntPtr.Zero, unchecked((uint)flags), low, IntPtr.Zero),
            _ => IntPtr.Zero,
        };
        var error = Marshal.GetLastPInvokeError();
        try
        {
            if (handle == new IntPtr(-1)) { handle = IntPtr.Zero; }
            if (handle != IntPtr.Zero) { Marshal.WriteInt64(view, 24, Duplicate(handle)); }
            Marshal.WriteInt32(view, 32, error);
            PackagedLaunchLog.Change(
                $"broker:{name}",
                $"The overlay broker answered {name}: "
                + $"{(handle != IntPtr.Zero ? "handle duplicated" : $"nothing, error {error}")}.");
        }
        finally
        {
            if (handle != IntPtr.Zero) { NativeMethods.CloseHandle(handle); }
        }
    }

    /// <summary>Where the renderer's own log goes when diagnostics asked for it to be separated.</summary>
    /// <remarks>
    ///     Steam's renderer writes <c>gameoverlay_renderer.txt</c> beside itself, where it describes
    ///     this wrapper rather than the game, which is actively misleading while diagnosing.
    /// </remarks>
    private static string RendererLogPath(int processId)
    {
        return System.IO.Path.Combine(
            // wsgm-allow-live-data-path: a diagnostic beside WSGM's own log, only when asked for.
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WSGM",
            $"packaged-launch.renderer-{processId}.log");
    }

    public void Dispose()
    {
        if (stopEvent != IntPtr.Zero) { Api.SetEvent(stopEvent); }
        worker?.Join();
        worker = null;
        if (view != IntPtr.Zero) { Api.UnmapViewOfFile(view); view = IntPtr.Zero; }
        for (var index = owned.Count - 1; index >= 0; index--) { NativeMethods.CloseHandle(owned[index]); }
        owned.Clear();
        stopEvent = IntPtr.Zero;
    }

    private static class Api
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateFileMappingW(IntPtr file, IntPtr attributes, uint protect, uint high, uint low, string? name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint high, uint low, UIntPtr size);
        [DllImport("kernel32.dll")]
        internal static extern bool UnmapViewOfFile(IntPtr address);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateEventW(IntPtr attributes, bool manual, bool initial, string? name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr OpenEventW(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateMutexW(IntPtr attributes, bool owner, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr OpenMutexW(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr attributes, uint disposition, uint flags, IntPtr templateFile);
        [DllImport("kernel32.dll")]
        internal static extern bool SetEvent(IntPtr handle);
        [DllImport("kernel32.dll")]
        internal static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool all, uint milliseconds);
        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target, uint access, bool inherit, uint options);
    }
}
