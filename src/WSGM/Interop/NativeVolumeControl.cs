using System.Runtime.InteropServices;

namespace WSGM.Interop;

/// <summary>Blittable bridge to the native Core Audio helper. Managed COM
/// interop stays disabled for the NativeAOT executable.</summary>
internal static partial class NativeVolumeControl
{
    [LibraryImport("WSGM.VolumeControl.dll", EntryPoint = "WsgmVolumeCommand")]
    internal static partial int ApplyCommand(int command, out int percentage, out int muted);
}
