using System.Runtime.InteropServices;

namespace WSGM.Interop;

/// <summary>Blittable bridge to the native radio helper, which owns the WinRT
/// and native-WLAN calls this NativeAOT executable cannot make itself (managed
/// COM interop stays disabled). Every entry point returns
/// <see cref="Ok"/>/<see cref="Failed"/>/<see cref="Panicked"/>; the message for
/// a failure is read back with <see cref="LastError"/> on the same thread.
///
/// The two returned record types are hand-marshalled from documented offsets
/// rather than declared as structs with inline string fields: that keeps the
/// AOT publish free of trim warnings, and the layouts become unit-testable from
/// a synthetic buffer — the same approach the WLAN interface list already
/// uses.</summary>
internal static unsafe partial class NativeRadio
{
    /// <summary>The name of the helper shipped beside the executable.</summary>
    internal const string Library = "WSGM.Radio.dll";

    /// <summary>The call succeeded.</summary>
    internal const int Ok = 0;

    /// <summary>The call failed; the reason is available from <see cref="LastError"/>.</summary>
    internal const int Failed = 1;

    /// <summary>A panic was caught inside the helper.</summary>
    internal const int Panicked = 2;

    // ---- radios ----

    [LibraryImport(Library, EntryPoint = "wsgm_radio_power")]
    internal static partial int GetRadioPower(int kind, out int state);

    [LibraryImport(Library, EntryPoint = "wsgm_radio_access")]
    internal static partial int RequestRadioAccess(out int access);

    [LibraryImport(Library, EntryPoint = "wsgm_radio_set_power")]
    internal static partial int SetRadioPower(int kind, int on, out int access);

    [LibraryImport(Library, EntryPoint = "wsgm_radio_consent", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetConsent(string capability, out int user, out int machine);

    // ---- Wi-Fi ----

    [LibraryImport(Library, EntryPoint = "wsgm_wifi_state")]
    internal static partial int GetWifiState(out int state);

    [LibraryImport(Library, EntryPoint = "wsgm_wifi_status")]
    private static partial int GetWifiStatus(
        out int state, out uint signal, char* ssid, uint ssidCapacity);

    [LibraryImport(Library, EntryPoint = "wsgm_wifi_scan")]
    internal static partial int RequestWifiScan();

    /// <summary>Reads interface state, joined network and signal in one call.
    /// The tile needs all three every tick.</summary>
    /// <param name="state">0 connected, 1 connecting, 2 disconnected, 3 unavailable.</param>
    /// <param name="signal">Signal quality, 0-100.</param>
    /// <param name="ssid">The joined network, or an empty string.</param>
    internal static int WifiStatus(out int state, out int signal, out string ssid)
    {
        var buffer = stackalloc char[64];
        var status = GetWifiStatus(out state, out var quality, buffer, 64);
        signal = (int)quality;
        if (status != Ok)
        {
            ssid = "";
            return status;
        }
        var length = 0;
        while (length < 64 && buffer[length] != '\0')
        {
            length++;
        }
        ssid = new string(buffer, 0, length);
        return status;
    }

    [LibraryImport(Library, EntryPoint = "wsgm_wifi_list")]
    internal static partial int ListWifiNetworks(out nint items, out uint count);

    [LibraryImport(Library, EntryPoint = "wsgm_wifi_free")]
    internal static partial void FreeWifiNetworks(nint items, uint count);

    [LibraryImport(Library, EntryPoint = "wsgm_wifi_connect", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int ConnectWifi(string ssid, string? passphrase, out uint reasonCode);

    [LibraryImport(Library, EntryPoint = "wsgm_wifi_disconnect")]
    internal static partial int DisconnectWifi();

    [LibraryImport(Library, EntryPoint = "wsgm_wifi_forget", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int ForgetWifi(string ssid);

    [LibraryImport(Library, EntryPoint = "wsgm_wifi_reason_verdict")]
    internal static partial int GetReasonVerdict(uint reasonCode);

    [LibraryImport(Library, EntryPoint = "wsgm_wifi_reason_text")]
    private static partial int GetReasonText(uint reasonCode, char* buffer, uint capacity);

    // ---- Bluetooth ----

    [LibraryImport(Library, EntryPoint = "wsgm_bt_list")]
    internal static partial int ListBluetoothDevices(int pairedOnly, out nint items, out uint count);

    [LibraryImport(Library, EntryPoint = "wsgm_bt_free")]
    internal static partial void FreeBluetoothDevices(nint items, uint count);

    [LibraryImport(Library, EntryPoint = "wsgm_bt_pair", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int PairBluetooth(
        string deviceId, nint onRequest, nint onDone, nint context);

    [LibraryImport(Library, EntryPoint = "wsgm_bt_respond", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RespondToPairing(uint token, int accept, string? pin);

    [LibraryImport(Library, EntryPoint = "wsgm_bt_unpair", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int UnpairBluetooth(string deviceId, out int removed);

    /// <summary>Counts Bluetooth devices with a live connection. Fast: answered
    /// from PnP state, no inquiry, so the status tick may poll it.</summary>
    [LibraryImport(Library, EntryPoint = "wsgm_bt_connected_count")]
    internal static partial int ConnectedBluetoothCount(out uint count);

    /// <summary>Lists the device containers that expose Bluetooth audio
    /// endpoints — the devices a Connect/Disconnect action exists for.</summary>
    [LibraryImport(Library, EntryPoint = "wsgm_bt_audio_list")]
    internal static partial int ListBluetoothAudio(out nint items, out uint count);

    [LibraryImport(Library, EntryPoint = "wsgm_bt_audio_free")]
    internal static partial void FreeBluetoothAudio(nint items, uint count);

    /// <summary>Connects or disconnects a paired Bluetooth audio device by its
    /// container id — the same one-shot the Settings app's Connect button uses.
    /// Soft: pairing is untouched.</summary>
    [LibraryImport(Library, EntryPoint = "wsgm_bt_audio_set", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SetBluetoothAudio(string container, int connect);

    [LibraryImport(Library, EntryPoint = "wsgm_bt_watch_start")]
    internal static partial int StartBluetoothWatch(nint onChange, nint context);

    [LibraryImport(Library, EntryPoint = "wsgm_bt_watch_stop")]
    internal static partial int StopBluetoothWatch();

    [LibraryImport(Library, EntryPoint = "wsgm_wifi_watch_start")]
    internal static partial int StartWifiWatch(nint onEvent, nint context);

    [LibraryImport(Library, EntryPoint = "wsgm_wifi_watch_stop")]
    internal static partial int StopWifiWatch();

    [LibraryImport(Library, EntryPoint = "wsgm_radio_last_error")]
    private static partial uint GetLastErrorText(char* buffer, uint capacity);

    // WsgmWifiNetwork: ssid[64] UTF-16 (128 bytes), then four 4-byte fields.
    private const int WifiSsidUnits = 64;
    private const int WifiSignalOffset = WifiSsidUnits * 2;
    private const int WifiSecurityOffset = WifiSignalOffset + 4;
    private const int WifiSavedOffset = WifiSecurityOffset + 4;
    private const int WifiConnectableOffset = WifiSavedOffset + 4;
    private const int WifiConnectedOffset = WifiConnectableOffset + 4;

    /// <summary>The size of one WsgmWifiNetwork record.</summary>
    internal const int WifiRecordSize = WifiConnectedOffset + 4;

    // WsgmBtDevice: id[256] then name[128] UTF-16, three 4-byte fields, then
    // container[40] UTF-16.
    private const int BtIdUnits = 256;
    private const int BtNameUnits = 128;
    private const int BtContainerUnits = 40;
    private const int BtNameOffset = BtIdUnits * 2;
    private const int BtPairedOffset = BtNameOffset + (BtNameUnits * 2);
    private const int BtCanPairOffset = BtPairedOffset + 4;
    private const int BtConnectedOffset = BtCanPairOffset + 4;
    private const int BtContainerOffset = BtConnectedOffset + 4;

    /// <summary>The size of one WsgmBtDevice record.</summary>
    internal const int BluetoothRecordSize = BtContainerOffset + (BtContainerUnits * 2);

    // WsgmBtAudioContainer: container[40] UTF-16, then one 4-byte field.
    private const int AudioActiveOffset = BtContainerUnits * 2;

    /// <summary>The size of one WsgmBtAudioContainer record.</summary>
    internal const int BluetoothAudioRecordSize = AudioActiveOffset + 4;

    /// <summary>One visible Wi-Fi network.</summary>
    /// <param name="Ssid">The network name.</param>
    /// <param name="Signal">Signal quality, 0-100.</param>
    /// <param name="Security">0 open, 1 pre-shared key, 2 enterprise.</param>
    /// <param name="Saved">Whether a saved profile already exists.</param>
    /// <param name="Connectable">Whether Windows believes it can be joined.</param>
    /// <param name="Connected">Whether this is the network currently joined.</param>
    internal readonly record struct WifiNetwork(
        string Ssid, int Signal, int Security, bool Saved, bool Connectable, bool Connected);

    /// <summary>One Bluetooth device.</summary>
    /// <param name="Id">The WinRT device id; the handle for every other call.</param>
    /// <param name="Name">The display name, possibly empty.</param>
    /// <param name="Paired">Whether the device is paired.</param>
    /// <param name="CanPair">Whether Windows thinks pairing is possible.</param>
    /// <param name="Connected">Whether the device has a live connection.</param>
    /// <param name="Container">The device container id, or empty.</param>
    internal readonly record struct BluetoothDevice(
        string Id, string Name, bool Paired, bool CanPair, bool Connected, string Container);

    /// <summary>One device container with Bluetooth audio endpoints.</summary>
    /// <param name="Container">The container id; matches <see cref="BluetoothDevice.Container"/>.</param>
    /// <param name="Active">Whether the audio device is connected right now.</param>
    internal readonly record struct BluetoothAudioContainer(string Container, bool Active);

    /// <summary>Reads a NUL-terminated UTF-16 field of at most <paramref name="units"/>
    /// characters. Stops at the terminator, and at the field edge when the helper
    /// had to clip.</summary>
    /// <param name="record">The start of the record.</param>
    /// <param name="offset">The field's byte offset within it.</param>
    /// <param name="units">The field's length in UTF-16 units.</param>
    internal static string ReadFixedString(nint record, int offset, int units)
    {
        var start = (char*)(record + offset);
        var length = 0;
        while (length < units && start[length] != '\0')
        {
            length++;
        }
        return length == 0 ? "" : new string(start, 0, length);
    }

    /// <summary>Decodes one WsgmWifiNetwork record.</summary>
    /// <param name="record">A pointer to the record.</param>
    internal static WifiNetwork ReadWifiNetwork(nint record) => new(
        ReadFixedString(record, 0, WifiSsidUnits),
        Marshal.ReadInt32(record, WifiSignalOffset),
        Marshal.ReadInt32(record, WifiSecurityOffset),
        Marshal.ReadInt32(record, WifiSavedOffset) != 0,
        Marshal.ReadInt32(record, WifiConnectableOffset) != 0,
        Marshal.ReadInt32(record, WifiConnectedOffset) != 0);

    /// <summary>Decodes one WsgmBtDevice record.</summary>
    /// <param name="record">A pointer to the record.</param>
    internal static BluetoothDevice ReadBluetoothDevice(nint record) => new(
        ReadFixedString(record, 0, BtIdUnits),
        ReadFixedString(record, BtNameOffset, BtNameUnits),
        Marshal.ReadInt32(record, BtPairedOffset) != 0,
        Marshal.ReadInt32(record, BtCanPairOffset) != 0,
        Marshal.ReadInt32(record, BtConnectedOffset) != 0,
        ReadFixedString(record, BtContainerOffset, BtContainerUnits));

    /// <summary>Decodes one WsgmBtAudioContainer record.</summary>
    /// <param name="record">A pointer to the record.</param>
    internal static BluetoothAudioContainer ReadBluetoothAudio(nint record) => new(
        ReadFixedString(record, 0, BtContainerUnits),
        Marshal.ReadInt32(record, AudioActiveOffset) != 0);

    /// <summary>Reads the helper's message for the last failure on this thread.
    /// Returns an empty string when there is none.</summary>
    internal static string LastError()
    {
        var buffer = stackalloc char[512];
        var written = GetLastErrorText(buffer, 512);
        return written == 0 ? "" : new string(buffer, 0, (int)written);
    }

    /// <summary>Asks Windows for its own localized text for a WLAN reason code.
    /// Falls back to the bare number if the helper cannot supply one.</summary>
    /// <param name="reasonCode">The WLAN reason code.</param>
    internal static string ReasonText(uint reasonCode)
    {
        var buffer = stackalloc char[1024];
        if (GetReasonText(reasonCode, buffer, 1024) != Ok)
        {
            return $"Wi-Fi reason code {reasonCode}";
        }
        var length = 0;
        while (length < 1024 && buffer[length] != '\0')
        {
            length++;
        }
        var text = new string(buffer, 0, length).Trim();
        return text.Length == 0 ? $"Wi-Fi reason code {reasonCode}" : text;
    }
}
