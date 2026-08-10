using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// Dev-only diagnostic harness. NOT shipped. Reads Steam's live CApplicationManager
// state via ReadProcessMemory to inspect the library-folder array — used while
// diagnosing the (now abandoned) in-process add path. The live add itself is done
// through Steam's CEF devtools API; see cdp-eval.mjs beside this file.
//
//   wsgm-libtest probe                 dump the live library-folder array (RPM, no inject)

internal static class Program
{
    // steamclient64.dll relative offsets (base-relative).
    private const long GLOBAL_OFFSET = 0x17D3628;        // *(base+off) => client context pointer
    private const long APP_MANAGER_OFFSET = 0xFB0;       // context + off => CApplicationManager (this)
    // Path-to-add member: CApplicationManager::AddLibraryFolder reads the folder it inserts
    // from *(this + 0x878) (== context + 0x1828), NOT from its char* argument. The argument is
    // only validated. So the member must be populated before the call or the add silently no-ops.
    private const long PENDING_PATH_MEMBER = APP_MANAGER_OFFSET + 0x878;   // context + 0x1828
    private const long STORED_ARG_MEMBER = APP_MANAGER_OFFSET + 0x98;      // context + 0x1048 (this+0x98)

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] != "probe")
        {
            Console.Error.WriteLine("usage: wsgm-libtest probe");
            return 2;
        }

        try
        {
            Probe();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static void Probe()
    {
        var steam = FindSteam();
        if (steam is null) { Console.WriteLine("steam.exe not running."); return; }

        nint scBase = 0;
        foreach (ProcessModule m in steam.Modules)
        {
            if (string.Equals(m.ModuleName, "steamclient64.dll", StringComparison.OrdinalIgnoreCase))
            {
                scBase = m.BaseAddress;
                break;
            }
        }
        if (scBase == 0) { Console.WriteLine("steamclient64.dll not loaded in steam.exe."); return; }
        Console.WriteLine($"steam.exe pid={steam.Id}  steamclient64.dll base=0x{scBase:X}");

        nint h = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, steam.Id);
        if (h == 0) { Console.WriteLine($"OpenProcess failed: {Marshal.GetLastWin32Error()}"); return; }
        try
        {
            long context = ReadU64(h, (nint)(scBase + GLOBAL_OFFSET));
            Console.WriteLine($"context = *(base+0x{GLOBAL_OFFSET:X}) = 0x{context:X}");
            if (context == 0) { Console.WriteLine("context is NULL — client engine not initialized."); return; }

            long thisPtr = context + APP_MANAGER_OFFSET;
            Console.WriteLine($"this (CApplicationManager) = 0x{thisPtr:X}");

            DumpCharPtrMember(h, "PENDING path-to-add member (this+0x878)", context + PENDING_PATH_MEMBER);
            DumpCharPtrMember(h, "STORED arg member         (this+0x98) ", context + STORED_ARG_MEMBER);

            // Ground truth: the live library-folder array. AddLibraryFolder inserts into the
            // array at *(this+0xa0); the count is the int at this+0xb0; each entry is 0x70 bytes
            // with the path char* at entry+0x0 and the contentid u64 at entry+0x10.
            long arrayBase = ReadU64(h, (nint)(thisPtr + 0xa0));
            int count = ReadI32(h, (nint)(thisPtr + 0xb0));
            Console.WriteLine($"library array: base=0x{arrayBase:X}  count={count}");
            if (arrayBase != 0 && count is > 0 and < 64)
            {
                for (int i = 0; i < count; i++)
                {
                    long entry = arrayBase + (long)i * 0x70;
                    long pathPtr = ReadU64(h, (nint)entry);
                    long contentId = ReadU64(h, (nint)(entry + 0x10));
                    string p = pathPtr != 0 ? ReadCString(h, (nint)pathPtr, 520) : "<null>";
                    Console.WriteLine($"  [{i}] contentid={(ulong)contentId} path=\"{p}\"");
                }
            }
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private static void DumpCharPtrMember(nint h, string label, long addr)
    {
        long charPtr = ReadU64(h, (nint)addr);
        if (charPtr == 0)
        {
            Console.WriteLine($"{label}: char* = NULL  => getter returns \"\" (EMPTY)");
            return;
        }
        string s = ReadCString(h, (nint)charPtr, 520);
        Console.WriteLine($"{label}: char* = 0x{charPtr:X}  => \"{s}\"");
    }

    private static Process? FindSteam()
    {
        foreach (var p in Process.GetProcessesByName("steam"))
        {
            try
            {
                if (string.Equals(p.MainModule?.ModuleName, "steam.exe", StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            catch { /* access denied for other steam-named procs */ }
        }
        return null;
    }

    private static long ReadU64(nint h, nint addr)
    {
        Span<byte> buf = stackalloc byte[8];
        if (!ReadProcessMemory(h, addr, ref MemoryMarshal.GetReference(buf), 8, out _))
            throw new InvalidOperationException($"RPM u64 @0x{addr:X} failed: {Marshal.GetLastWin32Error()}");
        return BitConverter.ToInt64(buf);
    }

    private static int ReadI32(nint h, nint addr)
    {
        Span<byte> buf = stackalloc byte[4];
        if (!ReadProcessMemory(h, addr, ref MemoryMarshal.GetReference(buf), 4, out _))
            throw new InvalidOperationException($"RPM i32 @0x{addr:X} failed: {Marshal.GetLastWin32Error()}");
        return BitConverter.ToInt32(buf);
    }

    private static string ReadCString(nint h, nint addr, int max)
    {
        var buf = new byte[max];
        ReadProcessMemory(h, addr, ref buf[0], (nuint)max, out nuint read);
        int n = Array.IndexOf(buf, (byte)0);
        if (n < 0) n = (int)read;
        return Encoding.UTF8.GetString(buf, 0, Math.Max(0, n));
    }

    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint h);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(nint h, nint addr, ref byte buf, nuint size, out nuint read);
}
