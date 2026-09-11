namespace WSGM.Plugin.Ir.Tests;

internal sealed class FakeEndpoint : IIrEndpoint
{
    internal IrPayload? Sent;
    internal int Identifications;
    internal (string Ssid, string Password, string Token)? Network;
    internal bool Connected = true;
    public IrEndpointIdentity? Identity { get; set; }
    public Task<IrEndpointIdentity> IdentifyAsync(CancellationToken token)
    {
        Identifications++;
        Identity = Describe();
        return Task.FromResult(Identity);
    }
    public Task<IrPayload> LearnAsync(TimeSpan timeout, CancellationToken token) => Task.FromResult(new IrPayload(36000, [9000, 4500], "measured"));
    public Task TransmitAsync(IrPayload payload, int repeats, int gapMs, CancellationToken token) { Sent = payload; return Task.CompletedTask; }
    public Task<IrEndpointIdentity> ConfigureNetworkAsync(string ssid, string password, string pairingToken, CancellationToken token)
    {
        Network = (ssid, password, pairingToken);
        return Task.FromResult(Describe());
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    private IrEndpointIdentity Describe() => new("test", "fake", "0.2.0", 1, 1024, "wsgm-ir-abc123", 7521,
        Network is { Ssid.Length: > 0 }, Connected && Network is { Ssid.Length: > 0 }, Connected && Network is { Ssid.Length: > 0 } ? "192.0.2.7" : "");
}
