namespace WSGM.Core;

/// <summary>One saved Core Audio endpoint, retained by stable ID and friendly name.</summary>
public sealed class AudioEndpointPreference
{
    /// <summary>Opaque Core Audio endpoint ID.</summary>
    public string? Id { get; set; }

    /// <summary>Last friendly name Windows reported for the endpoint.</summary>
    public string? Name { get; set; }
}
