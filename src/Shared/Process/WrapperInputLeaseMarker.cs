using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace WSGM.Core;

/// <summary>Publishes wrapper-owned input leases through the lifetime of a session/user-scoped kernel object.</summary>
/// <remarks>
///     The mutex is never acquired. Each successful wrapper holds a handle; the last handle closes
///     on release or process exit. Readers only open existing objects, so no stale file, polling or
///     shared counter is needed, and ordinary WSGM surface leases never publish this marker.
/// </remarks>
internal static partial class WrapperInputLeaseMarker
{
    private const uint Synchronize = 0x00100000;
    private static readonly Lazy<string> CurrentUserSid = new(() =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("No wrapper user identity is available.");
    });
    private static string CurrentName => @"Local\WSGM.WrapperInputLease." + CurrentUserSid.Value;

    internal static SafeWaitHandle Hold()
    {
        return Hold(CurrentName);
    }

    internal static bool IsActive()
    {
        return IsActive(CurrentName);
    }

    /// <summary>Holds one reference to an explicit marker name, also used for isolated lifecycle tests.</summary>
    internal static SafeWaitHandle Hold(string name)
    {
        // Read-only access can join an elevated wrapper's object without requesting write access.
        var handle = OpenMutexW(Synchronize, false, name);
        if (handle == 0)
        {
            handle = Create(name);
            if (handle == 0)
            {
                var error = Marshal.GetLastPInvokeError();
                // Another integrity level may have won creation between open and create.
                handle = OpenMutexW(Synchronize, false, name);
                if (handle == 0)
                {
                    throw new Win32Exception(error, "Could not publish the wrapper input-lease lifetime.");
                }
            }
        }

        return new SafeWaitHandle(handle, ownsHandle: true);
    }

    internal static bool IsActive(string name)
    {
        using var handle = new SafeWaitHandle(OpenMutexW(Synchronize, false, name), ownsHandle: true);
        if (handle.IsInvalid && Marshal.GetLastPInvokeError() is var error && error != 2)
        {
            throw new Win32Exception(error, "Could not query the wrapper input-lease lifetime.");
        }

        return !handle.IsInvalid;
    }

    private static nint Create(string name)
    {
        // Same-user endpoints must coexist at medium and elevated integrity. This labels only
        // our private kernel object; it changes no system or application security settings.
        var sddl = $"D:P(A;;GA;;;SY)(A;;GA;;;{CurrentUserSid.Value})S:(ML;;NW;;;ME)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var descriptor, out _))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create wrapper lease-marker security.");
        }

        try
        {
            var attributes = new SecurityAttributes
            {
                Length = (uint)Marshal.SizeOf<SecurityAttributes>(),
                Descriptor = descriptor
            };
            return CreateMutexW(ref attributes, false, name);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal uint Length;
        internal nint Descriptor;
        internal int Inherit;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateMutexW(ref SecurityAttributes attributes,
        [MarshalAs(UnmanagedType.Bool)] bool owned, string name);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenMutexW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision,
        out nint descriptor, out uint size);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
