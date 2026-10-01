using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using WSGM.Core;

namespace WSGM.Interop;

/// <summary>Reads the target of a Windows shortcut (.lnk) through the shell's own resolver.</summary>
/// <remarks>
///     <para>
///         Parsing the file format by hand is the alternative, and it is wrong for the cases that
///         matter: relative paths, environment-variable targets and 32-bit redirection are all resolved
///         by the shell, not by the stored bytes.
///     </para>
///     <para>
///         Resolution runs with no UI, no search, no link tracking and no update, and a short time-out.
///         A shortcut whose target has moved therefore cannot show the shell's "Problem with Shortcut"
///         dialog, which nobody sees in Game Mode and which would hang the reading thread, cannot start
///         a search or a network probe, and is never rewritten on the user's disk. When resolution fails
///         the stored target is read as it is; the caller decides whether that file exists.
///     </para>
///     <para>
///         An advertised shortcut, the kind Windows Installer writes, stores the installer's icon as its
///         path. Its real target is the installed component, which Windows Installer is asked for; an
///         advertised shortcut whose component is not installed names nothing to run.
///     </para>
/// </remarks>
internal static partial class ShellLink
{
    /// <summary>The longest path Windows handles, in characters with its terminator.</summary>
    private const int MaxLongPath = 32768;

    /// <summary>A GUID in braces, the size of an MSI product, feature or component code with its terminator.</summary>
    private const int GuidChars = 39;

    /// <summary><c>SLR_NO_UI</c>: never show a dialog; the high word is the time-out.</summary>
    private const uint NoUi = 0x0001;

    /// <summary><c>SLR_NOUPDATE</c>: never write a changed target back to the link.</summary>
    private const uint NoUpdate = 0x0008;

    /// <summary><c>SLR_NOSEARCH</c>: never search for a moved target.</summary>
    private const uint NoSearch = 0x0010;

    /// <summary><c>SLR_NOTRACK</c>: never ask the link-tracking service.</summary>
    private const uint NoTrack = 0x0020;

    /// <summary><c>SLR_NOLINKINFO</c>: never follow the network location the link recorded.</summary>
    private const uint NoLinkInfo = 0x0040;

    /// <summary>The longest resolution may take, in milliseconds, carried in the flags' high word.</summary>
    private const uint ResolveTimeoutMilliseconds = 500;

    private const uint ErrorSuccess = 0;
    private const int InstallStateLocal = 3;
    private const int InstallStateSource = 4;

    /// <summary>Returns the executable a shortcut points at, or null when it cannot be read.</summary>
    /// <param name="path">Full path of the .lnk file.</param>
    /// <returns>The stored target path, or null.</returns>
    internal static string? ReadTarget(string path)
    {
        return Read(path)?.Target;
    }

    /// <summary>Returns what a shortcut runs, or null when it cannot be read or names no file.</summary>
    /// <param name="path">Full path of the .lnk file.</param>
    /// <returns>The target, arguments and working directory, or null.</returns>
    internal static ShellLinkInfo? Read(string path)
    {
        object? instance = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), true)!;
            instance = Activator.CreateInstance(type)
                       ?? throw new COMException("Windows did not create the shortcut resolver.");
            ((IPersistFile)instance).Load(path, 0);
            var link = (IShellLinkW)instance;

            // The result is not checked: a target that cannot be resolved is still read as stored.
            _ = link.Resolve(
                IntPtr.Zero, NoUi | NoUpdate | NoSearch | NoTrack | NoLinkInfo | (ResolveTimeoutMilliseconds << 16));

            string target;
            if (AdvertisedTarget(path) is { } advertised)
            {
                if (advertised.Length == 0)
                {
                    return null;
                }

                target = advertised;
            }
            else
            {
                StringBuilder stored = new(MaxLongPath);
                link.GetPath(stored, stored.Capacity, IntPtr.Zero, 0);
                target = stored.ToString();
            }

            if (target.Length == 0)
            {
                return null;
            }

            StringBuilder arguments = new(MaxLongPath);
            link.GetArguments(arguments, arguments.Capacity);
            StringBuilder directory = new(MaxLongPath);
            link.GetWorkingDirectory(directory, directory.Capacity);
            return new ShellLinkInfo(
                target,
                arguments.ToString(),
                Environment.ExpandEnvironmentVariables(directory.ToString()));
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException
                                       or InvalidCastException or ArgumentException)
        {
            // An unreadable or malformed link, or one the process may not open.
            Log.Warn($"Shortcut could not be read from {path}: {ex.Message}");
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

    /// <summary>The installed program an advertised shortcut runs.</summary>
    /// <param name="path">The .lnk file.</param>
    /// <returns>
    ///     Null for an ordinary shortcut; empty for an advertised one whose component is not installed;
    ///     otherwise the component's installed file.
    /// </returns>
    private static string? AdvertisedTarget(string path)
    {
        var product = new char[GuidChars];
        var feature = new char[GuidChars];
        var component = new char[GuidChars];
        var result = MsiGetShortcutTarget(path, product, feature, component);
        if (result != ErrorSuccess)
        {
            return null;
        }

        var installed = new char[MaxLongPath];
        var length = (uint)installed.Length;
        var state = MsiGetComponentPath(Terminated(product), Terminated(component), installed, ref length);
        return state is InstallStateLocal or InstallStateSource && length > 0
            ? new string(installed, 0, (int)length)
            : string.Empty;
    }

    private static string Terminated(char[] buffer)
    {
        var end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? buffer.Length : end);
    }

    [LibraryImport("msi.dll", EntryPoint = "MsiGetShortcutTargetW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint MsiGetShortcutTarget(
        string shortcutPath,
        [Out] char[] productCode,
        [Out] char[] featureId,
        [Out] char[] componentCode);

    [LibraryImport("msi.dll", EntryPoint = "MsiGetComponentPathW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MsiGetComponentPath(
        string product,
        string component,
        [Out] char[] path,
        ref uint pathLength);

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out] [MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int fileLength,
            IntPtr findData, uint flags);

        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out] [MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int nameLength);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out] [MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int directoryLength);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out] [MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int argumentsLength);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out] [MarshalAs(UnmanagedType.LPWStr)] StringBuilder icon, int iconLength, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);

        [PreserveSig]
        int Resolve(IntPtr window, uint flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? file, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);

        /// <remarks><c>LPOLESTR*</c>: the callee allocates the string and the marshaller frees it.</remarks>
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
    }
}

/// <summary>What a Windows shortcut runs.</summary>
/// <param name="Target">The program or file it points at.</param>
/// <param name="Arguments">Its stored arguments, or empty.</param>
/// <param name="WorkingDirectory">Its working directory with variables expanded, or empty.</param>
internal sealed record ShellLinkInfo(string Target, string Arguments, string WorkingDirectory);
