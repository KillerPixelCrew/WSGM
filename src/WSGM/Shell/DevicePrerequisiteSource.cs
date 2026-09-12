using System;
using System.IO;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Reads what a device package on this install is missing, and switches on the half WSGM
/// owns.
///
/// The two halves are answered by looking at the machine rather than at what a package declares: a
/// device manifest names no prerequisites, and what matters is whether this install has the bytes
/// at all. Setup's Minimal and Desktop modes carry no controller support, and the protected package
/// slot is a directory an administrator can copy into afterwards.</summary>
/// <param name="readState">Reads the machine's current state.</param>
/// <param name="enableIntegration">Switches Device Integration on.</param>
internal sealed class DevicePrerequisiteSource(
    Func<DevicePrerequisiteState> readState,
    Func<Task> enableIntegration)
{
    /// <summary>Describes what is missing, or nothing when the install is complete.</summary>
    /// <returns>The advice to render.</returns>
    internal DevicePrerequisiteAdvice Read()
    {
        try { return DevicePrerequisites.Describe(readState()); }
        catch (Exception ex)
        {
            // A banner is not worth failing an overlay open over.
            Log.Warn("Reading the device prerequisites failed: " + ex.Message);
            return new("", false, false);
        }
    }

    /// <summary>Switches Device Integration on and persists it.</summary>
    /// <returns>A task that completes once the change is saved.</returns>
    internal Task EnableAsync() => enableIntegration();

    /// <summary>Whether the virtual controller library is installed beside WSGM.
    ///
    /// Presence of the file, not a load: asking the loader would either succeed and leave the
    /// library mapped for the rest of the session, or fail for reasons a banner cannot distinguish.
    /// </summary>
    /// <param name="applicationDirectory">The directory holding WSGM's executable.</param>
    /// <returns>True when the library file is there.</returns>
    internal static bool ControllerLibraryInstalled(string applicationDirectory)
    {
        try { return File.Exists(Path.Combine(applicationDirectory, "libviiper.dll")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Whether the HidHide control device answers, which is how its driver is detected.
    /// Opening and closing it changes nothing.</summary>
    /// <returns>True when the driver is installed.</returns>
    internal static bool HidHideInstalled()
    {
        if (!WSGM.Interop.NativeHidHide.TryOpen(
            out Microsoft.Win32.SafeHandles.SafeFileHandle handle, out _))
        {
            return false;
        }
        handle.Dispose();
        return true;
    }
}
