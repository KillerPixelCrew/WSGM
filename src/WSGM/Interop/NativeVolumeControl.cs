using System.Runtime.InteropServices;

namespace WSGM.Interop;

/// <summary>Blittable bridge to the native Core Audio helper. Managed COM
/// interop stays disabled for the NativeAOT executable.</summary>
internal static unsafe partial class NativeVolumeControl
{
    internal const string Library = "WSGM.VolumeControl.dll";
    internal const int Render = 0;
    internal const int Capture = 1;

    [LibraryImport(Library, EntryPoint = "WsgmVolumeCommand")]
    internal static partial int ApplyCommand(int command, out int percentage, out int muted);

    [LibraryImport(Library, EntryPoint = "WsgmVolumeGet")]
    internal static partial int GetVolume(out int percentage, out int muted);

    [LibraryImport(Library, EntryPoint = "WsgmVolumeSet")]
    internal static partial int SetVolume(int percentage, out int muted);

    [LibraryImport(Library, EntryPoint = "WsgmAudioListEndpoints")]
    internal static partial int ListEndpoints(int flow, out nint items, out uint count);

    [LibraryImport(Library, EntryPoint = "WsgmAudioFreeEndpoints")]
    internal static partial void FreeEndpoints(nint items);

    [LibraryImport(
        Library,
        EntryPoint = "WsgmAudioSetDefaultEndpoint",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SetDefaultEndpoint(string endpointId);

    [LibraryImport(Library, EntryPoint = "WsgmPlayVolumeFeedback")]
    internal static partial int PlayFeedback();

    [LibraryImport(Library, EntryPoint = "WsgmInitializeVolumeFeedback")]
    internal static partial int InitializeFeedback();

    private const int EndpointIdUnits = 512;
    private const int EndpointNameUnits = 256;
    private const int EndpointNameOffset = EndpointIdUnits * 2;
    private const int EndpointDefaultOffset = EndpointNameOffset + (EndpointNameUnits * 2);

    /// <summary>The byte size of one native endpoint record.</summary>
    internal const int EndpointRecordSize = EndpointDefaultOffset + 4;

    /// <summary>One active Core Audio endpoint returned by the helper.</summary>
    /// <param name="Id">The opaque endpoint identifier used when selecting it.</param>
    /// <param name="Name">The friendly name shown to the user.</param>
    /// <param name="IsDefault">Whether it is the current console default.</param>
    internal readonly record struct AudioEndpoint(string Id, string Name, bool IsDefault);

    /// <summary>Decodes one fixed-layout native endpoint record.</summary>
    /// <param name="record">A pointer to the record.</param>
    internal static AudioEndpoint ReadEndpoint(nint record) => new(
        ReadFixedString(record, 0, EndpointIdUnits),
        ReadFixedString(record, EndpointNameOffset, EndpointNameUnits),
        Marshal.ReadInt32(record, EndpointDefaultOffset) != 0);

    private static string ReadFixedString(nint record, int offset, int units)
    {
        var start = (char*)(record + offset);
        var length = 0;
        while (length < units && start[length] != '\0')
        {
            length++;
        }
        return length == 0 ? "" : new string(start, 0, length);
    }
}
