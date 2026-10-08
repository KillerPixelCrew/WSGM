using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WSGM.Interop;

/// <summary>Exact dynamically loaded RTSS profile API with no static DLL search.</summary>
internal sealed unsafe partial class RtssProfileApi : IDisposable
{
    private const uint LoadLibrarySearchDllLoadDirectory = 0x00000100;
    private const uint LoadLibrarySearchSystem32 = 0x00000800;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, uint, int> _getProfileProperty;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _loadProfile;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _saveProfile;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, uint, int> _setProfileProperty;
    private readonly delegate* unmanaged[Cdecl]<void> _updateProfiles;
    private nint _module;

    /// <summary>Loads a caller-verified RTSS profile DLL and resolves its required Cdecl exports.</summary>
    /// <param name="libraryPath">Exact trusted library path; this wrapper does not establish its signature or provenance.</param>
    /// <remarks>
    ///     The caller serializes profile transactions. Missing libraries/exports throw and release partial native
    ///     ownership.
    /// </remarks>
    internal RtssProfileApi(string libraryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);
        _module = LoadLibraryEx(
            libraryPath,
            0,
            LoadLibrarySearchDllLoadDirectory | LoadLibrarySearchSystem32);
        if (_module == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "The verified RTSS profile API could not be loaded.");
        }

        try
        {
            _loadProfile = (delegate* unmanaged[Cdecl]<nint, void>)GetExport("LoadProfile");
            _saveProfile = (delegate* unmanaged[Cdecl]<nint, void>)GetExport("SaveProfile");
            _getProfileProperty =
                (delegate* unmanaged[Cdecl]<nint, nint, uint, int>)GetExport("GetProfileProperty");
            _setProfileProperty =
                (delegate* unmanaged[Cdecl]<nint, nint, uint, int>)GetExport("SetProfileProperty");
            _updateProfiles = (delegate* unmanaged[Cdecl]<void>)GetExport("UpdateProfiles");
        }
        catch
        {
            FreeLibrary(_module);
            _module = 0;
            throw;
        }
    }

    /// <summary>Unloads the owned native module; repeated disposal is harmless.</summary>
    /// <remarks>No other call may be in flight; property operations after disposal throw.</remarks>
    public void Dispose()
    {
        if (_module == 0)
        {
            return;
        }

        FreeLibrary(_module);
        _module = 0;
    }

    /// <summary>Selects the native working profile for subsequent property operations.</summary>
    /// <param name="profile">RTSS profile name encoded as ANSI; caller owns the surrounding serialized transaction.</param>
    internal void LoadProfile(string profile)
    {
        InvokeString(_loadProfile, profile);
    }

    /// <summary>Saves the native working profile under the requested name.</summary>
    /// <param name="profile">RTSS profile name encoded as ANSI; invoke only after the caller's validated edits.</param>
    internal void SaveProfile(string profile)
    {
        InvokeString(_saveProfile, profile);
    }

    /// <summary>Reads one uint property from the currently loaded native profile.</summary>
    /// <param name="property">RTSS property name encoded as ANSI.</param>
    /// <param name="value">Native value on success; zero when the API did not write it.</param>
    /// <returns>Whether the native getter reported success; disposal errors propagate.</returns>
    internal bool TryGetUInt32(string property, out uint value)
    {
        ObjectDisposedException.ThrowIf(_module == 0, this);
        var propertyPointer = Marshal.StringToCoTaskMemAnsi(property);
        try
        {
            uint readValue = 0;
            var succeeded = _getProfileProperty(
                propertyPointer,
                (nint)(&readValue),
                sizeof(uint)) != 0;
            value = readValue;
            return succeeded;
        }
        finally
        {
            Marshal.FreeCoTaskMem(propertyPointer);
        }
    }

    /// <summary>Updates one uint property of the currently loaded native profile.</summary>
    /// <param name="property">RTSS property name encoded as ANSI.</param>
    /// <param name="value">Validated value to set; persistence remains the caller's SaveProfile operation.</param>
    /// <returns>Whether the native setter accepted the value; disposal errors propagate.</returns>
    internal bool TrySetUInt32(string property, uint value)
    {
        ObjectDisposedException.ThrowIf(_module == 0, this);
        var propertyPointer = Marshal.StringToCoTaskMemAnsi(property);
        try
        {
            return _setProfileProperty(propertyPointer, (nint)(&value), sizeof(uint)) != 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(propertyPointer);
        }
    }

    /// <summary>Notifies RTSS to reload profile state after the caller has saved its changes.</summary>
    internal void UpdateProfiles()
    {
        ObjectDisposedException.ThrowIf(_module == 0, this);
        _updateProfiles();
    }

    private nint GetExport(string name)
    {
        var address = GetProcAddress(_module, name);
        return address != 0
            ? address
            : throw new EntryPointNotFoundException($"RTSS profile API export is absent: {name}.");
    }

    private void InvokeString(delegate* unmanaged[Cdecl]<nint, void> function, string value)
    {
        ObjectDisposedException.ThrowIf(_module == 0, this);
        var pointer = Marshal.StringToCoTaskMemAnsi(value);
        try
        {
            function(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadLibraryEx(string fileName, nint file, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "FreeLibrary")]
    private static partial void FreeLibrary(nint module);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress",
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetProcAddress(nint module, string name);
}
