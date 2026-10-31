using System.Collections.Generic;

namespace WSGM.Shell;

internal sealed record BluetoothLogicalDevice(
    string Id,
    string EndpointId,
    string PairingEndpointId,
    string Name,
    string Container,
    bool Paired,
    bool CanPair,
    bool Connected,
    IReadOnlyList<string> EndpointIds);
