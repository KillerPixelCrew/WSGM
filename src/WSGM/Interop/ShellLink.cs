using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WSGM.Interop;

/// <summary>Reads the target of a Windows shortcut (.lnk) through the shell's own resolver.</summary>
/// <remarks>
/// Parsing the file format by hand is the alternative, and it is wrong for the cases that matter:
/// relative paths, environment-variable targets and 32-bit redirection are all resolved by the
/// shell, not by the stored bytes. Resolution is suppressed so a shortcut to a missing target
/// cannot start a search or a network probe.
/// </remarks>
internal static class ShellLink
{
    private const int MaxPath = 260;
    private const uint NoUi = 0x0004;
    private const uint NoSearch = 0x0010;
    private const uint NoTrack = 0x0020;
    private const uint NoLinkInfo = 0x0040;
    private const uint UncacheSitename = 0x0100;

    /// <summary>Returns the executable a shortcut points at, or null when it cannot be read.</summary>
    /// <param name="path">Full path of the .lnk file.</param>
    /// <returns>The stored target path, or null.</returns>
    internal static string? ReadTarget(string path)
    {
        object? instance = null;
        try
        {
            Type type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), throwOnError: true)!;
            instance = Activator.CreateInstance(type)
                ?? throw new COMException("Windows did not create the shortcut resolver.");
            ((IPersistFile)instance).Load(path, 0);
            var link = (IShellLinkW)instance;
            // Suppressed resolution: a shortcut whose target has moved must read as unknown rather
            // than send the shell looking for it.
            link.Resolve(0, NoUi | NoSearch | NoTrack | NoLinkInfo | UncacheSitename);
            StringBuilder target = new(MaxPath);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            string result = target.ToString();
            return result.Length == 0 ? null : result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            WSGM.Core.Log.Warn($"Shortcut target could not be read from {path}: {ex.Message}");
            return null;
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
            {
                _ = Marshal.FinalReleaseComObject(instance);
            }
        }
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int fileLength,
            IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int nameLength);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int directoryLength);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int argumentsLength);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder icon, int iconLength, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? file, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file);
    }
}
