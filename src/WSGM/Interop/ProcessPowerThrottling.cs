using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WSGM.Interop;

/// <summary>Sets the current process's Windows Quality of Service level.</summary>
internal static partial class ProcessPowerThrottling
{
    private const int ProcessPowerThrottlingClass = 4;
    private const uint CurrentVersion = 1;
    private const uint ExecutionSpeed = 1;

    /// <summary>Tags the process EcoQoS or HighQoS. Threads tagged on their own keep their tag.</summary>
    /// <param name="efficient">True selects EcoQoS, false HighQoS.</param>
    /// <exception cref="Win32Exception">Windows refused the change.</exception>
    internal static void SetEfficient(bool efficient)
    {
        var state = new State
        {
            Version = CurrentVersion,
            ControlMask = ExecutionSpeed,
            StateMask = efficient ? ExecutionSpeed : 0
        };
        if (!SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottlingClass, ref state,
                (uint)Marshal.SizeOf<State>()))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessInformation(nint process, int informationClass, ref State information,
        uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }
}
