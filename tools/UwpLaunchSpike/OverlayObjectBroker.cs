using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace Wsgm.UwpSpike;

/// Opens only the renderer's named IPC objects in the desktop namespace and duplicates
/// their handles into one game. The transport itself uses unnamed objects.
internal sealed class OverlayObjectBroker : IDisposable
{
    private const int RequestSize = 552;
    private readonly SpikeLog log;
    private readonly int pid;
    private readonly string gameId;
    private readonly List<IntPtr> owned = [];
    private IntPtr process;
    private IntPtr view;
    private IntPtr requestEvent;
    private IntPtr responseEvent;
    private IntPtr stopEvent;
    private Thread? worker;

    private OverlayObjectBroker(int pid, string gameId, SpikeLog log)
    {
        this.pid = pid;
        this.gameId = gameId;
        this.log = log;
    }

    internal static OverlayObjectBroker? Start(int pid, IReadOnlyList<string> environment, Injection injection, SpikeLog log)
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
            log.Error("IPC broker needs the Steam-launched wrapper's SteamOverlayGameId.");
            return null;
        }

        var broker = new OverlayObjectBroker(pid, gameId, log);
        try
        {
            broker.process = broker.Own(Native.OpenProcess(Native.ProcessDupHandle | Native.Synchronize, false, (uint)pid));
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
            if (!injection.SetRemoteEnvironment(pid,
                [$"WSGM_UWP_BRIDGE_MAPPING={remoteMapping:X}", $"WSGM_UWP_BRIDGE_REQUEST={remoteRequest:X}", $"WSGM_UWP_BRIDGE_RESPONSE={remoteResponse:X}"]))
            {
                throw new InvalidOperationException("Could not pass the bridge's handles to the game.");
            }

            broker.worker = new Thread(broker.Run) { IsBackground = true, Name = "UWP overlay object broker" };
            broker.worker.Start();
            log.Info($"IPC broker: ready for pid {pid}; transport handles duplicated without named transport objects.");
            return broker;
        }
        catch (Exception ex)
        {
            log.Error($"IPC broker: {ex.Message}");
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
                log.Error($"IPC broker request failed: {ex.Message}");
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
        if ((!rendererLog && !Allowed(name)) || (operation == 1 && (high != 0 || low > 64 * 1024 * 1024 || access != 4)))
        {
            log.Warn($"IPC broker: refused operation {operation} for {name}.");
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
            7 when rendererLog => Api.CreateFileW(System.IO.Path.ChangeExtension(log.Path, $"renderer-{pid}.log"),
                access, high | 1, IntPtr.Zero, unchecked((uint)flags), low, IntPtr.Zero),
            _ => IntPtr.Zero,
        };
        var error = Marshal.GetLastPInvokeError();
        try
        {
            if (handle == new IntPtr(-1)) { handle = IntPtr.Zero; }
            if (handle != IntPtr.Zero) { Marshal.WriteInt64(view, 24, Duplicate(handle)); }
            Marshal.WriteInt32(view, 32, error);
            log.Info($"IPC broker: op={operation} name={name} handle={(handle != IntPtr.Zero ? "duplicated" : "none")} error={error}");
        }
        finally
        {
            if (handle != IntPtr.Zero) { Native.CloseHandle(handle); }
        }
    }

    private bool Allowed(string name)
    {
        if (name == "SteamWebHelper_GPUProcRenderEvent") { return true; }
        if (!name.EndsWith("-IPCWrapper", StringComparison.Ordinal)) { return false; }
        var plain = name[..^"-IPCWrapper".Length];
        if (plain == $"SteamOverlayRunning_{gameId}") { return true; }
        foreach (var prefix in new[]
        {
            "SteamXInput", "GameOverlayRender_PIDStream", "GameOverlayRender_DetourErrorStream",
            $"GameOverlay_InputEventStream_{pid}", $"GameOverlay_ScreenshotStream_{pid}",
            $"GameOverlayRender_PaintCmdStream_{pid}", $"SteamGameStream_{pid}",
        })
        {
            if (plain == prefix + "_mem" || plain == prefix + "_mutex" || plain == prefix + "_written" || plain == prefix + "_avail") { return true; }
        }

        return plain == $"GameOverlay_VGUIPaintingCompleted_{pid}"
            || plain == $"GameOverlay_InGameRenderingCompleted_{pid}"
            || plain == $"GameOverlay_SerializedWorkQueued_{pid}"
            || plain == $"GameOverlay_GameExitingEvent_{pid}";
    }

    public void Dispose()
    {
        if (stopEvent != IntPtr.Zero) { Api.SetEvent(stopEvent); }
        worker?.Join();
        worker = null;
        if (view != IntPtr.Zero) { Api.UnmapViewOfFile(view); view = IntPtr.Zero; }
        for (var index = owned.Count - 1; index >= 0; index--) { Native.CloseHandle(owned[index]); }
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
